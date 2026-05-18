using System.Text;
using GemmaStage.Session.Prompts;

namespace GemmaStage.Session.Inquirer;

internal sealed record InquirerTurnContext(
    string Prompt,
    IReadOnlyList<long> ReservedConcernIds,
    HashSet<long> CurrentConcernIds);

internal sealed record InquirerApplyResult(
    InquirerStateSnapshot State,
    IReadOnlyList<ArchivedConcern> ArchivedConcerns);

internal sealed class InquirerStateTracker
{
    private readonly List<InquirerConcern> _openConcerns;
    private readonly int _concernCapacity;

    public InquirerStateTracker(
        IEnumerable<InquirerConcern>? openConcerns = null,
        int concernCapacity = InquirerSchemas.ConcernMaxItems)
    {
        if (concernCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(concernCapacity), concernCapacity, "Concern capacity must be at least 1.");
        }

        _concernCapacity = concernCapacity;

        _openConcerns = openConcerns?
            .Select(static concern => new InquirerConcern(
                concern.Id,
                NormalizeRequiredText(concern.Question, nameof(openConcerns)),
                concern.Type))
            .ToList()
            ?? new List<InquirerConcern>();

        if (_openConcerns.Any(static concern => concern.Id < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(openConcerns), "Concern ids cannot be negative.");
        }

        if (_openConcerns.GroupBy(static concern => concern.Id).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Concern ids must be unique.", nameof(openConcerns));
        }

        _nextConcernId = _openConcerns.Count == 0 ? 0 : _openConcerns.Max(static concern => concern.Id) + 1;
        TrimOverflow();
    }

    public InquirerConfusionScore? ConfusionScore { get; private set; }

    public int ConcernCapacity => _concernCapacity;

    public long NextConcernId => _nextConcernId;

    public IReadOnlyList<InquirerConcern> OpenConcerns => _openConcerns.ToArray();

    private long _nextConcernId;

    public InquirerTurnContext PrepareTurn(
        string? topic,
        string? mainIdeaUnderstanding,
        IReadOnlyList<string> retellings,
        int reservationCount = InquirerSchemas.NewConcernReservationCount,
        bool restricted = false)
    {
        Guard.NotNull(retellings);

        if (reservationCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reservationCount), reservationCount, "Reservation count must be positive.");
        }

        EnsureTopicConcern(topic);

        var reservedIds = Enumerable.Range(0, reservationCount)
            .Select(index => checked(_nextConcernId + index))
            .ToArray();

        var currentIds = _openConcerns.Select(static concern => concern.Id).ToHashSet();

        return new InquirerTurnContext(
            BuildPrompt(topic, mainIdeaUnderstanding, retellings, reservedIds, restricted),
            reservedIds,
            currentIds);
    }

    public InquirerApplyResult ApplyReflection(
        InquirerReflection reflection,
        InquirerTurnContext turn,
        string? topic,
        string? mainIdeaUnderstanding,
        Action<string>? warn = null)
    {
        Guard.NotNull(reflection);
        Guard.NotNull(turn);

        ValidateReflectionIds(reflection, turn);

        if (reflection.RemovedConcerns.Count > 0)
        {
            var removedIds = reflection.RemovedConcerns.Select(static concern => concern.Id).ToHashSet();
            _openConcerns.RemoveAll(concern => removedIds.Contains(concern.Id));
        }

        if (reflection.NewConcerns.Count > 0)
        {
            _openConcerns.InsertRange(0, reflection.NewConcerns);
        }

        if (turn.ReservedConcernIds.Count > 0)
        {
            _nextConcernId = Math.Max(_nextConcernId, turn.ReservedConcernIds[^1] + 1);
        }

        EnsureTopicConcern(topic, warn);
        var archived = TrimOverflow();
        ConfusionScore = DeriveConfusionScore(_openConcerns);

        return new InquirerApplyResult(Snapshot(topic, mainIdeaUnderstanding), archived);
    }

    // Confusion is derived from the live open-concern set after each reflection
    // is applied (and after overflow archiving). The thresholds below assume a
    // DeriveConfusionScore thresholds assume ConcernMaxItems cap of 10.
    public static InquirerConfusionScore DeriveConfusionScore(IReadOnlyList<InquirerConcern> openConcerns)
    {
        Guard.NotNull(openConcerns);

        var topicUnknown = 0;
        var comprehensionGap = 0;
        foreach (var concern in openConcerns)
        {
            switch (concern.Type)
            {
                case InquirerConcernType.TopicUnknown:
                    topicUnknown++;
                    break;
                case InquirerConcernType.ComprehensionGap:
                    comprehensionGap++;
                    break;
            }
        }

        if (topicUnknown > 0)
        {
            return InquirerConfusionScore.VeryHigh;
        }
        if (comprehensionGap >= 3)
        {
            return InquirerConfusionScore.High;
        }
        if (comprehensionGap >= 1)
        {
            return InquirerConfusionScore.Medium;
        }
        return InquirerConfusionScore.Low;
    }

    public InquirerStateSnapshot Snapshot(string? topic, string? mainIdeaUnderstanding)
    {
        return new InquirerStateSnapshot(
            topic,
            mainIdeaUnderstanding,
            ConfusionScore,
            _openConcerns.ToArray());
    }

    public IReadOnlyList<ArchivedConcern> ArchiveOpenConcerns()
    {
        var archived = _openConcerns
            .Select(static concern => new ArchivedConcern(concern.Id, concern.Question, concern.Type))
            .ToArray();

        _openConcerns.Clear();
        return archived;
    }

    public void LoadOpenConcerns(IEnumerable<InquirerConcern> concerns)
    {
        Guard.NotNull(concerns);

        var list = concerns
            .Select(static c => new InquirerConcern(
                c.Id,
                NormalizeRequiredText(c.Question, nameof(concerns)),
                c.Type))
            .ToList();

        if (list.Any(static c => c.Id < 0))
            throw new ArgumentOutOfRangeException(nameof(concerns), "Concern ids cannot be negative.");

        if (list.GroupBy(static c => c.Id).Any(static g => g.Count() > 1))
            throw new ArgumentException("Concern ids must be unique.", nameof(concerns));

        _openConcerns.Clear();
        _openConcerns.AddRange(list);

        if (_openConcerns.Count > 0)
            _nextConcernId = Math.Max(_nextConcernId, _openConcerns.Max(static c => c.Id) + 1);

        ConfusionScore = DeriveConfusionScore(_openConcerns);
    }

    private void ValidateReflectionIds(InquirerReflection reflection, InquirerTurnContext turn)
    {
        var removedIds = new HashSet<long>();
        var newIds = new HashSet<long>();
        var reservedIds = turn.ReservedConcernIds.ToHashSet();

        foreach (var removedConcern in reflection.RemovedConcerns)
        {
            if (!turn.CurrentConcernIds.Contains(removedConcern.Id))
            {
                throw new InvalidOperationException(
                    $"Removed concern id {removedConcern.Id} was not present in the current open-concern set.");
            }

            if (!removedIds.Add(removedConcern.Id))
            {
                throw new InvalidOperationException($"Removed concern id {removedConcern.Id} was repeated.");
            }
        }

        foreach (var concern in reflection.NewConcerns)
        {
            if (!reservedIds.Contains(concern.Id))
            {
                throw new InvalidOperationException(
                    $"New concern id {concern.Id} was not in the reserved id set.");
            }

            if (!newIds.Add(concern.Id))
            {
                throw new InvalidOperationException($"New concern id {concern.Id} was repeated.");
            }

            if (turn.CurrentConcernIds.Contains(concern.Id))
            {
                throw new InvalidOperationException(
                    $"New concern id {concern.Id} cannot reuse an existing live concern id.");
            }
        }
    }

    private string BuildPrompt(
        string? topic,
        string? mainIdeaUnderstanding,
        IReadOnlyList<string> retellings,
        IReadOnlyList<long> reservedIds,
        bool restricted)
    {
        return InquirerPrompts.UserTemplate
            .Replace("{retellings_list}", BuildRetellingsList(retellings))
            .Replace("{topic}", string.IsNullOrWhiteSpace(topic) ? "unknown" : topic)
            .Replace("{main_idea_understanding}", string.IsNullOrWhiteSpace(mainIdeaUnderstanding) ? "unknown" : mainIdeaUnderstanding)
            .Replace("{open_concerns_list}", BuildOpenConcernsList())
            .Replace("{reserved_ids_list}", BuildReservedIdsList(reservedIds))
            .Replace("{restriction_rules}", restricted ? InquirerPrompts.RestrictedRulesBlock : InquirerPrompts.NormalRulesBlock)
            .TrimEnd();
    }

    private static string BuildRetellingsList(IReadOnlyList<string> retellings)
    {
        if (retellings.Count == 0)
        {
            return "  (none — this is the first cycle)";
        }

        var sb = new StringBuilder();
        for (int i = 0; i < retellings.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            sb.Append("  [#").Append(i + 1).Append("] ").Append(retellings[i].Trim());
        }
        return sb.ToString();
    }

    private string BuildOpenConcernsList()
    {
        if (_openConcerns.Count == 0)
        {
            return "  - none";
        }

        var sb = new StringBuilder();
        for (int i = 0; i < _openConcerns.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            var concern = _openConcerns[i];
            sb.Append("  - [").Append(concern.Id).Append("] (").Append(TypeWireString(concern.Type)).Append(") ").Append(concern.Question);
        }
        return sb.ToString();
    }

    private static string BuildReservedIdsList(IReadOnlyList<long> reservedIds)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < reservedIds.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            sb.Append("  - ").Append(reservedIds[i]);
        }
        return sb.ToString();
    }

    private void EnsureTopicConcern(string? topic, Action<string>? warn = null)
    {
        if (!string.IsNullOrWhiteSpace(topic))
        {
            return;
        }

        if (_openConcerns.Any(static concern => IsTopicConcern(concern.Question)))
        {
            return;
        }

        warn?.Invoke("Inquirer topic remains unknown; reinserting the canonical topic concern.");
        _openConcerns.Insert(
            0,
            new InquirerConcern(_nextConcernId++, InquirerSchemas.TopicConcernQuestion, InquirerConcernType.TopicUnknown));
    }

    private List<ArchivedConcern> TrimOverflow()
    {
        var archived = new List<ArchivedConcern>();
        while (_openConcerns.Count > _concernCapacity)
        {
            var last = _openConcerns[^1];
            _openConcerns.RemoveAt(_openConcerns.Count - 1);
            archived.Add(new ArchivedConcern(last.Id, last.Question, last.Type));
        }

        return archived;
    }

    private static bool IsTopicConcern(string question)
    {
        return question.Contains("topic", StringComparison.OrdinalIgnoreCase);
    }

    private static string TypeWireString(InquirerConcernType type) => type switch
    {
        InquirerConcernType.TopicUnknown => InquirerSchemas.ConcernTypeTopicUnknown,
        InquirerConcernType.ComprehensionGap => InquirerSchemas.ConcernTypeComprehensionGap,
        InquirerConcernType.DetailRequest => InquirerSchemas.ConcernTypeDetailRequest,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown concern type."),
    };

    private static string NormalizeRequiredText(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Concern text cannot be empty.", paramName);
        }

        return value.Trim();
    }
}
