namespace ShelfCheck;

public sealed class ScanControl
{
    private readonly object _gate = new();
    public bool Paused { get; private set; }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            Paused = paused;
            Monitor.PulseAll(_gate);
        }
    }

    public void Wait(CancellationToken token)
    {
        lock (_gate)
        {
            while (Paused)
            {
                token.ThrowIfCancellationRequested();
                Monitor.Wait(_gate, 200);
            }
        }
        token.ThrowIfCancellationRequested();
    }
}
