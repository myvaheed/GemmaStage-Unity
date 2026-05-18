using GemmaStage.Session;
using GemmaStage.Session.CognitiveCycle;
using GemmaStage.Session.IdeaReflector;
using GemmaStage.Session.Inquirer;
using GemmaStage.Session.Native;
using GemmaStage.Session.Perceptor;
using GemmaStage.Session.Stores;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;

var options = SmokeTestOptions.Parse(args);
Console.WriteLine("GemmaStage.Session smoke test");
Console.WriteLine($"Model:   {options.ModelPath}");
Console.WriteLine($"Backend: {(string.IsNullOrWhiteSpace(options.Backend) ? "auto" : options.Backend)}");
Console.WriteLine();

AudioChunkerSmokeTest.Run(options.ResourceDir);

if (!File.Exists(options.ModelPath))
{
    Console.Error.WriteLine($"Model file not found: {options.ModelPath}");
    return 2;
}

try
{
    GemmaStageNative.SetMinLogLevel(1);

    using var settings = GemmaStageNative.EngineSettingsCreate(options.ModelPath, options.MmprojPath, options.Backend);
    GemmaStageNative.EngineSettingsSetContextSize(settings, options.ContextSize);
    GemmaStageNative.EngineSettingsSetGpuLayers(settings, options.GpuLayers);
    GemmaStageNative.EngineSettingsEnableBenchmark(settings);

    using var engine = GemmaStageNative.EngineCreate(settings);
    Console.WriteLine($"Native backend: {GemmaStageNative.EngineGetBackendName(engine)}");

    using var config = GemmaStageNative.ConversationConfigCreate(
        engine,
        "You are a concise assistant. Answer the user's question directly.",
        null,
        true);

    using var conversation = GemmaStageNative.ConversationCreate(engine, config);

    var shortBlocking = RunBlockingTurn(
        conversation,
        "What is 2 + 2? Reply with exactly 4.");
    Console.WriteLine("Blocking turn");
    Console.WriteLine($"  Wall time: {shortBlocking.WallTime.TotalMilliseconds:0.0} ms");
    Console.WriteLine($"  Assistant content: {shortBlocking.AssistantContent}");
    PrintBenchmark("  Native metrics", shortBlocking.Benchmark);

    if (string.IsNullOrWhiteSpace(shortBlocking.AssistantContent))
    {
        Console.Error.WriteLine("Blocking text response was empty.");
        return 1;
    }

    var longerBlocking = RunBlockingTurn(
        conversation,
        "Greet the user warmly in one short sentence of 8 to 12 words.");
    Console.WriteLine("Longer blocking turn");
    Console.WriteLine($"  Wall time: {longerBlocking.WallTime.TotalMilliseconds:0.0} ms");
    Console.WriteLine($"  Assistant content: {longerBlocking.AssistantContent}");
    PrintBenchmark("  Native metrics", longerBlocking.Benchmark);

    if (string.IsNullOrWhiteSpace(longerBlocking.AssistantContent))
    {
        Console.Error.WriteLine("Longer blocking response was empty.");
        return 1;
    }

    var inquirerExitCode = RunInquirerPhase(engine);
    if (inquirerExitCode != 0)
    {
        return inquirerExitCode;
    }

    if (string.IsNullOrWhiteSpace(options.MmprojPath))
    {
        Console.WriteLine();
        Console.WriteLine("Perceptor phase skipped: --mmproj was not supplied.");
    }
    else
    {
        var perceptorExitCode = RunPerceptorPhase(engine, options.ResourceDir);
        if (perceptorExitCode != 0)
        {
            return perceptorExitCode;
        }

        var cycleExitCode = RunCyclePhase(options);
        if (cycleExitCode != 0)
        {
            return cycleExitCode;
        }
    }

    AudioChunkerSmokeTest.Run(options.ResourceDir);

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

static BlockingTurnResult RunBlockingTurn(ConversationHandle conversation, string prompt)
{
    var wallClock = Stopwatch.StartNew();
    using var response = GemmaStageNative.ConversationSendText(conversation, prompt);
    wallClock.Stop();

    var responseJson = GemmaStageNative.JsonResponseGetString(response);
    using var benchmark = GemmaStageNative.ConversationGetBenchmarkInfo(conversation);
    var snapshot = GemmaStageNative.GetBenchmarkSnapshot(benchmark);

    return new BlockingTurnResult(
        ExtractAssistantContent(responseJson),
        wallClock.Elapsed,
        snapshot);
}

static string ExtractAssistantContent(string? responseJson)
{
    if (string.IsNullOrWhiteSpace(responseJson))
    {
        return string.Empty;
    }

    using var document = JsonDocument.Parse(responseJson);
    if (document.RootElement.TryGetProperty("content", out var contentElement) &&
        contentElement.ValueKind == JsonValueKind.String)
    {
        return contentElement.GetString() ?? string.Empty;
    }

    return responseJson;
}

static int RunPerceptorPhase(EngineHandle engine, string resourceDir)
{
    var imagePath = Path.Combine(resourceDir, "population.jpg");
    var audioPath = Path.Combine(resourceDir, "ted_test_sleep.wav");

    if (!File.Exists(imagePath) || !File.Exists(audioPath))
    {
        Console.WriteLine();
        Console.WriteLine($"Perceptor phase skipped: missing resources under {resourceDir}");
        return 0;
    }

    Console.WriteLine();
    Console.WriteLine("Perceptor phase (Asr + I2t)");

    using var asr = new AsrConversation(
        engine,
        warn: msg => Console.WriteLine($"  [warn] {msg}"));
    using var i2t = new I2tConversation(
        engine,
        warn: msg => Console.WriteLine($"  [warn] {msg}"));

    Console.WriteLine($"  Image input: {imagePath}");
    var imageBytes = File.ReadAllBytes(imagePath);
    var imageWall = Stopwatch.StartNew();
    var imageTurn = i2t.SendImage(imageBytes);
    imageWall.Stop();
    PrintPerceptorTurn("  Image turn", imageTurn, imageWall.Elapsed);

    if (imageTurn.Parse.IsFailure)
    {
        Console.Error.WriteLine($"  I2T image turn failed: {imageTurn.Parse.Error}");
        return 1;
    }

    Console.WriteLine($"  Audio input: {audioPath}");
    var audioBytes = File.ReadAllBytes(audioPath);
    var audioWall = Stopwatch.StartNew();
    var audioTurn = asr.SendAudio(audioBytes);
    audioWall.Stop();
    PrintPerceptorTurn("  Audio turn", audioTurn, audioWall.Elapsed);

    if (audioTurn.Parse.IsFailure)
    {
        Console.Error.WriteLine($"  ASR audio turn failed: {audioTurn.Parse.Error}");
        return 1;
    }

    return 0;
}

static int RunInquirerPhase(EngineHandle engine)
{
    Console.WriteLine();
    Console.WriteLine("Inquirer phase");

    using var inquirer = new InquirerConversation(
        engine,
        warn: msg => Console.WriteLine($"  [warn] {msg}"));

    try
    {
        var firstTurn = inquirer.Reflect(
            topic: "VR latency budgeting",
            mainIdeaUnderstanding: "The talk argues for keeping Perceptor and Inquirer caches hot to reduce latency.",
            retellings: Array.Empty<string>());
        PrintInquirerTurn("  Reflection 1", firstTurn);
        if (firstTurn.Parse.IsFailure || firstTurn.Parse.Reflection is null)
        {
            Console.Error.WriteLine($"  Inquirer reflection 1 failed: {firstTurn.Parse.Error}");
            return 1;
        }

        var secondTurn = inquirer.Reflect(
            topic: "VR latency budgeting",
            mainIdeaUnderstanding: "Cache residency is balanced against latency by draining queued input quickly after each cycle.",
            retellings: new[] { "VR latency is reduced through hot KV caches and fast queue draining after cycles." });
        PrintInquirerTurn("  Reflection 2", secondTurn);
        if (secondTurn.Parse.IsFailure || secondTurn.Parse.Reflection is null)
        {
            Console.Error.WriteLine($"  Inquirer reflection 2 failed: {secondTurn.Parse.Error}");
            return 1;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  Inquirer phase failed: {ex.Message}");
        return 1;
    }

    return 0;
}

static int RunCyclePhase(SmokeTestOptions options)
{
    var audioPath = Path.Combine(options.ResourceDir, "ted_test_sleep.wav");
    var imagePath = Path.Combine(options.ResourceDir, "population.jpg");
    if (!File.Exists(audioPath) || !File.Exists(imagePath))
    {
        Console.WriteLine();
        Console.WriteLine($"Cycle phase skipped: missing resources under {options.ResourceDir}");
        return 0;
    }

    Console.WriteLine();
    Console.WriteLine("Cognitive cycle phase");

    var config = new SessionConfig(
        ModelPath: options.ModelPath,
        MmprojPath: options.MmprojPath,
        Backend: options.Backend,
        ContextSize: options.ContextSize,
        GpuLayers: options.GpuLayers,
        CycleMinElapsed: TimeSpan.FromMinutes(30));

    using var session = Session.Start(config, warn: msg => Console.WriteLine($"  [warn] {msg}"));
    Console.WriteLine($"  Backend: {session.BackendName}");
    Console.WriteLine($"  Cycle gate: elapsed>={session.Scheduler.MinElapsed.TotalSeconds:0}s AND chunk_completed=true");

    try
    {
        var audioBytes = File.ReadAllBytes(audioPath);
        var imageBytes = File.ReadAllBytes(imagePath);

        Console.WriteLine("  Seeding Perceptor with one audio turn");
        var firstAudio = session.PushAudio(audioBytes);
        if (firstAudio is null || firstAudio.Parse.IsFailure)
        {
            Console.Error.WriteLine($"  Cycle phase: initial audio turn failed: {firstAudio?.Parse.Error}");
            return 1;
        }
        PrintPerceptorTurn("    Perceptor audio #1", firstAudio, TimeSpan.Zero);

        Console.WriteLine("  Seeding Perceptor with one image turn");
        var firstImage = session.PushImage(imageBytes);
        if (firstImage is null || firstImage.Parse.IsFailure)
        {
            Console.Error.WriteLine($"  Cycle phase: initial image turn failed: {firstImage?.Parse.Error}");
            return 1;
        }
        PrintPerceptorTurn("    Perceptor image #1", firstImage, TimeSpan.Zero);

        Console.WriteLine("  Running manual cognitive cycle");
        var cycleWall = Stopwatch.StartNew();
        var cycle = session.RunCycle();
        cycleWall.Stop();
        PrintCycle("    Cycle #1", cycle, cycleWall.Elapsed);
        if (cycle.IsFailure)
        {
            Console.Error.WriteLine($"  Cycle phase: cycle failed: {cycle.Error}");
            return 1;
        }

        Console.WriteLine("  Queueing audio during pause then replaying via coordinator (post-cycle push)");
        var postAudio = session.PushAudio(audioBytes);
        if (postAudio is null || postAudio.Parse.IsFailure)
        {
            Console.Error.WriteLine($"  Cycle phase: post-cycle audio turn failed: {postAudio?.Parse.Error}");
            return 1;
        }
        PrintPerceptorTurn("    Perceptor audio #2 (post-cycle)", postAudio, TimeSpan.Zero);

        Console.WriteLine("  Store snapshots:");
        Console.WriteLine($"    transcripts={session.TranscriptStore.Snapshot().Count}");
        Console.WriteLine($"    images={session.ImageStore.Snapshot().Count}");
        Console.WriteLine($"    metrics={session.MetricsStore.Snapshot().Count}");
        Console.WriteLine($"    retellings={session.RetellingsHistory.Snapshot().Count}");
        Console.WriteLine($"    open concerns={session.ConcernStore.OpenSnapshot().Count}");
        Console.WriteLine($"    archived concerns={session.ConcernArchive.Snapshot().Count}");
        Console.WriteLine($"    cycle count={session.CycleCount}");

        // Bundled post-performance pipeline. Equivalent to the user pressing
        // End Session in Unity: any trailing in-flight audio is delivered
        // through PushAudio first (the audio chunker is the single upstream
        // of Asr); GoFinalQAIfEnabledAndFinalize then runs one final cycle on
        // the raw content buffer, archives remaining concerns, and runs
        // Final Q&A (if enabled) → TranscriptSummarizer → DeepDive.
        Console.WriteLine("  Pushing trailing in-flight audio before finalize");
        var trailingTurn = session.PushAudio(audioBytes);
        if (trailingTurn is null || trailingTurn.Parse.IsFailure)
        {
            Console.Error.WriteLine($"  Cycle phase: trailing audio turn failed: {trailingTurn?.Parse.Error}");
            return 1;
        }
        PrintPerceptorTurn("    Perceptor audio #3 (trailing)", trailingTurn, TimeSpan.Zero);

        Console.WriteLine("  Finalizing session");
        var pipeline = session.GoFinalQAIfEnabledAndFinalize();
        Console.WriteLine("  Session finalized");

        var finalCycle = pipeline.FinalCycle;
        var trailingAudioSegments = finalCycle.RawContent.Count(s => s.Kind == RawContentKind.Audio);
        Console.WriteLine($"    final cycle index={finalCycle.Index}");
        Console.WriteLine($"    final cycle raw content segments={finalCycle.RawContent.Count} (audio={trailingAudioSegments})");
        Console.WriteLine($"    open concerns after finalize={session.ConcernStore.OpenSnapshot().Count}");
        Console.WriteLine($"    archived concerns after finalize={session.ConcernArchive.Snapshot().Count}");

        if (trailingAudioSegments == 0)
        {
            Console.Error.WriteLine("  Cycle phase: final cycle did not include the trailing in-flight audio.");
            return 1;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  Cycle phase failed: {ex.Message}");
        return 1;
    }

    return 0;
}

static void PrintCycle(string label, CognitiveCycleResult cycle, TimeSpan wall)
{
    Console.WriteLine(label);
    Console.WriteLine($"    Index: {cycle.Index}");
    Console.WriteLine($"    Wall time: {wall.TotalMilliseconds:0.0} ms");
    Console.WriteLine($"    Duration: {cycle.Duration.TotalMilliseconds:0.0} ms");

    if (cycle.IdeaReflectorTurn is { } ideaTurn && ideaTurn.Parse.Reflection is { } idea)
    {
        Console.WriteLine($"    IdeaReflector topic: {idea.Topic}");
        var retelling = idea.Retelling.Length > 240
            ? idea.Retelling[..240] + "..."
            : idea.Retelling;
        Console.WriteLine($"    IdeaReflector retelling: {retelling}");
        Console.WriteLine($"    IdeaReflector main_idea_understanding length: {idea.MainIdeaUnderstanding.Length} chars");
        var mainIdea = ideaTurn.MainIdeaUnderstandingAfter.Length > 240
            ? ideaTurn.MainIdeaUnderstandingAfter[..240] + "..."
            : ideaTurn.MainIdeaUnderstandingAfter;
        Console.WriteLine($"    Audience-side recall (rendered): {mainIdea}");
    }
    else
    {
        Console.WriteLine($"    IdeaReflector: <missing> ({cycle.IdeaReflectorTurn?.Parse.Error ?? "unknown"})");
    }

    if (cycle.InquirerTurn is { } inquirerTurn && inquirerTurn.Parse.Reflection is { } reflection)
    {
        Console.WriteLine($"    Inquirer confusion: {cycle.StateAfter.ConfusionScore?.ToString() ?? "<n/a>"}");
        Console.WriteLine($"    Inquirer topic: {cycle.StateAfter.Topic ?? "<unknown>"}");
        Console.WriteLine($"    Main idea: {cycle.StateAfter.MainIdeaUnderstanding ?? "<unknown>"}");
        Console.WriteLine($"    Removed: {reflection.RemovedConcerns.Count} concern(s)");
        Console.WriteLine($"    New: {reflection.NewConcerns.Count} concern(s)");
        Console.WriteLine($"    Open: {cycle.StateAfter.Concerns.Count} concern(s)");
        Console.WriteLine($"    Archived overflow: {inquirerTurn.ArchivedConcerns.Count} concern(s)");
    }
    else
    {
        Console.WriteLine($"    Inquirer reflection: <missing>");
    }

    Console.WriteLine($"    Queue depth-at-pause: {cycle.Queue.DepthAtPause}, drained: {cycle.Queue.Drained}");
}

static void PrintPerceptorTurn(string label, PerceptorTurnResult turn, TimeSpan wall)
{
    Console.WriteLine(label);
    Console.WriteLine($"    Wall time: {wall.TotalMilliseconds:0.0} ms");
    Console.WriteLine($"    Tool: {turn.Parse.ToolName ?? "<none>"}");
    if (turn.Parse.Audio is { } audio)
    {
        var transcript = audio.Transcript.Length > 160 ? audio.Transcript[..160] + "..." : audio.Transcript;
        Console.WriteLine($"    Transcript: {transcript}");
        Console.WriteLine($"    Clarity={audio.Clarity} Emotion={FormatEmotion(audio.Emotion)} Grammar={FormatGrammar(audio.Grammar)} ChunkCompleted={audio.ChunkCompleted}");
        Console.WriteLine($"    notes={audio.Notes.Count}");
    }
    if (turn.Parse.Image is { } image)
    {
        var examination = image.Examination.Length > 160 ? image.Examination[..160] + "..." : image.Examination;
        Console.WriteLine($"    Examination: {examination}");
        Console.WriteLine($"    notes={image.Notes.Count}");
    }
    PrintBenchmark("    Native metrics", turn.Benchmark);
}

static void PrintInquirerTurn(string label, InquirerTurnResult turn)
{
    Console.WriteLine(label);
    Console.WriteLine($"    Turn: {turn.TurnSequence}");
    Console.WriteLine($"    Tool: {turn.Parse.ToolName ?? "<none>"}");
    if (turn.Parse.Reflection is { } reflection)
    {
        Console.WriteLine($"    Topic: {turn.State.Topic ?? "<unknown>"}");
        Console.WriteLine($"    Confusion: {turn.State.ConfusionScore?.ToString() ?? "<n/a>"}");
        Console.WriteLine($"    Persisted main idea: {turn.State.MainIdeaUnderstanding ?? "<unknown>"}");
        Console.WriteLine($"    Removed concerns: {reflection.RemovedConcerns.Count}");
        Console.WriteLine($"    New concerns: {reflection.NewConcerns.Count}");
        Console.WriteLine($"    Open concerns: {turn.State.Concerns.Count}");
        foreach (var concern in turn.State.Concerns)
        {
            Console.WriteLine($"      [{concern.Id}] ({concern.Type}) {concern.Question}");
        }
        Console.WriteLine($"    Archived overflow: {turn.ArchivedConcerns.Count}");
    }
    else
    {
        Console.WriteLine($"    Error: {turn.Parse.Error ?? "<unknown>"}");
        Console.WriteLine($"    Open concerns: {turn.State.Concerns.Count}");
    }
    PrintBenchmark("    Native metrics", turn.Benchmark);
}

static void PrintBenchmark(string label, BenchmarkSnapshot benchmark)
{
    Console.WriteLine(label);
    Console.WriteLine($"    Native TTFT field: {benchmark.TimeToFirstTokenSeconds:0.000000}s");
    Console.WriteLine($"    Prefill tokens: {benchmark.PrefillTokenCount}");
    Console.WriteLine($"    Decode tokens: {benchmark.DecodeTokenCount}");
    Console.WriteLine($"    Decode speed: {benchmark.DecodeTokensPerSecond:0.0} tok/s");
}

static string FormatEmotion(PerceptorEmotion? emotion)
{
    return emotion switch
    {
        PerceptorEmotion.Calm => "calm",
        PerceptorEmotion.Enthusiastic => "enthusiastic",
        PerceptorEmotion.Tense => "tense",
        PerceptorEmotion.Uncertain => "uncertain",
        null => "<omitted>",
        _ => emotion.Value.ToString(),
    };
}

static string FormatGrammar(PerceptorGrammar? grammar)
{
    return grammar switch
    {
        PerceptorGrammar.Poor => "poor",
        PerceptorGrammar.Moderate => "moderate",
        PerceptorGrammar.Good => "good",
        PerceptorGrammar.Excellent => "excellent",
        null => "<omitted>",
        _ => grammar.Value.ToString(),
    };
}

internal sealed record BlockingTurnResult(
    string AssistantContent,
    TimeSpan WallTime,
    BenchmarkSnapshot Benchmark);

internal sealed record SmokeTestOptions(
    string ModelPath,
    string? MmprojPath,
    string? Backend,
    int ContextSize,
    int GpuLayers,
    string ResourceDir)
{
    public static SmokeTestOptions Parse(string[] args)
    {
        string? modelPath = null;
        string? mmprojPath = null;
        string? backend = null;
        string? resourceDir = null;
        var contextSize = 4096;
        var gpuLayers = -1;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model":
                    modelPath = ReadValue(args, ref i, "--model");
                    break;
                case "--mmproj":
                    mmprojPath = ReadValue(args, ref i, "--mmproj");
                    break;
                case "--backend":
                    backend = ReadValue(args, ref i, "--backend");
                    break;
                case "--context-size":
                    contextSize = int.Parse(ReadValue(args, ref i, "--context-size"));
                    break;
                case "--gpu-layers":
                    gpuLayers = int.Parse(ReadValue(args, ref i, "--gpu-layers"));
                    break;
                case "--res":
                    resourceDir = ReadValue(args, ref i, "--res");
                    break;
            }
        }

        var repoRoot = FindRepositoryRoot();
        modelPath ??= Path.Combine(repoRoot, "models", "gemma-4-E4B", "gemma-4-E4B-it-Q4_K_M.gguf");
        //modelPath ??= Path.Combine(repoRoot, "models", "gemma-4-E4B", "gemma-4-E4B-it-Q8_0.gguf");
        mmprojPath ??= Path.Combine(repoRoot, "models", "gemma-4-E4B", "mmproj-BF16.gguf");
        resourceDir ??= Path.Combine(repoRoot, "GemmaStage.Session.SmokeTest", "res");
        return new SmokeTestOptions(modelPath, mmprojPath, backend, contextSize, gpuLayers, resourceDir);
    }

    private static string ReadValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {optionName}.");
        }

        index++;
        return args[index];
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "GemmaStage")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root from the smoke test output directory.");
    }
}
