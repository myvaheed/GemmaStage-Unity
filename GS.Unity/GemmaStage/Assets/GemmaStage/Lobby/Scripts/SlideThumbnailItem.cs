using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GemmaStage.Lobby
{
    public class SlideThumbnailItem : MonoBehaviour
    {
        [SerializeField] RawImage thumbnailImage;
        [SerializeField] TextMeshProUGUI captionLabel;
        [SerializeField] Button moveUpButton;
        [SerializeField] Button moveDownButton;
        [SerializeField] Button removeButton;

        Texture2D _ownedTexture;
        bool _wired;

        public event Action<SlideThumbnailItem> Removed;
        public event Action<SlideThumbnailItem> MoveUpRequested;
        public event Action<SlideThumbnailItem> MoveDownRequested;

        void WireButtonsOnce()
        {
            if (_wired) return;
            _wired = true;
            if (removeButton != null)
                removeButton.onClick.AddListener(() => Removed?.Invoke(this));
            if (moveUpButton != null)
                moveUpButton.onClick.AddListener(() => MoveUpRequested?.Invoke(this));
            if (moveDownButton != null)
                moveDownButton.onClick.AddListener(() => MoveDownRequested?.Invoke(this));
        }

        public void SetMoveButtons(bool canMoveUp, bool canMoveDown)
        {
            if (moveUpButton != null) moveUpButton.gameObject.SetActive(canMoveUp);
            if (moveDownButton != null) moveDownButton.gameObject.SetActive(canMoveDown);
        }

        void OnDestroy()
        {
            if (_ownedTexture != null)
            {
                Destroy(_ownedTexture);
                _ownedTexture = null;
            }
        }

        public void Bind(string caption, Texture2D thumbnail, bool ownsTexture)
        {
            WireButtonsOnce();

            if (captionLabel != null)
                captionLabel.text = caption ?? string.Empty;

            if (thumbnailImage != null)
                thumbnailImage.texture = thumbnail;

            _ownedTexture = ownsTexture ? thumbnail : null;
        }
    }
}
