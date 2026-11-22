using ShelfCheck;

var root = Directory.CreateTempSubdirectory("shelfcheck-regression-");
try
{
    foreach (var relative in new[] { "TV/Show/Season 01/E01.MKV", "TV/Show/season01-poster.jpg", "TV/Show/cover.jpg",
        "TV/Show/Staffel 02/staffel.nfo", "TV/Flat/E01.mp4", "TV/Flat/E01.jpg", "Movies/Empty/movie.nfo",
        "Movies/Unknown/child/.ignore", "Movies/A/A-poster.jpg", "Movies/A/A.mp4", "Movies/Single.mp4", "Movies/Single.jpg" })
    {
        var path = Path.Join(root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture");
    }

    var scanner = new Scanner([".mp4", ".mkv"]);
    var roots = new List<LibraryRoot> { new("TV", Path.Join(root.FullName, "TV"), "tvshows"),
        new("Movies", Path.Join(root.FullName, "Movies"), "movies"), new("Offline", Path.Join(root.FullName, "missing"), "movies") };
    var report = scanner.Scan(roots, new Progress<double>(), CancellationToken.None);
    var actual = report.Findings.Select(f => Path.GetRelativePath(root.FullName, f.Path).Replace(Path.DirectorySeparatorChar, '/') + "|" + f.Code).Order().ToArray();
    string[] expected = ["TV/Show/Staffel 02|MissingCover", "TV/Show/Staffel 02|NoVideo",
        "TV/Flat|MissingCover", "TV/Flat|MissingSeason", "Movies/Empty|MissingCover", "Movies/Empty|NoVideo",
        "Movies/Unknown|MissingCover", "Movies/Unknown/child|Unchecked", "missing|Unchecked"];
    if (!actual.SequenceEqual(expected.Order()) || report.Videos != 4 || report.Titles != 6 || report.Seasons != 2)
        throw new InvalidOperationException(string.Join(Environment.NewLine, actual));
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try
    {
        new Scanner([]).Scan(roots, new Progress<double>(), cancelled.Token);
        throw new InvalidOperationException("Cancellation was ignored.");
    }
    catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
    Console.WriteLine("Scanner checks passed.");
}
finally
{
    root.Delete(recursive: true);
}
