using UnityEngine;

namespace GemmaStage.Boards
{
    /// <summary>
    /// Per-marker color metadata. Read by DrawingMarker (Task 4.1.2) when registering strokes.
    /// Variants override <see cref="strokeColor"/> alongside the body material.
    /// </summary>
    public class MarkerColor : MonoBehaviour
    {
        [SerializeField] Color strokeColor = Color.black;

        public Color StrokeColor => strokeColor;
    }
}
