using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace GemmaStage.Boards
{
    /// <summary>
    /// Floating world-space "Undo" button for the drawing board. Mirrors the look and
    /// interaction surface of <see cref="PayAttentionButton"/> (Canvas + UGUI Button +
    /// TrackedDeviceGraphicRaycaster) so both board controls feel the same to the player.
    /// Press routes through <see cref="DrawingBoard.Undo"/>; briefly tints the
    /// background for press confirmation.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UndoButton : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] Button button;
        [SerializeField] DrawingBoard drawingBoard;

        [Header("Press tint (optional)")]
        [SerializeField] Image tintImage;
        [SerializeField] Color tintColor = new Color(1f, 0.85f, 0.4f, 1f);
        [SerializeField] float tintDuration = 0.15f;

        Color tintInitialColor;
        bool tintInitialCaptured;
        Coroutine tintRoutine;

        void Awake()
        {
            if (tintImage != null)
            {
                tintInitialColor = tintImage.color;
                tintInitialCaptured = true;
            }
        }

        void OnEnable()
        {
            if (button != null) button.onClick.AddListener(OnClicked);
        }

        void OnDisable()
        {
            if (button != null) button.onClick.RemoveListener(OnClicked);
            if (tintRoutine != null)
            {
                StopCoroutine(tintRoutine);
                tintRoutine = null;
            }
            if (tintInitialCaptured && tintImage != null)
                tintImage.color = tintInitialColor;
        }

        void OnClicked()
        {
            if (drawingBoard != null) drawingBoard.Undo();
            if (tintImage == null) return;
            if (tintRoutine != null) StopCoroutine(tintRoutine);
            tintRoutine = StartCoroutine(PlayTint());
        }

        IEnumerator PlayTint()
        {
            tintImage.color = tintColor;
            yield return new WaitForSeconds(tintDuration);
            if (tintInitialCaptured) tintImage.color = tintInitialColor;
            tintRoutine = null;
        }
    }
}
