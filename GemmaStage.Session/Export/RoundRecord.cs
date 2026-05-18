namespace GemmaStage.Session.Export;

// Record of a single Q&A round (live or final). Pairs the speaker-produced
// answer text with the round's lifecycle timestamps so the XLSX export can
// present a "Q&A Rounds" sheet that joins QaRoundsHistory entries
// (concern id, phase, opened/closed) with the answer the speaker actually
// gave. Lives in the module so both the PoC and the Unity SessionLayer
// can construct the same shape.
public sealed record RoundRecord(
    string Phase,
    int RoundIndex,
    long ConcernId,
    string Question,
    string ConcernType,
    string Answer,
    DateTimeOffset OpenedAt,
    DateTimeOffset ClosedAt);
