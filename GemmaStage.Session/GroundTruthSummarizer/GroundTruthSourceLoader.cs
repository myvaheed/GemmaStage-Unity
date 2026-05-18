using GemmaStage.Session.Native;
using GemmaStage.Session.Perceptor;
using System;
using System.IO;
using UglyToad.PdfPig;

namespace GemmaStage.Session.GroundTruthSummarizer;

// Result of converting a user-picked ground-truth file into the plain text
// GroundTruthSummarizer ingests. Per docs/SESSION_ARCHITECTURE.md Sec. 4.6:
//
//   .txt / .md -> read directly
//   .pdf       -> first-page text only (multi-page deferred)
//   .jpg/.png  -> routed through I2tConversation; the examination becomes the text
//
// Text is null when the file is missing, unsupported, unreadable, conversion
// failed, or the resulting text is empty. The runner treats null as a skip.
public sealed record GroundTruthSourceLoadResult(
    GroundTruthSourceKind Kind,
    string? Text,
    PerceptorTurnResult? ImageTurn);

public sealed class GroundTruthSourceLoader
{
    private readonly EngineHandle _engine;
    private readonly Action<string>? _warn;

    public GroundTruthSourceLoader(EngineHandle engine, Action<string>? warn = null)
    {
        Guard.NotNull(engine);
        _engine = engine;
        _warn = warn;
    }

    public GroundTruthSourceLoadResult Load(string filePath)
    {
        Guard.NotNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            Warn($"GroundTruthSourceLoader: file not found at \"{filePath}\".");
            return new GroundTruthSourceLoadResult(GroundTruthSourceKind.Unsupported, null, null);
        }

        var kind = GroundTruthSourceKindDetector.Detect(filePath);
        return kind switch
        {
            GroundTruthSourceKind.Text => LoadText(filePath),
            GroundTruthSourceKind.Pdf => LoadPdfFirstPage(filePath),
            GroundTruthSourceKind.Image => LoadImage(filePath),
            _ => UnsupportedResult(filePath),
        };
    }

    private GroundTruthSourceLoadResult LoadText(string filePath)
    {
        try
        {
            var text = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(text))
            {
                Warn($"GroundTruthSourceLoader: \"{filePath}\" is empty.");
                return new GroundTruthSourceLoadResult(GroundTruthSourceKind.Text, null, null);
            }

            return new GroundTruthSourceLoadResult(GroundTruthSourceKind.Text, text, null);
        }
        catch (IOException ex)
        {
            Warn($"GroundTruthSourceLoader: failed to read \"{filePath}\": {ex.Message}");
            return new GroundTruthSourceLoadResult(GroundTruthSourceKind.Text, null, null);
        }
    }

    private GroundTruthSourceLoadResult LoadPdfFirstPage(string filePath)
    {
        try
        {
            using var pdf = PdfDocument.Open(filePath);
            if (pdf.NumberOfPages == 0)
            {
                Warn($"GroundTruthSourceLoader: PDF \"{filePath}\" has no pages.");
                return new GroundTruthSourceLoadResult(GroundTruthSourceKind.Pdf, null, null);
            }

            var firstPage = pdf.GetPage(1);
            var text = firstPage.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                Warn($"GroundTruthSourceLoader: PDF \"{filePath}\" first page yielded no extractable text (likely scanned/image-only - OCR is deferred).");
                return new GroundTruthSourceLoadResult(GroundTruthSourceKind.Pdf, null, null);
            }

            return new GroundTruthSourceLoadResult(GroundTruthSourceKind.Pdf, text, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Warn($"GroundTruthSourceLoader: failed to read PDF \"{filePath}\": {ex.Message}");
            return new GroundTruthSourceLoadResult(GroundTruthSourceKind.Pdf, null, null);
        }
    }

    private GroundTruthSourceLoadResult LoadImage(string filePath)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(filePath);
        }
        catch (IOException ex)
        {
            Warn($"GroundTruthSourceLoader: failed to read image \"{filePath}\": {ex.Message}");
            return new GroundTruthSourceLoadResult(GroundTruthSourceKind.Image, null, null);
        }

        if (bytes.Length == 0)
        {
            Warn($"GroundTruthSourceLoader: image \"{filePath}\" is empty.");
            return new GroundTruthSourceLoadResult(GroundTruthSourceKind.Image, null, null);
        }

        return LoadFromBytes(bytes, context: $"image \"{filePath}\"");
    }

    // Handles the RasterizedImageBytes case: bytes are already in memory (pre-encoded PNG
    // from a Texture2D at Lobby attach-time). Routes directly through I2tConversation.
    public GroundTruthSourceLoadResult LoadFromBytes(byte[] bytes)
        => LoadFromBytes(bytes, context: "rasterized image bytes");

    private GroundTruthSourceLoadResult LoadFromBytes(byte[] bytes, string context)
    {
        if (bytes is null || bytes.Length == 0)
        {
            Warn($"GroundTruthSourceLoader: {context} is null or empty.");
            return new GroundTruthSourceLoadResult(GroundTruthSourceKind.Image, null, null);
        }

        PerceptorTurnResult turn;
        using (var i2t = new I2tConversation(_engine, _warn))
        {
            turn = i2t.SendImage(bytes);
        }

        if (turn.Parse.Image is { } image && !string.IsNullOrWhiteSpace(image.Examination))
        {
            return new GroundTruthSourceLoadResult(GroundTruthSourceKind.Image, image.Examination, turn);
        }

        var error = turn.Parse.Error ?? "I2t did not return an image observation";
        Warn($"GroundTruthSourceLoader: {context} examination failed: {error}");
        return new GroundTruthSourceLoadResult(GroundTruthSourceKind.Image, null, turn);
    }

    private GroundTruthSourceLoadResult UnsupportedResult(string filePath)
    {
        Warn($"GroundTruthSourceLoader: \"{filePath}\" has an unsupported extension. Supported: .txt, .md, .pdf, .jpg, .jpeg, .png.");
        return new GroundTruthSourceLoadResult(GroundTruthSourceKind.Unsupported, null, null);
    }

    private void Warn(string message)
    {
        _warn?.Invoke(message);
    }
}
