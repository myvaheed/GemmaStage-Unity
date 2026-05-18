using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using GemmaStage.Session.Resilience;

namespace GemmaStage.Session.GroundTruthSummarizer;

public sealed record GroundTruthSummarizerParseResult(
    string? ToolName,
    GroundTruthSummarizerMapOutput? Map,
    GroundTruthSummarizerOutput? Reduce,
    string? Error,
    string? RawAssistantContent) : IToolCallParseResult
{
    public bool IsMap => Map is not null;
    public bool IsReduce => Reduce is not null;
    public bool IsFailure => Error is not null;
}

public static class GroundTruthSummarizerResponseParser
{
    public static GroundTruthSummarizerParseResult Parse(string? responseJson)
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
            catch (JsonException)
            {
                // Gemma frequently emits raw newlines / tabs inside JSON string values
                // (notably the REDUCE "claims" field, which is *designed* to be newline-
                // separated). System.Text.Json rejects unescaped control chars per spec.
                // Escape them and retry once before giving up.
                try
                {
                    arguments = JsonDocument.Parse(EscapeControlCharsInJsonStrings(argumentsRaw));
                }
                catch (JsonException ex2)
                {
                    return Failure($"Tool call \"{name}\" arguments were not valid JSON: {ex2.Message}", rawContent);
                }
            }

            using (arguments)
            {
                var args = arguments.RootElement;
                try
                {
                    return name switch
                    {
                        GroundTruthSummarizerSchemas.MapToolName => new GroundTruthSummarizerParseResult(
                            ToolName: name,
                            Map: ParseMapArguments(args),
                            Reduce: null,
                            Error: null,
                            RawAssistantContent: rawContent),
                        GroundTruthSummarizerSchemas.ReduceToolName => new GroundTruthSummarizerParseResult(
                            ToolName: name,
                            Map: null,
                            Reduce: ParseReduceArguments(args),
                            Error: null,
                            RawAssistantContent: rawContent),
                        _ => Failure($"Unknown tool \"{name}\".", rawContent),
                    };
                }
                catch (JsonException ex)
                {
                    return Failure($"Tool call \"{name}\" arguments did not satisfy schema: {ex.Message}", rawContent);
                }
            }
        }
    }

    private static GroundTruthSummarizerMapOutput ParseMapArguments(JsonElement args)
    {
        var claims = ParseClaimArray(args, "claims");
        return new GroundTruthSummarizerMapOutput(claims);
    }

    private static GroundTruthSummarizerOutput ParseReduceArguments(JsonElement args)
    {
        var mainThesis = GetRequiredString(args, "main_thesis");
        if (string.IsNullOrWhiteSpace(mainThesis))
            throw new JsonException("\"main_thesis\" cannot be empty.");

        var claimsText = GetRequiredString(args, "claims");
        var claims = SplitClaimsText(claimsText);
        if (claims.Count == 0)
            throw new JsonException("\"claims\" produced no non-empty lines.");

        return new GroundTruthSummarizerOutput(mainThesis.Trim(), claims);
    }

    private static IReadOnlyList<string> SplitClaimsText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new JsonException("\"claims\" cannot be empty.");

        var lines = text.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
        var items = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var claim = line.Trim();
            if (!string.IsNullOrEmpty(claim))
                items.Add(claim);
        }
        return items;
    }

    private static IReadOnlyList<string> ParseClaimArray(JsonElement args, string property)
    {
        if (!args.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"Required array property \"{property}\" was missing or not an array.");
        }

        var items = new List<string>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"Each item in \"{property}\" must be a string.");
            }
            var claim = item.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(claim))
            {
                throw new JsonException($"\"{property}[]\" entries cannot be empty.");
            }
            items.Add(claim.Trim());
        }
        return items;
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

    // Walks the JSON text and replaces raw control chars (LF/CR/TAB and other
    // U+0000..U+001F bytes) with their JSON escape sequences, but only when
    // they occur INSIDE a string value. Unescaped string delimiters and
    // escape backslashes outside strings are left untouched.
    private static string EscapeControlCharsInJsonStrings(string json)
    {
        var sb = new StringBuilder(json.Length + 16);
        bool inString = false;
        bool escaped = false;
        foreach (var c in json)
        {
            if (!inString)
            {
                sb.Append(c);
                if (c == '"') inString = true;
                continue;
            }

            if (escaped)
            {
                sb.Append(c);
                escaped = false;
                continue;
            }

            switch (c)
            {
                case '\\':
                    sb.Append(c);
                    escaped = true;
                    break;
                case '"':
                    sb.Append(c);
                    inString = false;
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u00").Append(((int)c).ToString("X2"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    private static GroundTruthSummarizerParseResult Failure(string error, string? rawContent)
    {
        return new GroundTruthSummarizerParseResult(
            ToolName: null,
            Map: null,
            Reduce: null,
            Error: error,
            RawAssistantContent: rawContent);
    }
}
