using ShelfCheck;

var root = Directory.CreateTempSubdirectory("shelfcheck-regression-");
try
{
    foreach (var relative in new[] { "TV/Show/Season 01/E01.MKV", "TV/Show/season01-poster.jpg", "TV/Show/cover.jpg",
        "TV/Show/Staffel 02/staffel.nfo", "TV/Flat/E01.mp4", "TV/Flat/E01.jpg", "Movies/Empty/movie.nfo",
        "Movies/Unknown/child/.ignore", "Movies/A/A-poster.jpg", "Movies/A/A.mp4", "Movies/Single.mp4", "Movies/Single.jpg",
        "Movies/Only/Only-trailer.mp4", "Movies/Only/extras/Interviews/deleted.mp4", "Movies/Only/poster.jpg",
        "Movies/Unknown/Unknown-trailer.mp4" })
    {
        var path = Path.Join(root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture");
    }

    var scanner = new Scanner([".mp4", ".mkv"],
        (path, _) => Path.GetFileNameWithoutExtension(path).EndsWith("-trailer", StringComparison.OrdinalIgnoreCase),
        ["extras"]);
    var roots = new List<LibraryRoot> { new("TV", Path.Join(root.FullName, "TV"), "tvshows"),
        new("Movies", Path.Join(root.FullName, "Movies"), "movies"), new("Offline", Path.Join(root.FullName, "missing"), "movies") };
    var report = scanner.Scan(roots, new Progress<double>(), CancellationToken.None);
    var actual = report.Findings.Select(f => Path.GetRelativePath(root.FullName, f.Path).Replace(Path.DirectorySeparatorChar, '/') + "|" + f.Code).Order().ToArray();
    string[] expected = ["TV/Show/Staffel 02|MissingCover", "TV/Show/Staffel 02|NoVideo",
        "TV/Flat|MissingCover", "TV/Flat|MissingSeason", "Movies/Empty|MissingCover", "Movies/Empty|NoVideo",
        "Movies/Unknown|MissingCover", "Movies/Unknown/child|Unchecked", "Movies/Only|OnlyExtras", "missing|Unchecked"];
    if (!actual.SequenceEqual(expected.Order()) || report.Videos != 7 || report.Titles != 7 || report.Seasons != 2)
        throw new InvalidOperationException(string.Join(Environment.NewLine, actual));
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try
    {
        new Scanner([]).Scan(roots, new Progress<double>(), cancelled.Token);
        throw new InvalidOperationException("Cancellation was ignored.");
    }
    catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
    Report? live = null;
    using var interrupted = new CancellationTokenSource();
    var partialScanner = new Scanner([".mp4", ".mkv"], update: snapshot =>
    {
        live = snapshot;
        interrupted.Cancel();
    });
    try { partialScanner.Scan(roots, new Progress<double>(), interrupted.Token); }
    catch (OperationCanceledException) when (interrupted.IsCancellationRequested) { }
    var partial = partialScanner.Finish("Cancelled");
    if (live?.State != "Running" || partial.State != "Cancelled" || partial.FinishedUtc == default)
        throw new InvalidOperationException("Partial report was lost.");
    var excludedPath = Path.Join(root.FullName, "Movies");
    var excludedReport = new Scanner([".mp4", ".mkv"], excludedPaths: [excludedPath])
        .Scan(roots, new Progress<double>(), CancellationToken.None);
    if (excludedReport.Findings.Any(f => f.Path.StartsWith(excludedPath, StringComparison.Ordinal)) ||
        excludedReport.Videos != 2)
        throw new InvalidOperationException("Excluded library was scanned.");
    Console.WriteLine("Scanner checks passed.");
    var cleanup = Path.Join(root.FullName, "Cleanup");
    var emptyMovie = Path.Join(cleanup, "Empty Movie");
    Directory.CreateDirectory(Path.Join(emptyMovie, "metadata"));
    File.WriteAllText(Path.Join(emptyMovie, "movie.nfo"), "metadata");
    File.WriteAllText(Path.Join(emptyMovie, "metadata", "poster.jpg"), "image");
    if (!MovieFolderDeletion.DeleteIfNoVideo(emptyMovie, [".mkv", ".mp4"], CancellationToken.None, out _) ||
        Directory.Exists(emptyMovie))
        throw new InvalidOperationException("Movie folder was not cleaned.");
    foreach (var protectedFile in new[] { "movie.mkv", "extras/trailer.mp4", "Season 01/episode.nfo", "tvshow.nfo", ".ignore" })
    {
        var protectedMovie = Path.Join(cleanup, Guid.NewGuid().ToString("N"));
        var protectedPath = Path.Join(protectedMovie, protectedFile);
        Directory.CreateDirectory(Path.GetDirectoryName(protectedPath)!);
        File.WriteAllText(protectedPath, "retain");
        File.WriteAllText(Path.Join(protectedMovie, "poster.jpg"), "retain");
        if (MovieFolderDeletion.DeleteIfNoVideo(protectedMovie, [".mkv", ".mp4"], CancellationToken.None, out _) ||
            !File.Exists(protectedPath) || !File.Exists(Path.Join(protectedMovie, "poster.jpg")))
            throw new InvalidOperationException("Protected movie or series content was deleted.");
    }
    Console.WriteLine("Movie folder deletion checks passed.");
    var seriesFolder = Path.Join(cleanup, "Series");
    var emptySeason = Path.Join(seriesFolder, "Season 02");
    Directory.CreateDirectory(Path.Join(emptySeason, "metadata"));
    File.WriteAllText(Path.Join(seriesFolder, "tvshow.nfo"), "retain series");
    File.WriteAllText(Path.Join(emptySeason, "season.nfo"), "season metadata");
    File.WriteAllText(Path.Join(emptySeason, "metadata", "poster.jpg"), "image");
    if (!MovieFolderDeletion.DeleteIfNoVideo(emptySeason, [".mkv"], CancellationToken.None, out _, kind: FolderCleanupKind.Season) ||
        !File.Exists(Path.Join(seriesFolder, "tvshow.nfo")) || !Directory.Exists(seriesFolder))
        throw new InvalidOperationException("Season cleanup damaged the series folder.");
    if (MovieFolderDeletion.DeleteIfNoVideo(seriesFolder, [".mkv"], CancellationToken.None, out _, kind: FolderCleanupKind.Season))
        throw new InvalidOperationException("Series folder was accepted as a season.");
    var protectedSeasonNumber = 3;
    foreach (var fileName in new[] { "episode.mkv", "tvshow.nfo", ".ignore" })
    {
        var protectedSeason = Path.Join(seriesFolder, "Season " + protectedSeasonNumber++);
        Directory.CreateDirectory(protectedSeason);
        File.WriteAllText(Path.Join(protectedSeason, fileName), "retain");
        if (MovieFolderDeletion.DeleteIfNoVideo(protectedSeason, [".mkv"], CancellationToken.None, out _, kind: FolderCleanupKind.Season) ||
            !File.Exists(Path.Join(protectedSeason, fileName)))
            throw new InvalidOperationException("Protected season content was deleted.");
    }
    var specials = Path.Join(seriesFolder, "Specials");
    Directory.CreateDirectory(specials);
    if (!MovieFolderDeletion.DeleteIfNoVideo(specials, [".mkv"], CancellationToken.None, out _, kind: FolderCleanupKind.Season))
        throw new InvalidOperationException("Empty specials folder was not cleaned.");
    Console.WriteLine("Season folder deletion checks passed.");
    var emptySeries = Path.Join(cleanup, "Empty Series");
    Directory.CreateDirectory(Path.Join(emptySeries, "Season 01", "metadata"));
    Directory.CreateDirectory(Path.Join(emptySeries, "Specials"));
    File.WriteAllText(Path.Join(emptySeries, "tvshow.nfo"), "series metadata");
    File.WriteAllText(Path.Join(emptySeries, "Season 01", "season.nfo"), "season metadata");
    File.WriteAllText(Path.Join(emptySeries, "Season 01", "metadata", "poster.jpg"), "image");
    if (!MovieFolderDeletion.DeleteIfNoVideo(emptySeries, [".mkv", ".strm"], CancellationToken.None, out _, kind: FolderCleanupKind.Series) ||
        Directory.Exists(emptySeries) || !Directory.Exists(cleanup))
        throw new InvalidOperationException("Empty series cleanup failed.");
    foreach (var protectedFile in new[] { "Season 01/episode.mkv", "Specials/deep/episode.strm", "Season 02/.ignore" })
    {
        var protectedSeries = Path.Join(cleanup, Guid.NewGuid().ToString("N"));
        var protectedPath = Path.Join(protectedSeries, protectedFile);
        Directory.CreateDirectory(Path.GetDirectoryName(protectedPath)!);
        File.WriteAllText(protectedPath, "retain");
        File.WriteAllText(Path.Join(protectedSeries, "tvshow.nfo"), "retain");
        if (MovieFolderDeletion.DeleteIfNoVideo(protectedSeries, [".mkv", ".strm"], CancellationToken.None, out _, kind: FolderCleanupKind.Series) ||
            !File.Exists(protectedPath) || !File.Exists(Path.Join(protectedSeries, "tvshow.nfo")))
            throw new InvalidOperationException("Series with protected contents was deleted.");
    }
    Console.WriteLine("Series folder deletion checks passed.");
    if (!OperatingSystem.IsWindows())
    {
        var target = Path.Join(root.FullName, "link-target.mkv");
        var broken = Path.Join(root.FullName, "broken.mkv");
        var healthy = Path.Join(root.FullName, "healthy.mkv");
        var restored = Path.Join(root.FullName, "restored.mkv");
        File.WriteAllText(target, "retain");
        File.CreateSymbolicLink(broken, Path.Join(root.FullName, "missing-target.mkv"));
        File.CreateSymbolicLink(healthy, target);
        File.CreateSymbolicLink(restored, Path.Join(root.FullName, "returned-target.mkv"));
        File.WriteAllText(Path.Join(root.FullName, "returned-target.mkv"), "returned");
        if (!BrokenLinkFile.DeleteIfBroken(broken, out _) || new FileInfo(broken).LinkTarget is not null ||
            BrokenLinkFile.DeleteIfBroken(healthy, out _) || BrokenLinkFile.DeleteIfBroken(target, out _) ||
            BrokenLinkFile.DeleteIfBroken(restored, out _) || File.ReadAllText(target) != "retain")
            throw new InvalidOperationException("Broken-link deletion guards failed.");
        var directoryLink = Path.Join(root.FullName, "linked-folder");
        Directory.CreateSymbolicLink(directoryLink, Path.Join(root.FullName, "Movies"));
        if (BrokenLinkFile.DeleteIfBroken(directoryLink, out _))
            throw new InvalidOperationException("Directory link was deleted.");
        Console.WriteLine("Broken-link deletion checks passed.");
        var linkedMovie = Path.Join(cleanup, "Linked Movie");
        Directory.CreateDirectory(linkedMovie);
        var lastLink = Path.Join(linkedMovie, "missing.mkv");
        File.CreateSymbolicLink(lastLink, Path.Join(root.FullName, "absent.mkv"));
        if (MovieFolderDeletion.DeleteIfNoVideo(linkedMovie, [".mkv"], CancellationToken.None, out _))
            throw new InvalidOperationException("Folder with a link was deleted.");
        if (MovieFolderDeletion.DeleteIfNoVideo(linkedMovie, [".mkv"], CancellationToken.None, out _, kind: FolderCleanupKind.Series))
            throw new InvalidOperationException("Series with a broken link was deleted.");
        if (!BrokenLinkFile.DeleteIfBroken(lastLink, out _) ||
            !MovieFolderDeletion.DeleteIfNoVideo(linkedMovie, [".mkv"], CancellationToken.None, out _))
            throw new InvalidOperationException("Cleanup after last broken link failed.");
    }
}
finally
{
    root.Delete(recursive: true);
}
