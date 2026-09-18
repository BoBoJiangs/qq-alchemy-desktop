using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Json;
using QQAlchemyDesktop;
using QQAlchemyDesktop.Automation;
using QQAlchemyDesktop.Domain;
using QQAlchemyDesktop.Infrastructure;
using QQAlchemyDesktop.Services;
using System.Text.Json.Serialization;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]

internal static class Program
{
    [STAThread]
    private static async Task Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args });
        builder.WebHost.UseUrls("http://127.0.0.1:62346");
        builder.Services.Configure<JsonOptions>(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddSingleton<AppPaths>();
        builder.Services.AddSingleton<SqliteStore>();
        builder.Services.AddSingleton<WindowsOcrService>();
        builder.Services.AddSingleton<WindowsGraphicsCaptureService>();
        builder.Services.AddSingleton<QqDesktopClient>();
        builder.Services.AddSingleton<RecipeCalculator>();
        builder.Services.AddSingleton<LegacyImportService>();
        builder.Services.AddSingleton<UserAlertService>();
        builder.Services.AddSingleton<AutomationCoordinator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AutomationCoordinator>());
        builder.Services.AddHostedService<ScreenshotRetentionService>();

        var app = builder.Build();
        app.UseExceptionHandler(error => error.Run(async context =>
        {
            var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            context.Response.StatusCode = exception is InvalidOperationException ? 409 : 500;
            await Results.Problem(exception?.Message ?? "未知错误", statusCode: context.Response.StatusCode)
                .ExecuteAsync(context);
        }));
        app.UseDefaultFiles();
        app.UseStaticFiles();
        MapApi(app);

        var store = app.Services.GetRequiredService<SqliteStore>();
        await store.InitializeAsync();
        await EnsureDefaultsAsync(store);
        await app.StartAsync();

        using var tray = new TrayApplicationContext(
            app,
            app.Services.GetRequiredService<AppPaths>(),
            app.Services.GetRequiredService<AutomationCoordinator>(),
            app.Services.GetRequiredService<UserAlertService>());
        System.Windows.Forms.Application.Run(tray);
        await app.DisposeAsync();
    }

    private static void MapApi(WebApplication app)
    {
        app.MapGet("/api/status", (AutomationCoordinator coordinator) => coordinator.GetStatus());
        app.MapGet("/api/diagnostics/qq", (QqDesktopClient qq) => qq.GetDiagnostics());
        app.MapPost("/api/diagnostics/screenshot", (QqDesktopClient qq) =>
            Results.Ok(new { path = qq.SaveScreenshot("qq-diagnostics") }));
        app.MapPost("/api/diagnostics/search-group", async (GroupLookupRequest request,
            QqDesktopClient qq, CancellationToken token) =>
            Results.Ok(await qq.SearchGroupAsync(request.GroupQq, token)));
        app.MapPost("/api/diagnostics/open-group", async (GroupLookupRequest request,
            QqDesktopClient qq, CancellationToken token) =>
            Results.Ok(await qq.OpenGroupAsync(request.GroupQq, token)));
        app.MapPost("/api/tasks/purchase/start", async (AutomationCoordinator coordinator, CancellationToken token) =>
        {
            await coordinator.StartPurchaseAsync(token);
            return Results.Accepted(value: coordinator.GetStatus());
        });
        app.MapPost("/api/tasks/purchase/stop", async (AutomationCoordinator coordinator, CancellationToken token) =>
        {
            await coordinator.StopAsync(TaskKind.Purchase, token);
            return Results.Ok(coordinator.GetStatus());
        });
        app.MapPost("/api/tasks/alchemy/start", async (AutomationCoordinator coordinator, CancellationToken token) =>
        {
            await coordinator.StartAlchemyAsync(token);
            return Results.Accepted(value: coordinator.GetStatus());
        });
        app.MapPost("/api/tasks/alchemy/stop", async (AutomationCoordinator coordinator, CancellationToken token) =>
        {
            await coordinator.StopAsync(TaskKind.Alchemy, token);
            return Results.Ok(coordinator.GetStatus());
        });
        app.MapPost("/api/tasks/emergency-stop", async (AutomationCoordinator coordinator, CancellationToken token) =>
        {
            await coordinator.EmergencyStopAsync(token);
            return Results.Ok(coordinator.GetStatus());
        });
        app.MapPost("/api/tasks/resume", async (AutomationCoordinator coordinator, CancellationToken token) =>
        {
            await coordinator.ResumeAsync(token);
            return Results.Ok(coordinator.GetStatus());
        });

        app.MapPost("/api/calibration/start", async (CalibrationRequest request, AutomationCoordinator coordinator,
            QqDesktopClient qq, CancellationToken token) =>
        {
            await coordinator.MarkCalibratingAsync(token);
            return Results.Ok(await qq.ProbeCalibrationAsync(request, token));
        });
        app.MapPost("/api/calibration/verify", async (AutomationCoordinator coordinator, QqDesktopClient qq,
            CancellationToken token) =>
        {
            var verified = await qq.VerifyCalibrationAsync(token);
            if (!verified) return Results.Conflict(new { message = "群标题、窗口尺寸、QQ 版本或 DPI 校验失败" });
            await coordinator.MarkCalibrationVerifiedAsync(token);
            return Results.Ok(new { verified = true });
        });
        app.MapPost("/api/calibration/verify-existing", async (AutomationCoordinator coordinator,
            QqDesktopClient qq, CancellationToken token) =>
        {
            var result = await qq.VerifyExistingCalibrationAsync(token);
            if (result.Verified) await coordinator.MarkCalibrationVerifiedAsync(token);
            return result.Verified ? Results.Ok(result) : Results.Conflict(result);
        });

        app.MapPost("/api/import", async (ImportRequest request, LegacyImportService importer,
            RecipeCalculator calculator, CancellationToken token) =>
        {
            var result = await importer.ImportAsync(request, token);
            var recipes = await calculator.GenerateCatalogAsync(token);
            return Results.Ok(new { import = result, recipeCount = recipes.Count });
        });

        app.MapGet("/api/settings", async (SqliteStore store, CancellationToken token) => new
        {
            calibration = await store.GetSettingAsync<CalibrationSettings>("calibration", token),
            alchemy = await store.GetSettingAsync<AlchemySettings>("alchemy", token),
            purchaseRules = await store.GetSettingAsync<List<PurchaseRule>>("purchaseRules", token) ?? []
        });
        app.MapPut("/api/settings/alchemy", async (AlchemySettings settings, SqliteStore store, CancellationToken token) =>
        {
            ValidateSettings(settings);
            await store.SetSettingAsync("alchemy", settings, token);
            return Results.Ok(settings);
        });
        app.MapPut("/api/settings/purchase-rules", async (List<PurchaseRule> rules, SqliteStore store,
            CancellationToken token) =>
        {
            var normalized = rules.Select((rule, index) => rule with
            {
                HerbName = rule.HerbName.Trim(),
                MaxPriceWan = Math.Max(0, rule.MaxPriceWan),
                InventoryLimit = Math.Max(0, rule.InventoryLimit),
                Order = index
            }).Where(rule => rule.HerbName.Length > 0).ToList();
            await store.SetSettingAsync("purchaseRules", normalized, token);
            return Results.Ok(normalized);
        });
        app.MapGet("/api/audit", async (int? count, SqliteStore store, CancellationToken token) =>
            await store.RecentAuditAsync(count ?? 100, token));
        app.MapGet("/api/screenshots/latest", (AutomationCoordinator coordinator, AppPaths paths) =>
        {
            var file = coordinator.GetStatus().LastScreenshot;
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return Results.NotFound();
            var full = Path.GetFullPath(file);
            var root = Path.GetFullPath(paths.Screenshots) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
            return Results.File(full, "image/png", enableRangeProcessing: false);
        });
    }

    private static async Task EnsureDefaultsAsync(SqliteStore store)
    {
        if (await store.GetSettingAsync<AlchemySettings>("alchemy") is null)
            await store.SetSettingAsync("alchemy", new AlchemySettings());
        if (await store.GetSettingAsync<List<PurchaseRule>>("purchaseRules") is null)
            await store.SetSettingAsync("purchaseRules", new List<PurchaseRule>());
    }

    private static void ValidateSettings(AlchemySettings settings)
    {
        if (settings.DanNumber is < 1 or > 20) throw new InvalidOperationException("丹药品数必须在 1–20 之间");
        if (settings.MakeNumber < 1) throw new InvalidOperationException("生成配方数量必须大于 0");
        if (settings.AlchemyNumber < 0) throw new InvalidOperationException("炼金收益安全阈值不能为负数");
        if (settings.LimitHerbsCount < 0) throw new InvalidOperationException("药材库存上限不能为负数");
        if (settings.TaskPurchaseLimit is < 1 or > 1000) throw new InvalidOperationException("任务采购上限必须在 1–1000 之间");
        if (settings.EmptyMarketRoundsBeforeStop is < 1 or > 20) throw new InvalidOperationException("空轮次数必须在 1–20 之间");
    }

    private sealed record GroupLookupRequest(long GroupQq);
}
