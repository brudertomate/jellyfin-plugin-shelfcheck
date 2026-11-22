using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace ShelfCheck;

public sealed class ReportStore(IApplicationPaths paths)
{
    private readonly string _file = Path.Combine(paths.DataPath, "shelfcheck", "report.json");
    private readonly object _sync = new();
    private Report? _current;

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
