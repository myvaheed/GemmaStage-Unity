using System;
using System.Collections.Generic;

namespace GemmaStage.Session.Tests.Audio;

internal static class AudioChunkerTests
{
    public static void Run()
    {
        Chunk_WorkedExample_MatchesExpected();
        Chunk_AllSilence_NoChunksEmitted();
        Chunk_AllVoice_OneChunkOrTrimmed();
        Chunk_VoiceLongSilenceVoice_TwoDistinctChunks();
        Chunk_MillisecondTrailingSilence_SplitsAtConfiguredWindow();
        Chunk_EndOfStreamWhileChunkOpen_Flushes();
    }

    private static void Chunk_WorkedExample_MatchesExpected()
    {
        var chunker = new GemmaStage.Session.Audio.AudioChunker(new GemmaStage.Session.Audio.AudioChunkerConfig
        {
            TargetChunkWindowX = TimeSpan.FromSeconds(4),
            TrailingSilenceY = TimeSpan.FromSeconds(2),
            WindowMsC = TimeSpan.FromMilliseconds(1000)
        });

        var input = "oooooVVoVoooVoooVVoo".ToCharArray();
        int sampleRate = 16000;
        
        var chunks = chunker.Chunk(input, sampleRate);

        AssertEx.Equal(2, chunks.Count, "Expected exactly 2 chunks");
        AssertEx.Equal("oVVoVooVo", new string(chunks[0].Symbols), "Chunk 1 should compact long internal silence to C-padding on both voice islands");
        AssertEx.Equal("oVVo", new string(chunks[1].Symbols), "Chunk 2 symbols match logic");
        AssertEx.Equal(2, chunks[0].Spans.Count, "Compacted internal silence should produce stitched sample spans");
    }
    
    private static void Chunk_AllSilence_NoChunksEmitted()
    {
        var chunker = new GemmaStage.Session.Audio.AudioChunker(new GemmaStage.Session.Audio.AudioChunkerConfig());
        var input = "oooooooooo".ToCharArray();
        var chunks = chunker.Chunk(input, 16000);
        AssertEx.Equal(0, chunks.Count, "All silence should have 0 chunks");
    }

    private static void Chunk_AllVoice_OneChunkOrTrimmed()
    {
        var chunker = new GemmaStage.Session.Audio.AudioChunker(new GemmaStage.Session.Audio.AudioChunkerConfig());
        var input = "VVVVVVVV".ToCharArray();
        var chunks = chunker.Chunk(input, 16000);
        AssertEx.True(chunks.Count >= 1, "All voice should trim into chunks");
    }

    private static void Chunk_VoiceLongSilenceVoice_TwoDistinctChunks()
    {
        // Use the same "each symbol = 1s" convention as the worked example in
        // IMPLEMENTATION_PLAN §2.2 (C=1000ms): X=2s -> 2 V symbols, Y=1s -> 1
        // trailing silence symbol. Each V island has 2 V, so each chunk
        // emits on its first trailing silence; the long run between islands
        // is consumed while no chunk is active.
        var chunker = new GemmaStage.Session.Audio.AudioChunker(new GemmaStage.Session.Audio.AudioChunkerConfig
        {
            TargetChunkWindowX = TimeSpan.FromSeconds(2),
            TrailingSilenceY = TimeSpan.FromMilliseconds(1000),
            WindowMsC = TimeSpan.FromMilliseconds(1000)
        });
        var input = "oVVo" + "oooo" + "oVVoo";
        var chunks = chunker.Chunk(input.ToCharArray(), 16000);
        AssertEx.Equal(2, chunks.Count, "Long silence should split chunks");
    }

    private static void Chunk_MillisecondTrailingSilence_SplitsAtConfiguredWindow()
    {
        var chunker = new GemmaStage.Session.Audio.AudioChunker(new GemmaStage.Session.Audio.AudioChunkerConfig
        {
            TargetChunkWindowX = TimeSpan.FromMilliseconds(600),
            TrailingSilenceY = TimeSpan.FromMilliseconds(300),
            WindowMsC = TimeSpan.FromMilliseconds(300)
        });

        var chunks = chunker.Chunk("oVVooVVoo".ToCharArray(), 16000);
        AssertEx.Equal(2, chunks.Count, "300ms trailing silence with 300ms classifier windows should split after one silent window");
        AssertEx.Equal("oVVo", new string(chunks[0].Symbols), "First millisecond-configured chunk should include one leading and trailing pad");
        AssertEx.Equal("oVVo", new string(chunks[1].Symbols), "Second millisecond-configured chunk should include one leading and trailing pad");
    }

    private static void Chunk_EndOfStreamWhileChunkOpen_Flushes()
    {
        var chunker = new GemmaStage.Session.Audio.AudioChunker(new GemmaStage.Session.Audio.AudioChunkerConfig());
        var input = "ooVV".ToCharArray();
        var chunks = chunker.Chunk(input, 16000);
        AssertEx.Equal(1, chunks.Count, "End of stream flushes open chunk");
        AssertEx.Equal("oVV", new string(chunks[0].Symbols), "Symbols should match");
    }
}
