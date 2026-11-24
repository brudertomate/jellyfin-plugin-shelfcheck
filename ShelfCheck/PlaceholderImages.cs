using System.Security.Cryptography;

namespace ShelfCheck;

public enum PlaceholderKind { Cover, Season }

public static class PlaceholderImages
{
    private sealed record Asset(PlaceholderKind Kind, byte[] Bytes, byte[] Hash);
    private static readonly Lazy<Asset[]> Assets = new(() =>
        Enum.GetValues<PlaceholderKind>().Select(kind =>
        {
            var file = kind == PlaceholderKind.Season ? "season-placeholder.png" : "cover-placeholder.png";
            using var stream = typeof(PlaceholderImages).Assembly.GetManifestResourceStream("ShelfCheck.Assets." + file)
                ?? throw new InvalidOperationException("Platzhalterbild fehlt im Plugin-Paket.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            return new Asset(kind, bytes, SHA256.HashData(bytes));
        }).ToArray());

    public static Stream Open(PlaceholderKind kind) => new MemoryStream(Assets.Value.Single(asset => asset.Kind == kind).Bytes, writable: false);

    public static PlaceholderKind? Identify(string path, long? length = null)
    {
        var size = length ?? new FileInfo(path).Length;
        var candidates = Assets.Value.Where(asset => asset.Bytes.LongLength == size).ToArray();
        if (candidates.Length == 0) return null;
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return candidates.FirstOrDefault(asset => hash.AsSpan().SequenceEqual(asset.Hash))?.Kind;
    }
}
