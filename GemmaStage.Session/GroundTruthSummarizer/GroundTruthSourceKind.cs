using System;
using System.IO;

namespace GemmaStage.Session.GroundTruthSummarizer;

public enum GroundTruthSourceKind
{
    Unsupported,
    Text,
    Pdf,
    Image,
}

public static class GroundTruthSourceKindDetector
{
    public static GroundTruthSourceKind Detect(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return GroundTruthSourceKind.Unsupported;
        }

        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext))
        {
            return GroundTruthSourceKind.Unsupported;
        }

        return ext.ToLowerInvariant() switch
        {
            ".txt" or ".md" => GroundTruthSourceKind.Text,
            ".pdf" => GroundTruthSourceKind.Pdf,
            ".jpg" or ".jpeg" or ".png" => GroundTruthSourceKind.Image,
            _ => GroundTruthSourceKind.Unsupported,
        };
    }
}
