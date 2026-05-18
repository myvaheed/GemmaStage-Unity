namespace GemmaStage.Session.PoC;

internal sealed record PocOptions(
    string WavPath,
    string ModelPath,
    string? MmprojPath,
    string? Backend,
    int ContextSize,
    int GpuLayers,
    int X,
    int Y,
    int C,
    double ThresholdDb,
    int DebriefMinSeconds,
    string OutDir,
    string? XlsxPath,
    string? GroundTruthPath,
    bool EnableLiveQA,
    bool EnableFinalQA)
{
    public static PocOptions Parse(string[] args)
    {
        string? wavPath = null;
        string? modelPath = null;
        string? mmprojPath = null;
        string? backend = null;
        int contextSize = 8192;
        int gpuLayers = -1;
        int x = 15;
        int y = 300;
        int c = 300;
        double thresholdDb = -45;
        int debriefMinSeconds = 60;
        string? outDir = null;
        bool generateXlsx = false;
        string? groundTruthPath = null;
        bool enableLiveQA = false;
        bool enableFinalQA = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--wav":
                    wavPath = ReadValue(args, ref i, "--wav");
                    break;
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
                case "--x":
                    x = int.Parse(ReadValue(args, ref i, "--x"));
                    break;
                case "--y":
                    y = int.Parse(ReadValue(args, ref i, "--y"));
                    break;
                case "--c":
                    c = int.Parse(ReadValue(args, ref i, "--c"));
                    break;
                case "--threshold-db":
                    thresholdDb = double.Parse(ReadValue(args, ref i, "--threshold-db"));
                    break;
                case "--debrief-min-seconds":
                    debriefMinSeconds = int.Parse(ReadValue(args, ref i, "--debrief-min-seconds"));
                    break;
                case "--out":
                    outDir = ReadValue(args, ref i, "--out");
                    break;
                case "--xlsx":
                    generateXlsx = true;
                    break;
                case "--ground-truth":
                    groundTruthPath = ReadValue(args, ref i, "--ground-truth");
                    break;
                case "--live-qa":
                case "--liveQA":
                    enableLiveQA = true;
                    break;
                case "--final-qa":
                case "--finalQA":
                case "--enable-final-qa":
                    enableFinalQA = true;
                    break;
            }
        }

        var repoRoot = FindRepositoryRoot();
        modelPath ??= Path.Combine(repoRoot, "models", "gemma-4-E4B", "gemma-4-E4B-it-Q4_K_M.gguf");
        mmprojPath ??= Path.Combine(repoRoot, "models", "gemma-4-E4B", "mmproj-BF16.gguf");
        outDir ??= Path.Combine(repoRoot, "logs", "run-001");

        if (string.IsNullOrWhiteSpace(wavPath))
        {
            throw new ArgumentException("--wav is required.");
        }

        if ((enableLiveQA || enableFinalQA) && string.IsNullOrWhiteSpace(groundTruthPath))
        {
            throw new ArgumentException("--live-qa / --final-qa require --ground-truth so the simulated speaker has material to answer from.");
        }

        var folderName = new DirectoryInfo(outDir).Name;
        var xlsxPath = generateXlsx ? Path.Combine(outDir, folderName + ".xlsx") : null;

        return new PocOptions(
            wavPath, modelPath, mmprojPath, backend,
            contextSize, gpuLayers,
            x, y, c, thresholdDb,
            debriefMinSeconds, outDir, xlsxPath, groundTruthPath, enableLiveQA, enableFinalQA);
    }

    public static void PrintUsage()
    {
        Console.WriteLine("Usage: gemma_session_poc --wav <path> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --wav <path>                        Path to input .wav file (required)");
        Console.WriteLine("  --model <path>                      Path to GGUF model file");
        Console.WriteLine("  --mmproj <path>                     Path to mmproj GGUF file");
        Console.WriteLine("  --backend <name>                    Backend (auto/cuda/vulkan)");
        Console.WriteLine("  --context-size <n>                  Context size (default: 8192)");
        Console.WriteLine("  --gpu-layers <n>                    GPU layers (default: -1 = all)");
        Console.WriteLine("  --x <n>                             Chunker target window in seconds (default: 15)");
        Console.WriteLine("  --y <n>                             Chunker trailing silence in milliseconds (default: 300)");
        Console.WriteLine("  --c <n>                             Chunker window granularity in ms (default: 300)");
        Console.WriteLine("  --threshold-db <n>                  Volume threshold in dBFS (default: -45)");
        Console.WriteLine("  --debrief-min-seconds <n>           Debrief minimum elapsed seconds (default: 60)");
        Console.WriteLine("  --out <dir>                         Output directory (default: logs/run-001)");
        Console.WriteLine("  --xlsx                              Export all session data to Excel (saved as <out-dir>/<folder-name>.xlsx)");
        Console.WriteLine("  --ground-truth <path>               Plain-text ground-truth file (drives GroundTruthSummarizer + Speaker answers)");
        Console.WriteLine("  --live-qa / --liveQA                Simulate Live Q&A: after every cycle with open concerns, pick one, ask it, and have the Speaker conversation answer it from the ground-truth text. Requires --ground-truth.");
        Console.WriteLine("  --final-qa / --finalQA              Run Clarification + Final Q&A rounds for every unresolved revised question, answered by the Speaker conversation. Requires --ground-truth.");
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

        throw new DirectoryNotFoundException("Could not find the repository root from the output directory.");
    }
}
