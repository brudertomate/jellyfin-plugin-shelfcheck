namespace ShelfCheck;

public enum FolderCleanupKind { Movie, Season, Series, Title }

public static class MovieFolderDeletion
{
    public static bool DeleteIfNoVideo(string path, IEnumerable<string> videoExtensions, CancellationToken token, out string detail,
        FolderCleanupKind kind = FolderCleanupKind.Movie)
    {
        var root = new DirectoryInfo(path);
        var label = kind switch
        {
            FolderCleanupKind.Season => "Staffelordner",
            FolderCleanupKind.Series => "Serienordner",
            FolderCleanupKind.Title => "Titelordner",
            _ => "Filmordner"
        };
        if (root.Parent is null) throw new InvalidOperationException("Ein Dateisystem-Stammordner kann nicht entfernt werden.");
        if (kind == FolderCleanupKind.Season && !Scanner.IsSeasonDirectory(root.Name))
        {
            detail = "Der Ordnername entspricht keinem erkannten Staffelordner.";
            return false;
        }
        if (!root.Exists)
        {
            detail = label + " wurde bereits entfernt.";
            return false;
        }
        var extensions = new HashSet<string>(videoExtensions.Concat([".disc"]), StringComparer.OrdinalIgnoreCase);
        var files = new List<FileInfo>();
        var directories = new List<DirectoryInfo>();
        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((root, 0));
        while (pending.TryPop(out var next))
        {
            token.ThrowIfCancellationRequested();
            EnsureNoDirectoryLinks(root.FullName, next.Directory.FullName);
            if (next.Depth > 96) throw new InvalidOperationException("Ordnerstruktur ist zu tief für eine vollständige Prüfung.");
            if (kind is not (FolderCleanupKind.Series or FolderCleanupKind.Title) && Scanner.IsSeasonDirectory(next.Directory.Name) &&
                !(kind == FolderCleanupKind.Season && next.Depth == 0))
            {
                detail = "Staffelstruktur erkannt. Ordner bleibt erhalten.";
                return false;
            }
            directories.Add(next.Directory);
            foreach (var entry in next.Directory.EnumerateFileSystemInfos("*", new EnumerationOptions
                { IgnoreInaccessible = false, AttributesToSkip = 0 }))
            {
                token.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    detail = "Weitere Verknüpfung vorhanden. Ordner bleibt erhalten.";
                    return false;
                }
                if (entry.Name.Equals(".ignore", StringComparison.OrdinalIgnoreCase) ||
                    (kind is not (FolderCleanupKind.Series or FolderCleanupKind.Title) && entry.Name.Equals("tvshow.nfo", StringComparison.OrdinalIgnoreCase)) ||
                    (kind == FolderCleanupKind.Movie && entry.Name.Equals("season.nfo", StringComparison.OrdinalIgnoreCase)))
                {
                    detail = "Serien-/Staffelmetadaten oder .ignore erkannt. Ordner bleibt erhalten.";
                    return false;
                }
                if (entry is DirectoryInfo directory) pending.Push((directory, next.Depth + 1));
                else if (extensions.Contains(entry.Extension))
                {
                    detail = "Videodatei oder Medienplatzhalter vorhanden. Ordner bleibt erhalten.";
                    return false;
                }
                else files.Add((FileInfo)entry);
            }
        }

        token.ThrowIfCancellationRequested();
        // Delete only the inspected files and then empty directories. Newly added files stop directory removal.
        foreach (var file in files)
        {
            EnsureNoDirectoryLinks(root.FullName, file.DirectoryName!);
            if ((File.GetAttributes(file.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Der Ordnerinhalt wurde zwischenzeitlich durch eine Verknüpfung ersetzt.");
            File.Delete(file.FullName);
        }
        for (var i = directories.Count - 1; i >= 0; i--)
        {
            EnsureNoDirectoryLinks(root.FullName, directories[i].FullName);
            Directory.Delete(directories[i].FullName, recursive: false);
        }
        detail = label + " ohne Video einschließlich seiner Begleitdateien entfernt.";
        return true;
    }

    private static void EnsureNoDirectoryLinks(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Pfad liegt außerhalb des geprüften Ordners.");
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Verzeichnisverknüpfung erkannt. Ordner bleibt erhalten.");
            if (directory.FullName.Equals(root, comparison)) return;
        }
        throw new IOException("Ordner konnte nicht eindeutig zugeordnet werden.");
    }
}
