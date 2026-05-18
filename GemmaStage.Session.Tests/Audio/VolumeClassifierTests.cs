using System;
using GemmaStage.Session.Audio;

namespace GemmaStage.Session.Tests.Audio;

internal static class VolumeClassifierTests
{
    public static void Run()
    {
        TestAbsoluteThreshold();
        TestPercentileThreshold();
    }

    private static void TestAbsoluteThreshold()
    {
        int sr = 1000;
        short[] samples = new short[3000];
        // Window 1: Silence
        for (int i = 0; i < sr; i++) samples[i] = 0;
        // Window 2: Full sine approx RMS ~23000 => -3 dBFS
        for (int i = sr; i < 2 * sr; i++) samples[i] = 23000;
        // Window 3: small signal approx RMS 100 => -50 dBFS
        for (int i = 2 * sr; i < 3 * sr; i++) samples[i] = 100;

        var data = new WavData(sr, samples);
        var config = new VolumeClassifierConfig
        {
            WindowMs = 1000,
            AbsoluteThresholdDb = -45.0
        };

        var result = VolumeClassifier.Classify(data, config);
        
        AssertEx.Equal(3, result.Length, "Should have 3 windows");
        AssertEx.Equal(VolumeClassifier.Silence, result[0], "Window 1 should be silence");
        AssertEx.Equal(VolumeClassifier.Voice, result[1], "Window 2 should be voice");
        AssertEx.Equal(VolumeClassifier.Silence, result[2], "Window 3 should be silence");
    }

    private static void TestPercentileThreshold()
    {
        int sr = 1000;
        short[] samples = new short[5000];
        // 5 windows format: [0, 1000), [1000, 2000), etc.
        for (int i = 4000; i < 5000; i++) samples[i] = 20000;

        var data = new WavData(sr, samples);
        var config = new VolumeClassifierConfig
        {
            WindowMs = 1000,
            PercentileThreshold = 90
        };

        var result = VolumeClassifier.Classify(data, config);
        
        AssertEx.Equal(5, result.Length, "Should have 5 windows");
        AssertEx.Equal(VolumeClassifier.Silence, result[0], "Window 1 should be silence");
        AssertEx.Equal(VolumeClassifier.Voice, result[4], "Window 5 should be voice");
    }
}
