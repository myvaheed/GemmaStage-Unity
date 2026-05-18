using GemmaStage.Session.Inquirer;

namespace GemmaStage.Session.LiveQA;

// Constants and helpers for the On-Live Q&A round. The session module owns the
// state transitions and marker text; the game owns concern selection and the
// UI panel. The picker below is a small default; callers can substitute a
// smarter algorithm without touching the runtime.
public static class LiveQARound
{
    // The canonical closing marker text appended to the raw content buffer when
    // the user finishes answering a live Q&A round. Per
    // docs/SESSION_ARCHITECTURE.md Sec. 9 this is the literal string the runtime
    // writes; the next cognitive cycle reads it as the round terminator.
    public const string ClosingMarkerText = "Hope I answered your question";
}

public static class LiveQAConcernPicker
{
    private static readonly Random SharedRandom = new();

    // Picks one concern from the open set uniformly at random. Returns null
    // when the set is empty so callers can short-circuit (no concern -> nothing
    // for the user to answer). Pass a seeded Random for deterministic tests.
    public static InquirerConcern? PickRandom(
        IReadOnlyList<InquirerConcern> openConcerns,
        Random? rng = null)
    {
        Guard.NotNull(openConcerns);
        if (openConcerns.Count == 0)
        {
            return null;
        }

        var picker = rng ?? SharedRandom;
        return openConcerns[picker.Next(openConcerns.Count)];
    }
}
