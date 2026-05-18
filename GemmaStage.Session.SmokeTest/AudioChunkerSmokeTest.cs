using System;
using System.IO;
using GemmaStage.Session.Audio;

internal static class AudioChunkerSmokeTest
{
    public static void Run(string resourceDir)
    {
        var audioPath = Path.Combine(resourceDir, "ted_test_sleep_full.wav");

        if (!File.Exists(audioPath))
        {
            Console.WriteLine();
            Console.WriteLine($"AudioChunker smoke test skipped: missing {audioPath}");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("AudioChunker smoke test");
        Console.WriteLine($"  Audio input: {audioPath}");

        // 1. Read WAV
        var wavBytes = File.ReadAllBytes(audioPath);
        var wavData = WavReader.Read(wavBytes);
        Console.WriteLine($"  WavReader: {wavData.SampleRate} Hz, {wavData.Samples.Length} samples ({(wavData.Samples.Length / (double)wavData.SampleRate):0.0} seconds)");

        // 2. Volume Classification
        var windowMs = 300; // C ms
        var classifierConfig = new VolumeClassifierConfig
        {
            WindowMs = windowMs,
            AbsoluteThresholdDb = -45.0
        };
        
        var symbols = VolumeClassifier.Classify(wavData, classifierConfig);
        Console.WriteLine($"  VolumeClassifier: Produced {symbols.Length} symbols ('o' or 'V')");
        
        // Print symbols overview
        string symbolsStr = new string(symbols);
        Console.WriteLine($"  Symbols snippet: {(symbolsStr.Length > 100 ? symbolsStr.Substring(0, 100) + "..." : symbolsStr)}");

        // 3. Audio Chunking
        var chunkerConfig = new AudioChunkerConfig
        {
            TargetChunkWindowX = TimeSpan.FromSeconds(15),
            TrailingSilenceY = TimeSpan.FromMilliseconds(300),
            WindowMsC = TimeSpan.FromMilliseconds(windowMs)
        };
        var chunker = new AudioChunker(chunkerConfig);
        var chunks = chunker.Chunk(symbols, wavData.SampleRate);

        Console.WriteLine($"  AudioChunker: Emitted {chunks.Count} chunks.");
        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            double startSec = chunk.StartSample / (double)wavData.SampleRate;
            double endSec = chunk.EndSample / (double)wavData.SampleRate;
            double stitchedDuration = CountChunkSamples(wavData, chunk) / (double)wavData.SampleRate;
            Console.WriteLine($"    Chunk {i + 1}: [{startSec:0.0}s - {endSec:0.0}s] (Stitched duration: {stitchedDuration:0.0}s) - {chunk.Symbols.Length} symbols");
        }
    }

    private static int CountChunkSamples(WavData wav, AudioChunk chunk)
    {
        var total = 0;
        foreach (var span in chunk.Spans)
        {
            int startSample = Math.Max(0, span.StartSample);
            int endSample = Math.Min(wav.Samples.Length - 1, span.EndSample);
            if (endSample >= startSample)
            {
                total += endSample - startSample + 1;
            }
        }

        return total;
    }
}
