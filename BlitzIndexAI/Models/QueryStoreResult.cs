namespace BlitzIndexAI.Models;

public class QueryStoreResult
{
    public string QueryText { get; set; } = string.Empty;
    public long ExecutionCount { get; set; }
    public long AvgLogicalReads { get; set; }
    public double AvgDurationMs { get; set; }
}
