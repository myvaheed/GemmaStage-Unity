namespace GemmaStage.Session.Audio;

public class WavData
{
    public int SampleRate { get; }
    public short[] Samples { get; }

    public WavData(int sampleRate, short[] samples)
    {
        SampleRate = sampleRate;
        Samples = samples;
    }
}
