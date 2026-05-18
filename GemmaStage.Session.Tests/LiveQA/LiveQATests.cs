using GemmaStage.Session;
using GemmaStage.Session.Inquirer;
using GemmaStage.Session.LiveQA;

namespace GemmaStage.Session.Tests.LiveQA;

internal static class LiveQAConcernPickerTests
{
    public static void Run()
    {
        AssertEx.True(LiveQAConcernPicker.PickRandom(Array.Empty<InquirerConcern>()) is null,
            "Empty open set should yield null so callers can short-circuit");

        var single = new[]
        {
            new InquirerConcern(7, "Why?", InquirerConcernType.ComprehensionGap),
        };
        var onlyOption = LiveQAConcernPicker.PickRandom(single);
        AssertEx.True(onlyOption is not null && onlyOption.Id == 7, "Single-element set picks that element");

        var concerns = new[]
        {
            new InquirerConcern(1, "What is the topic?", InquirerConcernType.TopicUnknown),
            new InquirerConcern(2, "Define the term", InquirerConcernType.ComprehensionGap),
            new InquirerConcern(3, "Show an example", InquirerConcernType.DetailRequest),
        };

        // Seeded RNG → deterministic pick. Ensures the picker round-trips ids
        // out of the supplied list without inventing or skipping.
        var seeded = LiveQAConcernPicker.PickRandom(concerns, new Random(42));
        AssertEx.True(seeded is not null, "Picker must return a concern when the set is non-empty");
        AssertEx.True(concerns.Any(c => c.Id == seeded!.Id),
            "Picked concern must come from the supplied list (no fabrication)");

        // Distribution sanity: across many seeded picks every id should appear.
        var rng = new Random(0);
        var seen = new HashSet<long>();
        for (int i = 0; i < 200; i++)
        {
            var pick = LiveQAConcernPicker.PickRandom(concerns, rng);
            seen.Add(pick!.Id);
        }
        AssertEx.Equal(3, seen.Count, "Random picker should reach every concern across many draws");
    }
}

internal static class OpenConcernsEventTests
{
    public static void Run()
    {
        var concerns = new[]
        {
            new InquirerConcern(1, "What is the topic?", InquirerConcernType.TopicUnknown),
        };
        var payload = new OpenConcernsEvent(CycleIndex: 3, OpenConcerns: concerns);

        AssertEx.Equal(3, payload.CycleIndex, "CycleIndex round-trips");
        AssertEx.Equal(1, payload.OpenConcerns.Count, "OpenConcerns list round-trips");

        // Empty payload is a valid signal — game decides whether to surface a
        // panel based on Live Q&A toggle + non-empty set.
        var empty = new OpenConcernsEvent(CycleIndex: 4, OpenConcerns: Array.Empty<InquirerConcern>());
        AssertEx.Equal(0, empty.OpenConcerns.Count, "Empty open set is a valid event payload");
    }
}

internal static class LiveQARoundConstantsTests
{
    public static void Run()
    {
        AssertEx.Equal(
            "Hope I answered your question",
            LiveQARound.ClosingMarkerText,
            "Closing marker text must match the literal sentinel from SESSION_ARCHITECTURE.md §9");
    }
}
