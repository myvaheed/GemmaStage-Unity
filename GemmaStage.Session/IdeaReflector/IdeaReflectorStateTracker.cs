using System.Text;

namespace GemmaStage.Session.IdeaReflector;

// Live state. Plain-text main_idea_understanding is enriched cumulatively by
// System2 each cycle; topic and thesis are scalar labels (thesis is set only
// at end-of-live by IdeaComprehension).
internal sealed class IdeaReflectorStateTracker
{
    private string? _topic;
    private string _thesis = string.Empty;
    private string _mainIdeaUnderstanding = string.Empty;

    public IdeaReflectorStateTracker(string? topic = null)
    {
        _topic = NormalizeTopic(topic);
    }

    public string? Topic => _topic;

    public string Thesis => _thesis;

    public string MainIdeaText => _mainIdeaUnderstanding;

    // Rendered audience-side recall used by Inquirer's prompt, I2T's per-image
    // context, and DeepDive prefill. Single text block: topic line + thesis
    // line + main_idea_understanding body.
    public string MainIdeaUnderstanding => RenderRecall();

    public AudienceSideRecall AudienceRecall => new(_topic, _thesis, _mainIdeaUnderstanding);

    public void SetTopic(string? topic)
    {
        _topic = NormalizeTopic(topic);
    }

    public void SetThesis(string thesis)
    {
        _thesis = string.IsNullOrWhiteSpace(thesis) ? string.Empty : thesis.Trim();
    }

    public void SetMainIdeaUnderstanding(string mainIdea)
    {
        _mainIdeaUnderstanding = string.IsNullOrWhiteSpace(mainIdea) ? string.Empty : mainIdea.Trim();
    }

    private string RenderRecall()
    {
        var sb = new StringBuilder();
        sb.Append("Topic: ").AppendLine(_topic ?? "unknown");
        sb.Append("Thesis: ").AppendLine(string.IsNullOrEmpty(_thesis) ? "(not yet established)" : _thesis);
        sb.Append("Main idea: ");
        sb.Append(string.IsNullOrEmpty(_mainIdeaUnderstanding) ? "(empty)" : _mainIdeaUnderstanding);
        return sb.ToString();
    }

    private static string? NormalizeTopic(string? topic)
    {
        var normalized = NormalizeText(topic);
        if (normalized is null)
        {
            return null;
        }
        return normalized.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;
    }

    private static string? NormalizeText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
