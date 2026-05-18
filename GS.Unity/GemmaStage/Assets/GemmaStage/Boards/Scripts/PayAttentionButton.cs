using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace GemmaStage.Boards
{
    /// <summary>
    /// Floating world-space button that asks the AI to "pay attention" to the
    /// associated board. Press routes through <see cref="BoardCapture.RequestCapture"/>;
    /// briefly tints the background for press confirmation. One instance authored
    /// per board prefab (DrawingBoard, PresentationBoard) per GAME_DESIGN §5.1.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PayAttentionButton : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] Button button;
        [SerializeField] BoardCapture boardCapture;

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
            if (boardCapture != null) boardCapture.RequestCapture();
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
