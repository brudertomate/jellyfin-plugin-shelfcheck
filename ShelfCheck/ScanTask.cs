using Emby.Naming.Common;
using Emby.Naming.Video;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;

namespace ShelfCheck;

public sealed class ScanTask(ILibraryManager libraries, NamingOptions naming, ReportStore store) : IScheduledTask
{
    public string Name => "Medienbestand prüfen";
    public string Key => "ShelfCheckScan";
    public string Category => "ShelfCheck";
    public string Description => "Prüft Bibliothekspfade auf Videos, lokale Poster und Staffelordner.";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var roots = libraries.GetVirtualFolders(false)
            .Where(f => f.CollectionType is null or CollectionTypeOptions.mixed or CollectionTypeOptions.movies or CollectionTypeOptions.tvshows)
            .SelectMany(f => f.Locations.Select(path => new LibraryRoot(f.Name, path, f.CollectionType?.ToString() ?? "mixed")))
            .Distinct().ToList();
        var scanner = new Scanner(naming.VideoFileExtensions,
            (path, root) => ExtraRuleResolver.GetExtraInfo(path, naming, root).ExtraType is not null,
            naming.AllExtrasTypesFolderNames.Keys);
        var report = await Task.Run(() => scanner.Scan(roots, progress, cancellationToken), cancellationToken);
        await store.PublishAsync(report, cancellationToken);
        progress.Report(100);
    }
}
