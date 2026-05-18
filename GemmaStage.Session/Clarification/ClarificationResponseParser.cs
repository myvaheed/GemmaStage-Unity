using System;
using System.Collections.Generic;
using System.Text.Json;
using GemmaStage.Session.Inquirer;
using GemmaStage.Session.Resilience;

namespace GemmaStage.Session.Clarification;

public sealed record ClarificationParseResult(
    string? ToolName,
    ClarificationReviseOutput? Revise,
    ClarificationResolveOutput? Resolve,
    string? Error,
    string? RawAssistantContent) : IToolCallParseResult
{
    public bool IsRevise => Revise is not null;
    public bool IsResolve => Resolve is not null;
    public bool IsFailure => Error is not null;
}

public static class ClarificationResponseParser
{
    public static ClarificationParseResult Parse(string? responseJson)
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
                        ClarificationSchemas.ReviseToolName => new ClarificationParseResult(
                            ToolName: name,
                            Revise: ParseReviseArguments(args),
                            Resolve: null,
                            Error: null,
                            RawAssistantContent: rawContent),
                        ClarificationSchemas.ResolveToolName => new ClarificationParseResult(
                            ToolName: name,
                            Revise: null,
                            Resolve: ParseResolveArguments(args),
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

    private static ClarificationReviseOutput ParseReviseArguments(JsonElement args)
    {
        if (!args.TryGetProperty("questions", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("\"questions\" must be an array.");
        }

        var list = new List<RevisedQuestion>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
        {
            var id = GetRequiredInt(item, "id");
            var question = GetRequiredString(item, "question");
            if (string.IsNullOrWhiteSpace(question))
            {
                throw new JsonException($"Question id={id} has an empty question.");
            }

            var type = ParseConcernType(GetRequiredString(item, "type"));
            list.Add(new RevisedQuestion(id, question.Trim(), type));
        }

        return new ClarificationReviseOutput(list);
    }

    private static ClarificationResolveOutput ParseResolveArguments(JsonElement args)
    {
        if (!args.TryGetProperty("resolved_ids", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("\"resolved_ids\" must be an array.");
        }

        var list = new List<long>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number)
            {
                throw new JsonException("Each entry in \"resolved_ids\" must be an integer.");
            }

            list.Add(item.GetInt64());
        }

        return new ClarificationResolveOutput(list);
    }

    private static InquirerConcernType ParseConcernType(string raw)
    {
        return raw switch
        {
            ClarificationSchemas.ConcernTypeTopicUnknown => InquirerConcernType.TopicUnknown,
            ClarificationSchemas.ConcernTypeComprehensionGap => InquirerConcernType.ComprehensionGap,
            ClarificationSchemas.ConcernTypeDetailRequest => InquirerConcernType.DetailRequest,
            _ => throw new JsonException($"\"questions[].type\" had unsupported value \"{raw}\"."),
        };
    }

    private static string GetRequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Required string property \"{property}\" was missing or not a string.");
        }

        return value.GetString() ?? string.Empty;
    }

    private static long GetRequiredInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new JsonException($"Required integer property \"{property}\" was missing or not a number.");
        }

        return value.GetInt64();
    }

    private static string? TryGetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static ClarificationParseResult Failure(string error, string? rawContent)
    {
        return new ClarificationParseResult(
            ToolName: null,
            Revise: null,
            Resolve: null,
            Error: error,
            RawAssistantContent: rawContent);
    }
}
