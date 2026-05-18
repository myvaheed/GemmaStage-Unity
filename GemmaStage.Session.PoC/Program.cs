using GemmaStage.Session;
using GemmaStage.Session.Audio;
using GemmaStage.Session.Clarification;
using GemmaStage.Session.DeepDive;
using GemmaStage.Session.Export;
using GemmaStage.Session.GroundTruthSummarizer;
using GemmaStage.Session.Inquirer;
using GemmaStage.Session.Perceptor;
using GemmaStage.Session.PoC;
using GemmaStage.Session.Stores;
using GemmaStage.Session.TranscriptSummarizer;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;

if (args.Length == 0 || args.Contains("--help"))
{
    PocOptions.PrintUsage();
    return 0;
}

PocOptions options;
try
{
    options = PocOptions.Parse(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Invalid arguments: {ex.Message}");
    PocOptions.PrintUsage();
    return 2;
}

Console.WriteLine("GemmaStage Session PoC");
Console.WriteLine($"  WAV:     {options.WavPath}");
Console.WriteLine($"  Model:   {options.ModelPath}");
Console.WriteLine($"  Mmproj:  {options.MmprojPath ?? "<none>"}");
Console.WriteLine($"  Backend: {options.Backend ?? "auto"}");
Console.WriteLine($"  Chunker: X={options.X}s Y={options.Y}ms C={options.C}ms threshold={options.ThresholdDb}dB");
Console.WriteLine($"  Cycle:   min_elapsed={options.DebriefMinSeconds}s");
Console.WriteLine($"  Output:  {options.OutDir}");
Console.WriteLine($"  GT:      {options.GroundTruthPath ?? "<none>"}");
Console.WriteLine($"  LiveQA:  {(options.EnableLiveQA ? "enabled" : "disabled")}");
Console.WriteLine($"  FinalQA: {(options.EnableFinalQA ? "enabled" : "disabled")}");
Console.WriteLine();

if (!File.Exists(options.WavPath))
{
    Console.Error.WriteLine($"WAV file not found: {options.WavPath}");
    return 2;
}

if (!File.Exists(options.ModelPath))
{
    Console.Error.WriteLine($"Model file not found: {options.ModelPath}");
    return 2;
}

string? speakerGroundTruthText = null;
if (options.EnableLiveQA || options.EnableFinalQA)
{
    if (string.IsNullOrWhiteSpace(options.GroundTruthPath) || !File.Exists(options.GroundTruthPath))
    {
        Console.Error.WriteLine("Live/Final Q&A simulation requires a readable --ground-truth file.");
        return 2;
    }
    var ext = Path.GetExtension(options.GroundTruthPath).ToLowerInvariant();
    if (ext != ".txt" && ext != ".md")
    {
        Console.Error.WriteLine($"--live-qa / --final-qa require a plain-text ground-truth file (.txt or .md). Got: {ext}");
        return 2;
    }
    speakerGroundTruthText = File.ReadAllText(options.GroundTruthPath);
    if (string.IsNullOrWhiteSpace(speakerGroundTruthText))
    {
        Console.Error.WriteLine($"--ground-truth file is empty: {options.GroundTruthPath}");
        return 2;
    }
}

Directory.CreateDirectory(options.OutDir);
var clock = Stopwatch.StartNew();

using var perceptorLog = new JsonlWriter(Path.Combine(options.OutDir, "perceptor.jsonl"), clock);
using var inquirerLog = new JsonlWriter(Path.Combine(options.OutDir, "inquirer.jsonl"), clock);
using var cycleLog = new JsonlWriter(Path.Combine(options.OutDir, "cycle.jsonl"), clock);
using var summarizerLog = new JsonlWriter(Path.Combine(options.OutDir, "transcript_summarizer.jsonl"), clock);
using var groundTruthLog = new JsonlWriter(Path.Combine(options.OutDir, "ground_truth.jsonl"), clock);
using var mainIdeaComparatorLog = new JsonlWriter(Path.Combine(options.OutDir, "main_idea_comparator.jsonl"), clock);
using var deepdiveLog = new JsonlWriter(Path.Combine(options.OutDir, "deepdive.jsonl"), clock);
using var clarificationLog = new JsonlWriter(Path.Combine(options.OutDir, "clarification.jsonl"), clock);
using var qaRoundsLog = new JsonlWriter(Path.Combine(options.OutDir, "qa_rounds.jsonl"), clock);
using var metricsLog = new JsonlWriter(Path.Combine(options.OutDir, "metrics.jsonl"), clock);
var memorySampler = new RuntimeMemorySampler();
var memSamples = new List<MetricsLogEntry>();

try
{
    // ── 1. Read and chunk the WAV ───────────────────────────────────────────────
    Console.WriteLine("Reading WAV file...");
    var wav = WavReader.ReadFile(options.WavPath);
    var durationSec = (double)wav.Samples.Length / wav.SampleRate;
    Console.WriteLine($"  {wav.SampleRate} Hz, {wav.Samples.Length} samples, {durationSec:0.1}s");

    Console.WriteLine("Classifying volume...");
    var classifierConfig = new VolumeClassifierConfig
    {
        WindowMs = options.C,
        AbsoluteThresholdDb = options.ThresholdDb,
    };
    var symbols = VolumeClassifier.Classify(wav, classifierConfig);

    var voiceCount = symbols.Count(s => s == VolumeClassifier.Voice);
    Console.WriteLine($"  {symbols.Length} windows, {voiceCount} voice, {symbols.Length - voiceCount} silence");

    Console.WriteLine("Chunking...");
    var chunkerConfig = new AudioChunkerConfig
    {
        TargetChunkWindowX = TimeSpan.FromSeconds(options.X),
        TrailingSilenceY = TimeSpan.FromMilliseconds(options.Y),
        WindowMsC = TimeSpan.FromMilliseconds(options.C),
    };
    var chunker = new AudioChunker(chunkerConfig);
    var audioChunks = chunker.Chunk(symbols, wav.SampleRate);
    Console.WriteLine($"  {audioChunks.Count} audio chunk(s) emitted");
    Console.WriteLine();

    if (audioChunks.Count == 0)
    {
        Console.Error.WriteLine("No audio chunks produced. Try adjusting threshold or chunker parameters.");
        return 1;
    }

    // ── 2. Start Session ────────────────────────────────────────────────────────
    Console.WriteLine("Starting session...");
    GroundTruthDocInput? gtInput = options.GroundTruthPath is null
        ? null
        : GroundTruthDocInput.FilePath(options.GroundTruthPath);

    var sessionConfig = new SessionConfig(
        ModelPath: options.ModelPath,
        MmprojPath: options.MmprojPath,
        Backend: options.Backend,
        ContextSize: options.ContextSize,
        GpuLayers: options.GpuLayers,
        CycleMinElapsed: TimeSpan.FromSeconds(options.DebriefMinSeconds),
        EnableLiveQA: options.EnableLiveQA,
        EnableFinalQA: options.EnableFinalQA,
        GroundTruthInput: gtInput);

    using var session = Session.Start(sessionConfig, warn: msg => Console.WriteLine($"  [warn] {msg}"));
    Console.WriteLine($"  Backend: {session.BackendName}");

    SpeakerConversation? speaker = null;
    if (speakerGroundTruthText is not null)
    {
        speaker = new SpeakerConversation(
            session.Engine,
            speakerGroundTruthText,
            warn: msg => Console.WriteLine($"  [warn] {msg}"));
    }
    var injector = new SpeakerAnswerInjector();
    var allRounds = new List<RoundRecord>();

    LogMetrics(
        metricsLog,
        memSamples,
        GemmaStage.Session.Native.BenchmarkSnapshot.Empty,
        session.BackendName,
        "session_start",
        memorySampler);

    // ── 3. Subscribe to session events for live logging ─────────────────────────
    var backendName = session.BackendName;

    session.Events.PerceptorTurnCompleted += turn =>
    {
        LogPerceptorTurn(perceptorLog, turn);
        LogMetrics(metricsLog, memSamples, turn.Benchmark, backendName, GetPerceptorStage(turn), memorySampler);
    };

    session.Events.CycleStarting += index =>
    {
        Console.WriteLine($"    [cycle #{index} starting]");
    };

    session.Events.CycleCompleted += result =>
    {
        var inquirerReflection = result.InquirerTurn?.Parse.Reflection;
        var inquirerError = result.InquirerTurn?.Parse.Error ?? result.Error;
        var rawInquirerResponse = inquirerReflection is null
            ? result.InquirerTurn?.RawResponseJson
            : null;
        var ideaReflection = result.IdeaReflectorTurn?.Parse.Reflection;
        var retelling = ideaReflection?.Retelling ?? string.Empty;
        var excerpt = retelling.Length > 120 ? retelling[..120] + "..." : retelling;

        inquirerLog.Write(new InquirerLogEntry(
            inquirerLog.MonotonicMs,
            result.Index,
            excerpt,
            inquirerReflection is not null,
            inquirerError,
            inquirerReflection is null ? null : new
            {
                confusion_score = (result.StateAfter.ConfusionScore ?? InquirerConfusionScore.Low).ToString().ToLowerInvariant(),
                topic = ideaReflection?.Topic,
                main_idea_understanding = ideaReflection?.MainIdeaUnderstanding ?? result.StateAfter.MainIdeaUnderstanding,
                new_concerns = inquirerReflection.NewConcerns.Count,
                new_concern_types = inquirerReflection.NewConcerns.Select(c => c.Type.ToString()).ToArray(),
                removed_concerns = inquirerReflection.RemovedConcerns.Count,
            },
            rawInquirerResponse));

        cycleLog.Write(new CycleLogEntry(
            cycleLog.MonotonicMs,
            result.Queue.DepthAtPause,
            result.Queue.DrainTime.TotalMilliseconds,
            result.Queue.Drained));

        Console.WriteLine($"    [cycle #{result.Index} done, queue drained {result.Queue.Drained} in {result.Queue.DrainTime.TotalMilliseconds:0}ms]");
    };

    // ── 4. Stream audio chunks through Session ──────────────────────────────────
    Console.WriteLine();
    Console.WriteLine("Streaming audio chunks...");

    int lastLiveQACycle = 0;
    int liveRoundCounter = 0;

    for (int i = 0; i < audioChunks.Count; i++)
    {
        var chunk = audioChunks[i];
        var chunkSamples = ExtractChunkWav(wav, chunk);
        var chunkDurationSec = (double)CountChunkSamples(wav, chunk) / wav.SampleRate;

        Console.Write($"  Chunk {i + 1}/{audioChunks.Count} ({chunkDurationSec:0.1}s, {chunkSamples.Length} bytes)... ");

        var turnResult = session.PushAudio(chunkSamples, TimeSpan.FromSeconds(chunkDurationSec));

        if (turnResult is null)
        {
            Console.WriteLine("queued (debrief in progress)");
        }
        else
        {
            var transcript = turnResult.Parse.Audio?.Transcript ?? "<no transcript>";
            var preview = transcript.Length > 80 ? transcript[..80] + "..." : transcript;
            Console.WriteLine($"OK [{turnResult.Benchmark.DecodeTokenCount} tok, {turnResult.Benchmark.DecodeTokensPerSecond:0.0} t/s]");
            Console.WriteLine($"    transcript: {preview}");
        }

        // ── Live Q&A drain ──────────────────────────────────────────────────────
        // Between chunks, if a new cycle has completed and produced open
        // concerns, simulate one Live Q&A round. Per GAME_DESIGN §6.1 only one
        // round may be active at a time; we cap at one per cycle to keep the
        // PoC pacing realistic.
        if (options.EnableLiveQA && speaker is not null && session.CycleCount > lastLiveQACycle)
        {
            var state = session.SnapshotCognitiveState();
            if (state.Concerns.Count > 0)
            {
                var concern = state.Concerns[0];
                liveRoundCounter++;
                Console.WriteLine($"    [live Q&A round #{liveRoundCounter}] id={concern.Id} type={concern.Type}");
                Console.WriteLine($"      Q: {concern.Question}");

                var openedAt = DateTimeOffset.UtcNow;
                session.BeginOnLiveQARound(concern);
                var answer = speaker.AnswerQuestion(concern.Question);
                Console.WriteLine($"      A: {Truncate(answer, 200)}");
                injector.Inject(session, answer);
                session.EndOnLiveQARound();
                var closedAt = DateTimeOffset.UtcNow;

                allRounds.Add(new RoundRecord("Live", liveRoundCounter, concern.Id, concern.Question, concern.Type.ToString(), answer, openedAt, closedAt));
                qaRoundsLog.Write(new
                {
                    ts_ms = qaRoundsLog.MonotonicMs,
                    phase = "Live",
                    round = liveRoundCounter,
                    concern_id = concern.Id,
                    question = concern.Question,
                    type = concern.Type.ToString(),
                    answer,
                });
            }

            lastLiveQACycle = session.CycleCount;
        }
    }

    Console.WriteLine();
    Console.WriteLine($"  Total cycles during live phase: {session.CycleCount}");

    // ── 5. Post-performance pipeline ────────────────────────────────────────────
    Console.WriteLine();
    Console.WriteLine("Running post-performance pipeline...");

    GemmaStage.Session.FinalQAFinalizeResult pipeline;
    if (options.EnableFinalQA)
    {
        Console.WriteLine("  EndLivePhase...");
        var finalCycle = session.EndLivePhase();

        Console.WriteLine("  RunClarification...");
        var clarification = session.RunClarification();
        session.LoadConcernsForFinalQA(clarification);

        // ── Final Q&A rounds ────────────────────────────────────────────────────
        if (speaker is not null && clarification.UnresolvedQuestions.Count > 0)
        {
            Console.WriteLine($"  Final Q&A: answering {clarification.UnresolvedQuestions.Count} unresolved question(s)...");
            for (int i = 0; i < clarification.UnresolvedQuestions.Count; i++)
            {
                var q = clarification.UnresolvedQuestions[i];
                var concern = new InquirerConcern(q.Id, q.Question, q.Type);
                Console.WriteLine($"    [final Q&A round #{i + 1}] id={concern.Id} type={concern.Type}");
                Console.WriteLine($"      Q: {concern.Question}");

                var openedAt = DateTimeOffset.UtcNow;
                session.BeginFinalQARound(concern);
                var answer = speaker.AnswerQuestion(concern.Question);
                Console.WriteLine($"      A: {Truncate(answer, 200)}");
                injector.Inject(session, answer);
                session.EndFinalQARound();
                var closedAt = DateTimeOffset.UtcNow;

                allRounds.Add(new RoundRecord("Final", i + 1, concern.Id, concern.Question, concern.Type.ToString(), answer, openedAt, closedAt));
                qaRoundsLog.Write(new
                {
                    ts_ms = qaRoundsLog.MonotonicMs,
                    phase = "Final",
                    round = i + 1,
                    concern_id = concern.Id,
                    question = concern.Question,
                    type = concern.Type.ToString(),
                    answer,
                });
            }
        }
        else if (clarification.UnresolvedQuestions.Count > 0)
        {
            Console.WriteLine($"  Final Q&A: {clarification.UnresolvedQuestions.Count} unresolved question(s) skipped (no SpeakerConversation; --final-qa needs --ground-truth).");
        }

        Console.WriteLine("  RunPostPerformancePipeline...");
        var post = session.RunPostPerformancePipeline(clarification);

        pipeline = new GemmaStage.Session.FinalQAFinalizeResult(
            finalCycle, clarification,
            post.TranscriptSummarizer, post.GroundTruthSummarizer,
            post.MainIdeaComparator, post.DeepDive, post.IdeaComprehension);
    }
    else
    {
        pipeline = session.GoFinalQAIfEnabledAndFinalize();
    }

    var finalState = session.SnapshotCognitiveState();
    Console.WriteLine($"  Final topic: {finalState.Topic ?? "<unknown>"}");
    Console.WriteLine($"  Final main idea: {finalState.MainIdeaUnderstanding ?? "<unknown>"}");
    Console.WriteLine($"  Open concerns: {session.ConcernStore.OpenSnapshot().Count}");
    Console.WriteLine($"  Archived concerns: {session.ConcernArchive.Snapshot().Count}");
    Console.WriteLine($"  Q&A rounds recorded: {session.QaRoundsHistory.Snapshot().Count}");

    // ── 6. TranscriptSummarizer ─────────────────────────────────────────────────
    Console.WriteLine();
    Console.WriteLine("TranscriptSummarizer outputs:");

    var summarizerResult = pipeline.TranscriptSummarizer;

    Console.WriteLine($"  MAP outputs: {summarizerResult.MapOutputs.Count}");
    for (int i = 0; i < summarizerResult.MapOutputs.Count; i++)
    {
        var map = summarizerResult.MapOutputs[i];
        Console.WriteLine($"    [{i + 1}] structure={map.Structure} consistency={map.Consistency} support={map.Support}");

        summarizerLog.Write(new TranscriptSummarizerLogEntry(
            summarizerLog.MonotonicMs,
            "map",
            i + 1,
            map.Retelling.Length,
            new
            {
                retelling = map.Retelling,
                structure = map.Structure,
                consistency = map.Consistency,
                support = map.Support,
                notes = map.Notes,
            }));

        if (summarizerResult.MapTurns.Count > i)
        {
            LogMetrics(metricsLog, memSamples, summarizerResult.MapTurns[i].Benchmark, backendName, "transcript_summarizer_map", memorySampler);
        }
    }

    if (summarizerResult.ReduceOutput is { } reduce)
    {
        Console.WriteLine($"  REDUCE: inferred main idea = {reduce.InferredMainIdeaFromTranscript}");

        summarizerLog.Write(new TranscriptSummarizerLogEntry(
            summarizerLog.MonotonicMs,
            "reduce",
            null,
            reduce.InferredMainIdeaFromTranscript.Length,
            new
            {
                inferred_main_idea_from_transcript = reduce.InferredMainIdeaFromTranscript,
            }));

        if (summarizerResult.ReduceTurn is not null)
        {
            LogMetrics(metricsLog, memSamples, summarizerResult.ReduceTurn.Benchmark, backendName, "transcript_summarizer_reduce", memorySampler);
        }
    }
    else
    {
        Console.Error.WriteLine("  TranscriptSummarizer REDUCE failed — no transcript summary available.");
    }

    // ── 7a. GroundTruthSummarizer (optional) ────────────────────────────────────
    var groundTruthResult = pipeline.GroundTruthSummarizer;
    Console.WriteLine();
    if (groundTruthResult.Ran && groundTruthResult.Output is { } gtOutput)
    {
        Console.WriteLine("GroundTruthSummarizer outputs:");
        Console.WriteLine($"  Source: {groundTruthResult.SourcePath} (kind={groundTruthResult.SourceKind})");
        Console.WriteLine($"  main_thesis: {gtOutput.MainThesis}");
        Console.WriteLine($"  claims ({gtOutput.Claims.Count}):");
        foreach (var c in gtOutput.Claims)
        {
            Console.WriteLine($"    - {c}");
        }

        var claimsTotalLength = 0;
        foreach (var c in gtOutput.Claims) claimsTotalLength += c.Length;
        groundTruthLog.Write(new GroundTruthSummarizerLogEntry(
            groundTruthLog.MonotonicMs,
            true,
            groundTruthResult.SourcePath,
            gtOutput.MainThesis.Length + claimsTotalLength,
            null,
            new
            {
                source_kind = groundTruthResult.SourceKind.ToString(),
                main_thesis = gtOutput.MainThesis,
                claims = gtOutput.Claims,
                map_chunks = groundTruthResult.MapTurns.Count,
            }));

        if (groundTruthResult.ImageTurn is not null)
        {
            LogMetrics(metricsLog, memSamples, groundTruthResult.ImageTurn.Benchmark, backendName, "ground_truth_image_examination", memorySampler);
        }

        foreach (var turn in groundTruthResult.MapTurns)
        {
            LogMetrics(metricsLog, memSamples, turn.Benchmark, backendName, "ground_truth_summarizer_map", memorySampler);
        }

        if (groundTruthResult.ReduceTurn is not null)
        {
            LogMetrics(metricsLog, memSamples, groundTruthResult.ReduceTurn.Benchmark, backendName, "ground_truth_summarizer_reduce", memorySampler);
        }
    }
    else if (groundTruthResult.SourcePath is not null)
    {
        var error = groundTruthResult.ReduceTurn?.Parse.Error ?? "conversion or summarization failed (see warnings)";
        Console.Error.WriteLine($"GroundTruthSummarizer skipped for \"{groundTruthResult.SourcePath}\" (kind={groundTruthResult.SourceKind}): {error}");
        groundTruthLog.Write(new GroundTruthSummarizerLogEntry(
            groundTruthLog.MonotonicMs,
            false,
            groundTruthResult.SourcePath,
            null,
            error,
            new { source_kind = groundTruthResult.SourceKind.ToString() }));
    }
    else
    {
        Console.WriteLine("GroundTruthSummarizer: skipped (no ground-truth file attached).");
        groundTruthLog.Write(new GroundTruthSummarizerLogEntry(
            groundTruthLog.MonotonicMs,
            false,
            null,
            null,
            null,
            null));
    }

    // ── 7b. MainIdeaComparator (optional — only when GT ran) ────────────────────
    var mainIdeaComparatorResult = pipeline.MainIdeaComparator;
    if (mainIdeaComparatorResult.Output is { } cmpOutput)
    {
        Console.WriteLine();
        Console.WriteLine("MainIdeaComparator outputs:");
        Console.WriteLine($"  Anchor thesis:   {cmpOutput.AnchorThesis}");
        Console.WriteLine($"  Audience thesis: {cmpOutput.AudienceThesis}");
        Console.WriteLine($"  Thesis comparison: {cmpOutput.ThesisComparison}");
        Console.WriteLine($"  Recall: {cmpOutput.Recall:0.00}  Anchor claims: {cmpOutput.AnchorClaims.Count}");
        Console.WriteLine("  Per-claim coverage:");
        for (int i = 0; i < cmpOutput.ClaimCoverages.Count; i++)
        {
            var c = cmpOutput.ClaimCoverages[i];
            Console.WriteLine($"    [{c.Coverage.ToString().ToLowerInvariant()}] {c.AnchorClaim}");
        }

        mainIdeaComparatorLog.Write(new
        {
            ts_ms = mainIdeaComparatorLog.MonotonicMs,
            ran = true,
            anchor_thesis = cmpOutput.AnchorThesis,
            audience_thesis = cmpOutput.AudienceThesis,
            thesis_comparison = cmpOutput.ThesisComparison,
            recall = cmpOutput.Recall,
            anchor_claim_count = cmpOutput.AnchorClaims.Count,
            claim_coverage = cmpOutput.ClaimCoverages.Select(c => new
            {
                coverage = c.Coverage.ToString().ToLowerInvariant(),
                anchor_claim = c.AnchorClaim,
                evidence = c.Evidence,
            }).ToArray(),
        });

        if (mainIdeaComparatorResult.ThesisTurn is not null)
        {
            LogMetrics(metricsLog, memSamples, mainIdeaComparatorResult.ThesisTurn.Benchmark, backendName, "main_idea_comparator_thesis", memorySampler);
        }
        foreach (var turn in mainIdeaComparatorResult.CoverageTurns)
        {
            LogMetrics(metricsLog, memSamples, turn.Benchmark, backendName, "main_idea_comparator_coverage", memorySampler);
        }
    }
    else
    {
        Console.WriteLine();
        Console.WriteLine("MainIdeaComparator: skipped (no ground-truth file attached or GT summarizer failed).");
        mainIdeaComparatorLog.Write(new { ts_ms = mainIdeaComparatorLog.MonotonicMs, ran = false });
    }

    // ── 7. Clarification (optional) ─────────────────────────────────────────────
    ClarificationResult? clarificationResult = pipeline.Clarification;
    if (clarificationResult is not null)
    {
        Console.WriteLine();
        Console.WriteLine("Clarification outputs:");
        Console.WriteLine($"  Archived: {clarificationResult.OriginalArchivedCount}, revised: {clarificationResult.RevisedQuestions.Count}, resolved: {clarificationResult.ResolvedIds.Count}, unresolved: {clarificationResult.UnresolvedQuestions.Count}");

        clarificationLog.Write(new ClarificationLogEntry(
            clarificationLog.MonotonicMs,
            clarificationResult.OriginalArchivedCount,
            clarificationResult.RevisedQuestions.Count,
            clarificationResult.ResolvedIds.Count,
            clarificationResult.UnresolvedQuestions.Count,
            clarificationResult.UnresolvedQuestions.Select(c => new { id = c.Id, question = c.Question, type = c.Type.ToString() }).ToArray()));

        if (clarificationResult.ReviseTurn is not null)
        {
            LogMetrics(metricsLog, memSamples, clarificationResult.ReviseTurn.Benchmark, backendName, "clarification_revise", memorySampler);
        }
        foreach (var turn in clarificationResult.ResolveTurns)
        {
            LogMetrics(metricsLog, memSamples, turn.Benchmark, backendName, "clarification_resolve", memorySampler);
        }
    }

    // ── 8. DeepDive ─────────────────────────────────────────────────────────────
    Console.WriteLine();
    Console.WriteLine("DeepDive outputs:");

    DeepDiveResult? deepDiveEvaluation = null;

    if (pipeline.DeepDive is null)
    {
        Console.Error.WriteLine("  Skipped: no TranscriptSummarizer REDUCE output (DeepDive was not run).");
    }
    else
    {
        var aggregate = pipeline.DeepDive;
        var evaluation = aggregate.Result;
        deepDiveEvaluation = evaluation;

        Console.WriteLine($"  Main Idea Clarity:      {evaluation.MainIdeaClarity.Value} — {evaluation.MainIdeaClarity.Verdict}");
        Console.WriteLine($"  Structure:              {evaluation.Structure.Value} — {evaluation.Structure.Verdict}");
        Console.WriteLine($"  Consistency & Focus:    {evaluation.ConsistencyFocus.Value} — {evaluation.ConsistencyFocus.Verdict}");
        Console.WriteLine($"  Support & Justification:{evaluation.SupportJustification.Value} — {evaluation.SupportJustification.Verdict}");
        Console.WriteLine($"  Language Quality:       {evaluation.LanguageQuality.Value} — {evaluation.LanguageQuality.Verdict}");
        Console.WriteLine($"  Emotional Delivery:     {evaluation.EmotionalDelivery.Value} — {evaluation.EmotionalDelivery.Verdict}");
        Console.WriteLine($"  Q&A Handling:           {evaluation.QaHandling.Value} — {evaluation.QaHandling.Verdict}");

        var resultJson = JsonSerializer.Serialize(new
        {
            main_idea_clarity = new { value = (int)evaluation.MainIdeaClarity.Value, verdict = evaluation.MainIdeaClarity.Verdict },
            structure = new { value = (int)evaluation.Structure.Value, verdict = evaluation.Structure.Verdict },
            consistency_focus = new { value = (int)evaluation.ConsistencyFocus.Value, verdict = evaluation.ConsistencyFocus.Verdict },
            support_justification = new { value = (int)evaluation.SupportJustification.Value, verdict = evaluation.SupportJustification.Verdict },
            language_quality = new { value = (int)evaluation.LanguageQuality.Value, verdict = evaluation.LanguageQuality.Verdict },
            emotional_delivery = new { value = FormatOptionalScore(evaluation.EmotionalDelivery.Value), verdict = evaluation.EmotionalDelivery.Verdict },
            qa_handling = new { value = FormatOptionalScore(evaluation.QaHandling.Value), verdict = evaluation.QaHandling.Verdict },
        }, new JsonSerializerOptions { WriteIndented = true });

        var resultPath = Path.Combine(options.OutDir, "result.json");
        File.WriteAllText(resultPath, resultJson);
        Console.WriteLine();
        Console.WriteLine($"  Result written to: {resultPath}");

        deepdiveLog.Write(new DeepDiveLogEntry(
            deepdiveLog.MonotonicMs,
            resultJson.Length,
            evaluation));

        foreach (var sub in aggregate.SubRoles)
        {
            if (sub.Benchmark is { } bench)
            {
                LogMetrics(metricsLog, memSamples, bench, backendName, $"deepdive.{sub.Role}", memorySampler);
            }
        }
    }

    // ── 9. XLSX Export (optional) ───────────────────────────────────────────────
    if (!string.IsNullOrWhiteSpace(options.XlsxPath))
    {
        Console.WriteLine();
        Console.WriteLine("Exporting to Excel...");
        XlsxExporter.Export(options.XlsxPath, session, summarizerResult, clarificationResult, deepDiveEvaluation, pipeline.MainIdeaComparator, pipeline.GroundTruthSummarizer, memSamples, allRounds);
        Console.WriteLine($"  Written to: {options.XlsxPath}");
    }

    speaker?.Dispose();

    // ── Done ────────────────────────────────────────────────────────────────────
    clock.Stop();
    Console.WriteLine();
    Console.WriteLine($"Total elapsed: {clock.Elapsed.TotalSeconds:0.1}s");
    Console.WriteLine($"Log files written to: {options.OutDir}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fatal error: {ex}");
    return 1;
}

// ── Helpers ─────────────────────────────────────────────────────────────────────

static byte[] ExtractChunkWav(WavData wav, AudioChunk chunk)
{
    int numSamples = CountChunkSamples(wav, chunk);

    if (numSamples <= 0)
    {
        return Array.Empty<byte>();
    }

    int dataSize = numSamples * 2;
    int fileSize = 44 + dataSize;

    using var ms = new MemoryStream(fileSize);
    using var writer = new BinaryWriter(ms);

    writer.Write("RIFF"u8);
    writer.Write(fileSize - 8);
    writer.Write("WAVE"u8);

    writer.Write("fmt "u8);
    writer.Write(16);
    writer.Write((short)1);
    writer.Write((short)1);
    writer.Write(wav.SampleRate);
    writer.Write(wav.SampleRate * 2);
    writer.Write((short)2);
    writer.Write((short)16);

    writer.Write("data"u8);
    writer.Write(dataSize);
    foreach (var span in chunk.Spans)
    {
        int startSample = Math.Max(0, span.StartSample);
        int endSample = Math.Min(wav.Samples.Length - 1, span.EndSample);
        for (int i = startSample; i <= endSample; i++)
        {
            writer.Write(wav.Samples[i]);
        }
    }

    return ms.ToArray();
}

static int CountChunkSamples(WavData wav, AudioChunk chunk)
{
    var total = 0;
    foreach (var span in chunk.Spans)
    {
        int startSample = Math.Max(0, span.StartSample);
        int endSample = Math.Min(wav.Samples.Length - 1, span.EndSample);
        if (endSample >= startSample)
        {
            total += endSample - startSample + 1;
        }
    }

    return total;
}

static void LogPerceptorTurn(JsonlWriter log, PerceptorTurnResult turn)
{
    var decodeMs = turn.Benchmark.DecodeTokenCount > 0 && turn.Benchmark.DecodeTokensPerSecond > 0
        ? turn.Benchmark.DecodeTokenCount / turn.Benchmark.DecodeTokensPerSecond * 1000.0
        : (double?)null;

    var ttftMs = turn.Benchmark.TimeToFirstTokenSeconds > 0
        ? turn.Benchmark.TimeToFirstTokenSeconds * 1000.0
        : (double?)null;

    string kind;
    object? outputJson = null;
    if (turn.Parse.Audio is { } audio)
    {
        kind = "audio";
        outputJson = new
        {
            clarity = audio.Clarity.ToString().ToLowerInvariant(),
            transcript = audio.Transcript,
            emotion = audio.Emotion?.ToString().ToLowerInvariant(),
            grammar = audio.Grammar?.ToString().ToLowerInvariant(),
            chunk_completed = audio.ChunkCompleted,
            notes_count = audio.Notes.Count,
        };
    }
    else if (turn.Parse.Image is { } image)
    {
        kind = "image";
        outputJson = new
        {
            examination = image.Examination,
            notes_count = image.Notes.Count,
        };
    }
    else
    {
        kind = "unknown";
    }

    log.Write(new PerceptorLogEntry(
        log.MonotonicMs,
        kind,
        turn.InputDuration?.TotalSeconds,
        outputJson,
        turn.Benchmark.DecodeTokenCount > 0 ? turn.Benchmark.DecodeTokenCount : null,
        decodeMs,
        ttftMs));
}

static string GetPerceptorStage(PerceptorTurnResult turn)
{
    if (turn.Parse.Audio is not null)
    {
        return "perceptor_audio";
    }

    if (turn.Parse.Image is not null)
    {
        return "perceptor_image";
    }

    return "perceptor_unknown";
}

static void LogMetrics(
    JsonlWriter log,
    List<MetricsLogEntry> memSamples,
    GemmaStage.Session.Native.BenchmarkSnapshot benchmark,
    string backendName,
    string stage,
    RuntimeMemorySampler memorySampler)
{
    var memory = memorySampler.Sample(backendName);
    var entry = new MetricsLogEntry(
        log.MonotonicMs,
        stage,
        benchmark.DecodeTokensPerSecond > 0 ? benchmark.DecodeTokensPerSecond : null,
        benchmark.DecodeTokenCount > 0 ? benchmark.DecodeTokenCount : null,
        benchmark.DecodeTokenCount > 0 && benchmark.DecodeTokensPerSecond > 0
            ? benchmark.DecodeTokenCount / benchmark.DecodeTokensPerSecond * 1000.0
            : null,
        benchmark.PrefillTokensPerSecond > 0 ? benchmark.PrefillTokensPerSecond : null,
        benchmark.PrefillTokenCount > 0 ? benchmark.PrefillTokenCount : null,
        benchmark.PrefillSeconds > 0 ? benchmark.PrefillSeconds * 1000.0 : null,
        benchmark.TemplateBuildSeconds > 0 ? benchmark.TemplateBuildSeconds * 1000.0 : null,
        benchmark.CommitTokenCount > 0 ? benchmark.CommitTokenCount : null,
        benchmark.CommitOverheadSeconds > 0 ? benchmark.CommitOverheadSeconds * 1000.0 : null,
        backendName,
        benchmark.KvCacheTokenCount > 0 ? benchmark.KvCacheTokenCount : null,
        benchmark.ContextSize > 0 ? benchmark.ContextSize : null,
        benchmark.ContextSize > 0 ? benchmark.KvCachePercent : null,
        memory.VramBytes,
        memory.VramSource,
        memory.RssBytes);

    memSamples.Add(entry);
    log.Write(entry);
}

static string FormatOptionalScore(DeepDiveScore score)
{
    return score == DeepDiveScore.NotApplicable ? "N/A" : ((int)score).ToString();
}

static string Truncate(string? s, int max)
{
    if (string.IsNullOrEmpty(s)) return string.Empty;
    return s.Length <= max ? s : s.Substring(0, max) + "...";
}
