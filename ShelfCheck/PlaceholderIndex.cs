using System.Collections.Immutable;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace ShelfCheck;

public sealed class PlaceholderIndex(IApplicationPaths paths)
{
    private readonly object _gate = new();
    private readonly string _directory = Path.Combine(paths.DataPath, "shelfcheck");
    private ImmutableHashSet<string>? _paths;

    public IReadOnlySet<string> Snapshot
    {
        get
        {
            lock (_gate)
            {
                if (_paths is not null) return _paths;
                using var db = Open();
                using var command = db.CreateCommand();
                command.CommandText = "SELECT path FROM placeholders";
                using var reader = command.ExecuteReader();
                var found = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
                while (reader.Read()) found.Add(reader.GetString(0));
                _paths = found.ToImmutable();
                return _paths;
            }
        }
    }

    public void Set(string path, bool placeholder)
    {
        lock (_gate)
        {
            if (Snapshot.Contains(path) == placeholder) return;
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = placeholder ? "INSERT OR IGNORE INTO placeholders(path) VALUES ($path)" :
                "DELETE FROM placeholders WHERE path=$path";
            command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
            _paths = placeholder ? _paths!.Add(path) : _paths!.Remove(path);
        }
    }

    public static Finding Decorate(Finding finding, IReadOnlySet<string> knownPaths) =>
        finding.Code == "MissingCover" && knownPaths.Contains(finding.Path)
            ? finding with { Code = "PlaceholderCover", Detail = "Bei der letzten ShelfCheck-Bildaktion wurde ein Platzhalter erfasst. Echtes Poster kann erneut gesucht werden." }
            : finding;

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(_directory);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "posters.db"), DefaultTimeout = 10
        }.ToString());
        try
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS placeholders(path TEXT PRIMARY KEY NOT NULL)";
            command.ExecuteNonQuery();
            return db;
        }
        catch { db.Dispose(); throw; }
    }
}
