using System.Reflection;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using ShelfCheck;

public class TestPaths : DispatchProxy
{
    public string Root { get; set; } = "";
    protected override object? Invoke(MethodInfo? method, object?[]? args) =>
        method?.Name == "get_DataPath" ? Root : throw new NotSupportedException(method?.Name);
}

public static class DateStoreChecks
{
    public static void Run(string root)
    {
        Directory.CreateDirectory(Path.Join(root, "shelfcheck"));
        using (var legacy = new SqliteConnection("Pooling=False;Data Source=" + Path.Join(root, "shelfcheck/dates.db")))
        {
            legacy.Open();
            using var command = legacy.CreateCommand();
            command.CommandText = """
                CREATE TABLE runs(id INTEGER PRIMARY KEY,mode TEXT,state TEXT,started INTEGER,ended INTEGER,scope TEXT,source INTEGER,message TEXT);
                INSERT INTO runs VALUES(1,'Apply','Completed',100,200,'old',NULL,NULL);
                CREATE TABLE checked(item TEXT PRIMARY KEY,revision INTEGER NOT NULL,status TEXT NOT NULL);
                INSERT INTO checked VALUES('00000000000000000000000000000001',123,'Changed');
                """;
            command.ExecuteNonQuery();
        }
        var paths = DispatchProxy.Create<IApplicationPaths, TestPaths>();
        ((TestPaths)(object)paths).Root = root;
        var store = new DateRunStore(paths);
        if (store.Find(1)?.Strategy != "Standard" || store.Checked([new Guid("00000000-0000-0000-0000-000000000001")]).Count != 0)
            throw new InvalidOperationException("Legacy date journal migration lost data or trusted incomplete fingerprints.");
        var changed = Guid.NewGuid(); var matching = Guid.NewGuid(); var missing = Guid.NewGuid(); var failed = Guid.NewGuid();
        var before = DateTime.UtcNow.Ticks; var after = new DateTime(2000, 1, 2).Ticks;
        var preview = store.Create("Preview", "scope", before, null, "Full", before);
        store.Enqueue(store.Find(preview)!, [changed, matching, missing], CancellationToken.None);
        store.SaveRows(preview, [new(changed,"Proposed","Movie","Movie",before,after,null),
            new(matching,"Unchanged","Matching","Movie",after,after,null,12,"/movies/matching.mkv"),
            new(missing,"Missing","Missing","Movie",before,null,null,13,"/movies/missing.mkv")]);
        store.State(preview, "Completed");
        var apply = store.Create("Apply", "scope", before+1, preview, "Preview", before);
        store.Enqueue(store.Find(apply)!, [], CancellationToken.None);
        if (store.Count(apply) != 1 || store.Batch(apply,128).Single().Id != changed)
            throw new InvalidOperationException("Preview reuse included unchanged or missing-date items.");
        store.SaveRows(apply, [new(changed,"Changed","Movie","Movie",before,after,null,14,"/movies/movie.mkv")]);
        store.Preparation(apply, 100, 99);
        store.State(apply,"Completed");
        var fingerprint = store.Checked([changed])[changed];
        if (fingerprint != new DateFingerprint(14,after,after,"/movies/movie.mkv") || store.Find(apply)?.Cached != 99)
            throw new InvalidOperationException("Successful date write did not cache the resulting values.");
        if (!fingerprint.Matches(after,after,"/movies/movie.mkv") || fingerprint.Matches(before,after,"/movies/movie.mkv") ||
            fingerprint.Matches(after,before,"/movies/movie.mkv") || fingerprint.Matches(after,after,"/movies/renamed.mkv"))
            throw new InvalidOperationException("Relevant date or path changes were hidden by the cache.");
        var regular = store.Create("Apply", "scope", before+2, null,"Incremental", before+2);
        store.Enqueue(store.Find(regular)!, [missing,failed],CancellationToken.None);
        store.SaveRows(regular,[new(missing,"Missing","Missing","Movie",before,null,null,13,"/movies/missing.mkv"),
            new(failed,"Failed","Failed","Movie",before,after,"Failure")]);
        store.State(regular,"Completed");
        var retry = store.Create("Apply","scope",before+3,null,"Retry",before+3);
        if (!store.OpenItems("scope").ToHashSet().SetEquals([missing,failed]) || store.OpenItems("other").Count != 0)
            throw new InvalidOperationException("Retry candidates were not restricted to open items in the same scope.");
        store.Enqueue(store.Find(retry)!, store.OpenItems("scope"),CancellationToken.None);
        store.SaveRows(retry,[new(missing,"Unchanged","Missing","Movie",after,after,null,15,"/movies/missing.mkv"),
            new(failed,"Failed","Failed","Movie",before,after,"Failure")]);
        store.State(retry,"Completed");
        if (store.LastCompleted("Apply","scope")?.Id != regular || !store.OpenItems("scope").SequenceEqual([failed]))
            throw new InvalidOperationException("Retry advanced the incremental checkpoint or retained a resolved item.");
        var stopped = store.Create("Apply","scope",before+4,null,"Retry",before+4);
        store.Enqueue(store.Find(stopped)!,[failed],CancellationToken.None);
        store.State(stopped,"Stopped");
        if (!store.OpenItems("scope").SequenceEqual([failed]))
            throw new InvalidOperationException("A cancelled retry hid the unresolved item behind its pending row.");
        var undo = store.Create("Undo","scope",before+4,apply);
        store.Enqueue(store.Find(undo)!, [],CancellationToken.None);
        var restoration = store.Batch(undo,128).Single();
        if (restoration.Id != changed || restoration.Before != after || restoration.After != before)
            throw new InvalidOperationException("Undo journal was confused with preview reuse.");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var cancelled = store.Create("Apply","cancelled",before+5,null);
        try { store.Enqueue(store.Find(cancelled)!,[Guid.NewGuid()],cancel.Token); throw new InvalidOperationException("Enqueue ignored cancellation."); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        if (store.IsQueued(cancelled) || store.Count(cancelled) != 0)
            throw new InvalidOperationException("Interrupted preparation left a partial queue.");
        Console.WriteLine("Date journal migration, preview reuse, fingerprints, retry checkpoints, undo and cancellation checks passed.");
    }
}
