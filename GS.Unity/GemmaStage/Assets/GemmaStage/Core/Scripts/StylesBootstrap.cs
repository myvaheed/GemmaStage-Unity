using TMPro;
using UnityEngine;

namespace GemmaStage.Core
{
    /// <summary>
    /// Injects the Inter TMP_FontAsset into <see cref="Styles.Type.Inter"/> on Awake.
    /// Static classes can't hold Inspector references, so a tiny bootstrap MonoBehaviour
    /// owns the asset reference. Place one instance in the Shared scene (alongside the
    /// other persistent systems) — the assignment is idempotent across additive loads.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class StylesBootstrap : MonoBehaviour
    {
        [SerializeField] private TMP_FontAsset interFont;

        private void Awake()
        {
            if (interFont != null)
            {
                Styles.Type.Inter = interFont;
            }
        }
    }
}
