namespace QQAlchemyDesktop.Tests;

public sealed class WebHostAssetTests
{
    [Fact]
    public void ManagementPanelIndexIsCopiedToRuntimeOutput()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");

        Assert.True(File.Exists(path), $"管理面板静态资源缺失：{path}");
        Assert.Contains("QQ 炼丹助手", File.ReadAllText(path), StringComparison.Ordinal);
    }
}
