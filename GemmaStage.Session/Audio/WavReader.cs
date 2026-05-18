using System;
using System.IO;

namespace GemmaStage.Session.Audio;

public static class WavReader
{
    public static WavData Read(byte[] wavBytes)
    {
        using var ms = new MemoryStream(wavBytes);
        using var reader = new BinaryReader(ms);
        return Read(reader);
    }
    
    public static WavData ReadFile(string path)
    {
        using var fs = File.OpenRead(path);
        using var reader = new BinaryReader(fs);
        return Read(reader);
    }

    private static WavData Read(BinaryReader reader)
    {
        string riff = new string(reader.ReadChars(4));
        if (riff != "RIFF") throw new NotSupportedException("Invalid WAV file: 'RIFF' signature not found.");
        
        reader.ReadInt32(); // ChunkSize
        
        string wave = new string(reader.ReadChars(4));
        if (wave != "WAVE") throw new NotSupportedException("Invalid WAV file: 'WAVE' format not found.");
        
        int sampleRate = 0;
        int numChannels = 0;
        int bitsPerSample = 0;
        short[] samples = Array.Empty<short>();

        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            string chunkId = new string(reader.ReadChars(4));
            int chunkSize = reader.ReadInt32();

            if (chunkId == "fmt ")
            {
                int audioFormat = reader.ReadInt16();
                if (audioFormat != 1) throw new NotSupportedException("Only PCM audio format is supported.");

                numChannels = reader.ReadInt16();
                if (numChannels != 1) throw new NotSupportedException("Only mono audio is supported.");

                sampleRate = reader.ReadInt32();
                reader.ReadInt32(); // ByteRate
                reader.ReadInt16(); // BlockAlign
                bitsPerSample = reader.ReadInt16();
                
                if (bitsPerSample != 16) throw new NotSupportedException("Only 16-bit audio is supported.");
                
                if (chunkSize > 16)
                {
                    reader.BaseStream.Seek(chunkSize - 16, SeekOrigin.Current);
                }
            }
            else if (chunkId == "data")
            {
                if (bitsPerSample == 0) throw new NotSupportedException("Invalid WAV file: 'data' chunk found before 'fmt ' chunk.");
                
                int numSamples = chunkSize / 2;
                samples = new short[numSamples];
                for (int i = 0; i < numSamples; i++)
                {
                    samples[i] = reader.ReadInt16();
                }
                
                break;
            }
            else
            {
                reader.BaseStream.Seek(chunkSize, SeekOrigin.Current);
            }
        }
        
        if (samples.Length == 0 && sampleRate == 0)
        {
            throw new NotSupportedException("Invalid WAV file: missing chunks.");
        }

        return new WavData(sampleRate, samples);
    }
}
