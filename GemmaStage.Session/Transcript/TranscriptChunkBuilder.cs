using System;
using System.Collections.Generic;

namespace GemmaStage.Session.Transcript;

// Accumulates transcript entries by token count + chunk_completed boundary.
// Shared by TranscriptSummarizer (MAP chunking, §19.3) and Clarification (transcript
// walk, §18). Token count is estimated from word count * tokensPerWord.
public sealed class TranscriptChunkBuilder
{
    private readonly int _tokenBudget;
    private readonly double _tokensPerWord;

    private readonly List<string> _buffer = new();
    private int _wordCount;

    public TranscriptChunkBuilder(int tokenBudget = 1000, double tokensPerWord = 1.33)
    {
        _tokenBudget = tokenBudget > 0 ? tokenBudget : 1000;
        _tokensPerWord = tokensPerWord > 0 ? tokensPerWord : 1.33;
    }

    // Feeds one Perceptor output entry. Returns a chunk if the boundary conditions are met:
    //   estimatedTokens >= tokenBudget AND chunkCompleted == true
    // Otherwise returns null (keep accumulating).
    public string? Feed(string text, bool chunkCompleted)
    {
        var words = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        _buffer.AddRange(words);
        _wordCount += words.Length;

        var estimatedTokens = (int)(_wordCount * _tokensPerWord);
        if (estimatedTokens >= _tokenBudget && chunkCompleted)
        {
            var chunk = string.Join(' ', _buffer);
            _buffer.Clear();
            _wordCount = 0;
            return chunk;
        }

        return null;
    }

    // Flushes any remaining buffered text as a final chunk.
    public string? Flush()
    {
        if (_buffer.Count == 0)
        {
            return null;
        }

        var chunk = string.Join(' ', _buffer);
        _buffer.Clear();
        _wordCount = 0;
        return chunk;
    }
}

// A built chunk plus the timestamp range it covers, used by the runner to
// intersect chunks with Q&A rounds.
public sealed record TimedTranscriptChunk(
    string Text,
    DateTimeOffset Start,
    DateTimeOffset End);
