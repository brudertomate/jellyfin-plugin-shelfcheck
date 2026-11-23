using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace ShelfCheck;

public sealed class ReportStore(IApplicationPaths paths)
{
    private readonly string _file = Path.Combine(paths.DataPath, "shelfcheck", "report.json");
    private readonly object _sync = new();
    private Report? _current;
    private DateTime _checkpoint;
    private readonly string _selectionFile = Path.Combine(paths.DataPath, "shelfcheck", "scan-excluded.json");

    public Guid[] Excluded
    {
        get
        {
            lock (_sync) return File.Exists(_selectionFile) ?
                JsonSerializer.Deserialize<Guid[]>(File.ReadAllText(_selectionFile)) ?? [] : [];
        }
        set
        {
            lock (_sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.WriteAllText(_selectionFile + ".tmp", JsonSerializer.Serialize(value));
                File.Move(_selectionFile + ".tmp", _selectionFile, true);
            }
        }
    }

    public void UpdateLive(Report report)
    {
        lock (_sync) _current = report;
        if (DateTime.UtcNow - _checkpoint < TimeSpan.FromSeconds(30)) return;
        PublishAsync(report, CancellationToken.None).GetAwaiter().GetResult();
        _checkpoint = DateTime.UtcNow;
    }

    public Report? Current
    {
        get
        {
            lock (_sync)
            {
                if (_current is null && File.Exists(_file))
                {
                    using var stream = File.OpenRead(_file);
                    _current = JsonSerializer.Deserialize<Report>(stream);
                    if (_current?.State == "Running") _current.State = "Interrupted";
                }

                return _current;
            }
        }
    }

    public async Task PublishAsync(Report report, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = File.Create(temporary))
                await JsonSerializer.SerializeAsync(output, report, cancellationToken: token);
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                File.Move(temporary, _file, true);
                _current = report;
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
