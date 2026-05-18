using System;
using UnityEngine;

namespace GemmaStage.Core
{
    public enum RayMode
    {
        Lobby,
        Performance,
    }

    // Owns the policy gate that decides whether a hand's far ray is allowed right now.
    // ActiveHandController is the only component that flips NearFarInteractor.enableFarCasting;
    // it consults IsFarCastingAllowed(Hand) before ever enabling and re-applies whenever
    // OnPolicyChanged fires. Lives on the XR Rig in Shared.unity.
    //
    // Sources that widen Performance-mode permission:
    //   - Manual A/X toggle (GS-175): ActiveHandController calls OnBelowButtonPressed each time
    //     A or X is pressed. The policy changes based on whether it was the active or inactive hand.
    //   - Far-panel auto-ray (PushFarPanelHold / PopFarPanelHold): End Session (5.9), Evaluation
    //     (5.11), and Results panels Push on show and Pop on close. While the refcount is > 0,
    //     IsFarCastingAllowed returns true; ApplyActiveHand still restricts to the active hand.
    public class RayController : MonoBehaviour
    {
        public RayMode CurrentMode { get; private set; } = RayMode.Lobby;
        public bool ManualOverrideOn { get; private set; }

        int _farPanelHoldCount;
        public bool FarPanelHoldActive => _farPanelHoldCount > 0;

        public event Action<RayMode> OnModeChanged;
        // Fires whenever IsFarCastingAllowed could change outcome for any hand: mode change,
        // manual-toggle flip, or far-panel hold 0↔1 transition. ActiveHandController re-evaluates
        // far casting on this event.
        public event Action OnPolicyChanged;

        public void SetMode(RayMode mode)
        {
            if (CurrentMode == mode) return;
            CurrentMode = mode;
            // Each entry into Performance starts with the ray off (per GAME_DESIGN §4.2).
            ManualOverrideOn = false;
            OnModeChanged?.Invoke(mode);
            OnPolicyChanged?.Invoke();
        }

        // Called by ActiveHandController when A (right) or X (left) is pressed, after the
        // hand claim has already been applied.
        //   wasAlreadyActive=true  → same hand pressed again → toggle ray
        //   wasAlreadyActive=false → inactive hand claimed  → ray off→on; ray on stays on
        public void OnBelowButtonPressed(Hand pressedHand, bool wasAlreadyActive)
        {
            if (CurrentMode == RayMode.Lobby) return;

            if (wasAlreadyActive)
                ManualOverrideOn = !ManualOverrideOn;
            else if (!ManualOverrideOn)
                ManualOverrideOn = true;

            OnPolicyChanged?.Invoke();
        }

        // Far panels (End Session 5.9, Evaluation 5.11, Results) push a hold while visible and
        // pop on close. Stacks across overlapping panels; only 0↔1 transitions notify policy.
        public void PushFarPanelHold()
        {
            _farPanelHoldCount++;
            if (_farPanelHoldCount == 1) OnPolicyChanged?.Invoke();
        }

        public void PopFarPanelHold()
        {
            if (_farPanelHoldCount == 0) return;
            _farPanelHoldCount--;
            if (_farPanelHoldCount == 0) OnPolicyChanged?.Invoke();
        }

        public bool IsFarCastingAllowed(Hand hand)
        {
            if (CurrentMode == RayMode.Lobby) return true;
            if (ManualOverrideOn) return true;
            if (FarPanelHoldActive) return true;
            return false;
        }
    }
}
