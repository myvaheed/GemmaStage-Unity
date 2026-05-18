using System.Text.Json;
using GemmaStage.Session.Resilience;

namespace GemmaStage.Session.MainIdeaComparator;

public sealed record ParsedCoverage(CoverageLabel Coverage, string? Evidence);

public sealed record ParsedThesisComparison(string ThesisComparison);

public sealed record MainIdeaComparatorParseResult(
    string? ToolName,
    ParsedCoverage? Coverage,
    ParsedThesisComparison? Thesis,
    string? Error,
    string? RawAssistantContent) : IToolCallParseResult
{
    public bool IsCoverage => Coverage is not null;
    public bool IsThesis => Thesis is not null;
    public bool IsFailure => Error is not null;
}

public static class MainIdeaComparatorResponseParser
{
    public static MainIdeaComparatorParseResult Parse(string? responseJson)
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
                        MainIdeaComparatorSchemas.CoverageToolName => new MainIdeaComparatorParseResult(
                            ToolName: name,
                            Coverage: ParseCoverage(args),
                            Thesis: null,
                            Error: null,
                            RawAssistantContent: rawContent),
                        MainIdeaComparatorSchemas.ThesisToolName => new MainIdeaComparatorParseResult(
                            ToolName: name,
                            Coverage: null,
                            Thesis: ParseThesis(args),
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

    private static ParsedCoverage ParseCoverage(JsonElement args)
    {
        var coverageText = GetRequiredString(args, "coverage");
        if (!TryParseCoverageLabel(coverageText, out var coverage))
        {
            throw new JsonException($"Unknown coverage label \"{coverageText}\".");
        }

        string? evidence = null;
        if (args.TryGetProperty("evidence", out var ev) && ev.ValueKind == JsonValueKind.String)
        {
            var text = ev.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                evidence = text.Trim();
            }
        }

        return new ParsedCoverage(coverage, evidence);
    }

    private static ParsedThesisComparison ParseThesis(JsonElement args)
    {
        var comparison = GetRequiredString(args, "thesis_comparison");
        if (string.IsNullOrWhiteSpace(comparison))
        {
            throw new JsonException("\"thesis_comparison\" cannot be empty.");
        }
        return new ParsedThesisComparison(comparison.Trim());
    }

    private static bool TryParseCoverageLabel(string text, out CoverageLabel label)
    {
        switch (text)
        {
            case "yes":
                label = CoverageLabel.Yes;
                return true;
            case "partial":
                label = CoverageLabel.Partial;
                return true;
            case "no":
                label = CoverageLabel.No;
                return true;
            default:
                label = default;
                return false;
        }
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

    private static MainIdeaComparatorParseResult Failure(string error, string? rawContent)
    {
        return new MainIdeaComparatorParseResult(
            ToolName: null,
            Coverage: null,
            Thesis: null,
            Error: error,
            RawAssistantContent: rawContent);
    }
}
