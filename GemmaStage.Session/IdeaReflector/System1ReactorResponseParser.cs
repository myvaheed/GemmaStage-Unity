using System.Text.Json;

namespace GemmaStage.Session.IdeaReflector;

public static class System1ReactorResponseParser
{
    public static System1ReactorParseResult Parse(string? responseJson)
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

            if (name != IdeaReflectorSchemas.System1ReactorToolName)
            {
                return Failure(
                    $"Tool call referenced \"{name}\" but expected \"{IdeaReflectorSchemas.System1ReactorToolName}\".",
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

                    if (!args.TryGetProperty("topic", out var topicEl) || topicEl.ValueKind != JsonValueKind.String)
                    {
                        throw new JsonException("Required string property \"topic\" was missing or not a string.");
                    }
                    var topic = topicEl.GetString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(topic))
                    {
                        throw new JsonException("\"topic\" cannot be empty.");
                    }

                    if (!args.TryGetProperty("retelling", out var el) || el.ValueKind != JsonValueKind.String)
                    {
                        throw new JsonException("Required string property \"retelling\" was missing or not a string.");
                    }
                    var retelling = el.GetString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(retelling))
                    {
                        throw new JsonException("\"retelling\" cannot be empty.");
                    }

                    return new System1ReactorParseResult(
                        ToolName: name,
                        Output: new System1ReactorOutput(topic.Trim(), retelling.Trim()),
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

    private static System1ReactorParseResult Failure(string error, string? rawContent)
    {
        return new System1ReactorParseResult(
            ToolName: null,
            Output: null,
            Error: error,
            RawAssistantContent: rawContent);
    }
}
