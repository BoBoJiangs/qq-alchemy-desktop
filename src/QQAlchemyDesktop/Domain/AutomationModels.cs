using System.Text.Json.Serialization;

namespace QQAlchemyDesktop.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AutomationState
{
    Idle,
    Calibrating,
    ReadingInventory,
    ScanningMarket,
    WaitingPurchaseResult,
    PreparingAlchemy,
    WaitingAlchemyResult,
    PausedCaptcha,
    PausedRecovery,
    Completed,
    Faulted
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskKind
{
    None,
    Purchase,
    Alchemy
}

public sealed record NormalizedRect(double X, double Y, double Width, double Height)
{
    public static readonly NormalizedRect Empty = new(0, 0, 0, 0);

    public Rectangle ToPixels(Rectangle bounds)
    {
        return new Rectangle(
            bounds.Left + (int)Math.Round(X * bounds.Width),
            bounds.Top + (int)Math.Round(Y * bounds.Height),
            Math.Max(1, (int)Math.Round(Width * bounds.Width)),
            Math.Max(1, (int)Math.Round(Height * bounds.Height)));
    }
}

public sealed class CalibrationSettings
{
    public string GroupName { get; set; } = "";
    public string GameBotDisplayName { get; set; } = "小小";
    public long GameBotQq { get; set; } = 3889001741L;
    public string QqExecutablePath { get; set; } = "";
    public string QqVersion { get; set; } = "";
    public string WindowTitleHint { get; set; } = "QQ";
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public uint Dpi { get; set; } = 96;
    public NormalizedRect GroupTitleRegion { get; set; } = new(0.22, 0.00, 0.62, 0.10);
    public NormalizedRect ChatRegion { get; set; } = new(0.22, 0.10, 0.77, 0.68);
    public NormalizedRect InputRegion { get; set; } = new(0.22, 0.78, 0.77, 0.21);
    public DateTimeOffset? VerifiedAt { get; set; }
    public bool IsVerified { get; set; }
}

public sealed class AlchemySettings
{
    public int DanNumber { get; set; } = 6;
    public int MakeNumber { get; set; } = 1000;
    public int AlchemyNumber { get; set; } = 30;
    public bool Alchemy { get; set; } = true;
    public string MakeName { get; set; } = "极品创世丹&极品混沌丹&九天蕴仙丹&金仙造化丹&大道归一丹&菩提证道丹&太清玉液丹";
    public long AlchemyQq { get; set; }
    public bool FinishAutoBuyHerb { get; set; }
    public int LimitHerbsCount { get; set; } = 30;
    public int AddPrice { get; set; } = -20;
    public int RandomDelay { get; set; }
    public int TaskPurchaseLimit { get; set; } = 50;
    public int EmptyMarketRoundsBeforeStop { get; set; } = 3;
    public bool DryRun { get; set; } = true;
    public bool AllowUnmentionedCommands { get; set; }
}

public sealed record PurchaseRule(
    string HerbName,
    int MaxPriceWan,
    int InventoryLimit,
    int Order,
    bool RepeatPurchase = false);

public sealed record PixelRect(int X, int Y, int Width, int Height)
{
    public Point Center => new(X + Width / 2, Y + Height / 2);
}

public sealed record MarketListing(
    string HerbName,
    double PriceWan,
    int Page,
    PixelRect ClickRect,
    string FrameHash,
    string ListingToken,
    string RawText);

public sealed record OcrWordData(string Text, PixelRect Bounds, double Confidence = 1d);

public sealed record OcrObservation(
    string RawText,
    IReadOnlyList<OcrWordData> Words,
    string FrameHash,
    DateTimeOffset CapturedAt);

public sealed record InventoryEntry(string HerbName, int Count);

public sealed class AutomationCheckpoint
{
    public AutomationState State { get; set; } = AutomationState.Idle;
    public AutomationState? ResumeState { get; set; }
    public TaskKind Task { get; set; } = TaskKind.None;
    public string Step { get; set; } = "待机";
    public string? LastError { get; set; }
    public string? LastScreenshot { get; set; }
    public string? LastCaptchaScreenshot { get; set; }
    public string? LastOcrText { get; set; }
    public string? PendingActionId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public int CurrentPage { get; set; }
    public int PurchaseCount { get; set; }
    public int EmptyMarketRounds { get; set; }
}

public sealed class AutomationStatus
{
    public AutomationState State { get; init; }
    public TaskKind Task { get; init; }
    public string Step { get; init; } = "";
    public bool QqConnected { get; init; }
    public bool CalibrationValid { get; init; }
    public bool DryRun { get; init; }
    public string? LastError { get; init; }
    public string? LastScreenshot { get; init; }
    public string? LastCaptchaScreenshot { get; init; }
    public string? LastOcrText { get; init; }
    public IReadOnlyDictionary<string, int> Inventory { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<MarketListing> Candidates { get; init; } = Array.Empty<MarketListing>();
    public IReadOnlyList<string> AlchemyQueue { get; init; } = Array.Empty<string>();
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ImportRequest(string SourceRoot, string AccountId);
public sealed record CalibrationRequest(string GroupName, string GameBotDisplayName, long GameBotQq = 3889001741L);
public sealed record ExistingCalibrationResult(bool Verified, bool RequiresPaginationProbe,
    string Message, string RawText, string FrameHash);
public sealed record QqWindowDiagnostics(bool Found, int ProcessId, string ProcessName, string WindowTitle,
    string Version, int WindowWidth, int WindowHeight, uint Dpi, bool Minimized,
    IReadOnlyList<string> AccessibleTexts);
