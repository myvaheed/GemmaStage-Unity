using System;
using System.Collections.Generic;
using System.IO;
using PDFtoImage;
using SkiaSharp;
using UnityEngine;

namespace GemmaStage.Lobby
{
    public sealed class PdfToImagePdfPageEnumerator : IPdfPageEnumerator
    {
        public IReadOnlyList<Texture2D> RasterizeAllPages(string pdfPath, int targetWidthPx)
        {
            if (string.IsNullOrEmpty(pdfPath) || !File.Exists(pdfPath))
                return null;

            byte[] bytes;
            try { bytes = File.ReadAllBytes(pdfPath); }
            catch (Exception ex)
            {
                Debug.LogError($"[PdfRasterizer] Cannot read '{pdfPath}': {ex.Message}");
                return null;
            }

            int pageCount;
            try
            {
                using var probe = new MemoryStream(bytes, writable: false);
                pageCount = Conversion.GetPageCount(probe);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PdfRasterizer] '{pdfPath}' is not a valid PDF: {ex.Message}");
                return null;
            }

            int width = Mathf.Clamp(targetWidthPx, 64, 2048);
            // WithAspectRatio = true is required: otherwise PDFium uses the page's native
            // height in points as the output pixel height, squashing A4 portrait pages
            // (842 pt tall) into ~2048×841 instead of the expected 2048×~2900.
            var opts = new RenderOptions { Width = width, WithAspectRatio = true };
            var pages = new List<Texture2D>(pageCount);
            for (int i = 0; i < pageCount; i++)
            {
                try
                {
                    using var stream = new MemoryStream(bytes, writable: false);
                    using var bmp = Conversion.ToImage(stream, page: i, options: opts);
                    pages.Add(ToTexture2D(bmp, i));
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[PdfRasterizer] Failed to rasterize page {i + 1} of '{pdfPath}': {ex.Message}");
                }
            }
            return pages;
        }

        static Texture2D ToTexture2D(SKBitmap bmp, int pageIndex)
        {
            // PDFium → SkiaSharp produces BGRA8888 by default. Unity's BGRA32
            // matches byte-for-byte and has to be flipped vertically because
            // Unity's UV origin is bottom-left.
            int w = bmp.Width;
            int h = bmp.Height;
            var src = bmp.GetPixelSpan();
            var dst = new byte[src.Length];
            int rowBytes = w * 4;
            for (int y = 0; y < h; y++)
            {
                src.Slice(y * rowBytes, rowBytes).CopyTo(new Span<byte>(dst, (h - 1 - y) * rowBytes, rowBytes));
            }
            var tex = new Texture2D(w, h, TextureFormat.BGRA32, mipChain: false)
            {
                name = $"pdf_p{pageIndex}",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            tex.LoadRawTextureData(dst);
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return tex;
        }
    }
}
