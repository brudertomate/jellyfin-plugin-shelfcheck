using Emby.Naming.Common;
using Emby.Naming.Video;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;

namespace ShelfCheck;

public sealed class ScanTask(ILibraryManager libraries, NamingOptions naming, ReportStore store, ScanControl control) : IScheduledTask
{
    public string Name => "Medienbestand prüfen";
    public string Key => "ShelfCheckScan";
    public string Category => "ShelfCheck";
    public string Description => "Prüft Bibliothekspfade auf Videos, lokale Poster und Staffelordner.";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        control.SetPaused(false);
        var excluded = store.Excluded.ToHashSet();
        var folders = libraries.GetVirtualFolders(false);
        var excludedPaths = folders.Where(f => Guid.TryParse(f.ItemId, out var id) && excluded.Contains(id))
            .SelectMany(f => f.Locations).Where(Path.IsPathFullyQualified).ToArray();
        var roots = folders
            .Where(f => !Guid.TryParse(f.ItemId, out var id) || !excluded.Contains(id))
            .Where(f => f.CollectionType is null or CollectionTypeOptions.mixed or CollectionTypeOptions.movies or CollectionTypeOptions.tvshows)
            .SelectMany(f => f.Locations.Select(path => new LibraryRoot(f.Name, path, f.CollectionType?.ToString() ?? "mixed")))
            .Distinct().ToList();
        var scanner = new Scanner(naming.VideoFileExtensions,
            (path, root) => ExtraRuleResolver.GetExtraInfo(path, naming, root).ExtraType is not null,
            naming.AllExtrasTypesFolderNames.Keys, store.UpdateLive, control.Wait, excludedPaths);
        try
        {
            var report = await Task.Run(() => scanner.Scan(roots, progress, cancellationToken), CancellationToken.None);
            await store.PublishAsync(report, CancellationToken.None);
            progress.Report(100);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await store.PublishAsync(scanner.Finish("Cancelled"), CancellationToken.None);
            throw;
        }
        catch
        {
            await store.PublishAsync(scanner.Finish("Failed"), CancellationToken.None);
            throw;
        }
        finally { control.SetPaused(false); }
    }
}
