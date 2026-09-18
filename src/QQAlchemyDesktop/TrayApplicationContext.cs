using System.Diagnostics;
using QQAlchemyDesktop.Infrastructure;
using QQAlchemyDesktop.Services;

namespace QQAlchemyDesktop;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly IHost _host;
    private readonly AppPaths _paths;
    private bool _exiting;

    public TrayApplicationContext(IHost host, AppPaths paths, AutomationCoordinator coordinator, UserAlertService alerts)
    {
        _host = host;
        _paths = paths;
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开管理面板", null, (_, _) => OpenPanel());
        menu.Items.Add("紧急停止", null, async (_, _) =>
        {
            await coordinator.EmergencyStopAsync();
            System.Media.SystemSounds.Exclamation.Play();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开数据目录", null, (_, _) =>
            Process.Start(new ProcessStartInfo("explorer.exe", _paths.Root) { UseShellExecute = true }));
        menu.Items.Add("退出", null, async (_, _) => await ExitAsync());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "QQ 炼丹助手（演练模式优先）",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => OpenPanel();
        alerts.AlertRaised += (title, message) =>
        {
            if (_exiting) return;
            _trayIcon.ShowBalloonTip(8000, title, message, ToolTipIcon.Warning);
        };
        _trayIcon.ShowBalloonTip(2500, "QQ 炼丹助手已启动", "管理面板：http://127.0.0.1:62346", ToolTipIcon.Info);
    }

    private static void OpenPanel() => Process.Start(new ProcessStartInfo("http://127.0.0.1:62346")
    {
        UseShellExecute = true
    });

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _trayIcon.Visible = false;
        await _host.StopAsync(TimeSpan.FromSeconds(5));
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _trayIcon.Dispose();
        base.Dispose(disposing);
    }
}
