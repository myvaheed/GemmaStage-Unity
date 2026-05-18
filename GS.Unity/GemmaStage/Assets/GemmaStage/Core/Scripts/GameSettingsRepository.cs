using UnityEngine;

namespace GemmaStage.Core
{
    public static class GameSettingsRepository
    {
        const string KeyVignette = "GS_Vignette";
        const string KeyTurnMode = "GS_TurnMode";
        const string KeyMicDevice = "GS_MicDevice";
        const string KeyLanguage = "GS_Language";
        const string KeyMasterVolume = "GS_MasterVolume";
        const string KeyWatchHand = "GS_WatchHand";
        const string KeyEchoEnabled = "GS_EchoEnabled";
        const string KeyEchoLevel = "GS_EchoLevel";

        public static GameSettings Load()
        {
            var defaults = new GameSettings();
            return new GameSettings
            {
                VignetteEnabled = PlayerPrefs.GetInt(KeyVignette, defaults.VignetteEnabled ? 1 : 0) != 0,
                TurningMode = ClampEnum(PlayerPrefs.GetInt(KeyTurnMode, (int)defaults.TurningMode), defaults.TurningMode),
                MicrophoneDeviceName = PlayerPrefs.GetString(KeyMicDevice, defaults.MicrophoneDeviceName),
                Language = ClampEnum(PlayerPrefs.GetInt(KeyLanguage, (int)defaults.Language), defaults.Language),
                MasterVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(KeyMasterVolume, defaults.MasterVolume)),
                WatchHand = ClampEnum(PlayerPrefs.GetInt(KeyWatchHand, (int)defaults.WatchHand), defaults.WatchHand),
                EchoEnabled = PlayerPrefs.GetInt(KeyEchoEnabled, defaults.EchoEnabled ? 1 : 0) != 0,
                EchoLevel = Mathf.Clamp01(PlayerPrefs.GetFloat(KeyEchoLevel, defaults.EchoLevel)),
            };
        }

        public static void Save(GameSettings settings)
        {
            if (settings == null)
                return;

            PlayerPrefs.SetInt(KeyVignette, settings.VignetteEnabled ? 1 : 0);
            PlayerPrefs.SetInt(KeyTurnMode, (int)settings.TurningMode);
            PlayerPrefs.SetString(KeyMicDevice, settings.MicrophoneDeviceName ?? string.Empty);
            PlayerPrefs.SetInt(KeyLanguage, (int)settings.Language);
            PlayerPrefs.SetFloat(KeyMasterVolume, Mathf.Clamp01(settings.MasterVolume));
            PlayerPrefs.SetInt(KeyWatchHand, (int)settings.WatchHand);
            PlayerPrefs.SetInt(KeyEchoEnabled, settings.EchoEnabled ? 1 : 0);
            PlayerPrefs.SetFloat(KeyEchoLevel, Mathf.Clamp01(settings.EchoLevel));
            PlayerPrefs.Save();
        }

        static T ClampEnum<T>(int rawValue, T fallback) where T : System.Enum
        {
            return System.Enum.IsDefined(typeof(T), rawValue) ? (T)(object)rawValue : fallback;
        }
    }
}
