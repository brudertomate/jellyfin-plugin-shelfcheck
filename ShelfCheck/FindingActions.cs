using System.Text.Json;
using Emby.Naming.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ShelfCheck;

public sealed record ActionRequest(string Action, bool AllMatching, string[]? Paths, string? Code, string? Library, string? Search,
    bool CleanMovieFolders = false);
public sealed record ActionPlan(Guid Id, string Action, DateTime CreatedUtc, string[] Paths, LibraryRoot[] Roots,
    bool CleanMovieFolders = false, string[]? MovieFolders = null);
public sealed record ActionResult(string Path, string State, string Detail);
public sealed class ActionStatus
{
    public Guid Id { get; set; }
    public string Action { get; set; } = "";
    public string State { get; set; } = "Idle";
    public int Total { get; set; }
    public int Done { get; set; }
    public int Succeeded { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public string? CurrentPath { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public List<ActionResult> Results { get; set; } = [];
}

public sealed class FindingActions(ReportStore reports, ILibraryManager libraries, IProviderManager providers,
    IFileSystem fileSystem, IApplicationPaths paths, IHostApplicationLifetime lifetime, ILogger<FindingActions> logger, NamingOptions naming)
    : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly string _directory = Path.Combine(paths.DataPath, "shelfcheck", "actions");
    private ActionPlan? _plan;
    private ActionStatus? _status;
    private CancellationTokenSource? _cancel;
    private Task _worker = Task.CompletedTask;
    private bool _disposed;

    public static bool Eligible(Finding finding, string action) => action switch
    {
        "Images" => finding.Code == "MissingCover",
        "DeleteMovieFolders" => finding.Code == "NoVideo" && finding.Kind == "Film",
        "DeleteSeasonFolders" => finding.Code == "NoVideo" && finding.Kind == "Staffel",
        "DeleteSeriesFolders" => finding.Code == "NoVideo" && finding.Kind == "Serie",
        "DeleteEmptyFolders" => finding.Code == "NoVideo" && finding.Kind is "Film" or "Serie" or "Staffel" or "Titel",
        "DeleteLinks" => finding.Code == "Unchecked" && finding.Kind == "Datei" &&
            finding.Detail == "Ziel der Dateiverknüpfung nicht erreichbar.",
        _ => false
    };

    public ActionPlan Prepare(ActionRequest request)
    {
        if (request.Action is not ("Images" or "DeleteLinks" or "DeleteMovieFolders" or "DeleteSeasonFolders" or "DeleteSeriesFolders" or "DeleteEmptyFolders")) throw new InvalidOperationException("Unbekannte Aktion.");
        var report = reports.Current ?? throw new InvalidOperationException("Es gibt noch keinen Scanbericht.");
        var selected = (request.Paths ?? []).ToHashSet(StringComparer.Ordinal);
        var matches = report.Findings.Where(f => Eligible(f, request.Action) &&
            (request.AllMatching || selected.Contains(f.Path)) &&
            (string.IsNullOrEmpty(request.Code) || f.Code == request.Code) &&
            (string.IsNullOrEmpty(request.Library) || f.Library == request.Library) &&
            (string.IsNullOrEmpty(request.Search) || f.Path.Contains(request.Search, StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.Path).Distinct(StringComparer.Ordinal).ToArray();
        if (matches.Length == 0) throw new InvalidOperationException("Die Auswahl enthält keine passenden Befunde.");
        lock (_gate)
        {
            if (_cancel is not null) throw new InvalidOperationException("Es läuft bereits eine Bulk-Aktion.");
            var roots = report.Roots.ToArray();
            var cleanup = request.Action == "DeleteLinks" && request.CleanMovieFolders;
            var movieFolders = cleanup ? matches.Select(path => MovieFolderForLink(path, roots))
                .OfType<string>().Distinct(StringComparer.Ordinal).ToArray() : [];
            _plan = new(Guid.NewGuid(), request.Action, DateTime.UtcNow, matches, roots, cleanup, movieFolders);
            return _plan;
        }
    }

    public void Start(Guid id)
    {
        lock (_gate)
        {
            if (_disposed || _cancel is not null || lifetime.ApplicationStopping.IsCancellationRequested)
                throw new InvalidOperationException("Eine Aktion läuft bereits oder der Server wird beendet.");
            if (_plan is not { } plan || plan.Id != id || DateTime.UtcNow - plan.CreatedUtc > TimeSpan.FromMinutes(10))
                throw new InvalidOperationException("Die Vorschau ist abgelaufen. Erstelle eine neue Auswahl.");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            _status = new() { Id = plan.Id, Action = plan.Action, State = "Running", Total = plan.Paths.Length, UpdatedUtc = DateTime.UtcNow };
            Persist();
            _cancel = cancellation;
            _plan = null;
            _worker = Task.Run(() => Execute(plan, cancellation));
        }
    }

    public ActionStatus? Status()
    {
        lock (_gate)
        {
            if (_status is null && File.Exists(StatePath))
            {
                _status = JsonSerializer.Deserialize<ActionStatus>(File.ReadAllText(StatePath));
                if (_status?.State is "Running" or "Waiting") _status.State = "Interrupted";
            }
            return _status is null ? null : JsonSerializer.Deserialize<ActionStatus>(JsonSerializer.Serialize(_status));
        }
    }

    public void Cancel() { lock (_gate) _cancel?.Cancel(); }

    private async Task Execute(ActionPlan plan, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        var refreshed = new HashSet<Guid>();
        try
        {
            foreach (var path in plan.Paths)
            {
                token.ThrowIfCancellationRequested();
                while (libraries.IsScanRunning)
                {
                    lock (_gate) { _status!.State = "Waiting"; _status.UpdatedUtc = DateTime.UtcNow; }
                    await Task.Delay(1500, token);
                }
                lock (_gate) { _status!.State = "Running"; _status.CurrentPath = path; _status.UpdatedUtc = DateTime.UtcNow; }
                ActionResult result;
                try
                {
                    if (plan.Action != "DeleteEmptyFolders") EnsureAllowed(path, plan.Roots);
                    result = plan.Action switch
                    {
                        "DeleteLinks" => DeleteLink(path, plan, token),
                        "DeleteMovieFolders" => DeleteMovieFolder(path, plan.Roots, token),
                        "DeleteSeasonFolders" => DeleteSeasonFolder(path, plan.Roots, token),
                        "DeleteSeriesFolders" => DeleteSeriesFolder(path, plan.Roots, token),
                        "DeleteEmptyFolders" => DeleteEmptyFolder(path, plan.Roots, token),
                        _ => await RefreshImages(path, refreshed, token)
                    };
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "ShelfCheck: Bulk-Aktion für {Path} fehlgeschlagen.", path);
                    result = new(path, "Failed", ex.Message.Split(['\r', '\n'])[0]);
                }
                lock (_gate)
                {
                    // Append each finished result before moving on to the next item.
                    File.AppendAllText(Path.Combine(_directory, plan.Id.ToString("N") + ".jsonl"),
                        JsonSerializer.Serialize(result) + Environment.NewLine);
                    _status!.Done++;
                    if (result.State == "Succeeded") _status.Succeeded++;
                    else if (result.State == "Skipped") _status.Skipped++;
                    else _status.Failed++;
                    _status.Results.Add(result);
                    if (_status.Results.Count > 50) _status.Results.RemoveAt(0);
                    _status.UpdatedUtc = DateTime.UtcNow;
                    Persist();
                }
                await Task.Delay(150, token);
            }
            lock (_gate) _status!.State = "Completed";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (_gate) _status!.State = lifetime.ApplicationStopping.IsCancellationRequested ? "Interrupted" : "Cancelled";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ShelfCheck: Bulk-Aktion unterbrochen.");
            lock (_gate) { _status!.State = "Failed"; _status.Results.Add(new("", "Failed", ex.Message)); }
        }
        finally
        {
            lock (_gate)
            {
                _status!.CurrentPath = null;
                _status.UpdatedUtc = DateTime.UtcNow;
                try { Persist(); }
                finally { _cancel = null; cancellation.Dispose(); }
            }
        }
    }

    private void EnsureAllowed(string path, LibraryRoot[] roots)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("Ungültiger Pfad.");
        var full = Path.GetFullPath(path);
        if (!roots.Any(root => Inside(root.Path, full)))
            throw new InvalidOperationException("Der Pfad liegt außerhalb der gescannten Bibliotheken.");
        var folders = libraries.GetVirtualFolders(false);
        var excluded = reports.Excluded.ToHashSet();
        if (!folders.Any(f => (!Guid.TryParse(f.ItemId, out var id) || !excluded.Contains(id)) && f.Locations.Any(root => Inside(root, full))) ||
            folders.Any(f => Guid.TryParse(f.ItemId, out var id) && excluded.Contains(id) && f.Locations.Any(root => Inside(root, full))))
            throw new InvalidOperationException("Die Bibliothek ist inzwischen ausgeschlossen oder nicht mehr vorhanden.");

        // Resolve no directory links on the way to the selected file or folder.
        for (var parent = Directory.GetParent(full); parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Ein übergeordneter Ordner ist eine Verknüpfung.");
    }

    private static bool Inside(string root, string path)
    {
        if (!Path.IsPathFullyQualified(root)) return false;
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private ActionResult DeleteLink(string path, ActionPlan plan, CancellationToken token)
    {
        var item = libraries.FindByPath(path, false);
        if (!BrokenLinkFile.DeleteIfBroken(path, out var detail))
            return new(path, "Skipped", detail);
        if (item is Video && string.Equals(item.Path, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            try
            {
                libraries.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false }, true);
            }
            catch (Exception ex)
            {
                return new(path, "Failed", "Verknüpfung entfernt. Jellyfin-Eintrag muss durch einen Bibliotheksscan abgeglichen werden. " + ex.Message);
            }
        }
        var message = "Defekte Dateiverknüpfung entfernt. Zugeordneter Jellyfin-Eintrag abgeglichen.";
        if (plan.CleanMovieFolders)
        {
            if (MovieFolderForLink(path, plan.Roots) is { } folder)
            {
                try { message += " " + DeleteMovieFolder(folder, plan.Roots, CancellationToken.None).Detail; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new(path, "Failed", message + " Filmordner konnte nicht bereinigt werden. " + ex.Message);
                }
            }
        }
        return new(path, "Succeeded", message);
    }

    private static string? MovieFolderForLink(string path, LibraryRoot[] roots)
    {
        var root = roots.FirstOrDefault(root => root.Type == "movies" && Inside(root.Path, path));
        if (root is null) return null;
        var relative = Path.GetRelativePath(root.Path, path);
        var separator = relative.IndexOf(Path.DirectorySeparatorChar);
        return separator > 0 ? Path.Combine(root.Path, relative[..separator]) : null;
    }

    private ActionResult DeleteMovieFolder(string path, LibraryRoot[] roots, CancellationToken token)
    {
        EnsureAllowed(path, roots);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        bool DirectChild(string root) => Path.IsPathFullyQualified(root) &&
            string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), parent, comparison);
        var folders = libraries.GetVirtualFolders(false);
        if (!roots.Any(root => root.Type == "movies" && DirectChild(root.Path)) ||
            !folders.Any(folder => folder.CollectionType == CollectionTypeOptions.movies && folder.Locations.Any(DirectChild)))
            return new(path, "Skipped", "Der Pfad ist kein direkter Filmordner einer Filmbibliothek.");
        if (folders.Any(folder => folder.Locations.Any(root => Inside(path, root))) ||
            folders.Any(folder => folder.CollectionType != CollectionTypeOptions.movies &&
                folder.Locations.Any(root => Inside(root, path))))
            return new(path, "Skipped", "Pfad überschneidet sich mit einem Bibliotheksstamm oder einer anderen Bibliotheksart. Ordner bleibt erhalten.");
        var item = libraries.FindByPath(path, true);
        if (item is MediaBrowser.Controller.Entities.TV.Series or MediaBrowser.Controller.Entities.TV.Season)
            return new(path, "Skipped", "Jellyfin erkennt diesen Ordner als Serie oder Staffel. Ordner bleibt erhalten.");
        if (item is Folder && libraries.GetItemList(new InternalItemsQuery
        {
            ParentId = item.Id, Recursive = true, Limit = 1, EnableTotalRecordCount = false,
            IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Series, Jellyfin.Data.Enums.BaseItemKind.Season,
                Jellyfin.Data.Enums.BaseItemKind.Episode]
        }).Count > 0)
            return new(path, "Skipped", "Jellyfin kennt Serien-, Staffel- oder Episodeneinträge in diesem Ordner. Ordner bleibt erhalten.");
        if (!MovieFolderDeletion.DeleteIfNoVideo(path, naming.VideoFileExtensions, token, out var detail))
            return new(path, "Skipped", detail);
        if (item is not null)
        {
            try { libraries.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false }, true); }
            catch (Exception ex) { return new(path, "Failed", detail + " Jellyfin-Eintrag durch Bibliotheksscan abgleichen. " + ex.Message); }
        }
        return new(path, "Succeeded", detail);
    }

    private ActionResult DeleteSeasonFolder(string path, LibraryRoot[] roots, CancellationToken token)
    {
        EnsureAllowed(path, roots);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var season = new DirectoryInfo(fullPath);
        if (!Scanner.IsSeasonDirectory(season.Name) || season.Parent?.Parent is not { } libraryRoot)
            return new(path, "Skipped", "Der Pfad ist kein erkannter Staffelordner unter einem Serienordner.");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool MatchesRoot(string root) => Path.IsPathFullyQualified(root) &&
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).Equals(libraryRoot.FullName, comparison);
        var folders = libraries.GetVirtualFolders(false);
        if (!roots.Any(root => root.Type is "tvshows" or "mixed" && MatchesRoot(root.Path)) ||
            !folders.Any(folder => folder.CollectionType is null or CollectionTypeOptions.tvshows or CollectionTypeOptions.mixed &&
                folder.Locations.Any(MatchesRoot)))
            return new(path, "Skipped", "Der Staffelordner liegt außerhalb der gescannten Serienstruktur. Serienordner und Bibliotheksstämme bleiben erhalten.");
        if (folders.Any(folder => folder.Locations.Any(root => Inside(fullPath, root))))
            return new(path, "Skipped", "Der Ordner enthält einen Bibliotheksstamm. Ordner bleibt erhalten.");
        var item = libraries.FindByPath(fullPath, true);
        if (item is not null && item is not MediaBrowser.Controller.Entities.TV.Season)
            return new(path, "Skipped", "Jellyfin erkennt diesen Eintrag nicht als Staffel. Ordner bleibt erhalten.");
        if (!MovieFolderDeletion.DeleteIfNoVideo(fullPath, naming.VideoFileExtensions, token, out var detail, kind: FolderCleanupKind.Season))
            return new(path, "Skipped", detail);
        if (item is not null)
        {
            try { libraries.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false }, true); }
            catch (Exception ex) { return new(path, "Failed", detail + " Jellyfin-Staffeleintrag durch Bibliotheksscan abgleichen. " + ex.Message); }
        }
        return new(path, "Succeeded", detail + " Serienordner erhalten.");
    }

    private ActionResult DeleteSeriesFolder(string path, LibraryRoot[] roots, CancellationToken token)
    {
        EnsureAllowed(path, roots);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Directory.GetParent(fullPath);
        if (parent is null) return new(path, "Skipped", "Ein Bibliotheksstamm oder Dateisystem-Stammordner kann nicht entfernt werden.");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool MatchesRoot(string root) => Path.IsPathFullyQualified(root) &&
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).Equals(parent.FullName, comparison);
        var folders = libraries.GetVirtualFolders(false);
        if (!roots.Any(root => root.Type is "tvshows" or "mixed" && MatchesRoot(root.Path)) ||
            !folders.Any(folder => folder.CollectionType is null or CollectionTypeOptions.tvshows or CollectionTypeOptions.mixed &&
                folder.Locations.Any(MatchesRoot)))
            return new(path, "Skipped", "Der Pfad ist kein direkter Serienordner einer gescannten Serien- oder gemischten Bibliothek.");
        if (folders.Any(folder => folder.Locations.Any(root => Inside(fullPath, root))))
            return new(path, "Skipped", "Der Ordner enthält einen Bibliotheksstamm. Ordner bleibt erhalten.");
        var item = libraries.FindByPath(fullPath, true);
        if (item is not null && item is not MediaBrowser.Controller.Entities.TV.Series)
            return new(path, "Skipped", "Jellyfin erkennt diesen Eintrag nicht als Serie. Ordner bleibt erhalten.");
        if (!MovieFolderDeletion.DeleteIfNoVideo(fullPath, naming.VideoFileExtensions, token, out var detail, kind: FolderCleanupKind.Series))
            return new(path, "Skipped", detail);
        if (item is not null)
        {
            try { libraries.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false }, true); }
            catch (Exception ex) { return new(path, "Failed", detail + " Jellyfin-Serieneintrag durch Bibliotheksscan abgleichen. " + ex.Message); }
        }
        return new(path, "Succeeded", detail);
    }

    private ActionResult DeleteEmptyFolder(string path, LibraryRoot[] roots, CancellationToken token)
    {
        try { File.GetAttributes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(path, "Skipped", "Ordner wurde bereits entfernt, möglicherweise zusammen mit seinem ausgewählten Serienordner.");
        }
        EnsureAllowed(path, roots);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var directory = new DirectoryInfo(fullPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool SamePath(string left, string right) => Path.IsPathFullyQualified(left) &&
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(right, comparison);
        var folders = libraries.GetVirtualFolders(false);
        if (roots.Any(root => Inside(fullPath, root.Path)) ||
            folders.Any(folder => folder.Locations.Any(root => Inside(fullPath, root))))
            return new(path, "Skipped", "Bibliotheksstamm oder darin eingebundene Bibliothek erkannt. Ordner bleibt erhalten.");
        var parent = directory.Parent;
        var isTitle = parent is not null && roots.Any(root => SamePath(root.Path, parent.FullName));
        var isSeason = parent?.Parent is { } ancestor && Scanner.IsSeasonDirectory(directory.Name) &&
            roots.Any(root => SamePath(root.Path, ancestor.FullName));
        if (!isTitle && !isSeason)
            return new(path, "Skipped", "Der Pfad liegt außerhalb der im Bericht geprüften Titel- oder Staffelstruktur.");
        var item = libraries.FindByPath(fullPath, true);
        if (item is CollectionFolder or AggregateFolder or UserRootFolder)
            return new(path, "Skipped", "Jellyfin-Bibliotheksordner erkannt. Ordner bleibt erhalten.");
        if (!MovieFolderDeletion.DeleteIfNoVideo(fullPath, naming.VideoFileExtensions, token, out var detail, kind: FolderCleanupKind.Title))
            return new(path, "Skipped", detail);
        if (item is not null)
        {
            try { libraries.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false }, true); }
            catch (Exception ex) { return new(path, "Failed", detail + " Jellyfin-Eintrag durch Bibliotheksscan abgleichen. " + ex.Message); }
        }
        return new(path, "Succeeded", detail);
    }

    private async Task<ActionResult> RefreshImages(string path, HashSet<Guid> refreshed, CancellationToken token)
    {
        var item = libraries.FindByPath(path, null);
        if ((item is null || item.GetType() == typeof(Folder)) && Directory.Exists(path))
        {
            item = null;
            foreach (var file in Directory.EnumerateFiles(path))
            {
                token.ThrowIfCancellationRequested();
                item = libraries.FindByPath(file, false);
                if (item is Video) break;
                item = null;
            }
        }
        if (item is null || item is Folder && item is not MediaBrowser.Controller.Entities.TV.Series && item is not MediaBrowser.Controller.Entities.TV.Season)
            return new(path, "Skipped", "Kein passender Titel in Jellyfin gefunden. Zuerst die Bibliothek einlesen.");
        if (!refreshed.Add(item.Id)) return new(path, "Skipped", "Dieser Jellyfin-Eintrag wurde in diesem Lauf bereits bearbeitet.");
        var before = item.ImageInfos.Length;
        await providers.RefreshSingleItem(item, new MetadataRefreshOptions(new DirectoryService(fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.None,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllImages = false,
            IsAutomated = false,
            EnableRemoteContentProbe = false,
            ForceSave = true
        }, token);
        var after = libraries.GetItemById(item.Id) ?? item;
        return after.HasImage(ImageType.Primary)
            ? new(path, "Succeeded", $"Bilder geprüft. Poster in Jellyfin vorhanden. {Math.Max(0, after.ImageInfos.Length - before)} zusätzliche Bilder.")
            : new(path, "Skipped", "Bildanbieter geprüft. Kein Poster verfügbar. Anbieter, Kennungen oder Bibliothekseinstellungen prüfen.");
    }

    private string StatePath => Path.Combine(_directory, "latest.json");
    private void Persist()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StatePath + ".tmp", JsonSerializer.Serialize(_status));
        File.Move(StatePath + ".tmp", StatePath, true);
    }

    public async ValueTask DisposeAsync()
    {
        Task worker;
        lock (_gate) { _disposed = true; _cancel?.Cancel(); worker = _worker; }
        await worker;
    }
}
