using System.Globalization;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace ShelfCheck;

public sealed record DateRun(long Id, string Mode, string State, long Started, long? Ended, string Scope, long? Source, string? Message,
    string Strategy = "Standard", long? Since = null, int Examined = 0, int Cached = 0);
public sealed record DateFingerprint(long Revision, long Created, long? Premiere, string? Path)
{
    public bool Matches(long created, long? premiere, string? path) =>
        Created == created && Premiere == premiere && string.Equals(Path, path, StringComparison.Ordinal);
}
public sealed record DateRow(Guid Id, string Status, string? Name, string? Kind, long? Before, long? After, string? Detail,
    long? Revision = null, string? Path = null, string? Libraries = null);
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
                        CREATE INDEX IF NOT EXISTS entries_open ON entries (status, run);
                        CREATE INDEX IF NOT EXISTS runs_scope ON runs (scope, mode, id);
                        CREATE TABLE IF NOT EXISTS checked (item TEXT PRIMARY KEY, revision INTEGER NOT NULL, status TEXT NOT NULL);
                        UPDATE runs SET state = 'Paused', message = 'Der Server wurde während des Laufs beendet.' WHERE state = 'Running';
                        """);
                    schema.ExecuteNonQuery();
                    var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using (var info = Command(connection, "PRAGMA table_info(entries)"))
                    using (var reader = info.ExecuteReader())
                        while (reader.Read()) columns.Add(reader.GetString(1));
                    foreach (var name in new[] { "path", "libraries" })
                    {
                        if (columns.Contains(name)) continue;
                        using var migration = Command(connection, "ALTER TABLE entries ADD COLUMN " + name + " TEXT");
                        migration.ExecuteNonQuery();
                    }
                    Migrate(connection, "runs", [("strategy", "TEXT NOT NULL DEFAULT 'Standard'"), ("since", "INTEGER"),
                        ("examined", "INTEGER NOT NULL DEFAULT 0"), ("cached", "INTEGER NOT NULL DEFAULT 0")]);
                    Migrate(connection, "checked", [("created", "INTEGER"), ("premiere", "INTEGER"), ("path", "TEXT")]);
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
            SELECT * FROM runs WHERE mode=$mode AND scope=$scope AND state='Completed'
              AND ($mode != 'Apply' OR strategy != 'Retry') ORDER BY id DESC LIMIT 1
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

    public long Create(string mode, string scope, long started, long? source, string strategy = "Standard", long? since = null)
    {
        using var db = Open();
        using var command = Command(db, """
            INSERT INTO runs (mode,state,started,scope,source,strategy,since) VALUES ($mode,'Running',$utc,$scope,$source,$strategy,$since) RETURNING id
            """, ("$mode", mode), ("$utc", started), ("$scope", scope), ("$source", source), ("$strategy", strategy), ("$since", since));
        return (long)command.ExecuteScalar()!;
    }

    public void Enqueue(DateRun run, IEnumerable<Guid> ids, CancellationToken token)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        if (run.Source is long source && run.Mode == "Undo")
        {
            using var undo = Command(db, """
                INSERT INTO entries (run,item,status,name,kind,before,after,path,libraries)
                SELECT $run,e.item,'Pending',e.name,e.kind,e.after,e.before,e.path,e.libraries FROM entries e
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
        else if (run.Source is long preview && run.Strategy == "Preview")
        {
            using var planned = Command(db, """
                INSERT INTO entries (run,item,status,name,kind,path,libraries)
                SELECT $run,item,'Pending',name,kind,path,libraries FROM entries
                WHERE run=$source AND status IN ('Proposed','Failed','Conflict')
                """, ("$run", run.Id), ("$source", preview));
            planned.Transaction = tx;
            planned.ExecuteNonQuery();
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
            SELECT e.item,e.status,e.name,e.kind,e.before,e.after,e.detail,c.revision,e.path,e.libraries
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
            UPDATE entries SET status=$status,name=$name,kind=$kind,before=$before,after=$after,detail=$detail,path=$path,libraries=$libraries
            WHERE run=$run AND item=$item
            """, ("$run", id), ("$item", ""), ("$status", ""), ("$name", null), ("$kind", null),
            ("$before", null), ("$after", null), ("$detail", null), ("$path", null), ("$libraries", null));
        command.Transaction = tx;
        command.Prepare();
        using var checkedItem = Command(db, """
            INSERT OR REPLACE INTO checked (item,revision,status,created,premiere,path)
            VALUES ($item,$revision,$status,$created,$premiere,$path)
            """, ("$item", ""), ("$revision", 0L), ("$status", ""), ("$created", null), ("$premiere", null), ("$path", null));
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
            command.Parameters["$path"].Value = (object?)row.Path ?? DBNull.Value;
            command.Parameters["$libraries"].Value = (object?)row.Libraries ?? DBNull.Value;
            command.ExecuteNonQuery();
            if (row.Revision is long revision && row.Status is "Changed" or "Unchanged" or "Missing")
            {
                checkedItem.Parameters["$item"].Value = row.Id.ToString("N");
                checkedItem.Parameters["$revision"].Value = revision;
                checkedItem.Parameters["$status"].Value = row.Status;
                checkedItem.Parameters["$created"].Value = (object?)(row.Status == "Changed" ? row.After : row.Before) ?? DBNull.Value;
                checkedItem.Parameters["$premiere"].Value = (object?)row.After ?? DBNull.Value;
                checkedItem.Parameters["$path"].Value = (object?)row.Path ?? DBNull.Value;
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
            SELECT item,status,name,kind,before,after,detail,NULL,path,libraries FROM entries
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
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), Number(r, 4), r.GetString(5), Number(r, 6), Text(r, 7),
            r.GetString(8), Number(r, 9), r.GetInt32(10), r.GetInt32(11));

    public Dictionary<Guid, DateFingerprint> Checked(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) return [];
        using var db = Open();
        var parameters = ids.Select((id, i) => (Name: "$i" + i, Value: (object?)id.ToString("N"))).ToArray();
        using var command = Command(db, "SELECT item,revision,created,premiere,path FROM checked WHERE created IS NOT NULL AND item IN (" +
            string.Join(',', parameters.Select(p => p.Name)) + ")", parameters);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<Guid, DateFingerprint>();
        while (reader.Read()) result.Add(Guid.Parse(reader.GetString(0)),
            new(reader.GetInt64(1), reader.GetInt64(2), Number(reader, 3), Text(reader, 4)));
        return result;
    }

    public List<Guid> OpenItems(string scope)
    {
        using var db = Open();
        using var command = Command(db, """
            SELECT e.item FROM entries e JOIN runs r ON r.id=e.run
            WHERE r.scope=$scope AND r.mode='Apply' AND e.status IN ('Failed','Conflict','Missing','Pending','Writing')
              AND NOT EXISTS (SELECT 1 FROM entries newer JOIN runs nr ON nr.id=newer.run
                WHERE newer.item=e.item AND nr.scope=$scope AND nr.mode IN ('Apply','Undo') AND newer.run>e.run)
            """, ("$scope", scope));
        using var reader = command.ExecuteReader();
        var result = new List<Guid>();
        while (reader.Read()) result.Add(Guid.Parse(reader.GetString(0)));
        return result;
    }

    public void Preparation(long id, int examined, int cached)
    {
        using var db = Open();
        using var command = Command(db, "UPDATE runs SET examined=$examined,cached=$cached WHERE id=$id",
            ("$id", id), ("$examined", examined), ("$cached", cached));
        command.ExecuteNonQuery();
    }

    private static void Migrate(SqliteConnection db, string table, (string Name, string Type)[] additions)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = Command(db, "PRAGMA table_info(" + table + ")"))
        using (var reader = info.ExecuteReader())
            while (reader.Read()) columns.Add(reader.GetString(1));
        foreach (var (name, type) in additions)
        {
            if (columns.Contains(name)) continue;
            using var migration = Command(db, "ALTER TABLE " + table + " ADD COLUMN " + name + " " + type);
            migration.ExecuteNonQuery();
        }
    }

    private static List<DateRow> ReadRows(SqliteCommand command)
    {
        using var r = command.ExecuteReader();
        var result = new List<DateRow>();
        while (r.Read())
            result.Add(new(Guid.Parse(r.GetString(0)), r.GetString(1), Text(r, 2), Text(r, 3), Number(r, 4), Number(r, 5), Text(r, 6),
                Number(r, 7), Text(r, 8), Text(r, 9)));
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
