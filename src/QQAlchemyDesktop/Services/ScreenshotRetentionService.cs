using QQAlchemyDesktop.Infrastructure;

namespace QQAlchemyDesktop.Services;

public sealed class ScreenshotRetentionService : BackgroundService
{
    private const long MaxBytes = 500L * 1024 * 1024;
    private readonly AppPaths _paths;

    public ScreenshotRetentionService(AppPaths paths) => _paths = paths;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        Clean();
        while (await timer.WaitForNextTickAsync(stoppingToken)) Clean();
    }

    private void Clean()
    {
        var files = new DirectoryInfo(_paths.Screenshots).EnumerateFiles("*.png")
            .OrderBy(x => x.LastWriteTimeUtc).ToList();
        foreach (var file in files.Where(x => x.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7)).ToArray())
        {
            try { file.Delete(); files.Remove(file); } catch { /* next pass */ }
        }
        var total = files.Sum(x => x.Exists ? x.Length : 0);
        foreach (var file in files)
        {
            if (total <= MaxBytes) break;
            try { total -= file.Length; file.Delete(); } catch { /* next pass */ }
        }
    }
}
