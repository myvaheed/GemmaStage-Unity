using System.Text.Json;

namespace GemmaStage.Session.Inquirer;

public sealed record InquirerParseResult(
    string? ToolName,
    InquirerReflection? Reflection,
    string? Error,
    string? RawAssistantContent)
{
    public bool IsFailure => Error is not null;
}

public static class InquirerResponseParser
{
    public static InquirerParseResult Parse(string? responseJson)
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
                    "Assistant response did not include a tool call. The Inquirer system prompt requires exactly one tool call per turn.",
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
                if (name != InquirerSchemas.ReflectionToolName)
                {
                    return Failure($"Tool call referenced unknown function \"{name}\".", rawContent);
                }

                try
                {
                    return new InquirerParseResult(
                        ToolName: name,
                        Reflection: ParseReflection(arguments.RootElement),
                        Error: null,
                        RawAssistantContent: rawContent);
                }
                catch (JsonException ex)
                {
                    return Failure(
                        $"Tool call \"{name}\" arguments did not satisfy the Inquirer schema: {ex.Message}",
                        rawContent);
                }
            }
        }
    }

    private static InquirerReflection ParseReflection(JsonElement args)
    {
        var newConcerns = ParseNewConcerns(args);
        var removedConcerns = ParseRemovedConcerns(args);

        return new InquirerReflection(newConcerns, removedConcerns);
    }

    private static IReadOnlyList<InquirerConcern> ParseNewConcerns(JsonElement args)
    {
        if (!args.TryGetProperty("new_concerns", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Required array property \"new_concerns\" was missing or not an array.");
        }

        var items = new List<InquirerConcern>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Each item in \"new_concerns\" must be an object.");
            }

            var id = GetRequiredId(item, "id");
            var question = GetRequiredString(item, "question");
            if (string.IsNullOrWhiteSpace(question))
            {
                throw new JsonException("\"new_concerns[].question\" cannot be empty.");
            }

            var type = ParseConcernType(GetRequiredString(item, "type"));
            var typeReason = TryGetString(item, "type_reason");
            if (typeReason is not null && string.IsNullOrWhiteSpace(typeReason))
            {
                typeReason = null;
            }
            items.Add(new InquirerConcern(id, question, type, typeReason));
        }

        return items;
    }

    private static IReadOnlyList<RemovedConcern> ParseRemovedConcerns(JsonElement args)
    {
        if (!args.TryGetProperty("removed_concerns", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Required array property \"removed_concerns\" was missing or not an array.");
        }

        var items = new List<RemovedConcern>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Each item in \"removed_concerns\" must be an object.");
            }

            var id = GetRequiredId(item, "id");
            var causeRaw = GetRequiredString(item, "cause");
            var note = GetRequiredString(item, "note");
            if (string.IsNullOrWhiteSpace(note))
            {
                throw new JsonException("\"removed_concerns[].note\" cannot be empty.");
            }

            var cause = causeRaw switch
            {
                "resolved" => RemovedConcernCause.Resolved,
                "irrelevant" => RemovedConcernCause.Irrelevant,
                _ => throw new JsonException($"\"removed_concerns[].cause\" had unsupported value \"{causeRaw}\"."),
            };

            items.Add(new RemovedConcern(id, cause, note.Trim()));
        }

        return items;
    }

    private static long GetRequiredId(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var id))
        {
            throw new JsonException($"Required integer property \"{property}\" was missing or not an integer.");
        }

        if (id < 0)
        {
            throw new JsonException($"\"{property}\" cannot be negative.");
        }

        return id;
    }

    private static InquirerConcernType ParseConcernType(string raw)
    {
        return raw switch
        {
            InquirerSchemas.ConcernTypeTopicUnknown => InquirerConcernType.TopicUnknown,
            InquirerSchemas.ConcernTypeComprehensionGap => InquirerConcernType.ComprehensionGap,
            InquirerSchemas.ConcernTypeDetailRequest => InquirerConcernType.DetailRequest,
            _ => throw new JsonException($"\"new_concerns[].type\" had unsupported value \"{raw}\"."),
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

    private static string? TryGetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static InquirerParseResult Failure(string error, string? rawContent)
    {
        return new InquirerParseResult(
            ToolName: null,
            Reflection: null,
            Error: error,
            RawAssistantContent: rawContent);
    }
}
