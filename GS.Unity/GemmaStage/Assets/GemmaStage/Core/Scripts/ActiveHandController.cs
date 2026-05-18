using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace GemmaStage.Core
{
    /// <summary>
    /// First controller to press Select owns the ray (far-casting). Implements the active-hand
    /// rule from GAME_DESIGN §4.2. Only far-casting is gated — near grab and poke remain active
    /// on both hands so markers and on-board buttons are always reachable.
    ///
    /// A/X (below face buttons) also claim the active hand in addition to triggering ray-toggle
    /// logic via RayController.OnBelowButtonPressed (GS-175).
    /// </summary>
    public class ActiveHandController : MonoBehaviour
    {
        [SerializeField] InputActionReference leftSelect;
        [SerializeField] InputActionReference rightSelect;

        [Tooltip("X button (left controller primary). Claims active hand; also drives ray-toggle logic in Performance mode.")]
        [SerializeField] InputActionReference leftBelowButton;

        [Tooltip("A button (right controller primary). Claims active hand; also drives ray-toggle logic in Performance mode.")]
        [SerializeField] InputActionReference rightBelowButton;

        [Tooltip("NearFarInteractor on the left hand. Only far-casting is toggled; near grab stays active on both hands.")]
        [SerializeField] XRBaseInteractor leftInteractor;

        [Tooltip("NearFarInteractor on the right hand. Only far-casting is toggled; near grab stays active on both hands.")]
        [SerializeField] XRBaseInteractor rightInteractor;

        [Tooltip("Hand that owns the ray on Awake before any trigger press.")]
        [SerializeField] Hand initialHand = Hand.Right;

        [Tooltip("Policy gate. When in Performance mode, suppresses far-casting until the manual toggle (5.5.2) or a far panel temporarily enables ray interaction.")]
        [SerializeField] RayController rayController;

        public Hand ActiveHand { get; private set; } = Hand.None;
        public event Action<Hand> OnActiveHandChanged;

        void Awake()
        {
            if (rayController == null)
                rayController = FindAnyObjectByType<RayController>();
        }

        void OnEnable()
        {
            BindAction(leftSelect,      ref _leftSelectEnabled,  OnLeftPerformed);
            BindAction(rightSelect,     ref _rightSelectEnabled, OnRightPerformed);
            BindAction(leftBelowButton, ref _leftBelowEnabled,   OnLeftBelowPerformed);
            BindAction(rightBelowButton,ref _rightBelowEnabled,  OnRightBelowPerformed);
            if (rayController != null)
                rayController.OnPolicyChanged += OnRayPolicyChanged;
        }

        void Start()
        {
            // Apply the initial hand in Start, not OnEnable: subscribers to OnActiveHandChanged
            // (e.g. UI panels) hook in during their own OnEnable, and Start runs strictly after
            // every component's OnEnable, so the initial transition is visible to all listeners.
            SetActiveHand(initialHand);
        }

        void OnDisable()
        {
            UnbindAction(leftSelect,       ref _leftSelectEnabled,  OnLeftPerformed);
            UnbindAction(rightSelect,      ref _rightSelectEnabled, OnRightPerformed);
            UnbindAction(leftBelowButton,  ref _leftBelowEnabled,   OnLeftBelowPerformed);
            UnbindAction(rightBelowButton, ref _rightBelowEnabled,  OnRightBelowPerformed);
            if (rayController != null)
                rayController.OnPolicyChanged -= OnRayPolicyChanged;
        }

        // Trigger (Select) — claims active hand only.
        void OnLeftPerformed(InputAction.CallbackContext _)  => SetActiveHand(Hand.Left);
        void OnRightPerformed(InputAction.CallbackContext _) => SetActiveHand(Hand.Right);

        // Below face button (X / A) — claims active hand AND drives ray-toggle logic.
        void OnLeftBelowPerformed(InputAction.CallbackContext _)  => HandleBelowButton(Hand.Left);
        void OnRightBelowPerformed(InputAction.CallbackContext _) => HandleBelowButton(Hand.Right);

        void HandleBelowButton(Hand hand)
        {
            bool wasAlreadyActive = ActiveHand == hand;
            SetActiveHand(hand);
            rayController?.OnBelowButtonPressed(hand, wasAlreadyActive);
        }

        void OnRayPolicyChanged() => ApplyActiveHand();

        void SetActiveHand(Hand hand)
        {
            if (hand == ActiveHand) return;
            ActiveHand = hand;
            ApplyActiveHand();
            OnActiveHandChanged?.Invoke(hand);
        }

        void ApplyActiveHand()
        {
            SetFarCasting(leftInteractor,  Hand.Left,  ActiveHand == Hand.Left);
            SetFarCasting(rightInteractor, Hand.Right, ActiveHand == Hand.Right);
        }

        void SetFarCasting(XRBaseInteractor interactor, Hand hand, bool enabled)
        {
            if (interactor == null) return;
            if (enabled && rayController != null && !rayController.IsFarCastingAllowed(hand))
                enabled = false;
            if (interactor is NearFarInteractor nfi)
                nfi.enableFarCasting = enabled;
            else
                interactor.gameObject.SetActive(enabled);
        }

        // Helpers to avoid duplicated null-checks across OnEnable/OnDisable.
        bool _leftSelectEnabled, _rightSelectEnabled, _leftBelowEnabled, _rightBelowEnabled;

        static void BindAction(InputActionReference r, ref bool flag, Action<InputAction.CallbackContext> handler)
        {
            if (r == null || r.action == null) return;
            r.action.performed += handler;
            r.action.Enable();
            flag = true;
        }

        static void UnbindAction(InputActionReference r, ref bool flag, Action<InputAction.CallbackContext> handler)
        {
            if (!flag || r == null || r.action == null) return;
            r.action.performed -= handler;
            flag = false;
        }
    }
}
