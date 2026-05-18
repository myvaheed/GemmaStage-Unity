using System.Collections.Generic;
using System.Text.Json;
using GemmaStage.Session.Perceptor;
using GemmaStage.Session.Prompts;

namespace GemmaStage.Session.Tests.Perceptor;

internal static class AsrResponseParserAudioTests
{
    public static void Run()
    {
        var json = """
            {
              "role": "assistant",
              "content": null,
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_audio_observation",
                    "arguments": "{\"clarity\":\"normal\",\"emotion\":\"calm\",\"transcript_of_this_chunk\":\"hello world\",\"grammar\":\"excellent\",\"chunk_completed\":true,\"notes\":[\"slight pause before the GPU section\"]}"
                  }
                }
              ]
            }
            """;

        var parse = AsrResponseParser.Parse(json);
        AssertEx.True(!parse.IsFailure, $"Audio parse should succeed: {parse.Error}");
        AssertEx.True(parse.IsAudio, "Result should report audio modality");
        AssertEx.True(parse.Audio is not null, "Audio payload should be populated");

        var audio = parse.Audio!;
        AssertEx.Equal(PerceptorClarity.Normal, audio.Clarity, "Clarity should round-trip");
        AssertEx.Equal(PerceptorEmotion.Calm, audio.Emotion!.Value, "Emotion should round-trip");
        AssertEx.Equal("hello world", audio.Transcript, "Transcript should round-trip");
        AssertEx.Equal(PerceptorGrammar.Excellent, audio.Grammar!.Value, "Grammar should round-trip");
        AssertEx.True(audio.ChunkCompleted, "ChunkCompleted should round-trip");
        AssertEx.Equal(1, audio.Notes.Count, "Audio notes should round-trip");
    }
}

internal static class AsrResponseParserPoorClarityTests
{
    public static void Run()
    {
        var json = """
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_audio_observation",
                    "arguments": "{\"clarity\":\"messy\",\"transcript_of_this_chunk\":\"[mostly background noise; speech unintelligible]\",\"chunk_completed\":false}"
                  }
                }
              ]
            }
            """;

        var parse = AsrResponseParser.Parse(json);
        AssertEx.True(!parse.IsFailure, $"Messy audio should parse: {parse.Error}");
        var audio = parse.Audio!;
        AssertEx.Equal(PerceptorClarity.Messy, audio.Clarity, "Clarity should be messy");
        AssertEx.True(audio.Emotion is null, "Emotion should be omitted under messy clarity");
        AssertEx.True(audio.Grammar is null, "Grammar should be omitted under messy clarity");
        AssertEx.True(!audio.ChunkCompleted, "Messy audio should normally leave chunk_completed=false");
        AssertEx.Equal(0, audio.Notes.Count, "Messy audio should not emit notes");
        AssertEx.Equal("[mostly background noise; speech unintelligible]", audio.Transcript, "Messy transcript should explain the problem inline");
    }
}

internal static class AsrResponseParserFailureTests
{
    public static void Run()
    {
        var noToolCalls = AsrResponseParser.Parse("""
            { "role": "assistant", "content": "I refuse to call a tool." }
            """);
        AssertEx.True(noToolCalls.IsFailure, "Missing tool_calls should be a failure");
        AssertEx.Equal("I refuse to call a tool.", noToolCalls.RawAssistantContent!, "Raw content should be surfaced on failure");

        var emptyToolCalls = AsrResponseParser.Parse("""
            { "tool_calls": [] }
            """);
        AssertEx.True(emptyToolCalls.IsFailure, "Empty tool_calls array should be a failure");

        // ASR parser must reject anything that is not the audio tool, since the
        // ASR conversation only declares that one tool.
        var imageInsteadOfAudio = AsrResponseParser.Parse("""
            {
              "tool_calls": [
                { "type": "function", "function": { "name": "report_image_observation", "arguments": "{\"examination\":\"a slide\"}" } }
              ]
            }
            """);
        AssertEx.True(imageInsteadOfAudio.IsFailure, "Non-audio tool should be a failure for ASR parser");

        var badArguments = AsrResponseParser.Parse("""
            {
              "tool_calls": [
                { "type": "function", "function": { "name": "report_audio_observation", "arguments": "not-json" } }
              ]
            }
            """);
        AssertEx.True(badArguments.IsFailure, "Malformed arguments should be a failure");

        var badEnum = AsrResponseParser.Parse("""
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_audio_observation",
                    "arguments": "{\"clarity\":\"sky-high\",\"emotion\":\"calm\",\"transcript_of_this_chunk\":\"x\",\"grammar\":\"good\",\"chunk_completed\":true,\"notes\":[]}"
                  }
                }
              ]
            }
            """);
        AssertEx.True(badEnum.IsFailure, "Out-of-enum value should fail validation");

        var missingChunkCompleted = AsrResponseParser.Parse("""
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_audio_observation",
                    "arguments": "{\"clarity\":\"normal\",\"emotion\":\"calm\",\"transcript_of_this_chunk\":\"x\",\"grammar\":\"good\",\"notes\":[]}"
                  }
                }
              ]
            }
            """);
        AssertEx.True(missingChunkCompleted.IsFailure, "Missing chunk_completed should fail validation");

        var missingTranscript = AsrResponseParser.Parse("""
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_audio_observation",
                    "arguments": "{\"clarity\":\"normal\",\"chunk_completed\":true}"
                  }
                }
              ]
            }
            """);
        AssertEx.True(missingTranscript.IsFailure, "Missing transcript_of_this_chunk should fail validation");

        var optionalFieldsMissing = AsrResponseParser.Parse("""
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_audio_observation",
                    "arguments": "{\"clarity\":\"normal\",\"transcript_of_this_chunk\":\"x\",\"chunk_completed\":true}"
                  }
                }
              ]
            }
            """);
        AssertEx.True(!optionalFieldsMissing.IsFailure, "Optional emotion/grammar/notes should be allowed to be absent");

        var outerJunk = AsrResponseParser.Parse("not even json");
        AssertEx.True(outerJunk.IsFailure, "Garbage outer payload should be a failure");

        var empty = AsrResponseParser.Parse(string.Empty);
        AssertEx.True(empty.IsFailure, "Empty payload should be a failure");
    }
}

internal static class AsrChunkCompletedDerivationTests
{
    public static void Run()
    {
        AssertEx.True(
            AsrConversation.DeriveChunkCompletedFromTranscript("This is a complete sentence."),
            "Trailing period should resolve to completed");
        AssertEx.True(
            AsrConversation.DeriveChunkCompletedFromTranscript("Is this a question?"),
            "Trailing question mark should resolve to completed");
        AssertEx.True(
            AsrConversation.DeriveChunkCompletedFromTranscript("That's amazing!"),
            "Trailing exclamation mark should resolve to completed");
        AssertEx.True(
            AsrConversation.DeriveChunkCompletedFromTranscript("Trailing whitespace.   "),
            "Trailing whitespace before terminator should still resolve to completed");
        AssertEx.True(
            AsrConversation.DeriveChunkCompletedFromTranscript("She said: \"go home.\""),
            "Closing quote after terminator should still resolve to completed");
        AssertEx.True(
            AsrConversation.DeriveChunkCompletedFromTranscript("He thought (quietly)."),
            "Closing parenthesis before terminator should still resolve to completed");
        AssertEx.True(
            AsrConversation.DeriveChunkCompletedFromTranscript("Smart quote.”"),
            "Unicode smart quote after terminator should still resolve to completed");

        AssertEx.True(
            !AsrConversation.DeriveChunkCompletedFromTranscript("This trails off mid"),
            "No terminator should resolve to incomplete");
        AssertEx.True(
            !AsrConversation.DeriveChunkCompletedFromTranscript("In addition, men who routinely sleep"),
            "Mid-sentence cut should resolve to incomplete");
        AssertEx.True(
            !AsrConversation.DeriveChunkCompletedFromTranscript(""),
            "Empty transcript should resolve to incomplete");
        AssertEx.True(
            !AsrConversation.DeriveChunkCompletedFromTranscript("\"\")"),
            "Punctuation-only/wrapper-only transcript should resolve to incomplete");
        AssertEx.True(
            !AsrConversation.DeriveChunkCompletedFromTranscript("trailing comma,"),
            "Trailing comma should resolve to incomplete (not a terminator)");
    }
}

internal static class AsrSchemaTests
{
    public static void Run()
    {
        AssertEx.Equal("[audio]", AsrPrompts.UserPrompt, "Default turn prompt should remain the stable audio marker");

        using var doc = JsonDocument.Parse(AsrPrompts.ToolsJson);
        AssertEx.Equal(JsonValueKind.Array, doc.RootElement.ValueKind, "tools_json must be a JSON array");
        AssertEx.Equal(1, doc.RootElement.GetArrayLength(), "ASR tools_json must contain exactly one tool definition (audio)");

        var function = doc.RootElement[0].GetProperty("function");
        AssertEx.Equal(AsrSchemas.AudioToolName, function.GetProperty("name").GetString()!, "ASR tool must be the audio observation tool");

        var parameters = function.GetProperty("parameters");
        var properties = parameters.GetProperty("properties");
        var required = parameters.GetProperty("required");
        AssertEx.True(properties.TryGetProperty("transcript_of_this_chunk", out _), "Audio tool must use the explicit transcript_of_this_chunk field");
        AssertEx.True(!properties.TryGetProperty("t", out _), "Audio tool must no longer expose the old terse 't' field");
        AssertEx.True(properties.TryGetProperty("chunk_completed", out _), "Audio tool should include chunk_completed");
        AssertEx.Equal(3, required.GetArrayLength(), "Audio tool should require only clarity, transcript_of_this_chunk, and chunk_completed");
    }
}

