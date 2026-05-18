using System;
using TMPro;
using UnityEngine;

namespace GemmaStage.Core
{
    // GAME_DESIGN.md §5.1 + PHASE_5_TASKS.md §5.6:
    //   Lobby   → wall clock HH:mm in countdown color.
    //   Session → MM:SS countdown in countdown color, regular beep at start and at 1 min remaining.
    //   Penalty → MM:SS count-up in penalty color, distinct beep at the 0-cross.
    public class WristTimer : MonoBehaviour
    {
        [Header("Anchors (siblings under XRRig)")]
        [SerializeField] Transform leftHandAnchor;
        [SerializeField] Transform rightHandAnchor;

        [Tooltip("Pose applied when parented under the right hand. Tune this in the Inspector to fit the right wrist; the left hand mirrors it across the body midline (see mirrorPoseForLeftHand).")]
        [SerializeField] Vector3 localPosition = Vector3.zero;
        [SerializeField] Vector3 localEulerAngles = Vector3.zero;

        [Tooltip("When true (default), the left-hand pose mirrors the right-hand pose across the body midline: position.x is negated, eulerAngles.y and z are negated. Disable to use the same raw pose on both hands.")]
        [SerializeField] bool mirrorPoseForLeftHand = true;

        [Header("Display")]
        [SerializeField] TMP_Text label;
        // Colors come from Styles.TimerCountdown / Styles.TimerPenalty — no Inspector override.

        [Header("Audio")]
        [SerializeField] AudioSource audioSource;
        [Tooltip("Played at session start and again at 1 minute remaining.")]
        [SerializeField] AudioClip regularBeepClip;
        [Tooltip("Played the moment the countdown crosses 0. Distinct from the regular beep.")]
        [SerializeField] AudioClip timeoutBeepClip;

        enum Mode { LobbyClock, Countdown, Penalty, Stopped }
        Mode _mode = Mode.LobbyClock;
        bool _paused;
        float _remainingSeconds;
        float _penaltySeconds;
        bool _oneMinuteCueFired;
        bool _warnedMissingRegularClip;
        bool _warnedMissingTimeoutClip;
        bool _warnedMissingLabel;

        public bool  InPenaltyMode    => _mode == Mode.Penalty;
        public float RemainingSeconds => _remainingSeconds;
        public float PenaltySeconds   => _penaltySeconds;

        public void SetWatchHand(WatchHand hand)
        {
            var anchor = hand == WatchHand.Left ? leftHandAnchor : rightHandAnchor;
            if (anchor == null)
            {
                Debug.LogWarning($"WristTimer: {hand} hand anchor is not assigned — cannot reparent.", this);
                return;
            }

            if (transform.parent != anchor)
                transform.SetParent(anchor, worldPositionStays: false);

            // OpenXR controllers share the same local-axis convention (both have +X = controller-right,
            // +Y = up, +Z = forward). When held naturally, the right controller's +X points outward and
            // the left controller's +X points inward toward the body, so a pose tuned for the right
            // wrist needs to be mirrored across the body midline to land correctly on the left wrist.
            bool mirror = hand == WatchHand.Left && mirrorPoseForLeftHand;
            transform.localPosition = mirror
                ? new Vector3(-localPosition.x, localPosition.y, localPosition.z)
                : localPosition;
            transform.localEulerAngles = mirror
                ? new Vector3(localEulerAngles.x, -localEulerAngles.y, -localEulerAngles.z)
                : localEulerAngles;
            transform.localScale = Vector3.one;
        }

        public void StartCountdown(int minutes)
        {
            _remainingSeconds = Mathf.Max(0, minutes) * 60f;
            _penaltySeconds = 0f;
            // Skip the 1-min cue if the duration was already at or under 1 minute —
            // otherwise we'd double-beep right after the start beep.
            _oneMinuteCueFired = minutes <= 1;
            _mode = Mode.Countdown;

            PlayBeep(regularBeepClip, ref _warnedMissingRegularClip, "regularBeepClip");
            RenderLabel();
        }

        // Freezes the visual at its currently-rendered MM:SS + color. Update() and RenderLabel()
        // become no-ops in Stopped, so the last text/color set by Countdown/Penalty stays on
        // screen. Reserved for hard-stop scenarios; the End Session Yes branch uses
        // SwitchToWallClock instead so the watch keeps showing wall-clock time during
        // Final Q&A / Evaluation.
        public void Stop()
        {
            if (_mode == Mode.Stopped) return;
            _mode = Mode.Stopped;
        }

        // Pauses the active Countdown / Penalty timer. The display freezes at its
        // current MM:SS + color until Resume() is called. Used by EndSessionController
        // while the End Session confirmation panel is visible (issue: timer must not
        // continue counting down while the player decides).
        public void Pause()  => _paused = true;
        public void Resume() => _paused = false;

        // Returns the watch to wall-clock display in the default countdown color.
        // Called when the live phase ends (End Session Yes) so the timer keeps
        // rendering useful information during Final Q&A / Evaluation, and when the
        // session is cancelled back to the Lobby.
        public void SwitchToWallClock()
        {
            _mode   = Mode.LobbyClock;
            _paused = false;
        }

        void Update()
        {
            if (_paused) return;

            switch (_mode)
            {
                case Mode.LobbyClock:
                    break;

                case Mode.Countdown:
                    _remainingSeconds = Mathf.Max(0f, _remainingSeconds - Time.deltaTime);

                    if (_remainingSeconds <= 0f)
                    {
                        PlayBeep(timeoutBeepClip, ref _warnedMissingTimeoutClip, "timeoutBeepClip");
                        _mode = Mode.Penalty;
                        _penaltySeconds = 0f;
                    }
                    else if (!_oneMinuteCueFired && _remainingSeconds <= 60f)
                    {
                        _oneMinuteCueFired = true;
                        PlayBeep(regularBeepClip, ref _warnedMissingRegularClip, "regularBeepClip");
                    }
                    break;

                case Mode.Penalty:
                    _penaltySeconds += Time.deltaTime;
                    break;

                case Mode.Stopped:
                    return; // visual already frozen — skip RenderLabel
            }

            RenderLabel();
        }

        void RenderLabel()
        {
            if (label == null)
            {
                if (!_warnedMissingLabel)
                {
                    Debug.LogWarning("WristTimer: label is not assigned — nothing to render.", this);
                    _warnedMissingLabel = true;
                }
                return;
            }

            switch (_mode)
            {
                case Mode.LobbyClock:
                    label.text = DateTime.Now.ToString("HH:mm");
                    label.color = Styles.TimerCountdown;
                    break;
                case Mode.Countdown:
                    label.text = FormatMmSs(_remainingSeconds);
                    label.color = Styles.TimerCountdown;
                    break;
                case Mode.Penalty:
                    label.text = FormatMmSs(_penaltySeconds);
                    label.color = Styles.TimerPenalty;
                    break;
                case Mode.Stopped:
                    break; // last-rendered values stay frozen
            }
        }

        void PlayBeep(AudioClip clip, ref bool warned, string fieldName)
        {
            if (clip == null)
            {
                if (!warned)
                {
                    Debug.LogWarning($"WristTimer: {fieldName} AudioClip not assigned — beep skipped.", this);
                    warned = true;
                }
                return;
            }
            if (audioSource == null) return;
            audioSource.PlayOneShot(clip);
        }

        static string FormatMmSs(float seconds)
        {
            int total = Mathf.FloorToInt(seconds);
            int mm = total / 60;
            int ss = total % 60;
            return $"{mm:00}:{ss:00}";
        }
    }
}
