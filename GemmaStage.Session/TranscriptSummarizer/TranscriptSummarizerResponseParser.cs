using System;
using System.Collections.Generic;
using System.Text.Json;
using GemmaStage.Session.Resilience;

namespace GemmaStage.Session.TranscriptSummarizer;

public sealed record TranscriptSummarizerParseResult(
    string? ToolName,
    TranscriptSummarizerMapRetellingOutput? MapRetelling,
    TranscriptSummarizerMapSignalsOutput? MapSignals,
    TranscriptSummarizerReduceOutput? Reduce,
    string? Error,
    string? RawAssistantContent) : IToolCallParseResult
{
    public bool IsMapRetelling => MapRetelling is not null;
    public bool IsMapSignals => MapSignals is not null;
    public bool IsReduce => Reduce is not null;
    public bool IsFailure => Error is not null;
}

public static class TranscriptSummarizerResponseParser
{
    public static TranscriptSummarizerParseResult Parse(string? responseJson)
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
            return Failure($"Failed to parse outer JSON: {ex.Message}", responseJson);
        }

        using (document)
        {
            var root = document.RootElement;
            var rawContent = TryGetString(root, "content");

            if (!root.TryGetProperty("tool_calls", out var toolCalls) ||
                toolCalls.ValueKind != JsonValueKind.Array ||
                toolCalls.GetArrayLength() == 0)
            {
                return Failure("Response did not include a tool call.", rawContent);
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

            JsonDocument arguments;
            try
            {
                arguments = JsonDocument.Parse(argumentsRaw);
            }
            catch (JsonException ex)
            {
                return Failure($"Tool call \"{name}\" arguments were not valid JSON: {ex.Message}", rawContent);
            }

            using (arguments)
            {
                var args = arguments.RootElement;
                try
                {
                    return name switch
                    {
                        TranscriptSummarizerSchemas.MapRetellingToolName => new TranscriptSummarizerParseResult(
                            ToolName: name,
                            MapRetelling: ParseMapRetellingArguments(args),
                            MapSignals: null,
                            Reduce: null,
                            Error: null,
                            RawAssistantContent: rawContent),
                        TranscriptSummarizerSchemas.MapSignalsToolName => new TranscriptSummarizerParseResult(
                            ToolName: name,
                            MapRetelling: null,
                            MapSignals: ParseMapSignalsArguments(args),
                            Reduce: null,
                            Error: null,
                            RawAssistantContent: rawContent),
                        TranscriptSummarizerSchemas.ReduceToolName => new TranscriptSummarizerParseResult(
                            ToolName: name,
                            MapRetelling: null,
                            MapSignals: null,
                            Reduce: ParseReduceArguments(args),
                            Error: null,
                            RawAssistantContent: rawContent),
                        _ => Failure($"Unknown tool \"{name}\".", rawContent),
                    };
                }
                catch (JsonException ex)
                {
                    return Failure(
                        $"Tool call \"{name}\" arguments did not satisfy schema: {ex.Message}",
                        rawContent);
                }
            }
        }
    }

    private static TranscriptSummarizerMapRetellingOutput ParseMapRetellingArguments(JsonElement args)
    {
        var retelling = GetRequiredString(args, "retelling");
        if (string.IsNullOrWhiteSpace(retelling))
        {
            throw new JsonException("\"retelling\" cannot be empty.");
        }

        return new TranscriptSummarizerMapRetellingOutput(retelling.Trim());
    }

    private static TranscriptSummarizerMapSignalsOutput ParseMapSignalsArguments(JsonElement args)
    {
        var structure = GetRequiredString(args, "structure");
        var consistency = GetRequiredString(args, "consistency");
        var support = GetRequiredString(args, "support");
        var notes = ParseOptionalStringArray(args, "notes");

        return new TranscriptSummarizerMapSignalsOutput(structure, consistency, support, notes);
    }

    private static TranscriptSummarizerReduceOutput ParseReduceArguments(JsonElement args)
    {
        var inferredMainIdea = GetRequiredString(args, "inferred_main_idea_from_transcript");
        if (string.IsNullOrWhiteSpace(inferredMainIdea))
        {
            throw new JsonException("\"inferred_main_idea_from_transcript\" cannot be empty.");
        }

        return new TranscriptSummarizerReduceOutput(inferredMainIdea.Trim());
    }

    private static IReadOnlyList<string> ParseOptionalStringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return ParseStringArray(arr);
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement arr)
    {
        var list = new List<string>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    list.Add(text.Trim());
                }
            }
        }

        return list;
    }

    private static string GetRequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Required string property \"{property}\" was missing or not a string.");
        }

        return value.GetString() ?? string.Empty;
    }

    private static string? TryGetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static TranscriptSummarizerParseResult Failure(string error, string? rawContent)
    {
        return new TranscriptSummarizerParseResult(
            ToolName: null,
            MapRetelling: null,
            MapSignals: null,
            Reduce: null,
            Error: error,
            RawAssistantContent: rawContent);
    }
}
