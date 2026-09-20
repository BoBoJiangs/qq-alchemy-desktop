using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using QQAlchemyDesktop.Domain;
using QQAlchemyDesktop.Infrastructure;
using QQAlchemyDesktop.Services;

namespace QQAlchemyDesktop.Automation;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class QqDesktopClient
{
    private readonly RapidOcrService _ocr;
    private readonly WindowsGraphicsCaptureService _capture;
    private readonly SqliteStore _store;
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _inputGate = new(1, 1);

    public QqDesktopClient(RapidOcrService ocr, WindowsGraphicsCaptureService capture, SqliteStore store, AppPaths paths)
    {
        _ocr = ocr;
        _capture = capture;
        _store = store;
        _paths = paths;
    }

    public bool IsConnected => TryLocateWindow(out _, out _);

    public QqWindowDiagnostics GetDiagnostics()
    {
        if (!TryLocateWindow(out var process, out var hwnd) || process is null)
            return new QqWindowDiagnostics(false, 0, "", "", "", 0, 0, 0, false, Array.Empty<string>());
        var width = 0;
        var height = 0;
        if (NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            width = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;
        }
        var texts = new List<string>();
        try
        {
            using var app = FlaUI.Core.Application.Attach(process);
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(2));
            if (window is not null)
            {
                texts.AddRange(window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .Select(x => x.Name?.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal)
                    .Take(200)!);
                texts.AddRange(window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
                    .Select(x => $"[EDIT] {x.Name?.Trim()} {x.BoundingRectangle.Left:0},{x.BoundingRectangle.Top:0} {x.BoundingRectangle.Width:0}x{x.BoundingRectangle.Height:0}"));
            }
        }
        catch (Exception exception)
        {
            texts.Add($"[UIA_ERROR] {exception.Message}");
        }
        var version = TryGetVersionForWindow(hwnd);
        var processId = 0;
        var processName = "";
        var windowTitle = "";
        try { processId = process.Id; } catch { }
        try { processName = process.ProcessName; } catch { }
        try { windowTitle = process.MainWindowTitle; } catch { }
        return new QqWindowDiagnostics(true, processId, processName, windowTitle,
            version, width, height, NativeMethods.GetDpiForWindow(hwnd), NativeMethods.IsIconic(hwnd), texts);
    }

    /// <summary>
    /// 在 QQ 的全局搜索框中输入群号，但不选择结果、不打开群聊，也不发送任何消息。
    /// 这是校准向导使用的只读探测步骤（输入的群号来自用户明确提供的配置）。
    /// </summary>
    public async Task<QqWindowDiagnostics> SearchGroupAsync(long groupQq,
        CancellationToken cancellationToken = default)
    {
        if (groupQq <= 0) throw new ArgumentOutOfRangeException(nameof(groupQq));
        await _inputGate.WaitAsync(cancellationToken);
        try
        {
            if (!TryLocateWindow(out var process, out var hwnd) || process is null)
                throw new InvalidOperationException("未找到正在运行的桌面 QQ 主窗口");
            RestoreAndActivate(hwnd);
            using var app = FlaUI.Core.Application.Attach(process);
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(2));
            if (window is null) throw new InvalidOperationException("无法读取 QQ UI Automation 控件树");

            var search = FindSearchEdit(window);
            if (search is null)
                throw new InvalidOperationException("QQ UI Automation 中没有找到名称为“搜索”的输入框");

            var rectangle = search.BoundingRectangle;
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
                throw new InvalidOperationException("QQ 搜索框的可点击区域无效");
            Click((int)Math.Round((double)(rectangle.Left + rectangle.Width / 2)),
                (int)Math.Round((double)(rectangle.Top + rectangle.Height / 2)));
            await Task.Delay(120, cancellationToken);
            KeyChord(NativeMethods.VkControl, NativeMethods.VkA);
            KeyPress(NativeMethods.VkBack);
            SendUnicode(groupQq.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await Task.Delay(1200, cancellationToken);
        }
        finally
        {
            _inputGate.Release();
        }
        return GetDiagnostics();
    }

    /// <summary>
    /// 搜索群号后，只在 UIA 控件树中发现唯一、精确包含该群号的结果时才点击。
    /// 找不到或存在多个候选时失败即暂停，绝不按回车或猜测点击。
    /// </summary>
    public async Task<QqWindowDiagnostics> OpenGroupAsync(long groupQq,
        CancellationToken cancellationToken = default)
    {
        if (groupQq <= 0) throw new ArgumentOutOfRangeException(nameof(groupQq));
        await _inputGate.WaitAsync(cancellationToken);
        try
        {
            if (!TryLocateWindow(out var process, out var hwnd) || process is null)
                throw new InvalidOperationException("未找到正在运行的桌面 QQ 主窗口");
            RestoreAndActivate(hwnd);
            using var app = FlaUI.Core.Application.Attach(process);
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(2));
            if (window is null) throw new InvalidOperationException("无法读取 QQ UI Automation 控件树");

            var search = FindSearchEdit(window);
            if (search is null)
                throw new InvalidOperationException("QQ UI Automation 中没有找到名称为“搜索”的输入框");
            var searchRectangle = search.BoundingRectangle;
            Click((int)Math.Round((double)(searchRectangle.Left + searchRectangle.Width / 2)),
                (int)Math.Round((double)(searchRectangle.Top + searchRectangle.Height / 2)));
            await Task.Delay(120, cancellationToken);
            KeyChord(NativeMethods.VkControl, NativeMethods.VkA);
            KeyPress(NativeMethods.VkBack);
            var query = groupQq.ToString(System.Globalization.CultureInfo.InvariantCulture);
            SendUnicode(query);
            await Task.Delay(1200, cancellationToken);

            var candidates = FindExactGroupCandidates(window, query);
            if (candidates.Count != 1)
                throw new InvalidOperationException(candidates.Count == 0
                    ? $"QQ 搜索结果中没有可访问的群号 {query}，未打开任何群聊"
                    : $"QQ 搜索结果中发现 {candidates.Count} 个包含群号 {query} 的候选，已拒绝猜测点击");
            var candidate = candidates[0];
            var rectangle = candidate.BoundingRectangle;
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
                throw new InvalidOperationException("QQ 群搜索结果的可点击区域无效");
            Click((int)Math.Round((double)(rectangle.Left + rectangle.Width / 2)),
                (int)Math.Round((double)(rectangle.Top + rectangle.Height / 2)));
            await Task.Delay(1000, cancellationToken);

            // QQ 搜索结果默认打开群资料页；点击“发送消息”只进入聊天窗口，
            // 不会产生一条消息，便于后续做群标题和输入框校验。
            try
            {
                var sendMessage = window.FindAllDescendants()
                    .Where(element => string.Equals(element.Name?.Trim(), "发送消息", StringComparison.Ordinal))
                    .Where(element => element.BoundingRectangle.Width > 20 && element.BoundingRectangle.Height > 10)
                    .OrderBy(element => element.BoundingRectangle.Width * element.BoundingRectangle.Height)
                    .FirstOrDefault();
                if (sendMessage is not null)
                {
                    var sendRectangle = sendMessage.BoundingRectangle;
                    // 某些 QQ 版本暴露了 InvokePattern，但调用会返回
                    // 0x80040201（没有订户）；这里始终采用屏幕点击，避免
                    // UIA 事件异常让已经成功打开的群聊被报告为失败。
                    Click((int)Math.Round((double)(sendRectangle.Left + sendRectangle.Width / 2)),
                        (int)Math.Round((double)(sendRectangle.Top + sendRectangle.Height / 2)));
                    await Task.Delay(800, cancellationToken);
                }
            }
            catch
            {
                // QQ 的资料页控件树可能在转场时失效；群搜索已经成功，
                // 保持当前页面并由后续校验决定是否允许发送。
            }
            KeyPress(NativeMethods.VkEscape);
            await Task.Delay(250, cancellationToken);
        }
        finally
        {
            _inputGate.Release();
        }
        await _store.AuditAsync("info", "qq_group_opened", groupQq.ToString(), cancellationToken: cancellationToken);
        return GetDiagnostics();
    }

    private static IReadOnlyList<AutomationElement> FindExactGroupCandidates(AutomationElement window, string groupQq)
    {
        var candidates = window.FindAllDescendants()
            .Where(element => element.ControlType != ControlType.Edit)
            .Select(element => new
            {
                Element = element,
                Text = string.Join(" ", new[] { element.Name }
                    .Concat(element.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                        .Select(child => child.Name)))
            })
            .Where(item => Regex.Replace(item.Text, @"\D", "")
                .Contains(groupQq, StringComparison.Ordinal))
            .Where(item => item.Element.BoundingRectangle.Width > 8 && item.Element.BoundingRectangle.Height > 8)
            .OrderBy(item => item.Element.BoundingRectangle.Width * item.Element.BoundingRectangle.Height)
            .Select(item => item.Element)
            .ToList();

        // 子元素和父容器通常会同时暴露同一段文本，只保留最小的可点击候选，
        // 但如果最小候选仍有多个不同矩形，则由调用方安全暂停。
        if (candidates.Count <= 1) return candidates;
        var smallestArea = candidates[0].BoundingRectangle.Width * candidates[0].BoundingRectangle.Height;
        return candidates
            .Where(element => Math.Abs(element.BoundingRectangle.Width * element.BoundingRectangle.Height - smallestArea) < 1)
            .GroupBy(element => $"{element.BoundingRectangle.Left:0.##},{element.BoundingRectangle.Top:0.##},{element.BoundingRectangle.Width:0.##},{element.BoundingRectangle.Height:0.##}")
            .Select(group => group.First())
            .ToList();
    }

    private static AutomationElement? FindSearchEdit(AutomationElement window)
    {
        var edits = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
            .Where(element => element.BoundingRectangle.Width > 80 && element.BoundingRectangle.Height > 8)
            .OrderBy(element => element.BoundingRectangle.Top)
            .ThenBy(element => element.BoundingRectangle.Left)
            .ToArray();
        return edits.FirstOrDefault(element =>
                   string.Equals(element.Name?.Trim(), "搜索", StringComparison.Ordinal))
               ?? edits.FirstOrDefault(element => element.BoundingRectangle.Top <= window.BoundingRectangle.Top + 120);
    }

    public bool TryLocateWindow(out Process? process, out IntPtr hwnd)
    {
        var candidates = new List<(Process Process, IntPtr Handle, long Area)>();
        foreach (var candidateProcess in Process.GetProcesses()
                     .Where(p => p.ProcessName.Equals("QQ", StringComparison.OrdinalIgnoreCase) ||
                                 p.ProcessName.Equals("QQNT", StringComparison.OrdinalIgnoreCase)))
        {
            var processId = candidateProcess.Id;
            NativeMethods.EnumWindows((candidate, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(candidate, out var pid);
                if (pid != processId || !NativeMethods.IsWindowVisible(candidate)) return true;
                if (!NativeMethods.GetWindowRect(candidate, out var rect)) return true;
                var width = Math.Max(0, rect.Right - rect.Left);
                var height = Math.Max(0, rect.Bottom - rect.Top);
                if (width > 0 && height > 0) candidates.Add((candidateProcess, candidate, (long)width * height));
                return true;
            }, IntPtr.Zero);
        }

        var selected = candidates.OrderByDescending(item => item.Area).FirstOrDefault();
        process = selected.Process;
        hwnd = selected.Handle;
        return process is not null && hwnd != IntPtr.Zero;
    }

    public async Task<CalibrationSettings> ProbeCalibrationAsync(CalibrationRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryLocateWindow(out var process, out var hwnd) || process is null)
            throw new InvalidOperationException("未找到正在运行的桌面 QQ 主窗口");
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) throw new InvalidOperationException("无法读取 QQ 窗口位置");
        var bounds = rect.ToRectangle();
        var settings = new CalibrationSettings
        {
            GroupName = request.GroupName.Trim(),
            GameBotDisplayName = request.GameBotDisplayName.Trim(),
            GameBotQq = request.GameBotQq,
            QqExecutablePath = TryGetQqExecutablePath(hwnd),
            QqVersion = TryGetVersionForWindow(hwnd),
            WindowTitleHint = process.MainWindowTitle,
            WindowWidth = bounds.Width,
            WindowHeight = bounds.Height,
            Dpi = NativeMethods.GetDpiForWindow(hwnd),
            IsVerified = false
        };
        TryDiscoverRegions(process, bounds, settings);
        await _store.SetSettingAsync("calibration", settings, cancellationToken);
        await _store.AuditAsync("info", "calibration_probe", $"检测到 QQ {settings.QqVersion}，窗口 {bounds.Width}x{bounds.Height}", cancellationToken: cancellationToken);
        return settings;
    }

    public async Task<bool> VerifyCalibrationAsync(CancellationToken cancellationToken = default)
    {
        var settings = await RequireCalibrationAsync(cancellationToken);
        if (!TryLocateWindow(out var process, out var hwnd) || process is null) return false;
        if (NativeMethods.IsIconic(hwnd)) return false;
        var titleMatches = TryFindExactText(process, settings.GroupName);
        if (!titleMatches)
        {
            var observation = await ObserveRegionTwiceAsync(settings.GroupTitleRegion, cancellationToken);
            titleMatches = HasExactOcrLine(observation.RawText, settings.GroupName);
        }
        if (!titleMatches) return false;
        if (!IsFingerprintCompatible(settings, process, hwnd)) return false;
        settings.IsVerified = true;
        await _store.SetSettingAsync("calibration", settings, cancellationToken);
        try
        {
            var query = "药材背包";
            var pageProbeSent = false;
            await SendAtCommandAsync(query, cancellationToken);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(900, cancellationToken);
                var observation = await ObserveRegionTwiceAsync(settings.ChatRegion, cancellationToken,
                    allowLiveRefresh: true);
                if (MessageClassifier.IsCaptcha(observation.RawText))
                    throw new InvalidOperationException("校准测试触发验证码，请人工处理后重新校准");
                // 整屏回复卡片会把命令行顶出可视区：改用可视区最底部内容做响应门。
                OcrResponseGate.TryExtractLatest(observation, 30, out var response);
                if (!MessageClassifier.IsInventoryPage(response.RawText))
                {
                    // 诊断：记录未命中的尾部原文（节流），定位门控失败原因。
                    var missTail = response.RawText.Length > 160 ? response.RawText[^160..] : response.RawText;
                    try
                    {
                        await _store.AuditAsync("warn", "verify_gate_miss", missTail,
                            cancellationToken: cancellationToken);
                    }
                    catch { /* 审计失败不影响主流程 */ }
                    continue;
                }
                if (!pageProbeSent && MessageClassifier.HasNextPage(response.RawText))
                {
                    pageProbeSent = true;
                    query = "药材背包2";
                    await SendAtCommandAsync(query, cancellationToken);
                    deadline = DateTimeOffset.UtcNow.AddSeconds(25);
                    continue;
                }
                if (pageProbeSent && !response.RawText.Contains("2页", StringComparison.Ordinal) &&
                    !response.RawText.Contains("第2", StringComparison.Ordinal)) continue;
                settings.VerifiedAt = DateTimeOffset.Now;
                settings.IsVerified = true;
                await _store.SetSettingAsync("calibration", settings, cancellationToken);
                await _store.AuditAsync("info", "calibration_verified",
                    "群标题、窗口尺寸、DPI、精确 @ 和药材背包 OCR 回读均通过", cancellationToken: cancellationToken);
                return true;
            }
            settings.IsVerified = false;
            await _store.SetSettingAsync("calibration", settings, cancellationToken);
            return false;
        }
        catch
        {
            settings.IsVerified = false;
            await _store.SetSettingAsync("calibration", settings, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// 只读取当前聊天中已经存在的“药材背包”响应，不发送新命令。
    /// 用于用户手动完成测试命令后的校准确认，避免重复发送产生副作用。
    /// </summary>
    public async Task<ExistingCalibrationResult> VerifyExistingCalibrationAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await RequireCalibrationAsync(cancellationToken);
        if (!TryLocateWindow(out var process, out var hwnd) || process is null)
            return new ExistingCalibrationResult(false, false, "未找到正在运行的桌面 QQ 主窗口", "", "");
        if (NativeMethods.IsIconic(hwnd))
            return new ExistingCalibrationResult(false, false, "QQ 窗口处于最小化状态", "", "");
        var titleMatches = TryFindExactText(process, settings.GroupName);
        if (!titleMatches)
        {
            try
            {
                var titleObservation = await ObserveRegionTwiceAsync(settings.GroupTitleRegion, cancellationToken);
                titleMatches = HasExactOcrLine(titleObservation.RawText, settings.GroupName);
            }
            catch (OcrConflictException exception)
            {
                return new ExistingCalibrationResult(false, false, exception.Message, exception.Second.RawText,
                    exception.Second.FrameHash);
            }
        }
        if (!titleMatches)
            return new ExistingCalibrationResult(false, false, $"当前群标题不是“{settings.GroupName}”", "", "");
        if (!IsFingerprintCompatible(settings, process, hwnd))
            return new ExistingCalibrationResult(false, false, "QQ 版本、窗口尺寸或 DPI 已变化", "", "");

        OcrObservation observation;
        try
        {
            observation = await ObserveRegionTwiceAsync(settings.ChatRegion, cancellationToken,
                allowLiveRefresh: true);
        }
        catch (OcrConflictException exception)
        {
            return await VerifyExistingFromAccessibleTexts(settings, exception.Message, exception.Second.RawText,
                exception.Second.FrameHash, cancellationToken);
        }
        if (MessageClassifier.IsCaptcha(observation.RawText))
            return new ExistingCalibrationResult(false, false, "识别到验证码，校准已暂停", observation.RawText,
                observation.FrameHash);
        if (!OcrResponseGate.TryExtractAfterCommand(observation, "药材背包", out var response) ||
            !MessageClassifier.IsInventoryPage(response.RawText))
            return await VerifyExistingFromAccessibleTexts(settings, "当前画面未识别到药材背包响应",
                observation.RawText, observation.FrameHash, cancellationToken);

        var responsePageCount = ParseInventoryPageCount(response.RawText);
        if (MessageClassifier.HasNextPage(response.RawText) || responsePageCount > 1)
            return await VerifyExistingFromAccessibleTexts(settings,
                $"已识别药材背包分页（共 {responsePageCount} 页）；仍需人工补齐全部分页校验",
                response.RawText, response.FrameHash, cancellationToken);

        settings.IsVerified = true;
        settings.VerifiedAt = DateTimeOffset.Now;
        await _store.SetSettingAsync("calibration", settings, cancellationToken);
        await _store.AuditAsync("info", "calibration_verified_existing",
            "用户已发送的药材背包响应通过群标题、窗口指纹和 OCR 校验", cancellationToken: cancellationToken);
        return new ExistingCalibrationResult(true, false, "药材背包响应校验通过", response.RawText,
            response.FrameHash);
    }

    private async Task<ExistingCalibrationResult> VerifyExistingFromAccessibleTexts(
        CalibrationSettings settings, string fallbackMessage, string rawText, string frameHash,
        CancellationToken cancellationToken)
    {
        var accessible = GetDiagnostics().AccessibleTexts;
        var hasGroup = accessible.Any(text => string.Equals(text.Trim(), settings.GroupName,
            StringComparison.Ordinal));
        var pageMarkers = accessible
            .SelectMany(text => Regex.Matches(text.Replace(" ", "", StringComparison.Ordinal),
                @"第(\d+)页/共(\d+)页").Cast<Match>())
            .Select(match => (Current: int.Parse(match.Groups[1].Value), Total: int.Parse(match.Groups[2].Value)))
            .ToArray();
        var totalPages = pageMarkers.Select(marker => marker.Total).DefaultIfEmpty(1).Max();
        var hasFirstPage = accessible.Any(text => text.Contains("一心的药材背包", StringComparison.Ordinal)) &&
                           pageMarkers.Any(marker => marker.Current == 1);
        if (!hasGroup || !hasFirstPage)
            return new ExistingCalibrationResult(false, false, fallbackMessage, rawText, frameHash);
        var completePages = Enumerable.Range(1, totalPages)
            .All(page => pageMarkers.Any(marker => marker.Current == page) &&
                         accessible.Any(text => page == 1
                             ? text.Contains("药材背包", StringComparison.Ordinal)
                             : text.Contains($"药材背包{page}", StringComparison.Ordinal)));
        if (!completePages)
            return new ExistingCalibrationResult(false, true,
                $"已从 QQ 控件树确认部分药材背包分页（共 {totalPages} 页）；仍需人工补齐剩余分页",
                rawText, frameHash);

        settings.IsVerified = true;
        settings.VerifiedAt = DateTimeOffset.Now;
        await _store.SetSettingAsync("calibration", settings, cancellationToken);
        await _store.AuditAsync("info", "calibration_verified_existing",
            "用户已发送的药材背包分页响应通过群标题、窗口指纹和 UIA/OCR 校验",
            cancellationToken: cancellationToken);
        return new ExistingCalibrationResult(true, false, $"药材背包共 {totalPages} 页响应校验通过", rawText, frameHash);
    }

    private static int ParseInventoryPageCount(string text)
    {
        return Regex.Matches(text.Replace(" ", "", StringComparison.Ordinal), @"第\d+页/共(\d+)页")
            .Cast<Match>()
            .Select(match => int.TryParse(match.Groups[1].Value, out var total) ? total : 1)
            .DefaultIfEmpty(1)
            .Max();
    }

    public async Task<OcrObservation> ObserveChatAsync(CancellationToken cancellationToken = default)
    {
        var settings = await RequireVerifiedCalibrationAsync(cancellationToken);
        return await ObserveRegionTwiceAsync(settings.ChatRegion, cancellationToken,
            allowLiveRefresh: true);
    }

    public async Task<OcrObservation> ObserveRegionTwiceAsync(NormalizedRect region,
        CancellationToken cancellationToken = default, bool allowLiveRefresh = false)
    {
        using var firstBitmap = await CaptureRegionAsync(region, cancellationToken);
        var first = await _ocr.RecognizeAsync(firstBitmap, cancellationToken);
        await Task.Delay(300, cancellationToken);
        using var secondBitmap = await CaptureRegionAsync(region, cancellationToken);
        var second = await _ocr.RecognizeAsync(secondBitmap, cancellationToken);
        if (!OcrConsensus.AreEquivalent(first, second))
        {
            if (allowLiveRefresh)
            {
                await Task.Delay(150, cancellationToken);
                using var thirdBitmap = await CaptureRegionAsync(region, cancellationToken);
                var third = await _ocr.RecognizeAsync(thirdBitmap, cancellationToken);
                if (OcrConsensus.AreEquivalent(second, third) || OcrConsensus.AreEquivalent(first, third))
                    return third;

                await RecordOcrAuditAsync("warn", "ocr_live_refresh",
                    $"聊天区域在刷新期间持续变化；first={OcrConsensus.Compact(first.RawText)} | " +
                    $"second={OcrConsensus.Compact(second.RawText)} | third={OcrConsensus.Compact(third.RawText)}");
                return third;
            }

            await RecordOcrAuditAsync("error", "ocr_conflict",
                $"区域={region}; first={OcrConsensus.Compact(first.RawText)} | " +
                $"second={OcrConsensus.Compact(second.RawText)}");
            throw new OcrConflictException("连续两次 OCR 结果不一致，任务已暂停", first, second);
        }
        return second;
    }

    private async Task RecordOcrAuditAsync(string level, string eventType, string detail)
    {
        try
        {
            await _store.AuditAsync(level, eventType, detail, cancellationToken: CancellationToken.None);
        }
        catch
        {
            // 审计失败不能掩盖原始 OCR 结果或改变自动化安全状态。
        }
    }

    public async Task SendAtCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        await _inputGate.WaitAsync(cancellationToken);
        try
        {
            var settings = await RequireVerifiedCalibrationAsync(cancellationToken);
            var behavior = await _store.GetSettingAsync<AlchemySettings>("alchemy", cancellationToken) ?? new AlchemySettings();
            var requireBotMention = !behavior.AllowUnmentionedCommands;
            if (!await VerifyGroupOnlyAsync(settings, cancellationToken))
                throw new InvalidOperationException("当前 QQ 群标题与校准配置不一致，拒绝发送");
            if (!TryLocateWindow(out var process, out var hwnd) || process is null) throw new InvalidOperationException("QQ 窗口已丢失");
            RestoreAndActivate(hwnd);
            if (!NativeMethods.GetWindowRect(hwnd, out var nativeRect)) throw new InvalidOperationException("无法读取 QQ 窗口位置");
            var input = settings.InputRegion.ToPixels(nativeRect.ToRectangle());
            Click(input.Left + input.Width / 2, input.Top + input.Height / 2);
            await Task.Delay(150, cancellationToken);
            KeyChord(NativeMethods.VkControl, NativeMethods.VkA);
            KeyPress(NativeMethods.VkBack);
            if (requireBotMention)
            {
                SendUnicode($"@{settings.GameBotDisplayName}");
                await Task.Delay(700, cancellationToken);
                if (!await TrySelectExactMentionWithUiaAsync(process, settings, cancellationToken) &&
                    !await TrySelectExactMentionWithOcrAsync(settings, cancellationToken))
                    throw new InvalidOperationException($"无法在 @ 建议中同时确认 {settings.GameBotDisplayName} 和 QQ {settings.GameBotQq}，拒绝发送");
                await Task.Delay(250, cancellationToken);
                SendUnicode($" {command}");
            }
            else
            {
                SendUnicode(command);
            }
            await Task.Delay(200, cancellationToken);
            if (!TryClickSendButton(settings))
                throw new InvalidOperationException("已选中 @候选并填入命令，但没有找到 QQ 的“发送”按钮，拒绝发送");
            await _store.AuditAsync("info", "qq_command_sent", command, Guid.NewGuid().ToString("N"), cancellationToken);
        }
        finally
        {
            _inputGate.Release();
        }
    }

    public async Task<string> ClickAndSendListingAsync(MarketListing listing, CancellationToken cancellationToken = default)
    {
        await _inputGate.WaitAsync(cancellationToken);
        try
        {
            var settings = await RequireVerifiedCalibrationAsync(cancellationToken);
            var behavior = await _store.GetSettingAsync<AlchemySettings>("alchemy", cancellationToken) ?? new AlchemySettings();
            var requireBotMention = !behavior.AllowUnmentionedCommands;
            if (!await VerifyGroupOnlyAsync(settings, cancellationToken))
                throw new InvalidOperationException("当前 QQ 群标题不匹配，拒绝点击");
            var fresh = await ObserveRegionTwiceAsync(settings.ChatRegion, cancellationToken);
            if (!string.Equals(fresh.FrameHash, listing.FrameHash, StringComparison.Ordinal))
                throw new InvalidOperationException("坊市页面已经变化，已取消购买点击");
            if (!TryLocateWindow(out var process, out var hwnd) || process is null)
                throw new InvalidOperationException("QQ 窗口已丢失");
            RestoreAndActivate(hwnd);
            NativeMethods.GetWindowRect(hwnd, out var nativeRect);
            var windowBounds = nativeRect.ToRectangle();
            var chat = settings.ChatRegion.ToPixels(windowBounds);
            var input = settings.InputRegion.ToPixels(windowBounds);
            var point = new Point(chat.Left + listing.ClickRect.Center.X, chat.Top + listing.ClickRect.Center.Y);

            Click(input.Left + input.Width / 2, input.Top + input.Height / 2);
            await Task.Delay(120, cancellationToken);
            KeyChord(NativeMethods.VkControl, NativeMethods.VkA);
            KeyPress(NativeMethods.VkBack);
            await Task.Delay(120, cancellationToken);

            var invoked = false;
            try
            {
                using var automation = new UIA3Automation();
                var element = automation.FromPoint(point);
                if (element is not null && element.Patterns.Invoke.IsSupported)
                {
                    element.Patterns.Invoke.Pattern.Invoke();
                    invoked = true;
                }
            }
            catch
            {
                invoked = false;
            }
            if (!invoked) Click(point.X, point.Y);
            await Task.Delay(450, cancellationToken);

            var preparedText = TryReadInputText(process, settings);
            if (!PurchaseCommandValidator.TryValidate(preparedText, settings.GameBotDisplayName, requireBotMention, out var command))
            {
                try
                {
                    var inputObservation = await ObserveRegionTwiceAsync(settings.InputRegion, cancellationToken);
                    preparedText = inputObservation.RawText;
                }
                catch (OcrConflictException)
                {
                    preparedText = "";
                }
            }
            if (!PurchaseCommandValidator.TryValidate(preparedText, settings.GameBotDisplayName, requireBotMention, out command))
            {
                ClearInput(input);
                var mentionHint = requireBotMention ? "@机器人、" : "";
                throw new InvalidOperationException($"点击药材后未能确认输入框中的 {mentionHint}坊市购买命令和采购码，已清空并拒绝发送");
            }
            if (!TryClickSendButton(settings))
            {
                ClearInput(input);
                throw new InvalidOperationException("采购命令已生成并通过校验，但没有找到 QQ 的“发送”按钮，已清空并拒绝发送");
            }
            await _store.AuditAsync("info", "market_purchase_sent",
                $"{listing.HerbName} {listing.PriceWan:0.####}万 page={listing.Page} command={command}",
                listing.ListingToken, cancellationToken);
            return command;
        }
        finally
        {
            _inputGate.Release();
        }
    }

    private static string TryReadInputText(Process process, CalibrationSettings settings)
    {
        try
        {
            using var app = FlaUI.Core.Application.Attach(process);
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(2));
            if (window is null || !TryLocateWindowBounds(out var nativeBounds)) return "";
            var inputBounds = settings.InputRegion.ToPixels(nativeBounds);
            var edit = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
                .Where(element => element.BoundingRectangle.Width > 20 && element.BoundingRectangle.Height > 8)
                .Where(element => element.BoundingRectangle.Left < inputBounds.Right &&
                                  element.BoundingRectangle.Right > inputBounds.Left &&
                                  element.BoundingRectangle.Top < inputBounds.Bottom &&
                                  element.BoundingRectangle.Bottom > inputBounds.Top)
                .OrderByDescending(element => element.BoundingRectangle.Width * element.BoundingRectangle.Height)
                .FirstOrDefault();
            if (edit is null) return "";
            var value = "";
            try { value = edit.AsTextBox().Text ?? ""; } catch { /* rich edit may not expose ValuePattern */ }
            if (!string.IsNullOrWhiteSpace(value)) return value;
            return GetAccessibleElementText(edit);
        }
        catch
        {
            return "";
        }
    }

    private static void ClearInput(Rectangle input)
    {
        Click(input.Left + input.Width / 2, input.Top + input.Height / 2);
        Thread.Sleep(80);
        KeyChord(NativeMethods.VkControl, NativeMethods.VkA);
        KeyPress(NativeMethods.VkBack);
    }

    public string SaveScreenshot(string prefix = "failure")
    {
        using var bitmap = CaptureWindowAsync().GetAwaiter().GetResult();
        var path = Path.Combine(_paths.Screenshots, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmssfff}.png");
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    private async Task<Bitmap> CaptureRegionAsync(NormalizedRect region, CancellationToken cancellationToken)
    {
        using var whole = await CaptureWindowAsync(cancellationToken);
        var requested = region.ToPixels(new Rectangle(0, 0, whole.Width, whole.Height));
        requested.Intersect(new Rectangle(0, 0, whole.Width, whole.Height));
        if (requested.Width <= 0 || requested.Height <= 0) throw new InvalidOperationException("校准区域无效");
        return whole.Clone(requested, PixelFormat.Format32bppArgb);
    }

    private async Task<Bitmap> CaptureWindowAsync(CancellationToken cancellationToken = default)
    {
        if (!TryLocateWindow(out _, out var hwnd)) throw new InvalidOperationException("未找到 QQ 窗口");
        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SwRestore);
            await Task.Delay(300, cancellationToken);
        }
        if (NativeMethods.IsIconic(hwnd)) throw new InvalidOperationException("QQ 窗口最小化且恢复失败，无法安全识别");
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) throw new InvalidOperationException("无法读取 QQ 窗口边界");
        var bounds = rect.ToRectangle();
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("QQ 窗口尺寸无效");
        return await _capture.CaptureAsync(hwnd, cancellationToken);
    }

    private bool TryFindExactText(Process process, string expected)
    {
        try
        {
            using var app = FlaUI.Core.Application.Attach(process);
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(2));
            if (window is null) return false;
            var windowTop = window.BoundingRectangle.Top;
            var windowHeight = window.BoundingRectangle.Height;
            return window.FindAllDescendants()
                .Any(x => string.Equals(x.Name?.Trim(), expected.Trim(), StringComparison.Ordinal) &&
                          x.BoundingRectangle.Top <= windowTop + windowHeight * 0.35);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> VerifyGroupOnlyAsync(CalibrationSettings settings, CancellationToken cancellationToken)
    {
        if (!TryLocateWindow(out var process, out var hwnd) || process is null) return false;
        if (!IsFingerprintCompatible(settings, process, hwnd)) return false;
        if (TryFindExactText(process, settings.GroupName)) return true;
        var observation = await ObserveRegionTwiceAsync(settings.GroupTitleRegion, cancellationToken);
        return HasExactOcrLine(observation.RawText, settings.GroupName);
    }

    private async Task<CalibrationSettings> RequireCalibrationAsync(CancellationToken cancellationToken) =>
        await _store.GetSettingAsync<CalibrationSettings>("calibration", cancellationToken)
        ?? throw new InvalidOperationException("尚未完成 QQ 校准");

    private async Task<CalibrationSettings> RequireVerifiedCalibrationAsync(CancellationToken cancellationToken)
    {
        var settings = await RequireCalibrationAsync(cancellationToken);
        if (!settings.IsVerified) throw new InvalidOperationException("QQ 校准尚未验证");
        if (!TryLocateWindow(out var process, out var hwnd) || process is null || !IsFingerprintCompatible(settings, process, hwnd))
            throw new InvalidOperationException("QQ 版本、窗口尺寸或 DPI 已变化，请重新校准");
        return settings;
    }

    public bool IsCalibrationValid(CalibrationSettings? settings) =>
        settings?.IsVerified == true && TryLocateWindow(out var process, out var hwnd) && process is not null &&
        IsFingerprintCompatible(settings, process, hwnd);

    private static bool IsFingerprintCompatible(CalibrationSettings settings, Process process, IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return false;
        var bounds = rect.ToRectangle();
        var version = TryGetVersionForWindow(hwnd);
        if (string.IsNullOrWhiteSpace(version) && File.Exists(settings.QqExecutablePath))
        {
            try { version = FileVersionInfo.GetVersionInfo(settings.QqExecutablePath).FileVersion ?? ""; }
            catch { /* 保持空版本并安全失败 */ }
        }
        if (string.IsNullOrWhiteSpace(version)) return false;
        return string.Equals(version, settings.QqVersion, StringComparison.Ordinal) &&
               Math.Abs(bounds.Width - settings.WindowWidth) <= 3 &&
               Math.Abs(bounds.Height - settings.WindowHeight) <= 3 &&
               NativeMethods.GetDpiForWindow(hwnd) == settings.Dpi;
    }

    private static string TryGetQqExecutablePath(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != 0)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                var path = proc.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path)) return path;
            }
            catch
            {
                // 进程权限不足时继续使用已知安装路径回退。
            }
        }
        foreach (var path in EnumerateKnownQqPaths())
            if (File.Exists(path)) return path;
        return "";
    }

    /// <summary>窗口所属 QQ 进程的版本号；MainModule 拒绝访问时按已知安装路径回退（QQNT 常见）。</summary>
    private static string TryGetVersionForWindow(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != 0)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                var version = proc.MainModule?.FileVersionInfo.FileVersion;
                if (!string.IsNullOrWhiteSpace(version)) return version;
            }
            catch
            {
                // QQNT 进程偶尔拒绝读取 MainModule，下面按已知安装路径回退。
            }
        }
        var fallbackPath = TryGetQqExecutablePath(hwnd);
        if (!string.IsNullOrWhiteSpace(fallbackPath))
        {
            try { return FileVersionInfo.GetVersionInfo(fallbackPath).FileVersion ?? ""; }
            catch { /* 失败即返回空，由指纹校验阻止自动化 */ }
        }
        return "";
    }

    private static IEnumerable<string> EnumerateKnownQqPaths()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tencent", "QQNT", "QQ.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tencent", "QQNT", "QQ.exe");
        foreach (var drive in DriveInfo.GetDrives()
                     .Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            yield return Path.Combine(drive.RootDirectory.FullName, "Program Files", "Tencent", "QQNT", "QQ.exe");
    }

    private static string TryGetProcessPath(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path)) return path;
        }
        catch
        {
            // 进程权限不足时继续使用空值，调用方仍会因版本指纹校验失败而暂停。
        }
        return "";
    }

    private static string TryGetProcessVersion(Process process)
    {
        try
        {
            var version = process.MainModule?.FileVersionInfo.FileVersion;
            if (!string.IsNullOrWhiteSpace(version)) return version;
        }
        catch
        {
            // QQNT 子进程偶尔拒绝读取 MainModule，下面按已知路径回退。
        }
        try
        {
            var path = TryGetProcessPath(process);
            if (!string.IsNullOrWhiteSpace(path))
                return FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";
        }
        catch
        {
            // 失败即返回空，由指纹校验阻止自动化。
        }
        return "";
    }

    private static bool HasExactOcrLine(string rawText, string expected)
    {
        var normalizedExpected = NormalizeForConsensus(expected);
        return rawText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => string.Equals(NormalizeForConsensus(line), normalizedExpected, StringComparison.Ordinal));
    }

    private static void TryDiscoverRegions(Process process, Rectangle windowBounds, CalibrationSettings settings)
    {
        try
        {
            using var app = FlaUI.Core.Application.Attach(process);
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(2));
            if (window is null) return;
            var title = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .FirstOrDefault(x => string.Equals(x.Name?.Trim(), settings.GroupName, StringComparison.Ordinal) &&
                                     x.BoundingRectangle.Top <= window.BoundingRectangle.Top + window.BoundingRectangle.Height * 0.25);
            var input = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
                .Where(x => x.BoundingRectangle.Width >= windowBounds.Width * 0.35 &&
                            x.BoundingRectangle.Top >= windowBounds.Top + windowBounds.Height * 0.55)
                .OrderByDescending(x => x.BoundingRectangle.Width * x.BoundingRectangle.Height)
                .FirstOrDefault();
            if (title is null || input is null) return;
            var titleRect = title.BoundingRectangle;
            var inputRect = input.BoundingRectangle;
            settings.GroupTitleRegion = NormalizeRectangle(
                Rectangle.FromLTRB((int)titleRect.Left - 12, (int)titleRect.Top - 8,
                    (int)titleRect.Right + 12, (int)titleRect.Bottom + 8), windowBounds);
            settings.InputRegion = NormalizeRectangle(
                Rectangle.FromLTRB((int)inputRect.Left, (int)inputRect.Top,
                    (int)inputRect.Right, (int)inputRect.Bottom), windowBounds);
            var chatTop = Math.Max((int)titleRect.Bottom + 6, windowBounds.Top);
            var chatBottom = Math.Min((int)inputRect.Top - 6, windowBounds.Bottom);
            if (chatBottom > chatTop)
                settings.ChatRegion = NormalizeRectangle(Rectangle.FromLTRB((int)inputRect.Left, chatTop,
                    (int)inputRect.Right, chatBottom), windowBounds);
        }
        catch
        {
            // QQ accessibility tree may be incomplete; safe relative defaults remain in effect.
        }
    }

    private static NormalizedRect NormalizeRectangle(Rectangle rectangle, Rectangle window)
    {
        rectangle.Intersect(window);
        return new NormalizedRect(
            Math.Clamp((double)(rectangle.Left - window.Left) / window.Width, 0, 1),
            Math.Clamp((double)(rectangle.Top - window.Top) / window.Height, 0, 1),
            Math.Clamp((double)rectangle.Width / window.Width, 0.01, 1),
            Math.Clamp((double)rectangle.Height / window.Height, 0.01, 1));
    }

    private static async Task<bool> TrySelectExactMentionWithUiaAsync(Process process,
        CalibrationSettings settings, CancellationToken cancellationToken)
    {
        // QQNT 的 @候选框通常在输入后 200–800ms 才完成 UIA 树更新；
        // 先重复读取短时间，避免把候选框动画误判为 OCR 冲突。
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2.2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TrySelectExactMentionWithUia(process, settings)) return true;
            await Task.Delay(180, cancellationToken);
        }
        return false;
    }

    private static bool TrySelectExactMentionWithUia(Process process, CalibrationSettings settings)
    {
        try
        {
            using var app = FlaUI.Core.Application.Attach(process);
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(2));
            if (window is null) return false;
            if (!TryLocateWindowBounds(out var nativeBounds)) return false;
            var inputBounds = settings.InputRegion.ToPixels(nativeBounds);
            var all = window.FindAllDescendants()
                .Where(element => element.ControlType != ControlType.Edit)
                .Select(element => new MentionCandidate(element, GetAccessibleElementText(element),
                    element.BoundingRectangle))
                .Where(x => x.Rectangle.Width > 5 && x.Rectangle.Height > 5 &&
                            IsNearMentionPopup(x.Rectangle, inputBounds) &&
                            x.Text.Contains(settings.GameBotDisplayName, StringComparison.Ordinal))
                .OrderBy(x => x.Rectangle.Width * x.Rectangle.Height)
                .ToArray();
            if (all.Length == 0) return false;

            var qq = settings.GameBotQq.ToString();
            var exact = DistinctMentionCandidates(all.Where(x =>
                Regex.Replace(x.Text, @"\D", "").Contains(qq, StringComparison.Ordinal)));
            if (exact.Count == 1)
            {
                ClickElementCenter(exact[0]);
                return true;
            }
            if (exact.Count > 1) return false;

            // 当前 QQ 版本的候选行只暴露“小小”及头像，不暴露 QQ 号。
            // 仅当输入框附近存在唯一的精确显示名候选时才接受，
            // 多候选仍然失败即暂停，绝不按位置猜测。
            var nameOnly = DistinctMentionCandidates(all.Where(x =>
                string.Equals(x.Element.Name?.Trim(), settings.GameBotDisplayName, StringComparison.Ordinal) ||
                IsRepeatedExactMentionName(x.Text, settings.GameBotDisplayName)));
            if (nameOnly.Count != 1) return false;
            ClickElementCenter(nameOnly[0]);
            return true;
        }
        catch { return false; }
    }

    private async Task<bool> TrySelectExactMentionWithOcrAsync(CalibrationSettings settings, CancellationToken cancellationToken)
    {
        var mentionRegion = GetMentionRegion(settings);
        if (!TryLocateWindow(out _, out var hwnd) || !NativeMethods.GetWindowRect(hwnd, out var nativeRect))
            return false;
        var windowBounds = nativeRect.ToRectangle();
        var mentionPixels = mentionRegion.ToPixels(windowBounds);
        var inputPixels = settings.InputRegion.ToPixels(windowBounds);
        OcrObservation first;
        OcrObservation second;
        try
        {
            first = await RecognizeRegionAsync(mentionRegion, cancellationToken);
            await Task.Delay(220, cancellationToken);
            second = await RecognizeRegionAsync(mentionRegion, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }

        var firstMatches = ExtractMentionMatches(first, settings, mentionPixels, inputPixels);
        var secondMatches = ExtractMentionMatches(second, settings, mentionPixels, inputPixels);
        if (firstMatches.Count != 1 || secondMatches.Count != 1 ||
            !string.Equals(firstMatches[0].Signature, secondMatches[0].Signature, StringComparison.Ordinal))
            return false;
        var match = secondMatches[0];
        Click(mentionPixels.Left + match.Bounds.Center.X, mentionPixels.Top + match.Bounds.Center.Y);
        return true;
    }

    private async Task<OcrObservation> RecognizeRegionAsync(NormalizedRect region,
        CancellationToken cancellationToken)
    {
        using var bitmap = await CaptureRegionAsync(region, cancellationToken);
        return await _ocr.RecognizeAsync(bitmap, cancellationToken);
    }

    private static IReadOnlyList<MentionOcrMatch> ExtractMentionMatches(
        OcrObservation observation, CalibrationSettings settings, Rectangle mentionPixels,
        Rectangle inputPixels)
    {
        var qq = settings.GameBotQq.ToString();
        return observation.Words
            .GroupBy(word => (int)Math.Round((word.Bounds.Y + word.Bounds.Height / 2d) / 12d))
            .Select(words => words.OrderBy(x => x.Bounds.X).ToArray())
            .Select(words => new MentionOcrMatch(
                string.Concat(words.Select(x => x.Text)),
                new PixelRect(words.Min(x => x.Bounds.X), words.Min(x => x.Bounds.Y),
                    words.Max(x => x.Bounds.X + x.Bounds.Width) - words.Min(x => x.Bounds.X),
                    words.Max(x => x.Bounds.Y + x.Bounds.Height) - words.Min(x => x.Bounds.Y))))
            .Where(match =>
            {
                var text = match.Text;
                var digits = Regex.Replace(text, @"\D", "");
                var absoluteCenterY = mentionPixels.Top + match.Bounds.Center.Y;
                return text.Contains(settings.GameBotDisplayName, StringComparison.Ordinal) &&
                       !text.TrimStart().StartsWith("@", StringComparison.Ordinal) &&
                       (digits.Length == 0 || digits.Contains(qq, StringComparison.Ordinal)) &&
                       absoluteCenterY >= inputPixels.Top - 140 &&
                       absoluteCenterY <= inputPixels.Top + 8;
            })
            .Select(match => match with
            {
                Signature = NormalizeForConsensus(match.Text)
            })
            .GroupBy(match => match.Signature, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(match => match.Bounds.Width).First())
            .ToArray();
    }

    private bool TryClickSendButton(CalibrationSettings settings)
    {
        try
        {
            if (!TryLocateWindow(out var process, out var hwnd) || process is null ||
                !NativeMethods.GetWindowRect(hwnd, out var nativeRect)) return false;
            using var app = FlaUI.Core.Application.Attach(process);
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(2));
            if (window is null) return false;
            var inputBounds = settings.InputRegion.ToPixels(nativeRect.ToRectangle());
            var candidates = window.FindAllDescendants()
                .Where(element => element.ControlType != ControlType.Edit)
                .Select(element => new MentionCandidate(element, element.Name?.Trim() ?? "",
                    element.BoundingRectangle))
                .Where(item => item.Text.StartsWith("发送", StringComparison.Ordinal) &&
                               item.Rectangle.Width > 25 && item.Rectangle.Height > 15 &&
                               item.Rectangle.Top >= inputBounds.Top + inputBounds.Height / 3 &&
                               item.Rectangle.Bottom <= inputBounds.Bottom + 30)
                .OrderByDescending(item => item.Rectangle.Top)
                .ThenBy(item => item.Rectangle.Width * item.Rectangle.Height)
                .ToArray();
            if (candidates.Length == 0) return false;
            ClickElementCenter(candidates[0].Element);
            return true;
        }
        catch { return false; }
    }

    private static string GetAccessibleElementText(AutomationElement element) =>
        string.Join(" ", new[] { element.Name }.Concat(
            element.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(x => x.Name)));

    private static bool IsRepeatedExactMentionName(string text, string expected)
    {
        var normalized = string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
        if (normalized.Length == 0 || expected.Length == 0 || normalized.Length % expected.Length != 0)
            return false;
        for (var offset = 0; offset < normalized.Length; offset += expected.Length)
            if (!normalized.AsSpan(offset, expected.Length).SequenceEqual(expected.AsSpan())) return false;
        return true;
    }

    private static bool IsNearMentionPopup(Rectangle rectangle,
        Rectangle inputBounds)
    {
        var top = (double)rectangle.Top;
        var bottom = (double)(rectangle.Top + rectangle.Height);
        return bottom >= inputBounds.Top - Math.Max(120, inputBounds.Height * 4) &&
               top <= inputBounds.Bottom + 30 &&
               rectangle.Left < inputBounds.Right + 80 &&
               rectangle.Right > inputBounds.Left - 80;
    }

    private static IReadOnlyList<AutomationElement> DistinctMentionCandidates(IEnumerable<MentionCandidate> source)
    {
        return source
            .GroupBy(item => $"{item.Rectangle.Left:0.##},{item.Rectangle.Top:0.##},{item.Rectangle.Width:0.##},{item.Rectangle.Height:0.##}")
            .Select(group => group.First().Element)
            .ToArray();
    }

    private static void ClickElementCenter(AutomationElement element)
    {
        var rectangle = element.BoundingRectangle;
        Click((int)Math.Round((double)(rectangle.Left + rectangle.Width / 2)),
            (int)Math.Round((double)(rectangle.Top + rectangle.Height / 2)));
    }

    private static NormalizedRect GetMentionRegion(CalibrationSettings settings)
    {
        var top = Math.Max(0, settings.InputRegion.Y - 0.24);
        var bottom = Math.Min(1, settings.InputRegion.Y + settings.InputRegion.Height + 0.04);
        return new NormalizedRect(settings.InputRegion.X, top,
            settings.InputRegion.Width, Math.Max(0.08, bottom - top));
    }

    private static bool TryLocateWindowBounds(out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        var processes = Process.GetProcesses()
            .Where(p => p.ProcessName.Equals("QQ", StringComparison.OrdinalIgnoreCase) ||
                        p.ProcessName.Equals("QQNT", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var windows = new List<(IntPtr Handle, long Area)>();
        foreach (var process in processes)
        {
            var processId = process.Id;
            NativeMethods.EnumWindows((candidate, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(candidate, out var pid);
                if (pid != processId || !NativeMethods.IsWindowVisible(candidate)) return true;
                if (!NativeMethods.GetWindowRect(candidate, out var rect)) return true;
                var item = rect.ToRectangle();
                if (item.Width > 0 && item.Height > 0) windows.Add((candidate, (long)item.Width * item.Height));
                return true;
            }, IntPtr.Zero);
        }
        var selected = windows.OrderByDescending(x => x.Area).FirstOrDefault();
        return selected.Handle != IntPtr.Zero && NativeMethods.GetWindowRect(selected.Handle, out var native)
            ? (bounds = native.ToRectangle()) != Rectangle.Empty
            : false;
    }

    private sealed record MentionCandidate(AutomationElement Element, string Text, Rectangle Rectangle);
    private sealed record MentionOcrMatch(string Text, PixelRect Bounds, string Signature = "");

    private static string NormalizeForConsensus(string value) => OcrConsensus.Normalize(value);

    private static void RestoreAndActivate(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SwRestore);
        if (!NativeMethods.SetForegroundWindow(hwnd)) throw new InvalidOperationException("无法激活 QQ 窗口");
        Thread.Sleep(120);
    }

    private static void Click(int screenX, int screenY)
    {
        var width = Math.Max(1, NativeMethods.GetSystemMetrics(0) - 1);
        var height = Math.Max(1, NativeMethods.GetSystemMetrics(1) - 1);
        var x = (int)Math.Round(screenX * 65535d / width);
        var y = (int)Math.Round(screenY * 65535d / height);
        var inputs = new[]
        {
            new NativeMethods.Input { Type = NativeMethods.InputMouse, Union = new NativeMethods.InputUnion { Mouse = new NativeMethods.MouseInput { Dx = x, Dy = y, Flags = NativeMethods.MouseeventfMove | NativeMethods.MouseeventfAbsolute } } },
            new NativeMethods.Input { Type = NativeMethods.InputMouse, Union = new NativeMethods.InputUnion { Mouse = new NativeMethods.MouseInput { Flags = NativeMethods.MouseeventfLeftdown } } },
            new NativeMethods.Input { Type = NativeMethods.InputMouse, Union = new NativeMethods.InputUnion { Mouse = new NativeMethods.MouseInput { Flags = NativeMethods.MouseeventfLeftup } } }
        };
        EnsureInput(inputs);
    }

    private static void SendUnicode(string text)
    {
        var inputs = new List<NativeMethods.Input>(text.Length * 2);
        foreach (var ch in text)
        {
            inputs.Add(Keyboard(0, ch, NativeMethods.KeyeventfUnicode));
            inputs.Add(Keyboard(0, ch, NativeMethods.KeyeventfUnicode | NativeMethods.KeyeventfKeyup));
        }
        EnsureInput(inputs.ToArray());
    }

    private static void KeyPress(ushort key) => EnsureInput([
        Keyboard(key, 0, 0), Keyboard(key, 0, NativeMethods.KeyeventfKeyup)]);

    private static void KeyChord(ushort modifier, ushort key) => EnsureInput([
        Keyboard(modifier, 0, 0), Keyboard(key, 0, 0),
        Keyboard(key, 0, NativeMethods.KeyeventfKeyup), Keyboard(modifier, 0, NativeMethods.KeyeventfKeyup)]);

    private static NativeMethods.Input Keyboard(ushort key, ushort scan, uint flags) => new()
    {
        Type = NativeMethods.InputKeyboard,
        Union = new NativeMethods.InputUnion { Keyboard = new NativeMethods.KeyboardInput { VirtualKey = key, ScanCode = scan, Flags = flags } }
    };

    private static void EnsureInput(NativeMethods.Input[] inputs)
    {
        if (NativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Input>()) != inputs.Length)
            throw new InvalidOperationException("Windows SendInput 未完整执行");
    }
}

public sealed class OcrConflictException : Exception
{
    public OcrConflictException(string message, OcrObservation first, OcrObservation second) : base(message)
    {
        First = first;
        Second = second;
    }
    public OcrObservation First { get; }
    public OcrObservation Second { get; }
}
