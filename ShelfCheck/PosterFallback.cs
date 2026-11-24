using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;

namespace ShelfCheck;

public sealed class PosterFallback(ILibraryManager libraries, IProviderManager providers, IFileSystem fileSystem, PlaceholderIndex index)
{
    public async Task<ActionResult?> Fill(BaseItem item, string reportPath, CancellationToken token)
    {
        if (item is not (Movie or Series or Season)) return null;
        if (HasRealPoster(item)) { index.Set(reportPath, false); return null; }
        var typeOptions = libraries.GetLibraryOptions(item).GetTypeOptions(item.GetType().Name)
            ?? new TypeOptions { Type = item.GetType().Name };
        if (typeOptions.GetLimit(ImageType.Primary) <= 0)
            return new(reportPath, "Skipped", "Poster sind für diesen Eintrag in der Bibliothek deaktiviert.");
        var options = new MetadataRefreshOptions(new DirectoryService(fileSystem))
        {
            ImageRefreshMode = MetadataRefreshMode.FullRefresh, MetadataRefreshMode = MetadataRefreshMode.None,
            IsAutomated = false, ReplaceAllImages = false
        };
        var sources = providers.GetImageProviders(item, options).OfType<IRemoteImageProvider>()
            .Select(provider => new PosterSource(provider.Name, ct => Fetch(item, provider, typeOptions.GetMinWidth(ImageType.Primary), ct)));
        var search = await PosterSearch.Run(sources, token);
        if (search.Found)
        {
            index.Set(reportPath, false);
            var warnings = search.Errors.Count == 0 ? "" : " Anbieterfehler während der Suche: " + string.Join(" | ", search.Errors);
            return new(reportPath, "Succeeded", "Echtes Poster vorhanden." + warnings);
        }
        if (search.Errors.Count > 0)
            return new(reportPath, "Failed", "Postersuche mit Anbieterfehlern. " + string.Join(" | ", search.Errors));
        if (!search.CanUsePlaceholder)
            return new(reportPath, "Skipped", "Keine aktive Bildquelle hat eine Postersuche abgeschlossen. Bildanbieter und Bibliothekseinstellungen prüfen.");
        if (HasRealPoster(item))
        {
            index.Set(reportPath, false);
            return new(reportPath, "Succeeded", "Echtes Poster ist inzwischen vorhanden.");
        }
        var kind = item is Season ? PlaceholderKind.Season : PlaceholderKind.Cover;
        var current = item.GetImageInfo(ImageType.Primary, 0);
        if (current is not null && File.Exists(current.Path) && PlaceholderImages.Identify(current.Path) == kind)
        {
            index.Set(reportPath, true);
            return new(reportPath, "Placeholder", "Alle aktiven Posteranbieter geprüft. Weiterhin kein echtes Poster verfügbar. Passender Platzhalter bleibt erhalten.");
        }
        await using var image = PlaceholderImages.Open(kind);
        await providers.SaveImage(item, image, "image/png", ImageType.Primary, 0, token);
        await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, CancellationToken.None);
        index.Set(reportPath, true);
        return new(reportPath, "Placeholder", kind == PlaceholderKind.Season
            ? "Alle aktiven Posteranbieter geprüft. Kein Staffelposter verfügbar. Separaten Staffelplatzhalter eingesetzt."
            : "Alle aktiven Posteranbieter geprüft. Kein echtes Cover verfügbar. Platzhaltercover eingesetzt.");
    }

    private static bool HasRealPoster(BaseItem item)
    {
        var image = item.GetImageInfo(ImageType.Primary, 0);
        if (image is null) return false;
        if (!image.IsLocalFile) return true;
        try { return PlaceholderImages.Identify(image.Path) is null; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private async Task<PosterAttempt> Fetch(BaseItem item, IRemoteImageProvider provider, int minimumWidth, CancellationToken token)
    {
        if (!provider.GetSupportedImages(item).Contains(ImageType.Primary)) return PosterAttempt.Unsupported;
        var images = await provider.GetImages(item, token)
            ?? throw new InvalidOperationException("Bildanbieter hat kein gültiges Suchergebnis geliefert.");
        var language = item.GetPreferredMetadataLanguage();
        var candidates = images.Where(image => image.Type == ImageType.Primary && (image.Width is null || image.Width >= minimumWidth) &&
                (string.IsNullOrWhiteSpace(language) || string.IsNullOrWhiteSpace(image.Language) ||
                 string.Equals(image.Language, language, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(image.Language, "en", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(image => string.Equals(image.Language, language, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(image => string.IsNullOrWhiteSpace(image.Language)).ToArray();
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (HasRealPoster(item)) return PosterAttempt.Found;
            using var response = await provider.GetImageResponse(candidate.Url, token);
            response.EnsureSuccessStatusCode();
            var mime = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(mime) || mime.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
            {
                mime = Path.GetExtension(new Uri(candidate.Url).AbsolutePath).ToLowerInvariant() switch
                {
                    ".png" => "image/png", ".webp" => "image/webp", ".jpg" or ".jpeg" => "image/jpeg",
                    _ => throw new InvalidOperationException("Das Bildformat des Anbieters konnte nicht bestimmt werden.")
                };
            }
            if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Der Anbieter hat keine Bilddatei zurückgegeben.");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            if (HasRealPoster(item)) return PosterAttempt.Found;
            await providers.SaveImage(item, stream, mime, ImageType.Primary, 0, token);
            await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, CancellationToken.None);
            return PosterAttempt.Found;
        }
        return PosterAttempt.Missing;
    }
}
