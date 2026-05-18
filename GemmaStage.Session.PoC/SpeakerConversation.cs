using System.Text.Json;
using GemmaStage.Session.Native;

namespace GemmaStage.Session.PoC;

// PoC-only. Simulates the human speaker answering audience questions during
// Live and Final Q&A rounds. The model is grounded on the same ground-truth
// document the GroundTruthSummarizer consumes, and is told to answer "I don't
// know" when the material does not cover the question. The PoC injects each
// answer text into the session's transcript + raw-content stores as if ASR
// had transcribed real spoken audio.
internal sealed class SpeakerConversation : IDisposable
{
    private const string SystemPromptTemplate = """
        You are the speaker of a presentation. You just finished saying things on the topic, and a member of the audience is asking you a question. Answer the question concisely (1-4 short sentences) from the source material below, in the speaker's first-person register.

        Source material you prepared the talk from:
        ===
        {ground_truth}
        ===

        Rules:
          - Always call exactly one tool: report_speaker_answer.
          - Produce one field "answer".
          - If the source material covers the question, answer in your own words from it.
          - If the source material does not cover the question, say so plainly (e.g. "I don't know - my notes don't cover that.").
          - Never invent facts that are not in the source material.
          - Keep "answer" to plain natural speech, no bullet lists, no markdown.
        """;

    private const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_speaker_answer",
              "description": "Return the speaker's spoken answer to an audience question.",
              "parameters": {
                "type": "object",
                "additionalProperties": false,
                "required": ["answer"],
                "properties": {
                  "answer": { "type": "string" }
                }
              }
            }
          }
        ]
        """;

    private readonly EngineHandle _engine;
    private readonly ConversationConfigHandle _config;
    private readonly Action<string>? _warn;
    private bool _disposed;

    public SpeakerConversation(EngineHandle engine, string groundTruthText, Action<string>? warn = null)
    {
        if (engine is null) throw new ArgumentNullException(nameof(engine));
        if (string.IsNullOrWhiteSpace(groundTruthText))
            throw new ArgumentException("Ground-truth text is required for the simulated speaker.", nameof(groundTruthText));
        _engine = engine;
        _warn = warn;

        _config = GemmaStageNative.ConversationConfigCreate(
            engine,
            SystemPromptTemplate.Replace("{ground_truth}", groundTruthText.Trim()),
            ToolsJson,
            enableConstrainedDecoding: true);
        GemmaStageNative.ConversationConfigSetEnableThinking(_config, false);
    }

    // Returns the answer text. On total failure returns a deterministic
    // fallback so the PoC Q&A flow still produces a transcript span instead of
    // crashing the run.
    public string AnswerQuestion(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Question cannot be empty.", nameof(question));
        ThrowIfDisposed();

        var userPrompt = $"Audience question: {question.Trim()}\n\nAnswer the question per the rules.";

        try
        {
            using var slot = _engine.AcquireConversationSlot();
            using var conversation = GemmaStageNative.ConversationCreate(_engine, _config);
            using var response = GemmaStageNative.ConversationSendText(conversation, userPrompt);
            var json = GemmaStageNative.JsonResponseGetString(response);
            var answer = ExtractAnswer(json);
            if (!string.IsNullOrWhiteSpace(answer))
            {
                return answer.Trim();
            }
            _warn?.Invoke($"Speaker: could not extract answer from response: {Truncate(json, 240)}");
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"Speaker: answer failed: {ex.Message}");
        }

        return "I don't know - my notes don't cover that.";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _config.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static string? ExtractAnswer(string? responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in toolCalls.EnumerateArray())
                {
                    if (TryReadAnswerFromCall(call, out var fromCall)) return fromCall;
                }
            }

            if (root.TryGetProperty("message", out var message) &&
                message.TryGetProperty("tool_calls", out var msgToolCalls) &&
                msgToolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in msgToolCalls.EnumerateArray())
                {
                    if (TryReadAnswerFromCall(call, out var fromCall)) return fromCall;
                }
            }

            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                var text = content.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        catch (JsonException)
        {
            // fall through
        }
        return null;
    }

    private static bool TryReadAnswerFromCall(JsonElement call, out string? answer)
    {
        answer = null;
        if (call.ValueKind != JsonValueKind.Object) return false;

        JsonElement argsElement;
        if (call.TryGetProperty("function", out var fn) && fn.TryGetProperty("arguments", out var fnArgs))
        {
            argsElement = fnArgs;
        }
        else if (!call.TryGetProperty("arguments", out argsElement))
        {
            return false;
        }

        if (argsElement.ValueKind == JsonValueKind.String)
        {
            var str = argsElement.GetString();
            if (string.IsNullOrWhiteSpace(str)) return false;
            try
            {
                using var inner = JsonDocument.Parse(str);
                if (inner.RootElement.TryGetProperty("answer", out var a) && a.ValueKind == JsonValueKind.String)
                {
                    answer = a.GetString();
                    return !string.IsNullOrWhiteSpace(answer);
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }
        else if (argsElement.ValueKind == JsonValueKind.Object &&
                 argsElement.TryGetProperty("answer", out var a) &&
                 a.ValueKind == JsonValueKind.String)
        {
            answer = a.GetString();
            return !string.IsNullOrWhiteSpace(answer);
        }

        return false;
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s.Substring(0, max) + "...";
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SpeakerConversation));
    }
}
