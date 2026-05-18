using System.Text.Json;

namespace GemmaStage.Session.IdeaReflector;

public static class System2ReflectorResponseParser
{
    public static System2ReflectorParseResult Parse(string? responseJson)
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

            if (name != IdeaReflectorSchemas.System2ReflectorToolName)
            {
                return Failure(
                    $"Tool call referenced \"{name}\" but expected \"{IdeaReflectorSchemas.System2ReflectorToolName}\".",
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

                    var topic = GetRequiredString(args, "topic");
                    if (string.IsNullOrWhiteSpace(topic))
                    {
                        throw new JsonException("\"topic\" cannot be empty.");
                    }

                    var mainIdea = GetRequiredString(args, "main_idea_understanding");
                    if (string.IsNullOrWhiteSpace(mainIdea))
                    {
                        throw new JsonException("\"main_idea_understanding\" cannot be empty.");
                    }

                    return new System2ReflectorParseResult(
                        ToolName: name,
                        Output: new System2ReflectorOutput(topic.Trim(), mainIdea.Trim()),
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

    private static System2ReflectorParseResult Failure(string error, string? rawContent)
    {
        return new System2ReflectorParseResult(
            ToolName: null,
            Output: null,
            Error: error,
            RawAssistantContent: rawContent);
    }
}
