using System;
using System.Collections.Generic;
using System.Text.Json;

namespace GemmaStage.Session.Perceptor;

public static class I2tResponseParser
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
                    "Assistant response did not include a tool call. The I2T system prompt requires exactly one tool call per turn.",
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

            if (name != I2tSchemas.ImageToolName)
            {
                return Failure(
                    $"Tool call referenced \"{name}\" but I2T turn must call \"{I2tSchemas.ImageToolName}\".",
                    rawContent);
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
                return Failure(
                    $"Tool call \"{name}\" arguments were not valid JSON: {ex.Message}",
                    rawContent);
            }

            using (arguments)
            {
                try
                {
                    var image = ParseImageArguments(arguments.RootElement);
                    return new PerceptorParseResult(
                        ToolName: name,
                        Audio: null,
                        Image: image,
                        Error: null,
                        RawAssistantContent: rawContent);
                }
                catch (JsonException ex)
                {
                    return Failure(
                        $"Tool call \"{name}\" arguments did not satisfy the I2T schema: {ex.Message}",
                        rawContent);
                }
            }
        }
    }

    private static PerceptorImageOutput ParseImageArguments(JsonElement args)
    {
        var examination = GetRequiredString(args, "examination");
        var notes = ParseOptionalStringArray(args, "notes");
        return new PerceptorImageOutput(examination, notes);
    }

    private static string GetRequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Required string property \"{property}\" was missing or not a string.");
        }

        return value.GetString() ?? string.Empty;
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
