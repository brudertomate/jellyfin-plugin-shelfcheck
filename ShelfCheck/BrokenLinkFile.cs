namespace ShelfCheck;

public static class BrokenLinkFile
{
    public static bool DeleteIfBroken(string path, out string detail)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not { } target)
        {
            detail = "Der Pfad ist keine Dateiverknüpfung oder wurde bereits entfernt.";
            return false;
        }
        if ((info.Attributes & FileAttributes.Directory) != 0)
        {
            detail = "Verzeichnisverknüpfung übersprungen.";
            return false;
        }
        var resolved = info.ResolveLinkTarget(true);
        if (resolved is null)
        {
            detail = "Verknüpfungsziel konnte nicht eindeutig bestimmt werden.";
            return false;
        }
        try
        {
            File.GetAttributes(resolved.FullName);
            detail = "Das Ziel ist inzwischen erreichbar.";
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        info.Refresh();
        if (info.LinkTarget != target)
        {
            detail = "Die Verknüpfung wurde zwischenzeitlich geändert.";
            return false;
        }
        File.Delete(path);
        detail = "Defekte Dateiverknüpfung entfernt.";
        return true;
    }
}
