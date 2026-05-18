namespace GemmaStage.Session.GroundTruthSummarizer;

/// <summary>
/// Session-module-boundary contract for the ground-truth document input.
/// Produced by the Unity layer in SessionLayer.Begin(); consumed by
/// GroundTruthSummarizerRunner in the post-performance pipeline.
/// See docs/SESSION_ARCHITECTURE.md §4.5.
/// </summary>
public abstract class GroundTruthDocInput
{
    // Private constructor prevents subclassing outside this file.
    private GroundTruthDocInput() { }

    /// <summary>No ground-truth document attached. GroundTruthSummarizer is skipped.</summary>
    public static readonly GroundTruthDocInput None = new NoneCase();

    /// <summary>
    /// Plain-text file (.txt or .md). The session module opens and reads the file at
    /// evaluation time; the path must remain valid until RunGroundTruthSummarizer() is called.
    /// </summary>
    public static GroundTruthDocInput FilePath(string path) => new FilePathCase(path);

    /// <summary>
    /// Pre-rasterized image bytes (PNG). Typically produced at Lobby attach-time by encoding
    /// a Texture2D (image or PDF first page). Routed through I2tConversation for image examination.
    /// </summary>
    public static GroundTruthDocInput RasterizedImageBytes(byte[] bytes) => new RasterizedImageBytesCase(bytes);

    public sealed class NoneCase : GroundTruthDocInput
    {
        internal NoneCase() { }
        public override string ToString() => "None";
    }

    public sealed class FilePathCase : GroundTruthDocInput
    {
        public string Path { get; }
        internal FilePathCase(string path) => Path = path;
        public override string ToString() => $"FilePath(\"{Path}\")";
    }

    public sealed class RasterizedImageBytesCase : GroundTruthDocInput
    {
        public byte[] Bytes { get; }
        internal RasterizedImageBytesCase(byte[] bytes) => Bytes = bytes;
        public override string ToString() => $"RasterizedImageBytes({Bytes.Length} bytes)";
    }
}
