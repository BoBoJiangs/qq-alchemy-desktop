using QQAlchemyDesktop.Domain;

namespace QQAlchemyDesktop.Automation;

public static class BlueLinkLocator
{
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
