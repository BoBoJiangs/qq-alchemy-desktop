using System.Diagnostics;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
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
    private readonly object _uiaSessionGate = new();
    private CachedUiAutomationSession? _uiaSession;

    // A clicked market link normally fills the QQ edit control almost
    // immediately.  Poll the control briefly before falling back to one OCR
    // read; the old fixed 450ms + two-frame OCR path made every listing wait
    // several seconds even when UIA already exposed the UUID.
    private const int PurchaseCodePostClickDelayMilliseconds = 180;
    private const int PurchaseCodePollMilliseconds = 60;
    private const int PurchaseCodePollTimeoutMilliseconds = 350;
    private const int CommandInputFocusDelayMilliseconds = 100;
    private const int CommandPostTypeDelayMilliseconds = 120;

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
            // Reuse the bound chat UIA session for the expensive text tree.
            // The edit-control probe below remains a separate, short scan
            // because it belongs to the composer rather than the message
            // container.
            texts.AddRange(GetAccessibleTextNodes(visibleOnly: false)
                .Select(node => node.Text)
                .Distinct(StringComparer.Ordinal)
                .Take(200));
            using var app = FlaUI.Core.Application.Attach(process);
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(2));
            if (window is not null)
            {
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
    /// Returns the currently exposed QQ text nodes without sending input.
    /// This is a read-only fallback for blue link/card text that OCR may miss.
    /// </summary>
    public IReadOnlyList<string> GetAccessibleTexts() => GetDiagnostics().AccessibleTexts;

    /// <summary>
    /// Returns only text nodes that are currently visible inside the chat
    /// viewport.  QQ keeps old card nodes in UIA even after they scroll away,
    /// so callers must not use the complete accessibility tree for prices.
    /// </summary>
    public IReadOnlyList<(string Text, Rectangle Bounds)> GetVisibleAccessibleTexts()
    {
        return GetAccessibleTextNodes(visibleOnly: true)
            .Select(item => (item.Text, item.Bounds))
            .ToArray();
    }

    private IReadOnlyList<AccessibleTextNode> GetAccessibleTextNodes(bool visibleOnly)
    {
        if (!TryLocateWindow(out var process, out var hwnd) || process is null)
            return Array.Empty<AccessibleTextNode>();
        lock (_uiaSessionGate)
        {
            try
            {
                var session = GetOrCreateUiAutomationSession(process, hwnd);
                var chatViewport = GetChatViewport(session.Window.BoundingRectangle);
                var root = session.ChatRoot ?? session.Window;
                var nodes = root.FindAllDescendants()
                    .Where(element => element.ControlType == ControlType.Text ||
                                      element.ControlType == ControlType.Hyperlink)
                    .Select(element => new AccessibleTextNode(
                        element.Name?.Trim() ?? "", element.BoundingRectangle))
                    .Where(item => item.Text.Length > 0 && item.Bounds.Width >= 4 &&
                                   item.Bounds.Height >= 6 &&
                                   (!visibleOnly || chatViewport.IntersectsWith(item.Bounds)))
                    .DistinctBy(item => (item.Text, item.Bounds.Left, item.Bounds.Top,
                        item.Bounds.Width, item.Bounds.Height))
                    .OrderBy(item => item.Bounds.Top)
                    .ThenBy(item => item.Bounds.Left)
                    .ToArray();
                if (nodes.Length > 0)
                    return nodes;

                // QQ occasionally exposes the DocumentControl before its
                // descendants are populated. Use the full window once in
                // that case, but keep the session so the next poll does not
                // recreate the UIA connection.
                return ReadAccessibleTextNodes(session.Window, visibleOnly, chatViewport);
            }
            catch
            {
                // A QQ navigation can invalidate cached UIA COM elements.
                // Rebuild on the next poll instead of returning stale text.
                ResetUiAutomationSession();
                return Array.Empty<AccessibleTextNode>();
            }
        }
    }

    private CachedUiAutomationSession GetOrCreateUiAutomationSession(Process process, IntPtr hwnd)
    {
        if (_uiaSession is not null && _uiaSession.Matches(process.Id, hwnd))
            return _uiaSession;

        ResetUiAutomationSession();
        var app = FlaUI.Core.Application.Attach(process);
        var automation = new UIA3Automation();
        var window = automation.FromHandle(hwnd)
                     ?? throw new InvalidOperationException("无法读取 QQ UI Automation 根窗口");
        var chatRoot = FindChatMessageRoot(window);
        _uiaSession = new CachedUiAutomationSession(process.Id, hwnd, app, automation, window, chatRoot);
        return _uiaSession;
    }

    private void ResetUiAutomationSession()
    {
        _uiaSession?.Dispose();
        _uiaSession = null;
    }

    private static IReadOnlyList<AccessibleTextNode> ReadAccessibleTextNodes(
        AutomationElement root, bool visibleOnly, Rectangle chatViewport)
    {
        return root.FindAllDescendants()
            .Where(element => element.ControlType == ControlType.Text ||
                              element.ControlType == ControlType.Hyperlink)
            .Select(element => new AccessibleTextNode(
                element.Name?.Trim() ?? "", element.BoundingRectangle))
            .Where(item => item.Text.Length > 0 && item.Bounds.Width >= 4 &&
                           item.Bounds.Height >= 6 &&
                           (!visibleOnly || chatViewport.IntersectsWith(item.Bounds)))
            .DistinctBy(item => (item.Text, item.Bounds.Left, item.Bounds.Top,
                item.Bounds.Width, item.Bounds.Height))
            .OrderBy(item => item.Bounds.Top)
            .ThenBy(item => item.Bounds.Left)
            .ToArray();
    }

    private static AutomationElement? FindChatMessageRoot(AutomationElement window)
    {
        var viewport = GetChatViewport(window.BoundingRectangle);
        var candidates = window.FindAllDescendants()
            .Where(element => IsPotentialChatContainer(element.ControlType))
            .Select(element => new
            {
                Element = element,
                Type = element.ControlType,
                Bounds = element.BoundingRectangle,
                Name = (element.Name ?? "").Trim(),
                ClassName = (element.ClassName ?? "").Trim()
            })
            .Where(item => item.Bounds.Width >= 160 && item.Bounds.Height >= 80 &&
                           viewport.IntersectsWith(item.Bounds))
            .Select(item => new
            {
                item.Element,
                item.Type,
                item.Bounds,
                Score = ChatRootScore(item.Type, item.Name, item.ClassName, item.Bounds, viewport)
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Bounds.Width * item.Bounds.Height)
            .ToArray();

        return candidates.FirstOrDefault()?.Element;
    }

    private static int ChatRootScore(ControlType type, string name, string className,
        Rectangle bounds, Rectangle viewport)
    {
        var score = type switch
        {
            ControlType.Document => 1_000_000,
            ControlType.List => 900_000,
            ControlType.Pane => 800_000,
            ControlType.Group => 700_000,
            ControlType.Custom => 600_000,
            _ => 0
        };
        var label = $"{name} {className}";
        if (label.Contains("消息", StringComparison.Ordinal) ||
            label.Contains("聊天", StringComparison.Ordinal) ||
            label.Contains("message", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("conversation", StringComparison.OrdinalIgnoreCase))
            score += 100_000;

        var overlap = Rectangle.Intersect(bounds, viewport);
        if (!overlap.IsEmpty)
        {
            var viewportArea = Math.Max(1L, (long)viewport.Width * viewport.Height);
            var overlapArea = (long)overlap.Width * overlap.Height;
            score += (int)Math.Min(90_000, overlapArea * 90_000 / viewportArea);
        }
        return score;
    }

    internal static bool IsPotentialChatContainer(ControlType type) =>
        type is ControlType.Document or ControlType.List or ControlType.Pane or
            ControlType.Group or ControlType.Custom;

    private static Rectangle GetChatViewport(Rectangle windowBounds) => Rectangle.FromLTRB(
        windowBounds.Left + (int)(windowBounds.Width * 0.20),
        windowBounds.Top + (int)(windowBounds.Height * 0.10),
        windowBounds.Left + (int)(windowBounds.Width * 0.80),
        windowBounds.Top + (int)(windowBounds.Height * 0.90));

    internal static Rectangle GetInventoryCardViewport(Rectangle windowBounds) => Rectangle.FromLTRB(
        windowBounds.Left + (int)(windowBounds.Width * 0.05),
        windowBounds.Top + (int)(windowBounds.Height * 0.10),
        windowBounds.Left + (int)(windowBounds.Width * 0.82),
        windowBounds.Top + (int)(windowBounds.Height * 0.90));

    private static bool TryGetWindowBounds(out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        return TryLocateWindowBounds(out bounds);
    }

    /// <summary>
    /// Reads the newest complete inventory card from QQ's full UIA tree.
    /// QQ keeps the card's text nodes even after its top scrolls above the
    /// viewport, so this avoids losing the first rows to a cropped OCR frame.
    /// The footer must still be visible and match the requested page; that
    /// freshness check prevents an old off-screen response from being reused.
    /// </summary>
    public bool TryObserveAccessibleInventoryPage(int expectedPage,
        IReadOnlyCollection<string> knownHerbNames, out OcrObservation observation)
        => TryObserveAccessibleInventoryPageCore(expectedPage, knownHerbNames, out observation);

    /// <summary>
    /// Reads the current inventory card without requiring the recipe catalog.
    /// Calibration only needs a page marker and inventory title; the full
    /// herb-name completeness check is reserved for the real inventory flow.
    /// </summary>
    public bool TryObserveAccessibleInventoryPage(int expectedPage,
        out OcrObservation observation) =>
        TryObserveAccessibleInventoryPageCore(expectedPage, Array.Empty<string>(), out observation);

    private bool TryObserveAccessibleInventoryPageCore(int expectedPage,
        IReadOnlyCollection<string> knownHerbNames, out OcrObservation observation)
    {
        observation = default!;
        if (expectedPage <= 0) return false;
        var nodes = GetAccessibleTextNodes(visibleOnly: false);
        if (nodes.Count == 0 || !TryGetWindowBounds(out var windowBounds)) return false;
        // Inventory cards in QQNT are rendered farther left than the generic
        // chat viewport (the footer may start around 8% of the window width).
        // Keep the generic viewport for UIA root selection, but widen only
        // this page-marker check so the footer is not filtered out.
        var chatViewport = GetInventoryCardViewport(windowBounds);
        var pageMarker = nodes
            .Where(node => ParsePageMarker(node.Text) is { Current: var current } &&
                           current == expectedPage && chatViewport.IntersectsWith(node.Bounds))
            .OrderByDescending(node => node.Bounds.Top)
            .FirstOrDefault();
        if (pageMarker is null) return false;

        var title = nodes
            .Where(node => IsInventoryTitle(node.Text, expectedPage) &&
                           node.Bounds.Top <= pageMarker.Bounds.Top &&
                           node.Bounds.Top >= pageMarker.Bounds.Top - 2200)
            .OrderByDescending(node => node.Bounds.Top)
            .FirstOrDefault();
        if (title is null) return false;

        var card = nodes
            .Where(node => node.Bounds.Top >= title.Bounds.Top &&
                           node.Bounds.Top <= pageMarker.Bounds.Bottom + 24)
            .OrderBy(node => node.Bounds.Top)
            .ThenBy(node => node.Bounds.Left)
            .DistinctBy(node => (node.Text, node.Bounds.Left, node.Bounds.Top,
                node.Bounds.Width, node.Bounds.Height))
            .ToArray();
        var raw = string.Join('\n', card.Select(node => node.Text));
        if (!IsCompleteInventoryText(raw, expectedPage, knownHerbNames)) return false;

        var words = card.Select(node => new OcrWordData(node.Text,
            new PixelRect(node.Bounds.Left, node.Bounds.Top,
                Math.Max(1, node.Bounds.Width), Math.Max(1, node.Bounds.Height)), 0.99d))
            .ToArray();
        var signature = string.Join('|', card.Select(node =>
            $"{node.Text}:{node.Bounds.Left},{node.Bounds.Top},{node.Bounds.Width},{node.Bounds.Height}"));
        var frameHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
        observation = new OcrObservation(raw, words, frameHash, DateTimeOffset.Now);
        return true;
    }

    /// <summary>
    /// Reads a useful, currently visible chat snapshot from UIA.  This is
    /// intentionally a fast path for inventory page detection; callers still
    /// fall back to OCR when QQ has not exposed enough text nodes yet.
    /// </summary>
    public bool TryObserveVisibleChat(out OcrObservation observation)
    {
        observation = default!;
        var visible = GetVisibleAccessibleTexts();
        if (visible.Count == 0) return false;
        var raw = string.Join('\n', visible.Select(item => item.Text));
        if (!HasUsefulChatMarker(raw)) return false;

        var words = visible.Select(item => new OcrWordData(item.Text,
            new PixelRect(item.Bounds.Left, item.Bounds.Top,
                Math.Max(1, item.Bounds.Width), Math.Max(1, item.Bounds.Height)), 0.99d))
            .ToArray();
        var signature = string.Join('|', visible.Select(item =>
            $"{item.Text}:{item.Bounds.Left},{item.Bounds.Top},{item.Bounds.Width},{item.Bounds.Height}"));
        var frameHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
        observation = new OcrObservation(raw, words, frameHash, DateTimeOffset.Now);
        return true;
    }

    /// <summary>
    /// Reads an inventory card from the visible UIA tree only when its page
    /// number matches the page currently requested by the coordinator.
    /// Requiring the page marker prevents a still-visible old card from being
    /// accepted immediately after a pagination command.
    /// </summary>
    public bool TryObserveVisibleInventoryPage(int expectedPage, out OcrObservation observation)
    {
        observation = default!;
        if (expectedPage <= 0) return false;
        var visible = GetVisibleAccessibleTexts();
        if (visible.Count == 0) return false;
        var raw = string.Join('\n', visible.Select(item => item.Text));
        if (!HasInventoryPayload(raw)) return false;
        var page = ParsePageStateFromAccessibleTexts(visible.Select(item => item.Text).ToArray());
        if (page is null || page.Value.Current != expectedPage) return false;

        var words = visible.Select(item => new OcrWordData(item.Text,
            new PixelRect(item.Bounds.Left, item.Bounds.Top,
                Math.Max(1, item.Bounds.Width), Math.Max(1, item.Bounds.Height)), 0.99d))
            .ToArray();
        var signature = string.Join('|', visible.Select(item =>
            $"{item.Text}:{item.Bounds.Left},{item.Bounds.Top},{item.Bounds.Width},{item.Bounds.Height}"));
        var frameHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
        observation = new OcrObservation(raw, words, frameHash, DateTimeOffset.Now);
        return true;
    }

    internal static bool HasUsefulChatMarker(string text) =>
        text.Contains("药材背包", StringComparison.Ordinal) ||
        text.Contains("坊市数据", StringComparison.Ordinal) ||
        text.Contains("坊市购买", StringComparison.Ordinal) ||
        text.Contains("价格", StringComparison.Ordinal) ||
        text.Contains("成功购买", StringComparison.Ordinal) ||
        text.Contains("未查询", StringComparison.Ordinal) ||
        text.Contains("坊市现在", StringComparison.Ordinal) ||
        text.Contains("上一条指令", StringComparison.Ordinal);

    internal static bool HasInventoryPayload(string text) =>
        MessageClassifier.IsInventoryPage(text) &&
        (text.Contains("拥有数量", StringComparison.Ordinal) ||
         text.Contains("数量", StringComparison.Ordinal));

    internal static bool IsInventoryTitle(string text, int expectedPage) =>
        string.Equals(text, "药材背包", StringComparison.Ordinal) ||
        string.Equals(text, $"药材背包{expectedPage}", StringComparison.Ordinal) ||
        text.Contains("的药材背包", StringComparison.Ordinal);

    private static (int Current, int Total)? ParsePageMarker(string text)
    {
        var compact = text.Replace(" ", "", StringComparison.Ordinal);
        var match = Regex.Match(compact, @"第(?<current>\d+)页/共(?<total>\d+)页");
        return match.Success && int.TryParse(match.Groups["current"].Value, out var current) &&
               int.TryParse(match.Groups["total"].Value, out var total)
            ? (current, total)
            : null;
    }

    internal static bool HasPurchaseSuccessFor(string text, string herbName)
    {
        // QQNT decorates hyperlink names with zero-width markers (for
        // example: "三尾\u200b风叶\u200b").  They are not visible to the user,
        // but an ordinal substring check would reject an otherwise valid
        // purchase confirmation and force the slow OCR fallback.
        var normalizedText = NormalizeAccessibleMatchText(text);
        var normalizedHerbName = NormalizeAccessibleMatchText(herbName);
        return MessageClassifier.IsPurchaseSuccess(normalizedText) &&
               normalizedHerbName.Length > 0 &&
               normalizedText.Contains(normalizedHerbName, StringComparison.Ordinal);
    }

    internal static string NormalizeAccessibleMatchText(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character) &&
            character is not ('\u200B' or '\u200C' or '\u200D' or '\u200E' or '\u200F' or '\u2060' or '\uFEFF')));

    /// <summary>
    /// Detect a captcha only when its UIA text node has a real on-screen
    /// rectangle in the QQ chat viewport. QQ keeps solved captcha messages in
    /// its accessibility tree, so text presence alone is not sufficient.
    /// </summary>
    public bool HasVisibleCaptcha()
    {
        if (!TryLocateWindow(out var process, out var hwnd) || process is null) return false;
        try
        {
            using var app = FlaUI.Core.Application.Attach(process);
            using var automation = new UIA3Automation();
            var window = automation.FromHandle(hwnd);
            if (window is null) return false;
            var bounds = window.BoundingRectangle;
            var chatViewport = Rectangle.FromLTRB(
                bounds.Left + (int)(bounds.Width * 0.20),
                bounds.Top + (int)(bounds.Height * 0.10),
                bounds.Left + (int)(bounds.Width * 0.80),
                bounds.Top + (int)(bounds.Height * 0.90));
            return window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Where(element => MessageClassifier.IsCaptcha(element.Name?.Trim() ?? ""))
                .Select(element => element.BoundingRectangle)
                .Any(rect => IsVisibleCaptchaBounds(rect, chatViewport));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// QQ leaves a solved captcha card in the chat history. A following bot
    /// result/reward message is the reliable signal that the user's click was
    /// accepted, even though the original captcha text remains visible.
    /// </summary>
    public bool HasCaptchaResolution()
    {
        try
        {
            return IsCaptchaResolvedByFollowingText(GetVisibleAccessibleTexts());
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsCaptchaResolvedByFollowingText(
        IReadOnlyList<(string Text, Rectangle Bounds)> nodes)
    {
        var captchaBottom = nodes
            .Where(node => MessageClassifier.IsCaptcha(node.Text))
            .Select(node => node.Bounds.Bottom)
            .DefaultIfEmpty(-1)
            .Max();
        if (captchaBottom < 0) return false;

        return nodes.Any(node => node.Bounds.Top > captchaBottom &&
            (MessageClassifier.IsPurchaseSuccess(node.Text) ||
             node.Text.Contains("奖励", StringComparison.Ordinal)));
    }

    internal static bool IsVisibleCaptchaBounds(Rectangle textBounds, Rectangle chatViewport) =>
        textBounds.Width >= 20 && textBounds.Height >= 8 && chatViewport.IntersectsWith(textBounds);

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
                // QQNT exposes several tiny helper/title-bar windows. They are
                // visible, but cannot host the chat UI and used to win the
                // area comparison when the real window was transitioning.
                // Keep only plausible top-level chat windows first; a small
                // fallback below still allows diagnostics to report a window
                // when QQ is minimized or in a transient state.
                if (IsUsableWindowBounds(width, height) && !NativeMethods.IsIconic(candidate))
                    candidates.Add((candidateProcess, candidate, (long)width * height));
                return true;
            }, IntPtr.Zero);
        }

        if (candidates.Count == 0)
        {
            foreach (var candidateProcess in Process.GetProcesses()
                         .Where(p => p.ProcessName.Equals("QQ", StringComparison.OrdinalIgnoreCase) ||
                                     p.ProcessName.Equals("QQNT", StringComparison.OrdinalIgnoreCase)))
            {
                var handle = candidateProcess.MainWindowHandle;
                if (handle == IntPtr.Zero || !NativeMethods.IsWindowVisible(handle)) continue;
                // When the QQ main window is minimized, its restored bounds
                // are reported as a tiny 160x28 title strip. Restore that
                // stable main handle before measuring it; otherwise callers
                // would persist invalid calibration regions.
                if (NativeMethods.IsIconic(handle))
                {
                    NativeMethods.ShowWindow(handle, NativeMethods.SwRestore);
                    Thread.Sleep(120);
                }
                if (!NativeMethods.GetWindowRect(handle, out var rect)) continue;
                var width = Math.Max(0, rect.Right - rect.Left);
                var height = Math.Max(0, rect.Bottom - rect.Top);
                if (IsUsableWindowBounds(width, height))
                    candidates.Add((candidateProcess, handle, (long)width * height));
            }
        }

        var selected = candidates.OrderByDescending(item => item.Area).FirstOrDefault();
        process = selected.Process;
        hwnd = selected.Handle;
        return process is not null && hwnd != IntPtr.Zero;
    }

    internal static bool IsUsableWindowBounds(int width, int height) => width >= 400 && height >= 300;

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
        TryDiscoverRegions(hwnd, bounds, settings);
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
            var expectedPage = 1;
            await SendAtCommandAsync(query, cancellationToken);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(900, cancellationToken);
                var observation = await ObserveRegionTwiceAsync(settings.ChatRegion, cancellationToken,
                    allowLiveRefresh: true);
                if (MessageClassifier.IsCaptcha(observation.RawText))
                    throw new InvalidOperationException("校准测试触发验证码，请人工处理后重新校准");
                // 整屏回复卡片会把命令行顶出可视区：响应判定直接用完整区域原文
                //（PP-OCR 已按行压缩空白，卡片内的名字/数量/页码都在其中）。
                var page = ParsePageState(observation.RawText);
                var inventoryPage = MessageClassifier.IsInventoryPage(observation.RawText);
                IReadOnlyList<string> accessibleTexts = Array.Empty<string>();
                if (page is null)
                {
                    // Reuse the same page-marker/viewport pairing used by
                    // the real inventory task. It selects the card whose
                    // footer is currently visible and avoids mistaking an
                    // older off-screen inventory response for this page.
                    if (TryObserveAccessibleInventoryPage(expectedPage, out var inventoryUia))
                    {
                        page = ParsePageState(inventoryUia.RawText);
                        inventoryPage = MessageClassifier.IsInventoryPage(inventoryUia.RawText);
                        if (page is not null && inventoryPage)
                        {
                            await _store.AuditAsync("info", "verify_page_uia_fallback",
                                $"OCR 未识别页脚，按当前可视背包卡片读取第{page.Value.Current}页/共{page.Value.Total}页",
                                cancellationToken: CancellationToken.None);
                        }
                    }
                }
                if (page is null)
                {
                    // QQNT exposes the rendered card text through UIA even
                    // when the blue footer is too small/low-contrast for OCR.
                    // Use that read-only view as a bounded fallback so a
                    // healthy response does not fail calibration solely on a
                    // missed "第N页/共M页" glyph.
                    // Read only the current chat viewport here.  The full
                    // QQ accessibility tree keeps scrolled-out cards alive,
                    // so using GetDiagnostics() could repeatedly return an
                    // old page marker after the next page command was sent.
                    var visibleTexts = GetVisibleAccessibleTexts()
                        .Select(item => item.Text)
                        .ToArray();
                    // If QQ temporarily exposes no visible text while the
                    // card is animating, allow one full-tree retry.  Never
                    // prefer the full tree when visible text is available;
                    // stale off-screen pages are unsafe for calibration.
                    accessibleTexts = PreferVisibleAccessibleTexts(
                        visibleTexts,
                        visibleTexts.Length == 0 ? GetDiagnostics().AccessibleTexts : Array.Empty<string>());
                    page = ParsePageStateFromAccessibleTexts(accessibleTexts);
                    inventoryPage = HasAccessibleInventoryResponse(accessibleTexts);
                    if (page is not null && inventoryPage)
                    {
                        await _store.AuditAsync("info", "verify_page_uia_fallback",
                            $"OCR 未识别页脚，改用 UIA 读取第{page.Value.Current}页/共{page.Value.Total}页",
                            cancellationToken: CancellationToken.None);
                    }
                    else
                    {
                        page = null;
                    }
                }
                else if (!inventoryPage)
                {
                    // OCR may recover the page footer but miss the blue
                    // response heading; consult UIA before discarding it.
                    var visibleTexts = GetVisibleAccessibleTexts()
                        .Select(item => item.Text)
                        .ToArray();
                    accessibleTexts = PreferVisibleAccessibleTexts(
                        visibleTexts,
                        visibleTexts.Length == 0 ? GetDiagnostics().AccessibleTexts : Array.Empty<string>());
                    inventoryPage = HasAccessibleInventoryResponse(accessibleTexts);
                }
                if (page is null)
                {
                    // 诊断：页脚“第N页/共M页”未被 OCR 识别（数字/文字误读），节流记录样本。
                    var missTail = observation.RawText.Length > 120 ? observation.RawText[^120..] : observation.RawText;
                    try
                    {
                        await _store.AuditAsync("warn", "verify_page_miss", missTail,
                            cancellationToken: CancellationToken.None);
                    }
                    catch { /* 审计失败不影响主流程 */ }
                    continue;
                }
                if (!inventoryPage) continue;
                if (page.Value.Current != expectedPage) continue;
                if (expectedPage >= page.Value.Total || expectedPage >= 20)
                {
                    settings.VerifiedAt = DateTimeOffset.Now;
                    settings.IsVerified = true;
                    await _store.SetSettingAsync("calibration", settings, cancellationToken);
                    await _store.AuditAsync("info", "calibration_verified",
                        $"群标题、窗口尺寸、DPI 校验通过，背包 {page.Value.Total} 页逐页回读确认", cancellationToken: cancellationToken);
                    return true;
                }
                expectedPage = page.Value.Current + 1;
                query = $"药材背包{expectedPage}";
                await SendAtCommandAsync(query, cancellationToken);
                deadline = DateTimeOffset.UtcNow.AddSeconds(45);
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
        return await ObserveRegionTwiceAsync(ExpandChatRegion(settings.ChatRegion), cancellationToken,
            allowLiveRefresh: true);
    }

    /// <summary>
    /// Performs a fast inventory OCR pass first. The 2x pass is accepted
    /// only when it resolves a complete page; otherwise a 3x pass preserves
    /// the old high-accuracy fallback for small herb names.
    /// </summary>
    public async Task<OcrObservation> ObserveInventoryPageAsync(
        int expectedPage, IReadOnlyCollection<string> knownHerbNames,
        CancellationToken cancellationToken = default)
    {
        var settings = await RequireVerifiedCalibrationAsync(cancellationToken);
        var region = ExpandChatRegion(settings.ChatRegion);
        var fast = await RecognizeRegionAsync(region, cancellationToken, scaleFactor: 2);
        if (IsCompleteInventoryObservation(fast, expectedPage, knownHerbNames)) return fast;
        return await RecognizeRegionAsync(region, cancellationToken);
    }

    internal static bool IsCompleteInventoryObservation(OcrObservation observation,
        int expectedPage, IReadOnlyCollection<string> knownHerbNames)
        => IsCompleteInventoryObservation(observation.RawText, expectedPage, knownHerbNames);

    internal static bool IsCompleteInventoryObservation(string rawText,
        int expectedPage, IReadOnlyCollection<string> knownHerbNames)
    {
        if (!HasInventoryPageFor(rawText, expectedPage) || knownHerbNames.Count == 0)
            return false;
        var entries = InventoryParser.Parse(rawText, new HerbNameResolver(knownHerbNames));
        var pageMatch = Regex.Match(OcrConsensus.Normalize(rawText),
            @"第(?<current>\d+)页/共(?<total>\d+)页");
        if (!pageMatch.Success || !int.TryParse(pageMatch.Groups["current"].Value, out var current) ||
            !int.TryParse(pageMatch.Groups["total"].Value, out var total)) return false;
        // Normal pages contain 20 entries and the last page contains the
        // remainder. Requiring nearly all entries prevents a 2x pass that
        // happened to recognize only a few rows from being accepted.
        var minimumEntries = current < total ? 18 : 15;
        return entries.Count >= minimumEntries;
    }

    internal static bool IsCompleteInventoryText(string rawText, int expectedPage,
        IReadOnlyCollection<string> knownHerbNames)
    {
        if (!HasInventoryPageFor(rawText, expectedPage) || knownHerbNames.Count == 0)
            return false;
        var entries = InventoryParser.Parse(rawText, new HerbNameResolver(knownHerbNames));
        var quantityCount = Regex.Matches(rawText,
            @"(?:拥有数量|数量)\s*[:：]?\s*\d+").Count;
        // A complete UIA card exposes one quantity line for every name link.
        // Requiring equality avoids accepting a clipped/partial card while
        // still supporting a shorter final page.
        return quantityCount >= 10 && entries.Count == quantityCount;
    }

    /// <summary>
    /// Reads only the recent bottom portion of the chat once.  Purchase
    /// confirmations are appended there, so the coordinator can validate the
    /// current command without waiting for a full-chat OCR consensus.
    /// </summary>
    public async Task<OcrObservation> ObservePurchaseResultAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await RequireVerifiedCalibrationAsync(cancellationToken);
        var recentChat = ExpandChatRegion(settings.ChatRegion);
        const double recentHeightRatio = 0.48;
        var recentRegion = new NormalizedRect(
            recentChat.X,
            recentChat.Y + recentChat.Height * (1 - recentHeightRatio),
            recentChat.Width,
            recentChat.Height * recentHeightRatio);
        return await RecognizeRegionAsync(recentRegion, cancellationToken);
    }

    private static NormalizedRect ExpandChatRegion(NormalizedRect region)
    {
        const double leftPadding = 0.18;
        var left = Math.Max(0, region.X - leftPadding);
        return new NormalizedRect(left, region.Y,
            Math.Min(1 - left, region.Width + (region.X - left)), region.Height);
    }

    /// <summary>
    /// Reads a market card with a small-text fallback.  The herb name is the
    /// source of truth: once OCR resolves it against the configured purchase
    /// rules, its OCR rectangle is safe to click.  Blue pixels are used only
    /// to split the dense card into likely text rows; they are not required
    /// for a successful match.
    /// </summary>
    public async Task<OcrObservation> ObserveMarketAsync(IEnumerable<string> knownHerbNames,
        CancellationToken cancellationToken = default)
    {
        var settings = await RequireVerifiedCalibrationAsync(cancellationToken);
        if (TryObserveAccessibleMarketPage(settings.ChatRegion, knownHerbNames, out var accessibleMarket))
        {
            await RecordOcrAuditAsync("info", "market_uia_fast_path",
                $"UIA 直接读取坊市卡片，候选词数={accessibleMarket.Words.Count}");
            return accessibleMarket;
        }

        var baseObservation = await ObserveRegionTwiceAsync(settings.ChatRegion, cancellationToken,
            allowLiveRefresh: true);
        var resolver = new HerbNameResolver(knownHerbNames);
        var baseResolved = baseObservation.Words
            .Select(word => (Word: word, Name: resolver.Resolve(word.Text)))
            .Where(x => x.Name is not null)
            .Select(x => (x.Word, Name: x.Name!))
            .ToArray();

        // The calibration input region is intentionally narrow for typing,
        // but the market card starts much farther left than the input box.
        // Capture an expanded market area and translate its OCR coordinates
        // back to the configured chat region before creating a listing.
        var marketRegion = ExpandChatRegion(settings.ChatRegion);
        using var wholeMarket = await CaptureWindowAsync(cancellationToken);
        var wholeBounds = new Rectangle(0, 0, wholeMarket.Width, wholeMarket.Height);
        var requestedMarket = marketRegion.ToPixels(wholeBounds);
        requestedMarket.Intersect(wholeBounds);
        var configuredChat = settings.ChatRegion.ToPixels(wholeBounds);
        var offsetX = requestedMarket.Left - configuredChat.Left;
        var offsetY = requestedMarket.Top - configuredChat.Top;
        using var bitmap = wholeMarket.Clone(requestedMarket, PixelFormat.Format32bppArgb);
        var regions = BlueLinkLocator.FindRegions(bitmap,
            new PixelRect(0, 0, bitmap.Width, bitmap.Height));
        if (regions.Count == 0)
        {
            await RecordOcrAuditAsync("warn", "market_text_regions_empty",
                "坊市卡片未找到颜色提示区域，继续使用整块 OCR 名称和坐标");
            return baseObservation;
        }

        var resolved = new List<(string Name, PixelRect Bounds, double? Price)>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        // Try one full-card OCR first.  The row crops remain a deliberate
        // fallback because very small blue names are sometimes missed when
        // they are surrounded by other card text.
        var fullMarketObservation = await _ocr.RecognizeAsync(bitmap, cancellationToken);
        var fullMarketWords = fullMarketObservation.Words
            .Select(word => (Word: word, Name: resolver.Resolve(word.Text)))
            .ToArray();
        foreach (var region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowTop = Math.Max(0, region.Y - 8);
            var rowBottom = Math.Min(bitmap.Height, region.Y + region.Height + 8);
            var rowHeight = Math.Max(1, rowBottom - rowTop);
            var candidate = fullMarketWords
                .Where(x => x.Name is not null &&
                            x.Word.Bounds.Y + x.Word.Bounds.Height / 2 >= rowTop &&
                            x.Word.Bounds.Y + x.Word.Bounds.Height / 2 <= rowBottom &&
                            !usedNames.Contains(x.Name!))
                .Select(x =>
                {
                    var shifted = x.Word with
                    {
                        Bounds = x.Word.Bounds with
                        {
                            X = x.Word.Bounds.X + offsetX,
                            Y = x.Word.Bounds.Y + offsetY
                        }
                    };
                    return (Word: shifted, Name: x.Name!);
                })
                .OrderByDescending(x => x.Word.Confidence)
                .FirstOrDefault();

            var fullRowText = string.Join(" ", fullMarketObservation.Words
                .Where(word => word.Bounds.Y + word.Bounds.Height / 2 >= rowTop &&
                               word.Bounds.Y + word.Bounds.Height / 2 <= rowBottom)
                .OrderBy(word => word.Bounds.X)
                .Select(word => word.Text));
            var price = ParseMarketPrice(fullRowText);
            if (candidate.Name is null || price is null)
            {
                using var row = bitmap.Clone(new Rectangle(0, rowTop, bitmap.Width, rowHeight),
                    PixelFormat.Format32bppArgb);
                var rowObservation = await _ocr.RecognizeAsync(row, cancellationToken);
                if (candidate.Name is null)
                {
                    candidate = rowObservation.Words
                        .Select(word =>
                        {
                            var shifted = word with
                            {
                                Bounds = word.Bounds with
                                {
                                    X = word.Bounds.X + offsetX,
                                    Y = word.Bounds.Y + rowTop + offsetY
                                }
                            };
                            return (Word: shifted, Name: resolver.Resolve(word.Text));
                        })
                        .Where(x => x.Name is not null)
                        .Select(x => (x.Word, Name: x.Name!))
                        .Where(x => !usedNames.Contains(x.Name))
                        .OrderByDescending(x => x.Word.Confidence)
                        .FirstOrDefault();
                }
                if (price is null) price = ParseMarketPrice(rowObservation.RawText);
                if (candidate.Name is not null && price is null)
                {
                    var nameCrop = bitmap.Clone(new Rectangle(
                        Math.Max(0, region.X - 6), rowTop,
                        Math.Min(bitmap.Width - Math.Max(0, region.X - 6), region.Width + 12), rowHeight),
                        PixelFormat.Format32bppArgb);
                    try
                    {
                        var nameObservation = await _ocr.RecognizeAsync(nameCrop, cancellationToken);
                        price = ParseMarketPrice(nameObservation.RawText);
                    }
                    finally
                    {
                        nameCrop.Dispose();
                    }
                }
            }

            if (candidate.Name is null) continue;

            usedNames.Add(candidate.Name);
            resolved.Add((candidate.Name, candidate.Word.Bounds, price));
        }

        if (resolved.Count == 0)
        {
            // The fallback is deliberately non-fatal.  A later OCR pass may
            // still recognize a normal black-text market card.
            await RecordOcrAuditAsync("warn", "market_text_enrich_empty",
                $"regions={regions.Count}; baseResolved={baseResolved.Length}; resolved=0; " +
                $"ocr={OcrConsensus.Compact(baseObservation.RawText)}");
            return baseObservation;
        }

        var accessiblePrices = ParseAccessibleMarketPrices(
            GetVisibleAccessibleTexts().Select(item => item.Text).ToArray());
        await RecordOcrAuditAsync("info", "market_text_enrich",
            $"regions={regions.Count}; baseResolved={baseResolved.Length}; resolved={resolved.Count}; " +
            $"fullWords={fullMarketWords.Length}; rowPrices={resolved.Count(x => x.Price is not null)}; " +
            $"uiaPrices={accessiblePrices.Count}");
        var words = new List<OcrWordData>();
        foreach (var item in resolved.Select((value, index) => (value, index)))
        {
            double? price = item.value.Price;
            if (price is null && item.index < accessiblePrices.Count)
                price = accessiblePrices[item.index];
            if (price is null) continue;
            words.Add(new OcrWordData(item.value.Name, item.value.Bounds, 0.9d));
            // MarketParser groups by Y, so the synthetic price only needs to
            // share the herb's row.  It is intentionally independent of any
            // color/link assumption.
            words.Add(new OcrWordData($"价格:{price.Value:0.####}万",
                new PixelRect(Math.Max(0, item.value.Bounds.X - 80), item.value.Bounds.Y, 70,
                    item.value.Bounds.Height), 0.99d));
        }

        if (words.Count == 0) return baseObservation;
        var raw = string.Join('\n', words.OrderBy(x => x.Bounds.Y).ThenBy(x => x.Bounds.X)
            .GroupBy(x => x.Bounds.Y)
            .Select(group => string.Concat(group.OrderBy(x => x.Bounds.X).Select(x => x.Text))));
        return new OcrObservation(raw, words, baseObservation.FrameHash, baseObservation.CapturedAt);
    }

    private bool TryObserveAccessibleMarketPage(NormalizedRect chatRegion,
        IEnumerable<string> knownHerbNames,
        out OcrObservation observation)
    {
        observation = default!;
        var visible = GetVisibleAccessibleTexts();
        if (!TryBuildAccessibleMarketWords(visible, knownHerbNames, out var screenWords) ||
            !TryLocateWindowBounds(out var windowBounds)) return false;
        var chatBounds = chatRegion.ToPixels(windowBounds);
        var words = screenWords.Select(word => word with
        {
            Bounds = word.Bounds with
            {
                X = word.Bounds.X - chatBounds.Left,
                Y = word.Bounds.Y - chatBounds.Top
            }
        }).ToArray();

        var signature = string.Join('|', words.Select(word =>
            $"{word.Text}:{word.Bounds.X},{word.Bounds.Y},{word.Bounds.Width},{word.Bounds.Height}"));
        var raw = string.Join('\n', words
            .GroupBy(word => word.Bounds.Y)
            .OrderBy(group => group.Key)
            .Select(group => string.Concat(group.OrderBy(word => word.Bounds.X).Select(word => word.Text))));
        observation = new OcrObservation(raw, words,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))),
            DateTimeOffset.Now);
        return true;
    }

    internal static bool TryBuildAccessibleMarketWords(
        IReadOnlyList<(string Text, Rectangle Bounds)> visible,
        IEnumerable<string> knownHerbNames, out IReadOnlyList<OcrWordData> words)
    {
        words = Array.Empty<OcrWordData>();
        var marker = visible
            .Where(node => node.Text.Contains("查看坊市药材", StringComparison.Ordinal) ||
                           node.Text.Contains("坊市查看", StringComparison.Ordinal))
            .OrderByDescending(node => node.Bounds.Top)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(marker.Text)) return false;

        var card = visible
            .Where(node => node.Bounds.Top >= marker.Bounds.Top)
            .OrderBy(node => node.Bounds.Top)
            .ThenBy(node => node.Bounds.Left)
            .ToArray();
        if (!card.Any(node => node.Text.Contains("翻页", StringComparison.Ordinal) ||
                              node.Text.Contains("下一页", StringComparison.Ordinal))) return false;

        var resolver = new HerbNameResolver(knownHerbNames);
        var herbs = card
            .Select(node => (Node: node, Name: resolver.Resolve(node.Text)))
            .Where(item => item.Name is not null)
            .Select(item => (item.Node, Name: item.Name!))
            .ToArray();
        var prices = card
            .Select((node, index) => (Node: node, Index: index, Price: ParseMarketPrice(node.Text)))
            .Where(item => item.Price is not null)
            .Select(item => (item.Node, item.Index, Price: item.Price!.Value))
            .ToArray();
        if (herbs.Length == 0 || prices.Length == 0) return false;

        var matched = new List<(string Name, Rectangle HerbBounds, double Price)>();
        var usedPrices = new HashSet<int>();
        foreach (var herb in herbs)
        {
            var herbCenterY = herb.Node.Bounds.Top + herb.Node.Bounds.Height / 2d;
            var price = prices
                .Where(item => !usedPrices.Contains(item.Index))
                .Select(item => new
                {
                    item.Index,
                    item.Price,
                    Distance = Math.Abs((item.Node.Bounds.Top + item.Node.Bounds.Height / 2d) - herbCenterY)
                })
                .Where(item => item.Distance <= 30)
                .OrderBy(item => item.Distance)
                .FirstOrDefault();
            if (price is null) continue;
            usedPrices.Add(price.Index);
            matched.Add((herb.Name, herb.Node.Bounds, price.Price));
        }

        // Accept the fast path only when nearly every visible price has a
        // paired herb. Otherwise the caller falls back to OCR instead of
        // silently advancing with an incomplete market page.
        var minimumMatches = prices.Length < 3
            ? prices.Length
            : Math.Max(3, (int)Math.Ceiling(prices.Length * 0.75));
        if (matched.Count < minimumMatches) return false;

        var result = matched.Select(item => new OcrWordData(item.Name,
            new PixelRect(item.HerbBounds.Left, item.HerbBounds.Top,
                Math.Max(1, item.HerbBounds.Width), Math.Max(1, item.HerbBounds.Height)), 0.99d))
            .Concat(matched.Select(item => new OcrWordData(
                $"价格:{item.Price:0.####}万",
                new PixelRect(item.HerbBounds.Left - 80, item.HerbBounds.Top,
                    70, Math.Max(1, item.HerbBounds.Height)), 0.99d)))
            .ToArray();
        words = result
            .OrderBy(word => word.Bounds.Y)
            .ThenBy(word => word.Bounds.X)
            .ToArray();
        return true;
    }

    private static double? ParseMarketPrice(string text)
    {
        var match = Regex.Match(text, @"(?<price>\d+(?:\.\d+)?)\s*(?<unit>万|亿)",
            RegexOptions.CultureInvariant);
        if (!match.Success || !double.TryParse(match.Groups["price"].Value,
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out var price)) return null;
        return match.Groups["unit"].Value == "亿" ? price * 10_000d : price;
    }

    private static IReadOnlyList<double> ParseAccessibleMarketPrices(IReadOnlyList<string> accessible)
    {
        var marker = accessible.Select((text, index) => (text, index))
            .Where(x => x.text.Contains("查看坊市药材", StringComparison.Ordinal) ||
                        x.text.Contains("坊市查看", StringComparison.Ordinal))
            .Select(x => x.index)
            .DefaultIfEmpty(-1)
            .Max();
        var prices = new List<double>();
        foreach (var text in accessible.Skip(marker + 1))
        {
            var price = ParseMarketPrice(text);
            if (price is not null) prices.Add(price.Value);
        }
        return prices;
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
            await Task.Delay(CommandInputFocusDelayMilliseconds, cancellationToken);
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
            await Task.Delay(CommandPostTypeDelayMilliseconds, cancellationToken);
            if (!TryClickSendButton(settings))
            {
                // UIA 忙碌找不到“发送”按钮时，直接回车发送（输入框内就是刚填入的命令）。
                KeyPress(NativeMethods.VkReturn);
                await _store.AuditAsync("warn", "send_via_enter", command, cancellationToken: cancellationToken);
            }
            if (NativeMethods.GetWindowRect(hwnd, out var sentRect))
                ScrollChatToBottom(settings.ChatRegion, sentRect.ToRectangle());
            await _store.AuditAsync("info", "qq_command_sent", command, Guid.NewGuid().ToString("N"), cancellationToken);
        }
        finally
        {
            _inputGate.Release();
        }
    }

    public async Task<string> CaptureListingCommandAsync(MarketListing listing,
        CancellationToken cancellationToken = default)
    {
        await _inputGate.WaitAsync(cancellationToken);
        try
        {
            var settings = await RequireVerifiedCalibrationAsync(cancellationToken);
            if (!await VerifyGroupOnlyAsync(settings, cancellationToken))
                throw new InvalidOperationException("当前 QQ 群标题不匹配，拒绝点击");
            if (!TryLocateWindow(out var process, out var hwnd) || process is null)
                throw new InvalidOperationException("QQ 窗口已丢失");
            RestoreAndActivate(hwnd);
            NativeMethods.GetWindowRect(hwnd, out var nativeRect);
            var windowBounds = nativeRect.ToRectangle();
            var chat = settings.ChatRegion.ToPixels(windowBounds);
            var input = settings.InputRegion.ToPixels(windowBounds);
            var point = new Point(chat.Left + listing.ClickRect.Center.X,
                chat.Top + listing.ClickRect.Center.Y + Math.Max(2, listing.ClickRect.Height / 3));

            var locatedByUia = TryFindVisibleMarketHyperlinkPoint(process, settings,
                windowBounds, listing.HerbName, out var uiaPoint);
            if (locatedByUia)
            {
                point = uiaPoint;
                await _store.AuditAsync("info", "market_click_target_uia",
                    $"{listing.HerbName} screen={point.X},{point.Y}",
                    listing.ListingToken, cancellationToken);
            }
            else
            {
                var fresh = await ObserveRegionTwiceAsync(settings.ChatRegion, cancellationToken);
                if (!string.Equals(fresh.FrameHash, listing.FrameHash, StringComparison.Ordinal))
                {
                    // The coordinator deliberately captures every command
                    // before sending any purchase.  If the card moved during
                    // that capture phase, do not issue a new market query and
                    // do not guess a replacement coordinate: pause safely so
                    // the user can inspect the changed page.
                    throw new MarketListingNotVisibleException(listing.HerbName);
                }
            }

            await _store.AuditAsync("info", "market_click_attempt",
                $"{listing.HerbName} rect={listing.ClickRect} screen={point.X},{point.Y} " +
                $"chat={chat.Left},{chat.Top},{chat.Width},{chat.Height}",
                listing.ListingToken, cancellationToken);

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
                var elementInfo = element is null
                    ? "null"
                    : $"name={element.Name};type={element.ControlType};rect={element.BoundingRectangle};" +
                      $"invoke={element.Patterns.Invoke.IsSupported}";
                await _store.AuditAsync("info", "market_click_element",
                    $"{listing.HerbName} {elementInfo}", listing.ListingToken, cancellationToken);
                if (element is not null && element.Patterns.Invoke.IsSupported)
                {
                    if (element.TryGetClickablePoint(out var clickable) &&
                        windowBounds.Contains(clickable))
                    {
                        await _store.AuditAsync("info", "market_click_clickable_point",
                            $"{listing.HerbName} screen={clickable.X},{clickable.Y}",
                            listing.ListingToken, cancellationToken);
                        Click(clickable.X, clickable.Y);
                    }
                    else
                    {
                        element.Patterns.Invoke.Pattern.Invoke();
                    }
                    invoked = true;
                }
            }
            catch
            {
                invoked = false;
            }
            if (!invoked) Click(point.X, point.Y);
            await Task.Delay(PurchaseCodePostClickDelayMilliseconds, cancellationToken);

            string preparedText = "";
            string? command = await WaitForPurchaseCommandAsync(process, settings, cancellationToken);
            if (command is null)
            {
                preparedText = await RecognizeInputOnceAsync(settings, cancellationToken);
                if (PurchaseCommandValidator.TryValidate(preparedText, settings.GameBotDisplayName,
                        requireBotMention: false, out var ocrCommand)) command = ocrCommand;
            }
            if (command is null)
            {
                // On the current QQNT market card the visible herb name is
                // usually decorative; the actionable purchase link is the
                // adjacent “物品功效” link. Try that proven target before
                // spending another OCR round on the same decorative link.
                var effectPoint = new Point(point.X - 100, point.Y - 24);
                await _store.AuditAsync("info", "market_click_effect_fallback",
                    $"{listing.HerbName} screen={effectPoint.X},{effectPoint.Y}",
                    listing.ListingToken, cancellationToken);
                Click(effectPoint.X, effectPoint.Y);
                await Task.Delay(PurchaseCodePostClickDelayMilliseconds, cancellationToken);
                command = await WaitForPurchaseCommandAsync(process, settings, cancellationToken);
                if (command is null)
                {
                    preparedText = await RecognizeInputOnceAsync(settings, cancellationToken, expandRegion: true);
                    if (PurchaseCommandValidator.TryValidate(preparedText, settings.GameBotDisplayName,
                            requireBotMention: false, out var effectCommand)) command = effectCommand;
                }
            }
            if (command is null)
            {
                // Some QQNT versions delay the first clickable-point event.
                // Keep the original herb-name click as a final fallback, but
                // still require a validated UUID before returning.
                ClearInput(input);
                Click(point.X, point.Y);
                await Task.Delay(PurchaseCodePostClickDelayMilliseconds, cancellationToken);
                command = await WaitForPurchaseCommandAsync(process, settings, cancellationToken);
                if (command is null)
                {
                    preparedText = await RecognizeInputOnceAsync(settings, cancellationToken);
                    if (PurchaseCommandValidator.TryValidate(preparedText, settings.GameBotDisplayName,
                            requireBotMention: false, out var retryCommand)) command = retryCommand;
                }
            }
            if (command is null)
            {
                ClearInput(input);
                await _store.AuditAsync("warn", "market_click_input_invalid",
                    $"{listing.HerbName} screen={point.X},{point.Y} input={preparedText}",
                    listing.ListingToken, cancellationToken);
                throw new InvalidOperationException("点击药材后未能确认输入框中的坊市购买命令和采购码，已清空并拒绝发送");
            }
            // The first phase only captures the validated command. Clear the
            // single QQ input box so the next listing can be clicked while
            // the market card is still on the original, stable page.
            ClearInput(input);
            return command;
        }
        finally
        {
            _inputGate.Release();
        }
    }

    private static async Task<string?> WaitForPurchaseCommandAsync(Process process,
        CalibrationSettings settings, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(PurchaseCodePollTimeoutMilliseconds);
        while (true)
        {
            var text = TryReadInputText(process, settings);
            if (PurchaseCommandValidator.TryValidate(text, settings.GameBotDisplayName,
                    requireBotMention: false, out var command)) return command;
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;
            await Task.Delay(Math.Min(PurchaseCodePollMilliseconds, (int)remaining.TotalMilliseconds),
                cancellationToken);
        }
    }

    private async Task<string> RecognizeInputOnceAsync(CalibrationSettings settings,
        CancellationToken cancellationToken, bool expandRegion = false)
    {
        // The command is typed in the calibrated edit box.  OCRing that
        // narrow region avoids running recognition over the whole chat card;
        // only the last-resort adjacent-link retry needs the expanded crop.
        var region = expandRegion ? ExpandChatRegion(settings.InputRegion) : settings.InputRegion;
        using var bitmap = await CaptureRegionAsync(region, cancellationToken);
        var observation = await _ocr.RecognizeAsync(bitmap, cancellationToken);
        return observation.RawText;
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

    private static bool TryFindVisibleMarketHyperlinkPoint(Process process,
        CalibrationSettings settings, Rectangle windowBounds, string herbName, out Point point)
    {
        point = default;
        try
        {
            using var app = FlaUI.Core.Application.Attach(process);
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(2));
            if (window is null) return false;
            var marketBounds = ExpandChatRegion(settings.ChatRegion).ToPixels(windowBounds);
            var resolver = new HerbNameResolver([herbName]);
            var candidates = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Hyperlink))
                .Select(element => new { Element = element, Name = element.Name?.Trim() ?? "", Rectangle = element.BoundingRectangle })
                .Where(item => item.Rectangle.Width >= 8 && item.Rectangle.Height >= 8 &&
                               marketBounds.IntersectsWith(item.Rectangle) &&
                               resolver.Resolve(item.Name) == herbName)
                .OrderBy(item => item.Rectangle.Top)
                .ThenBy(item => item.Rectangle.Left)
                .ToArray();
            if (candidates.Length != 1) return false;
            var rectangle = candidates[0].Rectangle;
            if (candidates[0].Element.TryGetClickablePoint(out var clickable) &&
                windowBounds.Contains(clickable))
            {
                point = clickable;
            }
            else
            {
                point = new Point((int)Math.Round((double)(rectangle.Left + rectangle.Width / 2)),
                    (int)Math.Round((double)(rectangle.Top + rectangle.Height / 2)));
            }
            return windowBounds.Contains(point);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sends a previously captured and validated market command.  Separating
    /// this from CaptureListingCommandAsync prevents purchase responses from
    /// scrolling the market card while the remaining commands are collected.
    /// </summary>
    public async Task SendPurchaseCommandAsync(MarketListing listing, string command,
        CancellationToken cancellationToken = default)
    {
        var settings = await RequireVerifiedCalibrationAsync(cancellationToken);
        if (!PurchaseCommandValidator.TryValidate(command, settings.GameBotDisplayName,
                requireBotMention: false, out var normalized))
            throw new InvalidOperationException($"{listing.HerbName} 的采购码未通过最终校验，拒绝发送");

        await SendAtCommandAsync(normalized, cancellationToken);
        await _store.AuditAsync("info", "market_purchase_sent",
            $"{listing.HerbName} {listing.PriceWan:0.####}万 page={listing.Page} command={normalized}",
            listing.ListingToken, cancellationToken);
    }

    /// <summary>
    /// Compatibility wrapper for callers that still need a single click and
    /// send operation.  The coordinator uses the two-phase methods above.
    /// </summary>
    public async Task<string> ClickAndSendListingAsync(MarketListing listing,
        CancellationToken cancellationToken = default)
    {
        var command = await CaptureListingCommandAsync(listing, cancellationToken);
        await SendPurchaseCommandAsync(listing, command, cancellationToken);
        return command;
    }

    private static void ScrollChatToBottom(NormalizedRect chatRegion, Rectangle windowBounds)
    {
        var chat = chatRegion.ToPixels(windowBounds);
        if (chat.Width <= 0 || chat.Height <= 0) return;
        var x = chat.Left + chat.Width / 2;
        var y = chat.Bottom - Math.Max(20, chat.Height / 12);
        var width = Math.Max(1, NativeMethods.GetSystemMetrics(0) - 1);
        var height = Math.Max(1, NativeMethods.GetSystemMetrics(1) - 1);
        var dx = (int)Math.Round(x * 65535d / width);
        var dy = (int)Math.Round(y * 65535d / height);
        var inputs = Enumerable.Range(0, 6)
            .Select(_ => new NativeMethods.Input
            {
                Type = NativeMethods.InputMouse,
                Union = new NativeMethods.InputUnion
                {
                    Mouse = new NativeMethods.MouseInput
                    {
                        Dx = dx,
                        Dy = dy,
                        MouseData = unchecked((uint)-1200),
                        Flags = NativeMethods.MouseeventfMove |
                                NativeMethods.MouseeventfAbsolute |
                                NativeMethods.MouseeventfWheel
                    }
                }
            })
            .ToArray();
        EnsureInput(inputs);
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

    private static void TryDiscoverRegions(IntPtr hwnd, Rectangle windowBounds, CalibrationSettings settings)
    {
        try
        {
            using var automation = new UIA3Automation();
            var window = automation.FromHandle(hwnd);
            if (window is null) return;
            // 群标题只认聊天面板头部标题带（排除左侧会话列表和聊天消息里的同名文本）。
            var discoveredTitle = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Where(x => string.Equals(x.Name?.Trim(), settings.GroupName, StringComparison.Ordinal))
                .Select(x => x.BoundingRectangle)
                .Where(r => r.Top <= windowBounds.Top + windowBounds.Height * 0.12 &&
                            r.Left >= windowBounds.Left + windowBounds.Width * 0.35 &&
                            r.Left <= windowBounds.Left + windowBounds.Width * 0.98)
                .OrderBy(r => r.Top)
                .Select(r => Rectangle.FromLTRB((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom))
                .FirstOrDefault();
            Rectangle? title = discoveredTitle.IsEmpty ? null : discoveredTitle;
            var input = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
                .Where(x => x.BoundingRectangle.Width >= windowBounds.Width * 0.35 &&
                            x.BoundingRectangle.Top >= windowBounds.Top + windowBounds.Height * 0.55)
                .OrderByDescending(x => x.BoundingRectangle.Width * x.BoundingRectangle.Height)
                .FirstOrDefault();
            // 新版 QQNT 的聊天输入框不再暴露为 Edit 控件；
            // 改用稳定的"发送"按钮 + 工具栏 icon-item 行几何推导输入区。
            var send = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Where(x => string.Equals(x.Name?.Trim(), "发送", StringComparison.Ordinal))
                .Select(x => x.BoundingRectangle)
                .FirstOrDefault(r => r.Width > 15 && r.Width < 80 && r.Height > 15 && r.Height < 60 &&
                                     r.Top >= windowBounds.Top + windowBounds.Height * 0.8);
            Rectangle fallbackInput = Rectangle.Empty;
            if (input is null && send != Rectangle.Empty)
            {
                var iconRects = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .Where(x => string.Equals(x.ClassName, "icon-item", StringComparison.Ordinal))
                    .Select(x => x.BoundingRectangle)
                    // 只取聊天面板内的工具栏图标，排除左侧栏/右键菜单里的同名 icon-item。
                    .Where(r => r.Top >= windowBounds.Top + windowBounds.Height * 0.7 &&
                                r.Bottom < send.Top &&
                                r.Left >= windowBounds.Left + windowBounds.Width * 0.25)
                    .ToArray();
                if (iconRects.Length > 0)
                {
                    var left = iconRects.Min(r => r.Left) - 12;
                    var right = iconRects.Max(r => r.Right) + 14;
                    var top = iconRects.Max(r => r.Bottom) + 2;
                    var bottom = send.Top - 4;
                    if (right > left && bottom > top)
                        fallbackInput = Rectangle.FromLTRB(left, top, right, bottom);
                }
            }
            if (title is null && input is null && fallbackInput.IsEmpty) return;
            if (title is not null && !title.Value.IsEmpty)
            {
                settings.GroupTitleRegion = NormalizeRectangle(
                    Rectangle.FromLTRB(title.Value.Left - 12, title.Value.Top - 8,
                        title.Value.Right + 12, title.Value.Bottom + 8), windowBounds);
            }
            var resolvedInput = input is not null
                ? Rectangle.FromLTRB((int)input.BoundingRectangle.Left, (int)input.BoundingRectangle.Top,
                    (int)input.BoundingRectangle.Right, (int)input.BoundingRectangle.Bottom)
                : fallbackInput;
            settings.InputRegion = NormalizeRectangle(resolvedInput, windowBounds);
            var titleBottom = title is not null && !title.Value.IsEmpty
                ? title.Value.Bottom
                : windowBounds.Top + (int)(windowBounds.Height * 0.10);
            var chatTop = Math.Max(titleBottom + 6, windowBounds.Top);
            var chatBottom = Math.Min(resolvedInput.Top - 6, windowBounds.Bottom);
            if (chatBottom > chatTop)
                settings.ChatRegion = NormalizeRectangle(Rectangle.FromLTRB(resolvedInput.Left, chatTop,
                    resolvedInput.Right, chatBottom), windowBounds);
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
            if (!TryLocateWindowBounds(out var nativeBounds)) return false;
            var inputBounds = settings.InputRegion.ToPixels(nativeBounds);
            // The mention popup can be owned by a QQ helper process and can
            // be nested below a desktop window that does not expose a useful
            // root rectangle. Search all desktop descendants, then constrain
            // candidates by their actual popup rectangle near the input.
            var all = automation.GetDesktop().FindAllDescendants()
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
        {
            await RecordOcrAuditAsync("warn", "mention_ocr_candidates",
                $"first={string.Join("|", firstMatches.Select(x => x.Text))}; " +
                $"second={string.Join("|", secondMatches.Select(x => x.Text))}");
            return false;
        }
        var match = secondMatches[0];
        Click(mentionPixels.Left + match.Bounds.Center.X, mentionPixels.Top + match.Bounds.Center.Y);
        return true;
    }

    private async Task<OcrObservation> RecognizeRegionAsync(NormalizedRect region,
        CancellationToken cancellationToken, int scaleFactor = 3)
    {
        using var bitmap = await CaptureRegionAsync(region, cancellationToken);
        return await _ocr.RecognizeAsync(bitmap, cancellationToken, scaleFactor);
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
                               item.Rectangle.Bottom <= inputBounds.Bottom + Math.Max(30, inputBounds.Height))
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
        var items = source.ToArray();
        // QQ exposes both the popup row and its nested text/icon nodes. Keep
        // the smallest clickable element inside a larger duplicate container,
        // while preserving separate rows with the same display name.
        var leaves = items.Where(item => !items.Any(other =>
            !ReferenceEquals(item, other) &&
            IsNestedMentionRectangle(other.Rectangle, item.Rectangle))).ToArray();
        return leaves
            .GroupBy(item => $"{item.Rectangle.Left:0.##},{item.Rectangle.Top:0.##},{item.Rectangle.Width:0.##},{item.Rectangle.Height:0.##}")
            .Select(group => group.First().Element)
            .ToArray();
    }

    internal static bool IsNestedMentionRectangle(Rectangle outer, Rectangle inner) =>
        outer.Width * outer.Height > inner.Width * inner.Height * 1.15d && outer.Contains(inner);

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

    private sealed class CachedUiAutomationSession : IDisposable
    {
        public CachedUiAutomationSession(int processId, IntPtr windowHandle,
            FlaUI.Core.Application application, UIA3Automation automation,
            AutomationElement window, AutomationElement? chatRoot)
        {
            ProcessId = processId;
            WindowHandle = windowHandle;
            Application = application;
            Automation = automation;
            Window = window;
            ChatRoot = chatRoot;
        }

        public int ProcessId { get; }
        public IntPtr WindowHandle { get; }
        public FlaUI.Core.Application Application { get; }
        public UIA3Automation Automation { get; }
        public AutomationElement Window { get; }
        public AutomationElement? ChatRoot { get; }

        public bool Matches(int processId, IntPtr windowHandle) =>
            ProcessId == processId && WindowHandle == windowHandle &&
            NativeMethods.IsWindowVisible(windowHandle);

        public void Dispose()
        {
            try { Automation.Dispose(); } catch { }
            try { Application.Dispose(); } catch { }
        }
    }

    private sealed record AccessibleTextNode(string Text, Rectangle Bounds);
    private sealed record MentionCandidate(AutomationElement Element, string Text, Rectangle Rectangle);
    private sealed record MentionOcrMatch(string Text, PixelRect Bounds, string Signature = "");

    private static string NormalizeForConsensus(string value) => OcrConsensus.Normalize(value);

    /// <summary>从背包/坊市卡片尾部解析“第N页/共M页”，识别不到返回 null。</summary>
    private static (int Current, int Total)? ParsePageState(string rawText)
    {
        var compact = OcrConsensus.Normalize(rawText);
        var m = Regex.Match(compact, @"第(\d+)页/共(\d+)页");
        if (!m.Success) return null;
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    internal static (int Current, int Total)? ParsePageStateFromAccessibleTexts(
        IReadOnlyList<string> accessibleTexts)
    {
        for (var i = accessibleTexts.Count - 1; i >= 0; i--)
        {
            var compact = accessibleTexts[i].Replace(" ", "", StringComparison.Ordinal);
            var m = Regex.Match(compact, @"第(\d+)页/共(\d+)页");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var current) &&
                int.TryParse(m.Groups[2].Value, out var total))
                return (current, total);
        }
        return null;
    }

    internal static IReadOnlyList<string> PreferVisibleAccessibleTexts(
        IReadOnlyList<string> visibleTexts, IReadOnlyList<string> fallbackTexts) =>
        visibleTexts.Count > 0 ? visibleTexts : fallbackTexts;

    internal static bool HasAccessibleInventoryResponse(IReadOnlyList<string> accessibleTexts) =>
        accessibleTexts.Any(text => text.Contains("药材背包", StringComparison.Ordinal));

    internal static bool HasInventoryPageFor(string text, int expectedPage)
    {
        if (expectedPage <= 0 || !HasInventoryPayload(text)) return false;
        var compact = OcrConsensus.Normalize(text);
        var match = Regex.Match(compact, @"第(\d+)页/共(\d+)页");
        return match.Success &&
               int.TryParse(match.Groups[1].Value, out var current) &&
               current == expectedPage;
    }

    private static void RestoreAndActivate(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SwRestore);
        var currentThread = NativeMethods.GetCurrentThreadId();
        var targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (NativeMethods.SetForegroundWindow(hwnd) &&
                NativeMethods.GetForegroundWindow() == hwnd)
            {
                Thread.Sleep(120);
                return;
            }

            // Windows prevents a background process from stealing focus in
            // many normal desktop situations. Temporarily attach the QQ and
            // foreground input queues so the same activation works after the
            // user or another app has taken focus between paged queries.
            var foreground = NativeMethods.GetForegroundWindow();
            var foregroundThread = foreground == IntPtr.Zero
                ? 0u
                : NativeMethods.GetWindowThreadProcessId(foreground, out _);
            var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread &&
                                     NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
            var attachedTarget = targetThread != 0 && targetThread != currentThread &&
                                 targetThread != foregroundThread &&
                                 NativeMethods.AttachThreadInput(currentThread, targetThread, true);
            try
            {
                NativeMethods.BringWindowToTop(hwnd);
                NativeMethods.SetActiveWindow(hwnd);
                NativeMethods.SetForegroundWindow(hwnd);
                NativeMethods.SetFocus(hwnd);
            }
            finally
            {
                if (attachedTarget) NativeMethods.AttachThreadInput(currentThread, targetThread, false);
                if (attachedForeground) NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }

            if (NativeMethods.GetForegroundWindow() == hwnd)
            {
                Thread.Sleep(120);
                return;
            }
            Thread.Sleep(120);
        }

        throw new InvalidOperationException("无法激活 QQ 窗口");
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

public sealed class MarketListingNotVisibleException : InvalidOperationException
{
    public MarketListingNotVisibleException(string herbName)
        : base($"采集采购码时坊市页面已变化，未重新查询以避免重复采购：{herbName}") => HerbName = herbName;

    public string HerbName { get; }
}
