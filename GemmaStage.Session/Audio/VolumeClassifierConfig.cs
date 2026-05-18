namespace GemmaStage.Session.Audio;

public class VolumeClassifierConfig
{
    public double WindowMs { get; set; } = 300;
    
    // Exact threshold in dBFS (e.g. -45.0)
    public double? AbsoluteThresholdDb { get; set; }
    
    // Percentile indicating how the threshold adapts (e.g., 30 for 30th percentile)
    public double? PercentileThreshold { get; set; }
}
