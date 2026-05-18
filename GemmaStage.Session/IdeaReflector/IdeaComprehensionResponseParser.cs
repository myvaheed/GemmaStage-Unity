using System.Text.Json;

namespace GemmaStage.Session.IdeaReflector;

public static class IdeaComprehensionResponseParser
{
    public static IdeaComprehensionParseResult Parse(string? responseJson)
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

            if (name != IdeaReflectorSchemas.IdeaComprehensionToolName)
            {
                return Failure(
                    $"Tool call referenced \"{name}\" but expected \"{IdeaReflectorSchemas.IdeaComprehensionToolName}\".",
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
                return Failure($"Tool call \"{name}\" arguments were not valid JSON: {ex.Message}", rawContent);
            }

            using (arguments)
            {
                try
                {
                    var args = arguments.RootElement;
                    if (!args.TryGetProperty("thesis", out var thesisEl) || thesisEl.ValueKind != JsonValueKind.String)
                    {
                        throw new JsonException("Required string property \"thesis\" was missing or not a string.");
                    }
                    var thesis = thesisEl.GetString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(thesis))
                    {
                        throw new JsonException("\"thesis\" cannot be empty.");
                    }

                    return new IdeaComprehensionParseResult(
                        ToolName: name,
                        Output: new IdeaComprehensionOutput(thesis.Trim()),
                        Error: null,
                        RawAssistantContent: rawContent);
                }
                catch (JsonException ex)
                {
                    return Failure($"Tool call \"{name}\" arguments did not satisfy schema: {ex.Message}", rawContent);
                }
            }
        }
    }

    private static string? TryGetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return value.GetString();
    }

    private static IdeaComprehensionParseResult Failure(string error, string? rawContent)
    {
        return new IdeaComprehensionParseResult(
            ToolName: null,
            Output: null,
            Error: error,
            RawAssistantContent: rawContent);
    }
}
