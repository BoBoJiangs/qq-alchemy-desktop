using QQAlchemyDesktop.Automation;
using QQAlchemyDesktop.Domain;
using QQAlchemyDesktop.Infrastructure;
using System.Security.Cryptography;
using System.Text;

namespace QQAlchemyDesktop.Services;

public sealed class AutomationCoordinator : BackgroundService
{
    private readonly SqliteStore _store;
    private readonly QqDesktopClient _qq;
    private readonly RecipeCalculator _calculator;
    private readonly UserAlertService _alerts;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, int> _inventory = new(StringComparer.Ordinal);
    private readonly List<MarketListing> _candidates = [];
    private readonly Queue<MarketListing> _marketQueue = new();
    private readonly Dictionary<string, string> _marketCommands = new(StringComparer.Ordinal);
    private readonly List<string> _alchemyQueue = [];
    private AutomationCheckpoint _checkpoint = new();
    private string? _lastFrameHash;
    private string? _lastMessageHash;
    private DateTimeOffset? _deadline;
    private DateTimeOffset _nextAccessibleProbe = DateTimeOffset.MinValue;
    private bool _queryRetried;
    private MarketListing? _pendingListing;
    private string? _lastQuery;
    private bool _initialized;

    public AutomationCoordinator(SqliteStore store, QqDesktopClient qq, RecipeCalculator calculator, UserAlertService alerts)
    {
        _store = store;
        _qq = qq;
        _calculator = calculator;
        _alerts = alerts;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            _checkpoint = await _store.LoadCheckpointAsync(cancellationToken);
            foreach (var pair in await _store.LoadInventoryAsync(cancellationToken)) _inventory[pair.Key] = pair.Value;
            if (_checkpoint.State == AutomationState.PausedRecovery)
                _alerts.Raise("QQ 炼丹助手等待恢复", "检测到上次未完成的任务，请在管理面板确认后继续。");
            await _store.SaveCheckpointAsync(_checkpoint, cancellationToken);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public AutomationStatus GetStatus()
    {
        lock (_inventory)
        {
            return new AutomationStatus
            {
                State = _checkpoint.State,
                Task = _checkpoint.Task,
                Step = _checkpoint.Step,
                QqConnected = _qq.IsConnected,
                CalibrationValid = _qq.IsCalibrationValid(
                    _store.GetSettingAsync<CalibrationSettings>("calibration").GetAwaiter().GetResult()),
                DryRun = _store.GetSettingAsync<AlchemySettings>("alchemy").GetAwaiter().GetResult()?.DryRun ?? true,
                LastError = _checkpoint.LastError,
                LastScreenshot = _checkpoint.LastScreenshot,
                LastOcrText = _checkpoint.LastOcrText,
                Inventory = new Dictionary<string, int>(_inventory, StringComparer.Ordinal),
                Candidates = _candidates.ToArray(),
                AlchemyQueue = _alchemyQueue.ToArray(),
                UpdatedAt = _checkpoint.UpdatedAt
            };
        }
    }

    public async Task StartPurchaseAsync(CancellationToken cancellationToken = default) =>
        await StartAsync(TaskKind.Purchase, cancellationToken);

    public async Task StartAlchemyAsync(CancellationToken cancellationToken = default) =>
        await StartAsync(TaskKind.Alchemy, cancellationToken);

    public async Task StopAsync(TaskKind task, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (task != TaskKind.None && _checkpoint.Task != task) return;
            await CompleteLockedAsync("任务已由用户停止", cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EmergencyStopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _checkpoint = new AutomationCheckpoint
            {
                State = AutomationState.Idle,
                Task = TaskKind.None,
                Step = "紧急停止：不会再执行任何待处理动作",
                UpdatedAt = DateTimeOffset.Now
            };
            ResetRuntime();
            await _store.SaveCheckpointAsync(_checkpoint, cancellationToken);
            await _store.AuditAsync("warn", "emergency_stop", "用户触发紧急停止", cancellationToken: cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_checkpoint.State is not AutomationState.PausedCaptcha and not AutomationState.PausedRecovery and not AutomationState.Faulted)
                throw new InvalidOperationException("当前状态不需要恢复");
            var task = _checkpoint.Task;
            if (task == TaskKind.None) throw new InvalidOperationException("没有可恢复的任务");
            ResetRuntime();
            _checkpoint.State = AutomationState.ReadingInventory;
            _checkpoint.Step = "恢复前重新读取药材背包";
            _checkpoint.LastError = null;
            _checkpoint.PendingActionId = null;
            _checkpoint.CurrentPage = 1;
            await _store.SaveCheckpointAsync(_checkpoint, cancellationToken);
            await SendQueryLockedAsync("药材背包", cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkCalibratingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _checkpoint.State = AutomationState.Calibrating;
            _checkpoint.Task = TaskKind.None;
            _checkpoint.Step = "等待 QQ 校准验证";
            await _store.SaveCheckpointAsync(_checkpoint, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task MarkCalibrationVerifiedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _checkpoint = new AutomationCheckpoint { State = AutomationState.Idle, Step = "校准完成，等待任务" };
            await _store.SaveCheckpointAsync(_checkpoint, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await InitializeAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(450));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { await PauseFromExceptionAsync(exception, stoppingToken); }
        }
    }

    private async Task StartAsync(TaskKind task, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_checkpoint.State is not AutomationState.Idle and not AutomationState.Completed)
                throw new InvalidOperationException("已有任务正在运行或等待处理");
            var calibration = await _store.GetSettingAsync<CalibrationSettings>("calibration", cancellationToken);
            if (calibration?.IsVerified != true) throw new InvalidOperationException("请先完成并验证 QQ 校准");
            var settings = await _store.GetSettingAsync<AlchemySettings>("alchemy", cancellationToken);
            if (settings is null) throw new InvalidOperationException("请先导入或保存炼丹配置");
            ResetRuntime();
            _inventory.Clear();
            _checkpoint = new AutomationCheckpoint
            {
                State = AutomationState.ReadingInventory,
                Task = task,
                Step = task == TaskKind.Purchase ? "采购任务：读取药材背包" : "炼丹任务：读取药材背包",
                CurrentPage = 1
            };
            await _store.SaveCheckpointAsync(_checkpoint, cancellationToken);
            await _store.AuditAsync("info", "task_started", task.ToString(), cancellationToken: cancellationToken);
            await SendQueryLockedAsync("药材背包", cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        if (_checkpoint.State is AutomationState.Idle or AutomationState.Completed or AutomationState.Calibrating or
            AutomationState.PausedCaptcha or AutomationState.PausedRecovery or AutomationState.Faulted) return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_deadline is not null && DateTimeOffset.Now > _deadline)
            {
                await HandleTimeoutLockedAsync(cancellationToken);
                return;
            }

            OcrObservation observation;
            if (_checkpoint.State == AutomationState.ScanningMarket)
            {
                var purchaseRules = await _store.GetSettingAsync<List<PurchaseRule>>("purchaseRules",
                    cancellationToken) ?? [];
                // ObserveMarketAsync already performs the base chat capture;
                // avoid doing a second full OCR pass before enriching it.
                observation = await _qq.ObserveMarketAsync(
                    purchaseRules.Select(rule => rule.HerbName), cancellationToken);
            }
            else if (_checkpoint.State == AutomationState.ReadingInventory &&
                     _qq.TryObserveVisibleChat(out var accessibleObservation) &&
                     QqDesktopClient.HasInventoryPayload(accessibleObservation.RawText))
            {
                observation = accessibleObservation;
            }
            else
            {
                observation = await _qq.ObserveChatAsync(cancellationToken);
            }
            _checkpoint.LastOcrText = observation.RawText;
            if (observation.FrameHash == _lastFrameHash) return;
            _lastFrameHash = observation.FrameHash;
            var messageHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{_checkpoint.State}|{_checkpoint.PendingActionId}|{InventoryParser.Normalize(observation.RawText)}")));
            if (messageHash == _lastMessageHash) return;
            _lastMessageHash = messageHash;

            if (MessageClassifier.IsCaptcha(observation.RawText))
            {
                await PauseLockedAsync(AutomationState.PausedCaptcha, "检测到验证码，请人工处理后恢复", cancellationToken);
                return;
            }

            // QQ 的验证码卡片常把文字暴露给 UIA，但蓝色/小号 OCR 区域
            // 可能完全漏掉它。定期读取一次 UIA 文本作为安全兜底，避免
            // 在验证码页面继续发送查询或重试。
            if (DateTimeOffset.UtcNow >= _nextAccessibleProbe)
            {
                _nextAccessibleProbe = DateTimeOffset.UtcNow.AddSeconds(2);
                if (_qq.HasVisibleCaptcha())
                {
                    await PauseLockedAsync(AutomationState.PausedCaptcha,
                        "检测到验证码，请人工处理后恢复", cancellationToken);
                    return;
                }
            }

            switch (_checkpoint.State)
            {
                // 整屏高的回复卡片会把命令行顶出可视区：统一改用可视区最底部内容做响应门。
                case AutomationState.ReadingInventory:
                {
                    OcrResponseGate.TryExtractLatest(observation, 30, out var inventoryResponse);
                    await HandleInventoryLockedAsync(inventoryResponse, cancellationToken);
                    break;
                }
                case AutomationState.ScanningMarket:
                {
                    OcrResponseGate.TryExtractLatest(observation, 30, out var marketResponse);
                    await HandleMarketLockedAsync(marketResponse, cancellationToken);
                    break;
                }
                case AutomationState.WaitingPurchaseResult:
                {
                    OcrResponseGate.TryExtractLatest(observation, 10, out var purchaseResponse);
                    await HandlePurchaseResultLockedAsync(OcrResponseGate.LatestText(purchaseResponse, 10), cancellationToken);
                    break;
                }
                case AutomationState.WaitingAlchemyResult:
                {
                    OcrResponseGate.TryExtractLatest(observation, 10, out var alchemyResponse);
                    await HandleAlchemyResultLockedAsync(OcrResponseGate.LatestText(alchemyResponse, 10), cancellationToken);
                    break;
                }
            }
        }
        finally
        {
            await _store.SaveCheckpointAsync(_checkpoint, cancellationToken);
            _gate.Release();
        }
    }

    private async Task HandleInventoryLockedAsync(OcrObservation observation, CancellationToken cancellationToken)
    {
        if (_calculator.HerbNames.Count == 0) await _calculator.GenerateCatalogAsync(cancellationToken);
        var resolver = new HerbNameResolver(_calculator.HerbNames);
        var inventoryText = observation.RawText;
        var entries = MessageClassifier.IsInventoryPage(inventoryText)
            ? InventoryParser.Parse(inventoryText, resolver)
            : Array.Empty<InventoryEntry>();
        if (entries.Count == 0)
        {
            var accessibleText = string.Join("\n", _qq.GetAccessibleTexts());
            if (MessageClassifier.IsInventoryPage(accessibleText))
            {
                var accessibleEntries = InventoryParser.Parse(accessibleText, resolver);
                if (accessibleEntries.Count > 0)
                {
                    inventoryText = accessibleText;
                    entries = accessibleEntries;
                    await _store.AuditAsync("info", "inventory_uia_fallback",
                        $"OCR 未解析到药材条目，改用 UIA 读取 {entries.Count} 条",
                        cancellationToken: CancellationToken.None);
                }
            }
        }
        if (entries.Count == 0) return;
        foreach (var entry in entries)
        {
            if (_inventory.TryGetValue(entry.HerbName, out var existing) && existing != entry.Count)
            {
                await PauseLockedAsync(AutomationState.PausedRecovery,
                    $"药材背包重复项数量冲突：{entry.HerbName}，已有 {existing}，本页为 {entry.Count}", cancellationToken);
                return;
            }
            _inventory[entry.HerbName] = entry.Count;
        }
        _deadline = null;
        _nextAccessibleProbe = DateTimeOffset.MinValue;
        _queryRetried = false;

        var tail = inventoryText.Length > 360 ? inventoryText[^360..] : inventoryText;
        if (MessageClassifier.HasNextPage(tail) && _checkpoint.CurrentPage < 20)
        {
            _checkpoint.CurrentPage++;
            _checkpoint.Step = $"读取药材背包第 {_checkpoint.CurrentPage} 页";
            await SendQueryLockedAsync($"药材背包{_checkpoint.CurrentPage}", cancellationToken);
            return;
        }

        await _store.SaveInventoryAsync(_inventory, cancellationToken);
        if (_checkpoint.Task == TaskKind.Purchase)
        {
            _checkpoint.State = AutomationState.ScanningMarket;
            _checkpoint.CurrentPage = 1;
            _checkpoint.Step = "扫描坊市药材第 1 页";
            await SendQueryLockedAsync("查看坊市药材1", cancellationToken);
        }
        else
        {
            _checkpoint.State = AutomationState.PreparingAlchemy;
            _checkpoint.Step = "生成炼丹配方队列";
            var settings = await _store.GetSettingAsync<AlchemySettings>("alchemy", cancellationToken) ?? new AlchemySettings();
            await _calculator.GenerateCatalogAsync(cancellationToken);
            _alchemyQueue.Clear();
            _alchemyQueue.AddRange(_calculator.BuildAlchemyQueue(_inventory));
            if (_alchemyQueue.Count == 0)
            {
                await CompleteLockedAsync("未匹配到可用丹方", cancellationToken);
                return;
            }
            if (settings.DryRun)
            {
                await CompleteLockedAsync($"演练完成：识别到 {_alchemyQueue.Count} 条炼丹命令，未发送", cancellationToken);
                return;
            }
            await SendAlchemyLockedAsync(cancellationToken);
        }
    }

    private async Task HandleMarketLockedAsync(OcrObservation observation, CancellationToken cancellationToken)
    {
        if (!MessageClassifier.IsMarketPage(observation.RawText)) return;
        var rules = await _store.GetSettingAsync<List<PurchaseRule>>("purchaseRules", cancellationToken) ?? [];
        var settings = await _store.GetSettingAsync<AlchemySettings>("alchemy", cancellationToken) ?? new AlchemySettings();
        var parsed = MarketParser.Parse(observation, _checkpoint.CurrentPage, new HerbNameResolver(rules.Select(x => x.HerbName)));
        _candidates.Clear();
        _candidates.AddRange(parsed);
        _deadline = null;
        _queryRetried = false;

        var selected = PurchaseSelector.SelectAll(parsed, rules, _inventory);
        _marketQueue.Clear();
        foreach (var listing in selected) _marketQueue.Enqueue(listing);
        if (selected.Count > 0)
        {
            _checkpoint.EmptyMarketRounds = 0;
            if (settings.DryRun)
            {
                foreach (var listing in selected)
                {
                    await _store.AuditAsync("info", "dry_run_candidate",
                        $"{listing.HerbName} {listing.PriceWan:0.####}万 page={listing.Page} " +
                        $"queue={selected.Count}", listing.ListingToken, cancellationToken);
                }
                await AdvanceMarketPageLockedAsync(settings, cancellationToken);
                return;
            }
            await CaptureMarketCommandsLockedAsync(selected, cancellationToken);
            await SendNextMarketPurchaseLockedAsync(settings, cancellationToken);
            return;
        }

        await AdvanceMarketPageLockedAsync(settings, cancellationToken);
    }

    private async Task CaptureMarketCommandsLockedAsync(
        IReadOnlyList<MarketListing> listings, CancellationToken cancellationToken)
    {
        _marketCommands.Clear();
        try
        {
            foreach (var listing in listings)
            {
                _checkpoint.State = AutomationState.ScanningMarket;
                _checkpoint.Step = $"采集采购码：{listing.HerbName} " +
                                   $"（剩余 {listings.Count - _marketCommands.Count - 1}）";
                _checkpoint.PendingActionId = listing.ListingToken;
                var command = await _qq.CaptureListingCommandAsync(listing, cancellationToken);
                _marketCommands[listing.ListingToken] = command;
                await _store.AuditAsync("info", "market_purchase_code_captured",
                    $"{listing.HerbName} {listing.PriceWan:0.####}万 page={listing.Page} " +
                    $"command={command} captured={_marketCommands.Count}/{listings.Count}",
                    listing.ListingToken, cancellationToken);
            }
        }
        catch
        {
            // Do not leave a partially captured queue eligible for sending.
            _marketCommands.Clear();
            throw;
        }
        finally
        {
            _checkpoint.PendingActionId = null;
        }
    }

    private async Task HandlePurchaseResultLockedAsync(string text, CancellationToken cancellationToken)
    {
        if (MessageClassifier.IsPurchaseSuccess(text))
        {
            if (_pendingListing is not null)
            {
                _inventory[_pendingListing.HerbName] = _inventory.GetValueOrDefault(_pendingListing.HerbName) + 1;
                _checkpoint.PurchaseCount++;
                await _store.SaveInventoryAsync(_inventory, cancellationToken);
            }
            var settings = await _store.GetSettingAsync<AlchemySettings>("alchemy", cancellationToken) ?? new AlchemySettings();
            if (PurchaseSelector.HasReachedTaskLimit(_checkpoint.PurchaseCount, settings.TaskPurchaseLimit))
            {
                _marketCommands.Clear();
                await CompleteLockedAsync($"达到单次采购上限 {settings.TaskPurchaseLimit}", cancellationToken);
                return;
            }
            _pendingListing = null;
            _checkpoint.PendingActionId = null;
            _checkpoint.State = AutomationState.ScanningMarket;
            if (_marketQueue.Count > 0)
            {
                await SendNextMarketPurchaseLockedAsync(settings, cancellationToken);
            }
            else
            {
                await AdvanceMarketPageLockedAsync(settings, cancellationToken);
            }
            return;
        }
        if (MessageClassifier.IsPurchaseTerminal(text))
        {
            _marketCommands.Clear();
            await CompleteLockedAsync("检测到余额不足或今日操作上限，采购结束", cancellationToken);
            return;
        }
        if (MessageClassifier.IsPurchaseNotFound(text) || MessageClassifier.IsTemporaryFailure(text))
        {
            var settings = await _store.GetSettingAsync<AlchemySettings>("alchemy", cancellationToken) ?? new AlchemySettings();
            _pendingListing = null;
            _checkpoint.PendingActionId = null;
            _checkpoint.State = AutomationState.ScanningMarket;
            if (_marketQueue.Count > 0)
            {
                await SendNextMarketPurchaseLockedAsync(settings, cancellationToken);
            }
            else
            {
                await AdvanceMarketPageLockedAsync(settings, cancellationToken);
            }
        }
    }

    private async Task SendNextMarketPurchaseLockedAsync(AlchemySettings settings,
        CancellationToken cancellationToken)
    {
        if (PurchaseSelector.HasReachedTaskLimit(_checkpoint.PurchaseCount, settings.TaskPurchaseLimit))
        {
            await CompleteLockedAsync($"达到单次采购上限 {settings.TaskPurchaseLimit}", cancellationToken);
            return;
        }
        if (_marketQueue.Count == 0)
        {
            await AdvanceMarketPageLockedAsync(settings, cancellationToken);
            return;
        }

        var selected = _marketQueue.Dequeue();
        if (!_marketCommands.TryGetValue(selected.ListingToken, out var command))
            throw new InvalidOperationException($"未找到 {selected.HerbName} 的已校验采购码");
        _pendingListing = selected;
        _checkpoint.State = AutomationState.ScanningMarket;
        _checkpoint.Step = $"准备购买：{selected.HerbName} {selected.PriceWan:0.####}万 " +
                           $"（本页剩余 {_marketQueue.Count}）";
        _checkpoint.PendingActionId = selected.ListingToken;
        await DelayActionAsync(settings, ActionDelayKind.Purchase, cancellationToken);
        await _qq.SendPurchaseCommandAsync(selected, command, cancellationToken);
        _lastQuery = "坊市购买";
        _checkpoint.State = AutomationState.WaitingPurchaseResult;
        _checkpoint.Step = $"等待购买结果：{selected.HerbName} {selected.PriceWan:0.####}万 " +
                           $"（本页剩余 {_marketQueue.Count}）";
        _deadline = DateTimeOffset.Now.AddSeconds(20);
    }

    private async Task HandleAlchemyResultLockedAsync(string text, CancellationToken cancellationToken)
    {
        if (MessageClassifier.IsAlchemyFatal(text))
        {
            await PauseLockedAsync(AutomationState.Faulted, "炼丹炉或药材状态与本地背包不一致", cancellationToken);
            return;
        }
        if (!MessageClassifier.IsAlchemySuccess(text)) return;
        if (_alchemyQueue.Count > 0) _alchemyQueue.RemoveAt(0);
        _checkpoint.PendingActionId = null;
        _deadline = null;
        if (_alchemyQueue.Count == 0)
        {
            await CompleteLockedAsync("自动炼丹完成", cancellationToken);
            return;
        }
        await SendAlchemyLockedAsync(cancellationToken);
    }

    private async Task AdvanceMarketPageLockedAsync(AlchemySettings settings, CancellationToken cancellationToken)
    {
        _checkpoint.CurrentPage++;
        if (_checkpoint.CurrentPage > 8)
        {
            _checkpoint.CurrentPage = 1;
            _checkpoint.EmptyMarketRounds++;
            if (_checkpoint.EmptyMarketRounds >= settings.EmptyMarketRoundsBeforeStop)
            {
                await CompleteLockedAsync($"连续 {settings.EmptyMarketRoundsBeforeStop} 轮未发现可采购商品", cancellationToken);
                return;
            }
        }
        _checkpoint.State = AutomationState.ScanningMarket;
        _checkpoint.Step = $"扫描坊市药材第 {_checkpoint.CurrentPage} 页";
        _marketCommands.Clear();
        await SendQueryLockedAsync($"查看坊市药材{_checkpoint.CurrentPage}", cancellationToken);
    }

    private async Task SendAlchemyLockedAsync(CancellationToken cancellationToken)
    {
        if (_alchemyQueue.Count == 0) return;
        var settings = await _store.GetSettingAsync<AlchemySettings>("alchemy", cancellationToken) ?? new AlchemySettings();
        var command = _alchemyQueue[0];
        var actionId = Guid.NewGuid().ToString("N");
        _checkpoint.State = AutomationState.WaitingAlchemyResult;
        _checkpoint.Step = $"等待炼丹结果，剩余 {_alchemyQueue.Count} 条";
        _checkpoint.PendingActionId = actionId;
        _lastQuery = command;
        await _store.SaveCheckpointAsync(_checkpoint, cancellationToken);
        await DelayActionAsync(settings, ActionDelayKind.Alchemy, cancellationToken);
        await _qq.SendAtCommandAsync(command, cancellationToken);
        _deadline = DateTimeOffset.Now.AddSeconds(30);
    }

    private async Task SendQueryLockedAsync(string command, CancellationToken cancellationToken)
    {
        var actionId = Guid.NewGuid().ToString("N");
        _checkpoint.PendingActionId = actionId;
        _lastQuery = command;
        await _store.SaveCheckpointAsync(_checkpoint, cancellationToken);
        var settings = await _store.GetSettingAsync<AlchemySettings>("alchemy", cancellationToken) ?? new AlchemySettings();
        await DelayActionAsync(settings, ActionDelayKind.Query, cancellationToken);
        await _qq.SendAtCommandAsync(command, cancellationToken);
        _deadline = DateTimeOffset.Now.AddSeconds(20);
    }

    private async Task HandleTimeoutLockedAsync(CancellationToken cancellationToken)
    {
        if ((_checkpoint.State is AutomationState.ReadingInventory or AutomationState.ScanningMarket) &&
            !_queryRetried && _lastQuery is not null)
        {
            _queryRetried = true;
            _deadline = null;
            await _store.AuditAsync("warn", "query_retry", _lastQuery, cancellationToken: cancellationToken);
            await SendQueryLockedAsync(_lastQuery, cancellationToken);
            return;
        }
        await PauseLockedAsync(AutomationState.Faulted,
            _checkpoint.State is AutomationState.WaitingPurchaseResult or AutomationState.WaitingAlchemyResult
                ? "操作结果超时；为防止重复操作，未自动重试"
                : "查询重试后仍未收到可识别结果", cancellationToken);
    }

    private async Task PauseFromExceptionAsync(Exception exception, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_checkpoint.State is AutomationState.Idle or AutomationState.Completed) return;
            await PauseLockedAsync(exception is OcrConflictException ? AutomationState.PausedRecovery : AutomationState.Faulted,
                exception.Message, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task PauseLockedAsync(AutomationState state, string reason, CancellationToken cancellationToken)
    {
        string? screenshot = null;
        try { screenshot = _qq.SaveScreenshot("paused"); } catch { /* window may be gone */ }
        _checkpoint.State = state;
        _checkpoint.Step = reason;
        _checkpoint.LastError = reason;
        _checkpoint.LastScreenshot = screenshot;
        _checkpoint.PendingActionId = null;
        _deadline = null;
        await _store.SaveCheckpointAsync(_checkpoint, cancellationToken);
        await _store.AuditAsync("error", "task_paused", reason, cancellationToken: cancellationToken);
        _alerts.Raise(state == AutomationState.PausedCaptcha ? "检测到验证码" : "QQ 自动化已暂停", reason);
    }

    private async Task CompleteLockedAsync(string reason, CancellationToken cancellationToken)
    {
        _checkpoint.State = AutomationState.Completed;
        _checkpoint.Step = reason;
        _checkpoint.PendingActionId = null;
        _deadline = null;
        await _store.SaveCheckpointAsync(_checkpoint, cancellationToken);
        await _store.AuditAsync("info", "task_completed", reason, cancellationToken: cancellationToken);
    }

    internal enum ActionDelayKind
    {
        Query,
        Purchase,
        Alchemy
    }

    internal static (int MinimumMilliseconds, int MaximumMilliseconds) GetActionDelayBounds(
        int randomDelaySeconds, ActionDelayKind kind)
    {
        // Keep a short but non-zero server-safe gap.  The previous fixed
        // 2-5 second random wait made every page unnecessarily slow; a
        // 1.2s query gap and 0.7s purchase gap retain pacing without making
        // the bot look like a burst of back-to-back commands.
        var baseline = kind == ActionDelayKind.Purchase ? 700 : 1200;
        var extra = Math.Clamp(randomDelaySeconds, 0, 30) * 1000;
        return (baseline, baseline + extra);
    }

    private static async Task DelayActionAsync(AlchemySettings settings, ActionDelayKind kind,
        CancellationToken cancellationToken)
    {
        var bounds = GetActionDelayBounds(settings.RandomDelay, kind);
        var milliseconds = bounds.MaximumMilliseconds == bounds.MinimumMilliseconds
            ? bounds.MinimumMilliseconds
            : Random.Shared.Next(bounds.MinimumMilliseconds, bounds.MaximumMilliseconds + 1);
        await Task.Delay(milliseconds, cancellationToken);
    }

    private void ResetRuntime()
    {
        _lastFrameHash = null;
        _lastMessageHash = null;
        _deadline = null;
        _queryRetried = false;
        _pendingListing = null;
        _lastQuery = null;
        _candidates.Clear();
        _marketQueue.Clear();
        _marketCommands.Clear();
        _alchemyQueue.Clear();
    }

}
