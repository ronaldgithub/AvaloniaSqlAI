namespace BlitzIndexAI.Models;

public class BenchmarkStats
{
    public string Label { get; set; } = string.Empty;
    public long LogicalReads { get; set; }
    public long PhysicalReads { get; set; }
    public long ReadAheadReads { get; set; }
    public string CpuMs { get; set; } = "-";
    public string ElapsedMs { get; set; } = "-";
}
