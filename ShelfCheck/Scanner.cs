using System.Globalization;
using System.Text.RegularExpressions;

namespace ShelfCheck;

public sealed partial class Scanner(IEnumerable<string> videoExtensions, Func<string, string, bool>? isExtra = null,
    IEnumerable<string>? extraFolders = null, Action<Report>? update = null,
    Action<CancellationToken>? wait = null, IEnumerable<string>? excludedPaths = null)
{
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> ImageExtensions = new([".jpg", ".jpeg", ".png", ".webp", ".tbn"], Names);
    private static readonly string[] PosterNames = ["poster", "folder", "cover", "default"];
    private readonly HashSet<string> _videoExtensions = new(videoExtensions, Names);
    private readonly HashSet<string> _extraFolders = new(extraFolders ?? [], Names);
    private readonly Report _report = new();
    private CancellationToken _token;
    private LibraryRoot _root = null!;
    private long _lastUpdate;

    [GeneratedRegex(@"^(?:(?:season|staffel|series|s)[ ._-]*(?<number>\d{1,4})|(?<special>specials|extraspecials))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonName();
    public static bool IsSeasonDirectory(string name) => SeasonName().IsMatch(name);

    private sealed class Listing
    {
        public string Path { get; init; } = "";
        public List<DirectoryInfo> Children { get; } = [];
        public List<string> Videos { get; } = [];
        public int Extras { get; set; }
        public bool InExtras { get; init; }
        public HashSet<string> Images { get; } = new(Names);
        public HashSet<string> Placeholders { get; } = new(Names);
        public HashSet<string> Nfos { get; } = new(Names);
        public bool Complete { get; set; } = true;
        public bool Ignored { get; set; }
    }

    public Report Scan(List<LibraryRoot> roots, IProgress<double> progress, CancellationToken token)
    {
        _token = token;
        _report.State = "Running";
        _report.Roots.AddRange(roots);
        Publish(true);
        var lastPercent = -1;
        for (int i = 0; i < roots.Count; i++)
        {
            _token.ThrowIfCancellationRequested();
            _root = roots[i];
            var directory = new DirectoryInfo(_root.Path);
            var listing = Read(directory);
            if (listing.Ignored) continue;
            CheckNfos(listing, _root.Type == "tvshows", false);
            if (_root.Type != "tvshows")
            {
                foreach (var video in listing.Videos)
                {
                    _token.ThrowIfCancellationRequested();
                    _report.Titles++;
                    if (listing.Complete && !HasNamedPoster(listing, video))
                        Add(Path.Combine(directory.FullName, video), "Videodatei",
                            HasNamedPoster(listing, video, placeholder: true) ? "PlaceholderCover" : "MissingCover",
                            HasNamedPoster(listing, video, placeholder: true) ? "ShelfCheck-Platzhalter vorhanden. Echtes Poster kann erneut gesucht werden." : "Dateibezogenes Poster fehlt.");
                }
            }
            else if (listing.Videos.Count > 0)
                Add(directory.FullName, "Bibliothek", "Layout", "Videodateien direkt im Serien-Bibliothekspfad.");

            for (int j = 0; j < listing.Children.Count; j++)
            {
                CheckTitle(listing.Children[j]);
                var percent = (int)(99.0 * (i + (j + 1.0) / listing.Children.Count) / roots.Count);
                if (percent != lastPercent)
                {
                    progress.Report(percent);
                    lastPercent = percent;
                }
            }
        }

        _token.ThrowIfCancellationRequested();
        _report.Findings.Sort((a, b) =>
        {
            var order = string.CompareOrdinal(a.Library, b.Library);
            if (order == 0) order = string.CompareOrdinal(a.Path, b.Path);
            return order == 0 ? string.CompareOrdinal(a.Code, b.Code) : order;
        });
        return Finish("Completed");
    }

    public Report Finish(string state)
    {
        _report.State = state;
        _report.FinishedUtc = DateTime.UtcNow;
        _report.CurrentPath = "";
        return Snapshot();
    }

    private Report Snapshot() => new()
    {
        StartedUtc = _report.StartedUtc, FinishedUtc = _report.FinishedUtc, UpdatedUtc = DateTime.UtcNow,
        State = _report.State, CurrentPath = _report.CurrentPath, Roots = _report.Roots.ToList(),
        Directories = _report.Directories, Titles = _report.Titles, Seasons = _report.Seasons,
        Videos = _report.Videos, Findings = _report.Findings.ToList()
    };

    private void Publish(bool force = false)
    {
        if (update is null || (!force && Environment.TickCount64 - _lastUpdate < 1500)) return;
        _lastUpdate = Environment.TickCount64;
        update(Snapshot());
    }

    private void CheckTitle(DirectoryInfo directory)
    {
        var listing = Read(directory);
        if (listing.Ignored) return;
        _report.Titles++;
        var series = _root.Type == "tvshows" || (_root.Type == "mixed" && listing.Children.Any(d => SeasonName().IsMatch(d.Name)));
        var movieNfo = !series && listing.Nfos.Contains("movie.nfo");
        CheckNfos(listing, series, movieNfo);
        var kind = series ? "Serie" : _root.Type == "movies" ? "Film" : "Titel";
        var hasSeason = false;
        var complete = listing.Complete;
        var videos = listing.Videos.Count;
        var extras = listing.Extras;
        foreach (var child in listing.Children)
        {
            var season = series ? SeasonName().Match(child.Name) : Match.Empty;
            hasSeason |= season.Success;
            var childListing = Read(child, _extraFolders.Contains(child.Name));
            if (childListing.Ignored)
            {
                complete = false;
                continue;
            }

            var result = ScanSubtree(childListing, 1, series, movieNfo);
            videos += result.Videos;
            extras += result.Extras;
            complete &= result.Complete;
            if (season.Success)
            {
                _report.Seasons++;
                var number = season.Groups["special"].Success ? 0 : int.Parse(season.Groups["number"].Value, CultureInfo.InvariantCulture);
                var prefix = number == 0 ? "season-specials" : "season" + number.ToString("00", CultureInfo.InvariantCulture);
                if (childListing.Complete && listing.Complete && !HasCover(childListing, "season") &&
                    !listing.Images.Contains(prefix + "-poster") && !listing.Images.Contains(child.Name.Replace(" ", "") + "-poster"))
                {
                    var placeholder = HasCover(childListing, "season", placeholder: true) ||
                        listing.Placeholders.Contains(prefix + "-poster") ||
                        listing.Placeholders.Contains(child.Name.Replace(" ", "") + "-poster");
                    Add(child.FullName, "Staffel", placeholder ? "PlaceholderCover" : "MissingCover",
                        placeholder ? "ShelfCheck-Staffelplatzhalter vorhanden. Echtes Staffelposter kann erneut gesucht werden." :
                        "Weder Staffelcover noch zugehöriges Poster im Serienordner gefunden.");
                }
                CheckVideos(child.FullName, "Staffel", result.Videos, result.Extras, result.Complete);
            }
        }

        CheckVideos(directory.FullName, kind, videos, extras, complete);
        if (series && videos > 0 && listing.Complete && !listing.Nfos.Contains("tvshow.nfo"))
            Add(directory.FullName, "Serie", "MissingNfo", "Serien-NFO fehlt. Erwartet: " + Path.Combine(directory.FullName, "tvshow.nfo"));
        if (listing.Complete && !HasCover(listing, series ? "show" : "movie"))
        {
            var placeholder = HasCover(listing, series ? "show" : "movie", placeholder: true);
            Add(directory.FullName, kind, placeholder ? "PlaceholderCover" : "MissingCover",
                placeholder ? "ShelfCheck-Platzhalter vorhanden. Echtes Cover kann erneut gesucht werden." : "Lokales Titelposter fehlt.");
        }
        if (series && !hasSeason && listing.Complete)
            Add(directory.FullName, kind, "MissingSeason", "Kein Staffelordner erkannt. Flache Episodenablage ist möglich.");
    }

    private static bool HasCover(Listing listing, string alternate, bool placeholder = false)
    {
        var images = placeholder ? listing.Placeholders : listing.Images;
        return PosterNames.Any(images.Contains) || images.Contains(alternate)
            || (alternate == "movie" && listing.Videos.Any(video => HasNamedPoster(listing, video, placeholder)));
    }

    private static bool HasNamedPoster(Listing listing, string video, bool placeholder = false)
    {
        var images = placeholder ? listing.Placeholders : listing.Images;
        var stem = Path.GetFileNameWithoutExtension(video);
        return images.Contains(stem) || PosterNames.Any(name => images.Contains(stem + "-" + name));
    }

    private void CheckVideos(string path, string kind, int videos, int extras, bool complete)
    {
        if (!complete || videos > 0) return;
        if (extras > 0)
            Add(path, kind, "OnlyExtras", "Nur Trailer oder Extras gefunden. Der Hauptfilm bzw. die Folgen fehlen.");
        else
            Add(path, kind, "NoVideo", "Keine Videodatei im Ordner oder seinen Unterordnern.");
    }

    private (int Videos, int Extras, bool Complete) ScanSubtree(Listing listing, int depth, bool series, bool movieNfo)
    {
        movieNfo |= !series && listing.Nfos.Contains("movie.nfo");
        CheckNfos(listing, series, movieNfo);
        var count = listing.Videos.Count;
        var extras = listing.Extras;
        var complete = listing.Complete && !listing.Ignored;
        foreach (var child in listing.Children)
        {
            if (depth >= 96)
            {
                Add(child.FullName, "Pfad", "Unchecked", "Maximale Verzeichnistiefe erreicht.");
                complete = false;
                continue;
            }

            var result = ScanSubtree(Read(child, listing.InExtras || _extraFolders.Contains(child.Name)), depth + 1, series, movieNfo);
            count += result.Videos;
            extras += result.Extras;
            complete &= result.Complete;
        }

        return (count, extras, complete);
    }

    private Listing Read(DirectoryInfo directory, bool inExtras = false)
    {
        _token.ThrowIfCancellationRequested();
        wait?.Invoke(_token);
        if ((excludedPaths ?? []).Any(root =>
        {
            var relative = Path.GetRelativePath(root, directory.FullName);
            return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        })) return new Listing { Ignored = true, Complete = false };
        _report.CurrentPath = directory.FullName;
        Publish();
        var listing = new Listing { InExtras = inExtras, Path = directory.FullName };
        try
        {
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint) && directory.Exists)
            {
                Skip("Verzeichnisverknüpfung nicht verfolgt.");
                return listing;
            }

            _report.Directories++;
            foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions
                     { IgnoreInaccessible = false, AttributesToSkip = 0, BufferSize = 64 * 1024 }))
            {
                _token.ThrowIfCancellationRequested();
                wait?.Invoke(_token);
                Publish();
                if (entry.Name == ".ignore")
                {
                    Skip("Ordner mit .ignore übersprungen.");
                    return listing;
                }

                if (entry is DirectoryInfo subdirectory)
                    listing.Children.Add(subdirectory);
                else if (_videoExtensions.Contains(entry.Extension) || ImageExtensions.Contains(entry.Extension) || Names.Equals(entry.Extension, ".nfo"))
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.ResolveLinkTarget(true)?.Exists != true)
                    {
                        listing.Complete = false;
                        Add(entry.FullName, "Datei", "Unchecked", "Ziel der Dateiverknüpfung nicht erreichbar.");
                    }
                    else if (_videoExtensions.Contains(entry.Extension))
                    {
                        if (inExtras || isExtra?.Invoke(entry.FullName, _root.Path) == true)
                            listing.Extras++;
                        else
                            listing.Videos.Add(entry.Name);
                    }
                    else if (Names.Equals(entry.Extension, ".nfo"))
                        listing.Nfos.Add(entry.Name);
                    else
                    {
                        var images = PlaceholderImages.Identify(entry.FullName, ((FileInfo)entry).Length) is null
                            ? listing.Images : listing.Placeholders;
                        images.Add(Path.GetFileNameWithoutExtension(entry.Name));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            listing.Complete = false;
            Add(directory.FullName, "Pfad", "Unchecked", ex.Message);
        }

        _report.Videos += listing.Videos.Count + listing.Extras;
        return listing;

        void Skip(string detail)
        {
            listing = new Listing { Ignored = true, Complete = false };
            Add(directory.FullName, "Pfad", "Unchecked", detail);
        }
    }

    private void Add(string path, string kind, string code, string detail) =>
        _report.Findings.Add(new(_root.Library, path, kind, code, detail));

    private void CheckNfos(Listing listing, bool series, bool movieNfo)
    {
        if (!listing.Complete || listing.Ignored || _root.Type is not ("movies" or "tvshows" or "mixed")) return;
        foreach (var video in listing.Videos)
        {
            var name = Path.ChangeExtension(video, ".nfo");
            if (listing.Nfos.Contains(name) || (!series && (movieNfo || listing.Nfos.Contains("movie.nfo")))) continue;
            Add(Path.Combine(listing.Path, video), series ? "Episode" : "Film", "MissingNfo",
                (series ? "Episoden-NFO fehlt. Erwartet: " : "Film-NFO fehlt. Erwartet: ") + Path.Combine(listing.Path, name) +
                (series ? "" : " (alternativ movie.nfo im Filmordner)."));
        }
    }
}
