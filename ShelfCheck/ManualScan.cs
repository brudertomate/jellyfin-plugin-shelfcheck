using Emby.Naming.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ShelfCheck;

public sealed record ScanStatus(string State = "Idle", int Progress = 0, string? Result = null, long FinishedRuns = 0);

public sealed class ManualScan(ILibraryManager libraries, NamingOptions naming, ReportStore store,
    IHostApplicationLifetime lifetime, ILogger<ManualScan> logger) : IAsyncDisposable
{
    private readonly object _gate = new();
    private ScanStatus _status = new();
    private CancellationTokenSource? _cancellation;
    private Task _worker = Task.CompletedTask;
    private bool _disposed;

    public ScanStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public bool TryStart()
    {
        lock (_gate)
        {
            if (_cancellation is not null || _disposed || lifetime.ApplicationStopping.IsCancellationRequested)
                return false;

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            _cancellation = cancellation;
            _status = _status with { State = "Running", Progress = 0 };
            _worker = Task.Run(() => RunAsync(cancellation));
            return true;
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_cancellation is null) return;
            _status = _status with { State = "Cancelling" };
            _cancellation.Cancel();
        }
    }

    private async Task RunAsync(CancellationTokenSource cancellation)
    {
        var result = "Completed";
        try
        {
            var token = cancellation.Token;
            token.ThrowIfCancellationRequested();
            var roots = libraries.GetVirtualFolders(false)
                .Where(f => f.CollectionType is null or CollectionTypeOptions.mixed or CollectionTypeOptions.movies or CollectionTypeOptions.tvshows)
                .SelectMany(f => f.Locations.Select(path => new LibraryRoot(f.Name, path, f.CollectionType?.ToString() ?? "mixed")))
                .Distinct().ToList();
            var progress = new ScanProgress(value =>
            {
                lock (_gate) _status = _status with { Progress = (int)value };
            });
            var report = new Scanner(naming.VideoFileExtensions).Scan(roots, progress, token);
            await store.PublishAsync(report, token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            result = "Cancelled";
        }
        catch (Exception ex)
        {
            result = "Failed";
            logger.LogError(ex, "ShelfCheck scan failed.");
        }
        finally
        {
            lock (_gate)
            {
                _status = new ScanStatus(Result: result, FinishedRuns: _status.FinishedRuns + 1);
                _cancellation = null;
                cancellation.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task worker;
        lock (_gate)
        {
            _disposed = true;
            _cancellation?.Cancel();
            worker = _worker;
        }
        await worker;
    }

    private sealed class ScanProgress(Action<double> update) : IProgress<double>
    {
        public void Report(double value) => update(value);
    }
}
