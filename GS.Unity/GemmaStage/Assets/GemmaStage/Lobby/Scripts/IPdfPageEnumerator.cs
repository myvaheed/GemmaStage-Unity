using System.Collections.Generic;
using UnityEngine;

namespace GemmaStage.Lobby
{
    public interface IPdfPageEnumerator
    {
        // Rasterize every page of the PDF at attach time. Each returned
        // texture is owned by the caller. Returns null when the file cannot
        // be opened as a PDF.
        IReadOnlyList<Texture2D> RasterizeAllPages(string pdfPath, int targetWidthPx);
    }
}
