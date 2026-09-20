namespace QQAlchemyDesktop.Tests;

public sealed class WebHostAssetTests
{
    [Fact]
    public void ManagementPanelIndexIsCopiedToRuntimeOutput()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");

        Assert.True(File.Exists(path), $"管理面板静态资源缺失：{path}");
        var index = File.ReadAllText(path);
        Assert.Contains("QQ 炼丹助手", index, StringComparison.Ordinal);
        Assert.Contains("allowUnmentionedSetting", index, StringComparison.Ordinal);

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "app.js");
        Assert.Contains("/api/settings/alchemy", File.ReadAllText(scriptPath), StringComparison.Ordinal);
        Assert.Contains("allowUnmentionedCommands", File.ReadAllText(scriptPath), StringComparison.Ordinal);
    }
}
