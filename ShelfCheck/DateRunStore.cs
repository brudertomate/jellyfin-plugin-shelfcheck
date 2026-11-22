using System.Globalization;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace ShelfCheck;

public sealed record DateRun(long Id, string Mode, string State, long Started, long? Ended, string Scope, long? Source, string? Message);
public sealed record DateRow(Guid Id, string Status, string? Name, string? Kind, long? Before, long? After, string? Detail, long? Revision = null);
public sealed record DateRunReport(DateRun Run, Dictionary<string, int> Counts, List<DateRow> Items, int Total, int Offset);

public sealed class DateRunStore(IApplicationPaths paths)
{
    private readonly string _directory = Path.Combine(paths.DataPath, "shelfcheck");
    private readonly object _gate = new();
    private bool _initialized;

    private SqliteConnection Open()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_directory, "dates.db"),
                DefaultTimeout = 10,
                Pooling = false
            }.ToString());
            try
            {
                connection.Open();
                if (!_initialized)
                {
                    using var schema = Command(connection, """
                        PRAGMA journal_mode=WAL;
                        CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                        CREATE TABLE IF NOT EXISTS runs (
                          id INTEGER PRIMARY KEY, mode TEXT NOT NULL, state TEXT NOT NULL, started INTEGER NOT NULL,
                          ended INTEGER, scope TEXT NOT NULL, source INTEGER, message TEXT
                        );
                        CREATE TABLE IF NOT EXISTS entries (
                          run INTEGER NOT NULL, item TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'Pending',
                          name TEXT, kind TEXT, before INTEGER, after INTEGER, detail TEXT,
                          PRIMARY KEY (run, item)
                        );
                        CREATE INDEX IF NOT EXISTS entries_status ON entries (run, status);
                        CREATE INDEX IF NOT EXISTS entries_item ON entries (item, run);
                        CREATE TABLE IF NOT EXISTS checked (item TEXT PRIMARY KEY, revision INTEGER NOT NULL, status TEXT NOT NULL);
                        UPDATE runs SET state = 'Paused', message = 'Der Server wurde während des Laufs beendet.' WHERE state = 'Running';
                        """);
                    schema.ExecuteNonQuery();
                    _initialized = true;
                }
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
    }

    public Guid[] GetSelection()
    {
        using var db = Open();
        using var command = Command(db, "SELECT value FROM settings WHERE key='libraries'");
        return command.ExecuteScalar() is string value ? JsonSerializer.Deserialize<Guid[]>(value) ?? [] : [];
    }

    public void SaveSelection(Guid[] ids)
    {
        using var db = Open();
        using var command = Command(db, "INSERT OR REPLACE INTO settings VALUES ('libraries', $value)", ("$value", JsonSerializer.Serialize(ids)));
        command.ExecuteNonQuery();
    }

    public List<DateRun> Recent()
    {
        using var db = Open();
        using var command = Command(db, "SELECT * FROM runs ORDER BY id DESC LIMIT 20");
        using var reader = command.ExecuteReader();
        var result = new List<DateRun>();
        while (reader.Read()) result.Add(ReadRun(reader));
        return result;
    }

    public DateRun? Find(long id)
    {
        using var db = Open();
        using var command = Command(db, "SELECT * FROM runs WHERE id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    public DateRun? LastCompleted(string mode, string scope)
    {
        using var db = Open();
        using var command = Command(db, """
            SELECT * FROM runs WHERE mode=$mode AND scope=$scope AND state='Completed' ORDER BY id DESC LIMIT 1
            """, ("$mode", mode), ("$scope", scope));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    public DateRun? Unfinished()
    {
        using var db = Open();
        using var command = Command(db, "SELECT * FROM runs WHERE state IN ('Running','Paused') ORDER BY id DESC LIMIT 1");
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    public List<Guid> RetryItems(long id)
    {
        using var db = Open();
        using var command = Command(db, """
            SELECT item FROM entries WHERE run=$id AND status IN ('Failed','Conflict','Proposed')
            """, ("$id", id));
        using var reader = command.ExecuteReader();
        var result = new List<Guid>();
        while (reader.Read()) result.Add(Guid.Parse(reader.GetString(0)));
        return result;
    }

    public long Create(string mode, string scope, long started, long? source)
    {
        using var db = Open();
        using var command = Command(db, """
            INSERT INTO runs (mode,state,started,scope,source) VALUES ($mode,'Running',$utc,$scope,$source) RETURNING id
            """, ("$mode", mode), ("$utc", started), ("$scope", scope), ("$source", source));
        return (long)command.ExecuteScalar()!;
    }

    public void Enqueue(DateRun run, IEnumerable<Guid> ids, CancellationToken token)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        if (run.Source is long source)
        {
            using var undo = Command(db, """
                INSERT INTO entries (run,item,status,name,kind,before,after)
                SELECT $run,e.item,'Pending',e.name,e.kind,e.after,e.before FROM entries e
                WHERE e.run=$source AND e.status IN ('Changed','Writing','Failed') AND e.before IS NOT NULL
                AND NOT EXISTS (
                    SELECT 1 FROM entries newer JOIN runs r ON r.id=newer.run
                    WHERE newer.item=e.item AND newer.run > $source AND newer.run < $run
                    AND r.mode='Apply' AND newer.status IN ('Changed','Writing','Failed') AND newer.before IS NOT NULL
                )
                """, ("$run", run.Id), ("$source", source));
            undo.Transaction = tx;
            undo.ExecuteNonQuery();
        }
        else
        {
            using var command = Command(db, "INSERT OR IGNORE INTO entries (run,item) VALUES ($run,$item)", ("$run", run.Id), ("$item", ""));
            command.Transaction = tx;
            command.Prepare();
            foreach (var id in ids)
            {
                token.ThrowIfCancellationRequested();
                command.Parameters["$item"].Value = id.ToString("N");
                command.ExecuteNonQuery();
            }
        }

        using var ready = Command(db, "INSERT OR IGNORE INTO settings VALUES ($key,'1')", ("$key", QueueKey(run.Id)));
        ready.Transaction = tx;
        ready.ExecuteNonQuery();
        tx.Commit();
    }

    public bool IsQueued(long id)
    {
        using var db = Open();
        using var command = Command(db, "SELECT 1 FROM settings WHERE key=$key", ("$key", QueueKey(id)));
        return command.ExecuteScalar() is not null;
    }

    public List<DateRow> Batch(long id, int limit)
    {
        using var db = Open();
        using var command = Command(db, """
            SELECT e.item,e.status,e.name,e.kind,e.before,e.after,e.detail,c.revision
            FROM entries e LEFT JOIN checked c ON c.item=e.item
            WHERE e.run=$run AND e.status = CASE WHEN EXISTS (
                SELECT 1 FROM entries WHERE run=$run AND status='Writing'
            ) THEN 'Writing' ELSE 'Pending' END ORDER BY e.rowid LIMIT $limit
            """, ("$run", id), ("$limit", limit));
        return ReadRows(command);
    }

    public void SaveRows(long id, IReadOnlyList<DateRow> rows)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var command = Command(db, """
            UPDATE entries SET status=$status,name=$name,kind=$kind,before=$before,after=$after,detail=$detail
            WHERE run=$run AND item=$item
            """, ("$run", id), ("$item", ""), ("$status", ""), ("$name", null), ("$kind", null),
            ("$before", null), ("$after", null), ("$detail", null));
        command.Transaction = tx;
        command.Prepare();
        using var checkedItem = Command(db, "INSERT OR REPLACE INTO checked VALUES ($item,$revision,$status)",
            ("$item", ""), ("$revision", 0L), ("$status", ""));
        checkedItem.Transaction = tx;
        checkedItem.Prepare();
        foreach (var row in rows)
        {
            command.Parameters["$item"].Value = row.Id.ToString("N");
            command.Parameters["$status"].Value = row.Status;
            command.Parameters["$name"].Value = (object?)row.Name ?? DBNull.Value;
            command.Parameters["$kind"].Value = (object?)row.Kind ?? DBNull.Value;
            command.Parameters["$before"].Value = (object?)row.Before ?? DBNull.Value;
            command.Parameters["$after"].Value = (object?)row.After ?? DBNull.Value;
            command.Parameters["$detail"].Value = (object?)row.Detail ?? DBNull.Value;
            command.ExecuteNonQuery();
            if (row.Revision is long revision && row.Status is "Changed" or "Unchanged" or "Missing")
            {
                checkedItem.Parameters["$item"].Value = row.Id.ToString("N");
                checkedItem.Parameters["$revision"].Value = revision;
                checkedItem.Parameters["$status"].Value = row.Status;
                checkedItem.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    public void State(long id, string state, string? message = null)
    {
        using var db = Open();
        using var command = Command(db, "UPDATE runs SET state=$state,ended=$ended,message=$message WHERE id=$id",
            ("$state", state), ("$ended", state == "Running" ? null : DateTime.UtcNow.Ticks), ("$message", message), ("$id", id));
        command.ExecuteNonQuery();
    }

    public DateRunReport? Report(long id, string? status, int offset)
    {
        var run = Find(id);
        if (run is null) return null;
        using var db = Open();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var command = Command(db, "SELECT status,COUNT(*) FROM entries WHERE run=$run GROUP BY status", ("$run", id)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) counts[reader.GetString(0)] = reader.GetInt32(1);
        var total = status is null
            ? counts.Where(c => c.Key is not ("Pending" or "Writing" or "Unchanged")).Sum(c => c.Value)
            : counts.GetValueOrDefault(status);
        offset = Math.Clamp(offset, 0, Math.Max(0, (total - 1) / 50 * 50));
        using var rows = Command(db, """
            SELECT item,status,name,kind,before,after,detail,NULL FROM entries
            WHERE run=$run AND (($status IS NULL AND status NOT IN ('Pending','Writing','Unchanged')) OR status=$status)
            ORDER BY rowid LIMIT 50 OFFSET $offset
            """, ("$run", id), ("$status", status), ("$offset", offset));
        return new(run, counts, ReadRows(rows), total, offset);
    }

    public int Count(long id)
    {
        using var db = Open();
        using var command = Command(db, "SELECT COUNT(*) FROM entries WHERE run=$run", ("$run", id));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string QueueKey(long id) => "queue:" + id.ToString(CultureInfo.InvariantCulture);

    private static DateRun ReadRun(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), Number(r, 4), r.GetString(5), Number(r, 6), Text(r, 7));

    private static List<DateRow> ReadRows(SqliteCommand command)
    {
        using var r = command.ExecuteReader();
        var result = new List<DateRow>();
        while (r.Read())
            result.Add(new(Guid.Parse(r.GetString(0)), r.GetString(1), Text(r, 2), Text(r, 3), Number(r, 4), Number(r, 5), Text(r, 6), Number(r, 7)));
        return result;
    }

    private static long? Number(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static SqliteCommand Command(SqliteConnection db, string sql, params (string Name, object? Value)[] values)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
