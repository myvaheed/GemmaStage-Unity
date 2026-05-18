using System.Collections.Generic;

namespace GemmaStage.Core
{
    public sealed class LobbySessionConfig
    {
        public EnvironmentId Environment = EnvironmentId.Stage;
        public int DurationMinutes = 10;
        public bool LiveQaEnabled = true;
        public DisturbanceLevel Disturbance = DisturbanceLevel.Middle;
        public bool FinalQaEnabled = true;
        public string GroundTruthDocPath = string.Empty;
        public GroundTruthAttachmentKind GroundTruthKind = GroundTruthAttachmentKind.None;
        public UnityEngine.Texture2D GroundTruthRenderedTexture; // attach-time preview/validation artifact; not semantic processing
        public int GroundTruthPdfPageCount;
        public List<SlideEntry> Slides = new();
    }

    public enum GroundTruthAttachmentKind
    {
        None = 0,
        Text = 1,
        Pdf = 2,
        Image = 3
    }

    public sealed class SlideEntry
    {
        public string SourcePath;
        public int PdfPageIndex; // 0 for image entries; 0..N-1 for pages of a PDF
        public UnityEngine.Texture2D RenderedTexture; // rasterized at attach; owned by the picker
    }

    public enum DisturbanceLevel
    {
        Low = 0,
        Middle = 1,
        High = 2
    }

    public static class DisturbanceLevelExtensions
    {
        public static float ToProbability(this DisturbanceLevel level) => level switch
        {
            DisturbanceLevel.Low => 0.15f,
            DisturbanceLevel.Middle => 0.30f,
            DisturbanceLevel.High => 0.80f,
            _ => 0.30f,
        };
    }
}
