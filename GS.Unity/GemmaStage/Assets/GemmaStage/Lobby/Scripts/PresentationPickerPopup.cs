using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using GemmaStage.Core;

namespace GemmaStage.Lobby
{
    public class PresentationPickerPopup : MonoBehaviour
    {
        const int RasterTargetWidthPx = 2048;

        static readonly FilePickerFilter[] AddFilters =
        {
            new("Slides", "pdf", "png", "jpg", "jpeg"),
            new("PDF",    "pdf"),
            new("Image",  "png", "jpg", "jpeg"),
        };

        [SerializeField] RectTransform contentRoot;
        [SerializeField] SlideThumbnailItem thumbnailItemPrefab;
        [SerializeField] Button addFileButton;
        [SerializeField] Button closeButton;
        [SerializeField] TextMeshProUGUI emptyStateLabel;
        [SerializeField] GameObject[] hideWhileOpen;

        readonly List<SlideThumbnailItem> _items = new();
        IPdfPageEnumerator _pdfEnumerator;
        List<SlideEntry> _slides;
        Action _onClosed;

        void Awake()
        {
            if (addFileButton != null)
                addFileButton.onClick.AddListener(OnAddFileClicked);

            if (closeButton != null)
                closeButton.onClick.AddListener(Close);
        }

        public void Open(List<SlideEntry> slides, IPdfPageEnumerator pdfEnumerator, Action onClosed)
        {
            _slides = slides ?? throw new ArgumentNullException(nameof(slides));
            _pdfEnumerator = pdfEnumerator ?? throw new ArgumentNullException(nameof(pdfEnumerator));
            _onClosed = onClosed;

            SetSiblingsHidden(true);
            gameObject.SetActive(true);
            RebuildItems();
        }

        public void Close()
        {
            gameObject.SetActive(false);
            SetSiblingsHidden(false);
            ClearItems();
            var cb = _onClosed;
            _slides = null;
            _onClosed = null;
            cb?.Invoke();
        }

        void SetSiblingsHidden(bool hidden)
        {
            if (hideWhileOpen == null) return;
            for (int i = 0; i < hideWhileOpen.Length; i++)
                if (hideWhileOpen[i] != null)
                    hideWhileOpen[i].SetActive(!hidden);
        }

        void OnAddFileClicked()
        {
            FilePicker.OpenFile("Add Presentation File", AddFilters, path =>
            {
                if (string.IsNullOrEmpty(path) || _slides == null)
                    return;

                AppendFromPath(path);
                RebuildItems();
            });
        }

        void AppendFromPath(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".pdf")
            {
                var pages = _pdfEnumerator.RasterizeAllPages(path, RasterTargetWidthPx);
                if (pages == null || pages.Count == 0)
                {
                    Debug.LogWarning($"[PresentationPicker] '{path}' produced no rasterizable pages — skipping.");
                    return;
                }
                for (int i = 0; i < pages.Count; i++)
                    _slides.Add(new SlideEntry { SourcePath = path, PdfPageIndex = i, RenderedTexture = pages[i] });
            }
            else
            {
                var tex = LoadImageTexture(path);
                if (tex == null)
                    return;
                _slides.Add(new SlideEntry { SourcePath = path, PdfPageIndex = 0, RenderedTexture = tex });
            }
        }

        static Texture2D LoadImageTexture(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
                if (!tex.LoadImage(bytes, markNonReadable: true))
                {
                    Destroy(tex);
                    Debug.LogWarning($"[PresentationPicker] '{path}' is not a decodable image.");
                    return null;
                }
                tex.name = Path.GetFileName(path);
                return tex;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PresentationPicker] Failed to load image '{path}': {ex.Message}");
                return null;
            }
        }

        void RebuildItems()
        {
            ClearItems();

            if (thumbnailItemPrefab == null || contentRoot == null || _slides == null)
            {
                RefreshEmptyState();
                return;
            }

            for (int i = 0; i < _slides.Count; i++)
            {
                var entry = _slides[i];
                var item = Instantiate(thumbnailItemPrefab, contentRoot);
                item.gameObject.SetActive(true);
                // Texture is owned by the SlideEntry; the item only holds a view.
                item.Bind(BuildCaption(entry), entry.RenderedTexture, ownsTexture: false);
                item.Removed += OnItemRemoved;
                item.MoveUpRequested += OnItemMoveUp;
                item.MoveDownRequested += OnItemMoveDown;
                _items.Add(item);
            }

            RefreshMoveInteractables();
            RefreshEmptyState();
        }

        void RefreshMoveInteractables()
        {
            int last = _items.Count - 1;
            for (int i = 0; i <= last; i++)
                _items[i].SetMoveButtons(canMoveUp: i > 0, canMoveDown: i < last);
        }

        void OnItemRemoved(SlideThumbnailItem item)
        {
            int index = _items.IndexOf(item);
            if (index < 0)
                return;

            var entry = _slides[index];
            if (entry.RenderedTexture != null)
                Destroy(entry.RenderedTexture);

            _items.RemoveAt(index);
            _slides.RemoveAt(index);
            Destroy(item.gameObject);

            RefreshMoveInteractables();
            RefreshEmptyState();
        }

        void OnItemMoveUp(SlideThumbnailItem item) => Swap(_items.IndexOf(item), -1);
        void OnItemMoveDown(SlideThumbnailItem item) => Swap(_items.IndexOf(item), +1);

        void Swap(int index, int delta)
        {
            int other = index + delta;
            if (index < 0 || other < 0 || other >= _slides.Count) return;

            (_slides[index], _slides[other]) = (_slides[other], _slides[index]);
            (_items[index], _items[other])   = (_items[other], _items[index]);
            _items[other].transform.SetSiblingIndex(other);
            _items[index].transform.SetSiblingIndex(index);
            RefreshMoveInteractables();
        }

        static string BuildCaption(SlideEntry entry)
        {
            string filename = Path.GetFileName(entry.SourcePath ?? string.Empty);
            string ext = Path.GetExtension(entry.SourcePath ?? string.Empty).ToLowerInvariant();
            return ext == ".pdf"
                ? $"{filename} (p.{entry.PdfPageIndex + 1})"
                : filename;
        }

        void RefreshEmptyState()
        {
            if (emptyStateLabel != null)
                emptyStateLabel.gameObject.SetActive(_slides == null || _slides.Count == 0);
        }

        void ClearItems()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i] == null) continue;
                _items[i].Removed -= OnItemRemoved;
                _items[i].MoveUpRequested -= OnItemMoveUp;
                _items[i].MoveDownRequested -= OnItemMoveDown;
                Destroy(_items[i].gameObject);
            }
            _items.Clear();
        }
    }
}
