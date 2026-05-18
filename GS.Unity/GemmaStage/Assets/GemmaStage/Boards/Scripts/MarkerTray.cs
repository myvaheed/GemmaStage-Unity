using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace GemmaStage.Boards
{
    /// <summary>
    /// Holds N socket slots, one per marker variant. On Awake, instantiates each slot's variant
    /// into the slot's attach transform, force-seats it via the XRSocketInteractor, and pins
    /// that instance with a <see cref="MarkerSocketFilter"/> so a released Red marker only
    /// re-seats in the Red slot.
    ///
    /// On controller release, the tray force-re-seats the marker into its assigned socket.
    /// Without this, releasing the marker over the tray panel lets the rigidbody fall through
    /// the panel because the marker's Instantaneous-movement grab was bypassing physics until
    /// the moment of release.
    /// </summary>
    public class MarkerTray : MonoBehaviour
    {
        [Serializable]
        public class Slot
        {
            public XRSocketInteractor socket;
            public GameObject markerVariantPrefab;
        }

        [SerializeField] List<Slot> slots = new();

        readonly List<XRBaseInteractable> spawnedMarkers = new();

        public IReadOnlyList<XRBaseInteractable> SpawnedMarkers => spawnedMarkers;

        void Awake()
        {
            foreach (var slot in slots)
            {
                if (slot == null || slot.socket == null || slot.markerVariantPrefab == null)
                    continue;

                var socketAttach = slot.socket.attachTransform != null
                    ? slot.socket.attachTransform
                    : slot.socket.transform;

                // Pre-offset the spawn so the marker's own attach lands exactly on the socket's
                // attach. Without this, SelectEnter would shift the marker by the marker-attach
                // local offset on the first frame.
                var prefabGrab = slot.markerVariantPrefab.GetComponent<XRGrabInteractable>();
                var prefabAttach = prefabGrab != null ? prefabGrab.attachTransform : null;
                var attachLocalOffset = prefabAttach != null
                    ? prefabAttach.localPosition
                    : Vector3.zero;
                var spawnPosition = socketAttach.position - socketAttach.rotation * attachLocalOffset;

                // Parent under the tray so DrawingMarker.Awake's GetComponentInParent<DrawingBoard>
                // resolves on first frame. Without a parent argument, Awake fires before any later
                // socket reparenting and the cached board reference stays null forever.
                var instance = Instantiate(slot.markerVariantPrefab, spawnPosition, socketAttach.rotation, transform);
                var interactable = instance.GetComponent<XRBaseInteractable>();
                if (interactable == null)
                {
                    Debug.LogError($"[MarkerTray] Variant '{slot.markerVariantPrefab.name}' has no XRBaseInteractable; skipping slot.", this);
                    Destroy(instance);
                    continue;
                }

                spawnedMarkers.Add(interactable);

                var filter = slot.socket.gameObject.AddComponent<MarkerSocketFilter>();
                filter.AcceptedInteractable = interactable;
                slot.socket.selectFilters.Add(filter);

                if (instance.TryGetComponent<Rigidbody>(out var rb))
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }

                // Re-seat the marker into its socket whenever a non-socket interactor
                // (i.e. the player's hand) releases it. The handler is captured per-marker
                // so we keep the socket reference even if the dictionary later changes.
                var capturedInteractable = interactable;
                var capturedSocket = slot.socket;
                interactable.selectExited.AddListener(args => OnMarkerSelectExited(args, capturedInteractable, capturedSocket));
            }
        }

        void OnMarkerSelectExited(SelectExitEventArgs args, XRBaseInteractable interactable, XRSocketInteractor socket)
        {
            if (args.interactorObject == (IXRSelectInteractor)socket) return; // socket released because hand grabbed
            if (interactable == null || socket == null) return;
            StartCoroutine(ReseatNextFrame(interactable, socket));
        }

        IEnumerator ReseatNextFrame(XRBaseInteractable interactable, XRSocketInteractor socket)
        {
            // Defer one frame so the InteractionManager finishes dispatching the current
            // selectExited before we issue a new SelectEnter on it. Calling SelectEnter
            // synchronously inside a selectExited listener is supported but has caused
            // ordering issues in the past with non-XRI components observing the events.
            yield return null;
            if (interactable == null || socket == null) yield break;
            if (interactable.isSelected) yield break;   // player grabbed it again during the wait
            if (socket.hasSelection) yield break;       // already has something seated
            var manager = socket.interactionManager;
            if (manager == null) yield break;
            manager.SelectEnter((IXRSelectInteractor)socket, (IXRSelectInteractable)interactable);
        }

        void Start()
        {
            // Force-seat each spawned marker into its socket. The interaction manager has
            // registered both sides by Start (sockets in their own Awake, markers in their
            // OnEnable, both before any Start runs). Sockets that have already hover-snapped
            // their marker are skipped to avoid a double-select warning.
            for (int i = 0; i < slots.Count && i < spawnedMarkers.Count; i++)
            {
                var slot = slots[i];
                var interactable = spawnedMarkers[i];
                if (slot?.socket == null || interactable == null) continue;
                if (slot.socket.hasSelection) continue;

                var manager = slot.socket.interactionManager;
                if (manager != null)
                    manager.SelectEnter((IXRSelectInteractor)slot.socket, (IXRSelectInteractable)interactable);
            }
        }
    }
}
