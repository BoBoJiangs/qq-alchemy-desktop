using System.Drawing.Text;
using QQAlchemyDesktop.Automation;
using QQAlchemyDesktop.Domain;
using QQAlchemyDesktop.Services;

namespace QQAlchemyDesktop.Tests;

public sealed class OcrAutomationTests
{
    [Fact]
    public void BlueLinkLocator_FindsLinkPixelsInsideMixedPriceLine()
    {
        using var bitmap = new Bitmap(520, 90);
        using var graphics = Graphics.FromImage(bitmap);
        using var font = new Font("Microsoft YaHei UI", 22, FontStyle.Regular, GraphicsUnit.Pixel);
        using var black = new SolidBrush(Color.Black);
        using var blue = new SolidBrush(Color.FromArgb(0, 153, 255));
        graphics.Clear(Color.White);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.DrawString("价格:320万", font, black, 12, 24);
        graphics.DrawString("九叶芝", font, blue, 155, 24);

        Assert.True(BlueLinkLocator.TryFindBounds(bitmap, new PixelRect(5, 15, 250, 50), out var bounds));
        Assert.True(bounds.X >= 150);
        Assert.InRange(bounds.Center.Y, 25, 60);
    }

    [Fact]
    public void BlueLinkLocator_FindsSeparateRowsForMultipleLinks()
    {
        using var bitmap = new Bitmap(260, 90);
        using var graphics = Graphics.FromImage(bitmap);
        using var font = new Font("Microsoft YaHei UI", 18, FontStyle.Regular, GraphicsUnit.Pixel);
        using var blue = new SolidBrush(Color.FromArgb(0, 153, 255));
        graphics.Clear(Color.White);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.DrawString("九叶芝", font, blue, 40, 8);
        graphics.DrawString("五柳根", font, blue, 40, 45);

        var regions = BlueLinkLocator.FindRegions(bitmap, new PixelRect(0, 0, 260, 90));

        Assert.Equal(2, regions.Count);
        Assert.True(regions[0].Y < regions[1].Y);
        Assert.All(regions, region => Assert.True(region.Width >= 6 && region.Height >= 5));
    }

    [Theory]
    [InlineData("@小小 坊市购买22906fc8-99c2-4356-a466-4c17e3d8dcb5")]
    [InlineData("小小\n坊市购买 22906FC8-99C2-4356-A466-4C17E3D8DCB5")]
    public void PurchaseCommandValidator_AcceptsPreparedQqCommand(string text)
    {
        Assert.True(PurchaseCommandValidator.TryValidate(text, "小小", out var command));
        Assert.Equal("坊市购买22906fc8-99c2-4356-a466-4c17e3d8dcb5", command);
    }

    [Theory]
    [InlineData("@别人 坊市购买22906fc8-99c2-4356-a466-4c17e3d8dcb5")]
    [InlineData("@小小 坊市购买22906fc8")]
    [InlineData("@小小 物品功效")]
    public void PurchaseCommandValidator_RejectsUntrustedInput(string text) =>
        Assert.False(PurchaseCommandValidator.TryValidate(text, "小小", out _));

    [Fact]
    public void PurchaseCommandValidator_AllowsNoMentionWhenConfigured()
    {
        Assert.True(PurchaseCommandValidator.TryValidate(
            "坊市购买22906fc8-99c2-4356-a466-4c17e3d8dcb5", "小小", false, out var command));
        Assert.Equal("坊市购买22906fc8-99c2-4356-a466-4c17e3d8dcb5", command);
    }

    [Fact]
    public void PurchaseCommandValidator_AcceptsOcrTrailingPresentationNoise()
    {
        Assert.True(PurchaseCommandValidator.TryValidate(
            "@小小 坊市购买22906fc8-99c2-4356-a466-4c17e3d8dcb5|", "小小", false, out var command));
        Assert.Equal("坊市购买22906fc8-99c2-4356-a466-4c17e3d8dcb5", command);
    }

    [Fact]
    public void OcrConsensus_NormalizesPresentationNoiseButRejectsDifferentContent()
    {
        Assert.True(OcrConsensus.AreEquivalent("药材背包： 1 页", "药材背包:1页"));
        Assert.False(OcrConsensus.AreEquivalent("药材背包", "坊市数据"));
        Assert.EndsWith("…", OcrConsensus.Compact(new string('字', 181)));
    }

    [Fact]
    public void QqDesktopClient_AccessibleChatMarkerRecognizesUsefulResponsesOnly()
    {
        Assert.True(QqDesktopClient.HasUsefulChatMarker("药材背包 第2页/共5页"));
        Assert.True(QqDesktopClient.HasUsefulChatMarker("未查询到该物品"));
        Assert.False(QqDesktopClient.HasUsefulChatMarker("普通聊天消息"));
        Assert.True(QqDesktopClient.HasInventoryPayload("药材背包 名字：九叶芝 拥有数量：2"));
        Assert.False(QqDesktopClient.HasInventoryPayload("药材背包"));
    }

    [Fact]
    public void HerbNameResolver_StripsUiZeroWidthDecorations()
    {
        Assert.Equal("七彩月兰", new HerbNameResolver(["七彩月兰"]).Resolve("七彩月兰\u200b"));
    }

    [Fact]
    public async Task RapidOcrV5_RecognizesChineseMarketLineAndCoordinates()
    {
        using var bitmap = new Bitmap(620, 120);
        using var graphics = Graphics.FromImage(bitmap);
        using var font = new Font("Microsoft YaHei UI", 30, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(0, 153, 255));
        graphics.Clear(Color.White);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.DrawString("价格:320万 九叶芝", font, brush, 24, 34);

        var observation = await new RapidOcrService().RecognizeAsync(bitmap);

        Assert.Contains("320", observation.RawText);
        Assert.Contains("九叶芝", observation.RawText);
        Assert.Contains(observation.Words, word => word.Bounds.Width > 0 && word.Bounds.Height > 0);
        Assert.Contains(observation.Words, word => word.Confidence > 0.5d);
    }

    [Fact]
    public async Task RapidOcrV5_ParsesRealMarketScreenshotAndTargetsBlueLinks()
    {
        var screenshot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "market-sample.png");
        using var bitmap = new Bitmap(screenshot);
        var observation = await new RapidOcrService().RecognizeAsync(bitmap);
        var herbNames = new[]
        {
            "九叶芝", "五柳根", "伏龙参", "伴妖草", "何首乌",
            "八角玄冰草", "冥胎骨", "冰灵果", "冰灵焰草"
        };

        var listings = MarketParser.Parse(observation, 1, new HerbNameResolver(herbNames));

        Assert.Equal(herbNames, listings.Select(x => x.HerbName));
        Assert.Equal(new double[] { 320, 300, 320, 280, 140, 500, 360, 260, 370 },
            listings.Select(x => x.PriceWan));
        Assert.All(listings, listing =>
        {
            Assert.InRange(listing.ClickRect.Center.X, 75, 170);
            Assert.True(ContainsBluePixel(bitmap, listing.ClickRect),
                $"{listing.HerbName} 的点击区域没有落在蓝色链接像素上：{listing.ClickRect}");
        });
    }

    private static bool ContainsBluePixel(Bitmap bitmap, PixelRect rect)
    {
        var left = Math.Clamp(rect.X, 0, bitmap.Width);
        var top = Math.Clamp(rect.Y, 0, bitmap.Height);
        var right = Math.Clamp(rect.X + rect.Width, 0, bitmap.Width);
        var bottom = Math.Clamp(rect.Y + rect.Height, 0, bitmap.Height);
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                if (BlueLinkLocator.IsQqLinkBlue(bitmap.GetPixel(x, y))) return true;
            }
        }

        return false;
    }
}
