using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace GemmaStage.Boards
{
    /// <summary>
    /// Select filter that pins a tray socket to a single marker instance — a released Red marker
    /// only seats in the Red slot. <see cref="MarkerTray"/> wires this up at runtime.
    /// </summary>
    public class MarkerSocketFilter : MonoBehaviour, IXRSelectFilter
    {
        public IXRSelectInteractable AcceptedInteractable { get; set; }

        public bool canProcess => isActiveAndEnabled;

        public bool Process(IXRSelectInteractor interactor, IXRSelectInteractable interactable)
            => AcceptedInteractable != null && interactable == AcceptedInteractable;
    }
}
