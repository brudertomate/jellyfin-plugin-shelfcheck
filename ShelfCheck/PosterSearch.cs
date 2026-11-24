namespace ShelfCheck;

public enum PosterAttempt { Unsupported, Missing, Found }
public sealed record PosterSource(string Name, Func<CancellationToken, Task<PosterAttempt>> Fetch);
public sealed record PosterSearchResult(bool Found, int Checked, List<string> Errors)
{
    public bool CanUsePlaceholder => !Found && Checked > 0 && Errors.Count == 0;
}

public static class PosterSearch
{
    public static async Task<PosterSearchResult> Run(IEnumerable<PosterSource> sources, CancellationToken token)
    {
        var errors = new List<string>();
        var checkedCount = 0;
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var attempt = await source.Fetch(token);
                if (attempt == PosterAttempt.Unsupported) continue;
                checkedCount++;
                if (attempt == PosterAttempt.Found) return new(true, checkedCount, errors);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var message = ex.Message.Split(['\r', '\n'])[0];
                errors.Add(source.Name + " – " + message[..Math.Min(message.Length, 240)]);
            }
        }
        return new(false, checkedCount, errors);
    }
}
