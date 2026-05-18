using System;
using System.IO;
using System.Text;
using GemmaStage.Session.Audio;

namespace GemmaStage.Session.Tests.Audio;

internal static class WavReaderTests
{
    public static void Run()
    {
        byte[] validWav = CreateSyntheticWav(16000, 1, 16, new short[] { 100, -100, 200, -200 });
        var data = WavReader.Read(validWav);
        AssertEx.Equal(16000, data.SampleRate, "SampleRate should match");
        AssertEx.Equal(4, data.Samples.Length, "Samples length should match");
        AssertEx.Equal((short)100, data.Samples[0], "Sample 0");
        AssertEx.Equal((short)-200, data.Samples[3], "Sample 3");

        byte[] stereoWav = CreateSyntheticWav(16000, 2, 16, new short[] { 0, 0 });
        AssertThrowsNotSupported(() => WavReader.Read(stereoWav), "Stereo should not be supported");

        byte[] bit8Wav = CreateSyntheticWav(16000, 1, 8, new short[] { 0 });
        AssertThrowsNotSupported(() => WavReader.Read(bit8Wav), "8-bit should not be supported");
    }

    private static void AssertThrowsNotSupported(Action action, string message)
    {
        try
        {
            action();
            throw new InvalidOperationException("Expected NotSupportedException: " + message);
        }
        catch (NotSupportedException)
        {
            // success
        }
    }

    private static byte[] CreateSyntheticWav(int sampleRate, short numChannels, short bitsPerSample, short[] samples)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write((int)0); // Placeholder for ChunkSize
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write((int)16); // chunkSize
        writer.Write((short)1); // AudioFormat PCM
        writer.Write(numChannels);
        writer.Write(sampleRate);
        writer.Write((int)(sampleRate * numChannels * (bitsPerSample / 8))); // ByteRate
        writer.Write((short)(numChannels * (bitsPerSample / 8))); // BlockAlign
        writer.Write(bitsPerSample);
        
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write((int)(samples.Length * 2)); // chunkSize
        foreach (var s in samples)
        {
            writer.Write(s);
        }
        
        // update chunk size
        long currentPos = ms.Position;
        writer.Seek(4, SeekOrigin.Begin);
        writer.Write((int)(currentPos - 8));
        
        return ms.ToArray();
    }
}
