using System;

namespace GemmaStage.Session.Audio;

public static class VolumeClassifier
{
    public const char Silence = 'o';
    public const char Voice = 'V';

    public static char[] Classify(WavData wav, VolumeClassifierConfig config)
    {
        Guard.NotNull(wav);
        Guard.NotNull(config);
        
        if (config.WindowMs <= 0) throw new ArgumentOutOfRangeException(nameof(config.WindowMs), "Window size must be positive.");
        if (config.AbsoluteThresholdDb == null && config.PercentileThreshold == null)
            throw new ArgumentException("Either AbsoluteThresholdDb or PercentileThreshold must be specified.");

        int samplesPerWindow = (int)Math.Floor(wav.SampleRate * (config.WindowMs / 1000.0));
        if (samplesPerWindow == 0) throw new ArgumentException("WindowMs is too small for the sample rate.");

        int numWindows = (int)Math.Ceiling(wav.Samples.Length / (double)samplesPerWindow);
        if (wav.Samples.Length == 0) return Array.Empty<char>();
        
        double[] windowDb = new double[numWindows];
        
        for (int i = 0; i < numWindows; i++)
        {
            int start = i * samplesPerWindow;
            int end = Math.Min(wav.Samples.Length, start + samplesPerWindow);
            int count = end - start;
            
            double sumSquares = 0;
            for (int j = start; j < end; j++)
            {
                double s = wav.Samples[j];
                sumSquares += s * s;
            }
            double rms = Math.Sqrt(sumSquares / count);
            
            // Prevent log(0), floor at minimal value of -100 dBFS
            double dbfs = rms > 0 ? 20 * Math.Log10(rms / 32768.0) : -100.0;
            windowDb[i] = dbfs;
        }

        double threshold = DetermineThreshold(windowDb, config);
        
        char[] results = new char[numWindows];
        for (int i = 0; i < numWindows; i++)
        {
            results[i] = windowDb[i] >= threshold ? Voice : Silence;
        }
        
        return results;
    }

    private static double DetermineThreshold(double[] windowDb, VolumeClassifierConfig config)
    {
        if (config.AbsoluteThresholdDb.HasValue)
        {
            return config.AbsoluteThresholdDb.Value;
        }
        
        var sorted = (double[])windowDb.Clone();
        Array.Sort(sorted);
        
        double p = config.PercentileThreshold!.Value / 100.0;
        p = Math.Max(0.0, Math.Min(1.0, p));
        
        int n = sorted.Length;
        double index = p * (n - 1);
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);
        
        if (lower == upper)
            return sorted[lower];
            
        double fraction = index - lower;
        return sorted[lower] * (1 - fraction) + sorted[upper] * fraction;
    }
}
