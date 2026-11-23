using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ShelfCheck;

public sealed record DateLibrary(Guid Id, string Name, string[] Paths, Guid[] PhysicalIds, bool Eligible);
public sealed record DateSyncProgress(long Id, int Done, int Total, string Phase,
    string? CurrentItem = null, DateTime UpdatedUtc = default);

public sealed class DateSync(ILibraryManager libraries, DateRunStore store, IHostApplicationLifetime lifetime,
    ILogger<DateSync> logger) : IAsyncDisposable
{
    private const int BatchSize = 32;
    private static readonly TimeSpan WriteDelay = TimeSpan.FromMilliseconds(100);
    private readonly object _gate = new();
    private CancellationTokenSource? _cancel;
    private Task _worker = Task.CompletedTask;
    private DateSyncProgress? _progress;
    private bool _disposed;
    private bool _endRequested;

    public object Status()
    {
        lock (_gate)
        {
            var selection = store.GetSelection();
            var scope = GetScope(selection);
            return new
            {
                Libraries = scope.Libraries.Select(l => new { l.Id, l.Name }),
                Selection = selection,
                Active = _progress,
                Paused = _cancel is null ? store.Unfinished() : null,
                CanApply = scope.Included.Length > 0 && CanApply(scope),
                Incremental = store.LastCompleted("Apply", scope.Key) is not null,
                History = store.Recent()
            };
        }
    }

    public string? SetSelection(Guid[] ids)
    {
        lock (_gate)
        {
            if (_cancel is not null || store.Unfinished() is not null)
                return "Beende zuerst den laufenden oder pausierten Abgleich.";
            if (ids.Except(GetLibraries().Where(l => l.Eligible).Select(l => l.Id)).Any())
                return "Mindestens eine Bibliothek steht für den Datumsabgleich nicht mehr zur Verfügung.";
            store.SaveSelection(ids.Distinct().Order().ToArray());
            return null;
        }
    }

    public string? Start(string mode, long? source = null)
    {
        lock (_gate) return StartCore(mode, source, null, CancellationToken.None);
    }

    public async Task RunScheduledAsync(IProgress<double> progress, CancellationToken token)
    {
        Task worker;
        lock (_gate)
        {
            var error = StartCore("Apply", null, progress, token);
            if (error is not null)
            {
                logger.LogInformation("ShelfCheck: Zeitplan übersprungen. {Reason}", error);
                return;
            }
            worker = _worker;
        }
        await worker;
    }

    public void Pause()
    {
        lock (_gate) _cancel?.Cancel();
    }

    public void Abort()
    {
        lock (_gate)
        {
            if (_cancel is null) return;
            _endRequested = true;
            _cancel.Cancel();
        }
    }

    public string? EndPaused()
    {
        lock (_gate)
        {
            if (_cancel is not null || store.Unfinished() is not { State: "Paused" } run)
                return "Es gibt keinen pausierten Lauf.";
            store.State(run.Id, "Stopped", "Lauf beendet. Bereits übernommene Änderungen bleiben im Verlauf.");
            return null;
        }
    }

    private string? StartCore(string mode, long? source, IProgress<double>? progress, CancellationToken token)
    {
        if (_cancel is not null || _disposed || lifetime.ApplicationStopping.IsCancellationRequested)
            return "Es läuft bereits ein Datumsabgleich oder der Server wird beendet.";
        var scope = GetScope(store.GetSelection());
        var unfinished = store.Unfinished();
        var started = DateTime.UtcNow.Ticks;
        long id;
        if (mode == "Resume")
        {
            if (unfinished is not { State: "Paused" }) return "Es gibt keinen pausierten Lauf.";
            if (unfinished.Scope != scope.Key) return "Bibliothekspfade haben sich geändert. Beende den alten Lauf und starte eine neue Vorschau.";
            id = unfinished.Id;
            store.State(id, "Running");
        }
        else
        {
            if (unfinished is not null) return "Setze den pausierten Lauf fort oder beende ihn.";
            if (scope.Included.Length == 0) return "Wähle zuerst mindestens eine Bibliothek aus.";
            if (mode == "Apply" && !CanApply(scope)) return "Erstelle zuerst eine Vorschau für diese Bibliotheksauswahl.";
            if (mode == "Undo" && (source is null || store.Find(source.Value) is not { Mode: "Apply" }))
                return "Der gewählte Änderungslauf wurde nicht gefunden.";
            id = store.Create(mode, scope.Key, started, source);
        }

        var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.ApplicationStopping);
        _endRequested = false;
        _cancel = cancel;
        _progress = new(id, 0, 0, "Preparing", UpdatedUtc: DateTime.UtcNow);
        _worker = Task.Run(() => RunAsync(id, scope, progress, cancel));
        return null;
    }

    private bool CanApply(Scope scope) => store.LastCompleted("Apply", scope.Key) is not null ||
        store.LastCompleted("Preview", scope.Key) is not null;

    private async Task RunAsync(long id, Scope scope, IProgress<double>? progress, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            var run = store.Find(id)!;
            var baseline = run.Mode == "Apply" ? store.LastCompleted("Apply", scope.Key) : null;
            if (!store.IsQueued(id))
            {
                await WaitForLibraryAsync(id, 0, 0, token);
                var ids = run.Source.HasValue ? [] : FindCandidates(scope, baseline);
                store.Enqueue(run, ids, token);
            }

            var total = store.Count(id);
            var report = store.Report(id, "Pending", 0)!;
            var done = total - report.Counts.GetValueOrDefault("Pending") - report.Counts.GetValueOrDefault("Writing");
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var batch = store.Batch(id, BatchSize);
                if (batch.Count == 0) break;
                await WaitForLibraryAsync(id, done, total, token);
                await ProcessBatch(run, scope, batch, token);
                done += batch.Count;
                SetProgress(id, done, total, "Processing");
                progress?.Report(100d * done / total);
                await Task.Delay(120, token);
            }
            store.State(id, "Completed");
            progress?.Report(100);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            store.State(id, _endRequested ? "Stopped" : "Paused", _endRequested ?
                "Lauf abgebrochen. Bereits übernommene Änderungen bleiben im Verlauf." :
                "Fortschritt gespeichert. Du kannst den Lauf fortsetzen.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ShelfCheck: Datumsabgleich {RunId} fehlgeschlagen.", id);
            store.State(id, "Paused", "Lauf unterbrochen: " + Brief(ex));
        }
        finally
        {
            lock (_gate)
            {
                _progress = null;
                _cancel = null;
                cancellation.Dispose();
            }
        }
    }

    private IEnumerable<Guid> FindCandidates(Scope scope, DateRun? baseline)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode],
            TopParentIds = scope.Included.SelectMany(l => l.PhysicalIds).Distinct().ToArray(),
            IsVirtualItem = false,
            EnableTotalRecordCount = false,
            MinDateLastSaved = baseline is null ? null : new DateTime(baseline.Started, DateTimeKind.Utc).AddSeconds(-5)
        };
        var candidates = libraries.GetItemIds(query);
        if (baseline is null) return candidates;
        var retry = store.RetryItems(baseline.Id);
        if (store.LastCompleted("Preview", scope.Key) is { } preview && preview.Id > baseline.Id)
            retry.AddRange(store.RetryItems(preview.Id));
        return candidates.Concat(retry);
    }

    private async Task ProcessBatch(DateRun run, Scope scope, List<DateRow> batch, CancellationToken token)
    {
        var items = libraries.GetItemList(new InternalItemsQuery
        {
            ItemIds = batch.Select(r => r.Id).ToArray(),
            EnableTotalRecordCount = false
        }).ToDictionary(i => i.Id);

        var results = new List<DateRow>(batch.Count);
        var writes = new List<DateRow>();
        foreach (var row in batch)
        {
            token.ThrowIfCancellationRequested();
            var planned = Plan(run, scope, row, items.GetValueOrDefault(row.Id));
            if (planned.Status == "Writing") writes.Add(planned);
            else results.Add(planned);
        }

        // Journal the original values before touching Jellyfin. On restart the current value resolves any open write.
        store.SaveRows(run.Id, results.Concat(writes).ToArray());
        results.Clear();
        try
        {
            foreach (var row in writes)
            {
                token.ThrowIfCancellationRequested();
                await WaitForLibraryAsync(run.Id, null, null, token);
                var fresh = libraries.GetItemById(row.Id);
                lock (_gate) _progress = _progress! with { CurrentItem = fresh?.Name, UpdatedUtc = DateTime.UtcNow };
                var planned = Plan(run, scope, row, fresh);
                if (planned.Status == "Writing" && fresh is not null)
                {
                    var written = await Write(run, fresh, planned);
                    store.SaveRows(run.Id, [written]);
                    lock (_gate) _progress = _progress! with { Done = _progress.Done + 1, UpdatedUtc = DateTime.UtcNow };
                    await Task.Delay(WriteDelay, token);
                }
                else results.Add(planned);
            }
        }
        finally
        {
            if (results.Count > 0) store.SaveRows(run.Id, results);
        }
    }

    private static DateRow Plan(DateRun run, Scope scope, DateRow row, BaseItem? item)
    {
        if (item is null || item.IsVirtualItem || !item.IsFileProtocol || item.Path is not { Length: > 0 } path ||
            !scope.Allows(path))
            return row with { Status = "Skipped", Detail = "Eintrag fehlt, ist virtuell oder liegt außerhalb der freigegebenen Bibliotheken." };

        row = row with { Name = item.Name, Kind = item.GetType().Name, Detail = null };
        var current = item.DateCreated.Ticks;
        if (row.Status == "Writing" || run.Mode == "Undo")
        {
            if (current == row.After)
                return row with { Status = row.Status != "Writing" ? "Unchanged" : run.Mode == "Undo" ? "Restored" : "Changed",
                    Revision = run.Mode != "Undo" && item.PremiereDate?.Ticks == row.After ? item.DateLastSaved.Ticks : null };
            if (current != row.Before || (run.Mode != "Undo" && item.PremiereDate?.Ticks != row.After))
                return row with { Status = "Conflict", Detail = "Datum wurde zwischenzeitlich geändert." };
            return row with { Status = "Writing" };
        }

        var revision = item.DateLastSaved.Ticks;
        var premiere = item.PremiereDate;
        if (premiere is null || premiere.Value.Year <= 1)
            return row with { Status = "Missing", Before = current, Detail = "Kein Veröffentlichungsdatum in Jellyfin.", Revision = revision };
        if ((revision > 0 && row.Revision == revision) || item.DateCreated.Date == premiere.Value.Date)
            return row with { Status = "Unchanged", Before = current, After = premiere.Value.Ticks, Revision = revision };

        return row with { Status = run.Mode == "Preview" ? "Proposed" : "Writing", Before = current, After = premiere.Value.Ticks };
    }

    private async Task<DateRow> Write(DateRun run, BaseItem item, DateRow row)
    {
        var original = item.DateCreated;
        item.DateCreated = new DateTime(row.After!.Value, DateTimeKind.Utc);
        try
        {
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None);
            return row with { Status = run.Mode == "Undo" ? "Restored" : "Changed",
                Revision = run.Mode == "Undo" ? null : item.DateLastSaved.Ticks };
        }
        catch (Exception ex)
        {
            item.DateCreated = original;
            logger.LogWarning(ex, "ShelfCheck: Datum für Eintrag {ItemId} konnte nicht gespeichert werden.", item.Id);
            return row with { Status = "Failed", Detail = Brief(ex) };
        }
    }

    private async Task WaitForLibraryAsync(long id, int? done, int? total, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        while (libraries.IsScanRunning)
        {
            SetProgress(id, done, total, "Waiting");
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
        if (done.HasValue) SetProgress(id, done, total, "Processing");
    }

    private void SetProgress(long id, int? done, int? total, string phase)
    {
        lock (_gate) _progress = new(id, done ?? _progress!.Done, total ?? _progress!.Total, phase,
            _progress?.CurrentItem, DateTime.UtcNow);
    }

    private DateLibrary[] GetLibraries()
    {
        return libraries.GetVirtualFolders(false)
            .Where(f => Guid.TryParse(f.ItemId, out _))
            .Select(f =>
            {
                var id = Guid.Parse(f.ItemId);
                var physicalIds = (libraries.GetItemById(id) as CollectionFolder)?.PhysicalFolderIds ?? [];
                return new DateLibrary(id, f.Name, f.Locations.Where(Path.IsPathFullyQualified).Select(Path.GetFullPath).ToArray(),
                    physicalIds, physicalIds.Length > 0 && f.CollectionType is
                        (null or CollectionTypeOptions.mixed or CollectionTypeOptions.movies or CollectionTypeOptions.tvshows));
            }).ToArray();
    }

    private Scope GetScope(Guid[] selection)
    {
        var all = GetLibraries();
        var included = all.Where(l => l.Eligible && selection.Contains(l.Id) && l.Paths.Length > 0).ToArray();
        var includedIds = included.Select(l => l.Id).ToHashSet();
        var excluded = all.Where(l => !includedIds.Contains(l.Id)).SelectMany(l => l.Paths).Distinct().Order().ToArray();
        var shape = included.OrderBy(l => l.Id).Select(l => new { l.Id, Paths = l.Paths.Order(), Ids = l.PhysicalIds.Order() });
        var key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { shape, excluded })));
        return new(all.Where(l => l.Eligible && l.Paths.Length > 0).ToArray(), included, excluded, key);
    }

    private static string Brief(Exception ex)
    {
        var line = ex.Message.Split(['\r', '\n'], 2)[0];
        return line[..Math.Min(240, line.Length)];
    }

    public async ValueTask DisposeAsync()
    {
        Task worker;
        lock (_gate)
        {
            _disposed = true;
            _cancel?.Cancel();
            worker = _worker;
        }
        await worker;
    }

    private sealed record Scope(DateLibrary[] Libraries, DateLibrary[] Included, string[] Excluded, string Key)
    {
        public bool Allows(string path) => !Excluded.Any(root => Contains(root, path)) &&
            Included.Any(l => l.Paths.Any(root => Contains(root, path)));

        private static bool Contains(string root, string path)
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            var relative = Path.GetRelativePath(root, path);
            return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !Path.IsPathRooted(relative);
        }
    }
}

public sealed class DateSyncTask(DateSync sync) : IScheduledTask
{
    public string Name => "Hinzugefügt-Datum abgleichen";
    public string Key => "ShelfCheckDateSync";
    public string Category => "ShelfCheck";
    public string Description => "Übernimmt das Veröffentlichungsdatum für freigegebene Bibliotheken nach der ersten Vorschau.";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        sync.RunScheduledAsync(progress, cancellationToken);
}
