using QQAlchemyDesktop.Domain;

namespace QQAlchemyDesktop.Automation;

public static class BlueLinkLocator
{
    /// <summary>
    /// Finds separate blue text runs instead of returning one bounding box for
    /// the whole chat card.  The result is only a segmentation hint: callers
    /// must still validate the text with OCR and the configured herb list.
    /// </summary>
    public static IReadOnlyList<PixelRect> FindRegions(Bitmap bitmap, PixelRect searchArea)
    {
        var left = Math.Clamp(searchArea.X, 0, bitmap.Width);
        var top = Math.Clamp(searchArea.Y, 0, bitmap.Height);
        var right = Math.Clamp(searchArea.X + searchArea.Width, 0, bitmap.Width);
        var bottom = Math.Clamp(searchArea.Y + searchArea.Height, 0, bitmap.Height);
        if (right <= left || bottom <= top) return Array.Empty<PixelRect>();

        var runs = new List<PixelRect>();
        for (var y = top; y < bottom; y++)
        {
            var runStart = -1;
            var lastBlue = -1;
            for (var x = left; x <= right; x++)
            {
                var isBlue = x < right && IsQqLinkBlue(bitmap.GetPixel(x, y));
                if (isBlue)
                {
                    if (runStart < 0) runStart = x;
                    lastBlue = x;
                    continue;
                }

                if (runStart >= 0 && lastBlue >= runStart)
                {
                    runs.Add(new PixelRect(runStart, y, lastBlue - runStart + 1, 1));
                    runStart = -1;
                    lastBlue = -1;
                }
            }
        }

        var merged = new List<PixelRect>();
        foreach (var run in runs)
        {
            var index = -1;
            for (var i = 0; i < merged.Count; i++)
            {
                var candidate = merged[i];
                var overlapsX = run.X <= candidate.X + candidate.Width + 4 &&
                                candidate.X <= run.X + run.Width + 4;
                var touchesY = run.Y <= candidate.Y + candidate.Height + 2 &&
                               candidate.Y <= run.Y + run.Height + 2;
                if (overlapsX && touchesY)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                merged.Add(run);
                continue;
            }

            var existing = merged[index];
            var x = Math.Min(existing.X, run.X);
            var y = Math.Min(existing.Y, run.Y);
            var rightEdge = Math.Max(existing.X + existing.Width, run.X + run.Width);
            var bottomEdge = Math.Max(existing.Y + existing.Height, run.Y + run.Height);
            merged[index] = new PixelRect(x, y, rightEdge - x, bottomEdge - y);
        }

        // A Chinese word is often split into one connected component per
        // glyph.  Collapse components on the same visual baseline into one
        // row before returning regions to OCR.
        var rows = new List<PixelRect>();
        foreach (var piece in merged.OrderBy(rect => rect.Y).ThenBy(rect => rect.X))
        {
            var rowIndex = rows.FindIndex(row =>
                Math.Abs((row.Y + row.Height / 2d) - (piece.Y + piece.Height / 2d)) <=
                Math.Max(3, Math.Max(row.Height, piece.Height) * 0.6));
            if (rowIndex < 0)
            {
                rows.Add(piece);
                continue;
            }

            var existing = rows[rowIndex];
            var x = Math.Min(existing.X, piece.X);
            var y = Math.Min(existing.Y, piece.Y);
            var rightEdge = Math.Max(existing.X + existing.Width, piece.X + piece.Width);
            var bottomEdge = Math.Max(existing.Y + existing.Height, piece.Y + piece.Height);
            rows[rowIndex] = new PixelRect(x, y, rightEdge - x, bottomEdge - y);
        }

        return rows.Where(rect => rect.Width >= 6 && rect.Height >= 5)
            .OrderBy(rect => rect.Y)
            .ThenBy(rect => rect.X)
            .ToArray();
    }

    public static bool TryFindBounds(Bitmap bitmap, PixelRect searchArea, out PixelRect bounds)
    {
        var left = Math.Clamp(searchArea.X - 3, 0, bitmap.Width);
        var top = Math.Clamp(searchArea.Y - 3, 0, bitmap.Height);
        var right = Math.Clamp(searchArea.X + searchArea.Width + 3, 0, bitmap.Width);
        var bottom = Math.Clamp(searchArea.Y + searchArea.Height + 3, 0, bitmap.Height);
        var minX = right;
        var minY = bottom;
        var maxX = left - 1;
        var maxY = top - 1;
        var count = 0;

        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var color = bitmap.GetPixel(x, y);
                if (!IsQqLinkBlue(color)) continue;
                count++;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (count < 6 || maxX < minX || maxY < minY)
        {
            bounds = searchArea;
            return false;
        }

        bounds = new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
        return true;
    }

    public static bool IsQqLinkBlue(Color color) =>
        color.B >= 165 && color.G >= 85 && color.B - color.R >= 65 && color.G - color.R >= 30;
}
