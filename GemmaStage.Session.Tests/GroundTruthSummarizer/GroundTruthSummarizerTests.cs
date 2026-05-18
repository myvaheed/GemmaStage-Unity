using GemmaStage.Session.GroundTruthSummarizer;

namespace GemmaStage.Session.Tests.GroundTruthSummarizer;

internal static class GroundTruthMapParserTests
{
    public static void Run()
    {
        var json = """
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_chunk_claims",
                    "arguments": "{\"claims\":[\"Sleep consolidates memory.\",\"Sleep loss harms immunity.\"]}"
                  }
                }
              ]
            }
            """;

        var parse = GroundTruthSummarizerResponseParser.Parse(json);
        AssertEx.True(!parse.IsFailure, $"MAP parse should succeed: {parse.Error}");
        AssertEx.True(parse.IsMap, "Should be a MAP result");
        AssertEx.Equal(2, parse.Map!.Claims.Count, "Both claims should round-trip");
        AssertEx.Equal("Sleep consolidates memory.", parse.Map.Claims[0], "First claim");
        AssertEx.Equal("Sleep loss harms immunity.", parse.Map.Claims[1], "Second claim");
    }
}

internal static class GroundTruthReduceParserTests
{
    public static void Run()
    {
        var json = """
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_ground_truth_decomposition",
                    "arguments": "{\"main_thesis\":\"Sleep is foundational to health.\",\"claims\":\"Sleep consolidates declarative memory.\nSleep loss harms immune responses.\"}"
                  }
                }
              ]
            }
            """;
        var parse = GroundTruthSummarizerResponseParser.Parse(json);
        AssertEx.True(!parse.IsFailure, $"REDUCE parse should succeed: {parse.Error}");
        AssertEx.True(parse.IsReduce, "Should be a REDUCE result");
        AssertEx.Equal("Sleep is foundational to health.", parse.Reduce!.MainThesis, "main_thesis should round-trip");
        AssertEx.Equal(2, parse.Reduce.Claims.Count, "Both claims should round-trip");
        AssertEx.Equal("Sleep consolidates declarative memory.", parse.Reduce.Claims[0], "First claim");
    }
}

internal static class GroundTruthSchemaTests
{
    public static void Run()
    {
        AssertEx.Equal("report_chunk_claims", GroundTruthSummarizerSchemas.MapToolName, "MAP tool name");
        AssertEx.Equal("report_ground_truth_decomposition", GroundTruthSummarizerSchemas.ReduceToolName, "REDUCE tool name");
    }
}

internal static class GroundTruthChunkBuildingTests
{
    public static void Run()
    {
        var text = "First sentence. Second one! Third? Yes.";
        var chunks = GroundTruthSummarizerRunner.BuildChunks(text, tokenBudget: 200);
        AssertEx.True(chunks.Count >= 1, $"Should produce at least one chunk; got {chunks.Count}");

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 30; i++) sb.Append($"Sentence {i} body. ");
        var manyChunks = GroundTruthSummarizerRunner.BuildChunks(sb.ToString(), tokenBudget: 5);
        AssertEx.True(manyChunks.Count > 1, $"Tiny budget should split into multiple chunks (got {manyChunks.Count})");
    }
}

internal static class GroundTruthSourceKindDetectorTests
{
    public static void Run()
    {
        AssertEx.Equal(GroundTruthSourceKind.Text,
            GroundTruthSourceKindDetector.Detect("notes.txt"), ".txt should detect as Text");
        AssertEx.Equal(GroundTruthSourceKind.Text,
            GroundTruthSourceKindDetector.Detect("README.md"), ".md should detect as Text");
        AssertEx.Equal(GroundTruthSourceKind.Pdf,
            GroundTruthSourceKindDetector.Detect("paper.pdf"), ".pdf should detect as Pdf");
        AssertEx.Equal(GroundTruthSourceKind.Image,
            GroundTruthSourceKindDetector.Detect("slide.jpg"), ".jpg should detect as Image");
        AssertEx.Equal(GroundTruthSourceKind.Image,
            GroundTruthSourceKindDetector.Detect("slide.JPEG"), ".JPEG should detect case-insensitively as Image");
        AssertEx.Equal(GroundTruthSourceKind.Image,
            GroundTruthSourceKindDetector.Detect("slide.png"), ".png should detect as Image");

        AssertEx.Equal(GroundTruthSourceKind.Unsupported,
            GroundTruthSourceKindDetector.Detect("paper.docx"), ".docx should be Unsupported");
        AssertEx.Equal(GroundTruthSourceKind.Unsupported,
            GroundTruthSourceKindDetector.Detect("notes"), "no extension should be Unsupported");
    }
}
