namespace GemmaStage.Core
{
    public sealed class GameSettings
    {
        public bool VignetteEnabled = true;
        public TurningMode TurningMode = TurningMode.Off;
        public string MicrophoneDeviceName = string.Empty;
        public LanguageCode Language = LanguageCode.English;
        public float MasterVolume = 1f;
        public WatchHand WatchHand = WatchHand.Right;
        public bool EchoEnabled = false;
        public float EchoLevel = 0.5f;
    }

    public enum TurningMode { Off, Snap }

    public enum LanguageCode { English, French, German, Spanish, Turkish, Italian, Russian /* debug only, no need to mention on DOCS */ }

    public enum WatchHand { Right, Left }
}
