namespace ShelfCheck;

public sealed record LibraryRoot(string Library, string Path, string Type);
public sealed record Finding(string Library, string Path, string Kind, string Code, string Detail);

public sealed class Report
{
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public DateTime FinishedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string State { get; set; } = "Completed";
    public string CurrentPath { get; set; } = "";
    public List<LibraryRoot> Roots { get; init; } = [];
    public int Directories { get; set; }
    public int Titles { get; set; }
    public int Seasons { get; set; }
    public int Videos { get; set; }
    public List<Finding> Findings { get; init; } = [];
}
