using System.Text.RegularExpressions;

namespace GemmaStage.Session.IdeaReflector;

// Canonicalizes a free-text domain key into a stable lookup string. Used by
// IdeaReflectorStateTracker (audience-side) and GroundTruthSummarizer
// (anchor-side) so set-diff in the comparator works on the same form.
public static class DomainCanonicalizer
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static string Canonicalize(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return string.Empty;
        }

        var trimmed = domain.Trim().ToLowerInvariant();
        return Whitespace.Replace(trimmed, " ");
    }

    public static string NormalizeDisplay(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return string.Empty;
        }

        return Whitespace.Replace(domain.Trim(), " ");
    }
}
