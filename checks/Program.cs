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
    }
}
finally
{
    root.Delete(recursive: true);
}
