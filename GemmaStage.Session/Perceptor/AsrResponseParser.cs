using System;
using System.Collections.Generic;
using System.Text.Json;

namespace GemmaStage.Session.Perceptor;

// Parser for AsrConversation responses. Handles the single audio tool call
// produced by the ASR system prompt. Image tool calls are owned by
// I2tConversation; per-cycle retellings are owned by IdeaReflectorConversation.
public static class AsrResponseParser
{
    public static PerceptorParseResult Parse(string? responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return Failure("Empty response payload.", null);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseJson);
        }
        catch (JsonException ex)
        {
            return Failure($"Failed to parse outer assistant JSON: {ex.Message}", responseJson);
        }

        using (document)
        {
            var root = document.RootElement;
            var rawContent = TryGetString(root, "content");

            if (!root.TryGetProperty("tool_calls", out var toolCalls) ||
                toolCalls.ValueKind != JsonValueKind.Array ||
                toolCalls.GetArrayLength() == 0)
            {
                return Failure(
                    "Assistant response did not include a tool call. The ASR system prompt requires exactly one tool call per turn.",
                    rawContent);
            }

            var firstCall = toolCalls[0];
            if (!firstCall.TryGetProperty("function", out var function) ||
                function.ValueKind != JsonValueKind.Object)
            {
                return Failure("First tool call did not contain a function object.", rawContent);
            }

            var name = TryGetString(function, "name");
            var argumentsRaw = TryGetString(function, "arguments");

            if (string.IsNullOrEmpty(name))
            {
                return Failure("Tool call had no function name.", rawContent);
            }

            if (string.IsNullOrEmpty(argumentsRaw))
            {
                return Failure($"Tool call \"{name}\" had no arguments payload.", rawContent);
            }

            if (name != AsrSchemas.AudioToolName)
            {
                return Failure(
                    $"Tool call referenced \"{name}\" but ASR turn must call \"{AsrSchemas.AudioToolName}\".",
                    rawContent);
            }

            JsonDocument arguments;
            try
            {
                arguments = JsonDocument.Parse(argumentsRaw);
            }
            catch (JsonException ex)
            {
                return Failure(
                    $"Tool call \"{name}\" arguments were not valid JSON: {ex.Message}",
                    rawContent);
            }

            using (arguments)
            {
                try
                {
                    var audio = ParseAudioArguments(arguments.RootElement);
                    return new PerceptorParseResult(
                        ToolName: name,
                        Audio: audio,
                        Image: null,
                        Error: null,
                        RawAssistantContent: rawContent);
                }
                catch (JsonException ex)
                {
                    return Failure(
                        $"Tool call \"{name}\" arguments did not satisfy the ASR schema: {ex.Message}",
                        rawContent);
                }
            }
        }
    }

    private static PerceptorAudioOutput ParseAudioArguments(JsonElement args)
    {
        var clarity = ParseClarity(GetRequiredString(args, "clarity"));
        var emotion = TryParseEmotion(args, "emotion");
        var transcript = GetRequiredString(args, "transcript_of_this_chunk");
        var grammar = TryParseGrammar(args, "grammar");
        var chunkCompleted = GetRequiredBoolean(args, "chunk_completed");
        var notes = ParseOptionalStringArray(args, "notes");

        return new PerceptorAudioOutput(
            clarity,
            emotion,
            transcript,
            grammar,
            chunkCompleted,
            notes);
    }

    private static PerceptorClarity ParseClarity(string raw)
    {
        return raw switch
        {
            "messy" => PerceptorClarity.Messy,
            "normal" => PerceptorClarity.Normal,
            _ => throw new JsonException($"\"clarity\" had unsupported value \"{raw}\"."),
        };
    }

    private static PerceptorGrammar ParseGrammar(string raw)
    {
        return raw switch
        {
            "poor" => PerceptorGrammar.Poor,
            "moderate" => PerceptorGrammar.Moderate,
            "good" => PerceptorGrammar.Good,
            "excellent" => PerceptorGrammar.Excellent,
            _ => throw new JsonException($"\"grammar\" had unsupported value \"{raw}\"."),
        };
    }

    private static PerceptorEmotion ParseEmotion(string raw)
    {
        return raw switch
        {
            "calm" => PerceptorEmotion.Calm,
            "enthusiastic" => PerceptorEmotion.Enthusiastic,
            "tense" => PerceptorEmotion.Tense,
            "uncertain" => PerceptorEmotion.Uncertain,
            _ => throw new JsonException($"\"emotion\" had unsupported value \"{raw}\"."),
        };
    }

    private static PerceptorEmotion? TryParseEmotion(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Optional string property \"{property}\" was not a string.");
        }

        return ParseEmotion(value.GetString() ?? string.Empty);
    }

    private static PerceptorGrammar? TryParseGrammar(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Optional string property \"{property}\" was not a string.");
        }

        return ParseGrammar(value.GetString() ?? string.Empty);
    }

    private static string GetRequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Required string property \"{property}\" was missing or not a string.");
        }

        return value.GetString() ?? string.Empty;
    }

    private static bool GetRequiredBoolean(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) ||
            (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False))
        {
            throw new JsonException($"Required boolean property \"{property}\" was missing or not a boolean.");
        }

        return value.GetBoolean();
    }

    private static IReadOnlyList<string> ParseOptionalStringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return Array.Empty<string>();
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"Optional array property \"{property}\" was not an array.");
        }

        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"Optional array property \"{property}\" contained a non-string item.");
            }

            var text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                result.Add(text.Trim());
            }
        }

        return result;
    }

    private static string? TryGetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static PerceptorParseResult Failure(string error, string? rawContent)
    {
        return new PerceptorParseResult(
            ToolName: null,
            Audio: null,
            Image: null,
            Error: error,
            RawAssistantContent: rawContent);
    }
}
