using QQAlchemyDesktop.Domain;
using QQAlchemyDesktop.Infrastructure;
using QQAlchemyDesktop.Services;
using QQAlchemyDesktop.Automation;

namespace QQAlchemyDesktop.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qq-alchemy-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(500, 0.05)]
    [InlineData(501, 0.10)]
    [InlineData(1001, 0.15)]
    [InlineData(1501, 0.20)]
    [InlineData(2001, 0.30)]
    public void FeeRate_UsesLegacyTiers(int value, double expected) =>
        Assert.Equal(expected, RecipeCalculator.FeeRate(value));

    [Fact]
    public void InventoryParser_ParsesInlineAndLegacyMarkdown()
    {
        var resolver = new HerbNameResolver(["冰灵果", "地心火芝"]);
        var entries = InventoryParser.Parse("""
            [冰灵果](mqqapi://test) - 数量：2 炼金 | 坊市数据
            名字：[地心火芝](mqqapi://test) 拥有数量：16 炼金 | 坊市数据
            """, resolver).ToDictionary(x => x.HerbName, x => x.Count);
        Assert.Equal(2, entries["冰灵果"]);
        Assert.Equal(16, entries["地心火芝"]);
    }

    [Fact]
    public void InventoryParser_ParsesUiAVisibleCardLines()
    {
        var resolver = new HerbNameResolver(["冰灵果", "地心火芝"]);
        var entries = InventoryParser.Parse("""
            一心的药材背包
            名字：冰灵果
            拥有数量:4---炼金|坊市数据
            名字：地心火芝
            拥有数量:16---炼金|坊市数据
            """, resolver).ToDictionary(x => x.HerbName, x => x.Count);
        Assert.Equal(4, entries["冰灵果"]);
        Assert.Equal(16, entries["地心火芝"]);
    }

    [Fact]
    public void InventoryParser_ParsesMultiplePairsFromOneUiACardTextNode()
    {
        var resolver = new HerbNameResolver(["冰灵果", "地心火芝"]);
        var entries = InventoryParser.Parse(
            "一心的药材背包 名字：冰灵果 拥有数量:4---炼金|坊市数据 名字：地心火芝 拥有数量:16---炼金|坊市数据",
            resolver).ToDictionary(x => x.HerbName, x => x.Count);
        Assert.Equal(4, entries["冰灵果"]);
        Assert.Equal(16, entries["地心火芝"]);
    }

    [Fact]
    public void HerbResolver_OnlyCorrectsUniqueSingleCharacterError()
    {
        var unique = new HerbNameResolver(["冰灵果", "地心火芝"]);
        Assert.Equal("冰灵果", unique.Resolve("冰灵杲"));
        var ambiguous = new HerbNameResolver(["冰灵果", "冰灵草"]);
        Assert.Null(ambiguous.Resolve("冰灵花"));
    }

    [Fact]
    public void MessageClassifier_RecognizesCaptchaFromUiAText()
    {
        Assert.True(MessageClassifier.IsCaptcha("请点击图中第2个表情对应的按钮"));
    }

    [Fact]
    public void CaptchaGuard_RequiresVisibleTextBounds()
    {
        var viewport = new Rectangle(100, 100, 500, 700);
        Assert.True(QqDesktopClient.IsVisibleCaptchaBounds(new Rectangle(120, 400, 180, 24), viewport));
        Assert.False(QqDesktopClient.IsVisibleCaptchaBounds(new Rectangle(120, 900, 180, 24), viewport));
        Assert.False(QqDesktopClient.IsVisibleCaptchaBounds(new Rectangle(120, 400, 10, 6), viewport));
    }

    [Fact]
    public void MentionDeduplication_RecognizesNestedPopupText()
    {
        Assert.True(QqDesktopClient.IsNestedMentionRectangle(
            new Rectangle(20, 890, 260, 42), new Rectangle(80, 900, 36, 20)));
        Assert.False(QqDesktopClient.IsNestedMentionRectangle(
            new Rectangle(20, 890, 260, 42), new Rectangle(320, 900, 36, 20)));
    }

    [Fact]
    public void MarketParser_ConvertsYiToWanAndKeepsClickRect()
    {
        var observation = new OcrObservation("玄冰花 价格 1.2 亿", [
            new OcrWordData("玄冰花", new PixelRect(10, 20, 80, 24)),
            new OcrWordData("价格", new PixelRect(110, 20, 40, 24)),
            new OcrWordData("1.2亿", new PixelRect(170, 20, 65, 24))
        ], "FRAME", DateTimeOffset.Now);
        var listing = Assert.Single(MarketParser.Parse(observation, 3, new HerbNameResolver(["玄冰花"])));
        Assert.Equal(12_000, listing.PriceWan);
        Assert.Equal(new PixelRect(10, 20, 80, 24), listing.ClickRect);
        Assert.Equal("FRAME", listing.FrameHash);
    }

    [Fact]
    public void ResponseGate_IgnoresOldCardAboveCurrentCommand()
    {
        var observation = new OcrObservation("旧坊市\n查看坊市药材2\n新坊市", [
            new OcrWordData("乌灵参", new PixelRect(10, 10, 60, 20)),
            new OcrWordData("价格80万", new PixelRect(90, 10, 80, 20)),
            new OcrWordData("查看坊市药材2", new PixelRect(10, 80, 150, 20)),
            new OcrWordData("玄冰花", new PixelRect(10, 130, 60, 20)),
            new OcrWordData("价格90万", new PixelRect(90, 130, 80, 20))
        ], "FRAME", DateTimeOffset.Now);
        Assert.True(OcrResponseGate.TryExtractAfterCommand(observation, "查看坊市药材2", out var response));
        Assert.DoesNotContain("乌灵参", response.RawText);
        Assert.Contains("玄冰花", response.RawText);
    }

    [Fact]
    public void PurchaseSelector_PrioritizesRepeatThenFallsBackToNormalRule()
    {
        var listings = new[]
        {
            Listing("血灵芝", 20, "NORMAL"), Listing("乌灵参", 70, "REPEAT")
        };
        var rules = new[]
        {
            new PurchaseRule("血灵芝", 120, 30, 0),
            new PurchaseRule("乌灵参", 80, 30, 1, true),
            new PurchaseRule("乌灵参", 95, 30, 2)
        };
        Assert.Equal("REPEAT", PurchaseSelector.Select(listings, rules, new Dictionary<string, int>())!.ListingToken);

        var tooExpensiveForRepeat = new[] { Listing("乌灵参", 90, "FALLBACK") };
        Assert.Equal("FALLBACK", PurchaseSelector.Select(tooExpensiveForRepeat, rules, new Dictionary<string, int>())!.ListingToken);
    }

    [Fact]
    public void PurchaseSelector_EnforcesInventoryAndTaskCaps()
    {
        var result = PurchaseSelector.Select([Listing("玄冰花", 80, "X")],
            [new PurchaseRule("玄冰花", 100, 30, 0)], new Dictionary<string, int> { ["玄冰花"] = 30 });
        Assert.Null(result);
        Assert.False(PurchaseSelector.HasReachedTaskLimit(49, 50));
        Assert.True(PurchaseSelector.HasReachedTaskLimit(50, 50));
    }

    [Fact]
    public void PurchaseSelector_SelectAllKeepsPriorityAndRespectsPlannedInventory()
    {
        var listings = new[]
        {
            Listing("玄冰花", 70, "CHEAP"),
            Listing("玄冰花", 80, "SECOND"),
            Listing("乌灵参", 60, "OTHER")
        };
        var rules = new[]
        {
            new PurchaseRule("玄冰花", 100, 2, 0),
            new PurchaseRule("乌灵参", 100, 30, 1)
        };

        var selected = PurchaseSelector.SelectAll(listings, rules,
            new Dictionary<string, int> { ["玄冰花"] = 0 });

        Assert.Equal(new[] { "OTHER", "CHEAP", "SECOND" }, selected.Select(x => x.ListingToken));
    }

    [Fact]
    public void PurchaseSelector_SelectAllDoesNotQueueDuplicateBeyondLimit()
    {
        var listings = new[]
        {
            Listing("玄冰花", 70, "CHEAP"),
            Listing("玄冰花", 80, "SECOND")
        };
        var rules = new[] { new PurchaseRule("玄冰花", 100, 1, 0) };

        var selected = PurchaseSelector.SelectAll(listings, rules,
            new Dictionary<string, int> { ["玄冰花"] = 0 });

        Assert.Equal("CHEAP", Assert.Single(selected).ListingToken);
    }

    [Fact]
    public void AutomationCoordinator_UsesShortBaselineAndHonorsOptionalRandomDelay()
    {
        Assert.Equal((700, 700), AutomationCoordinator.GetActionDelayBounds(
            0, AutomationCoordinator.ActionDelayKind.Purchase));
        Assert.Equal((1200, 3200), AutomationCoordinator.GetActionDelayBounds(
            2, AutomationCoordinator.ActionDelayKind.Query));
    }

    [Theory]
    [InlineData(960, 764, true)]
    [InlineData(399, 764, false)]
    [InlineData(960, 299, false)]
    [InlineData(160, 28, false)]
    public void QqWindowLocator_RejectsTinyHelperWindows(int width, int height, bool expected) =>
        Assert.Equal(expected, QqDesktopClient.IsUsableWindowBounds(width, height));

    [Fact]
    public void QqWindowLocator_UsesStableMinimumWindowSizeForActivation()
    {
        Assert.False(QqDesktopClient.IsUsableWindowBounds(399, 300));
        Assert.True(QqDesktopClient.IsUsableWindowBounds(400, 300));
    }

    [Fact]
    public void QqCalibration_FallsBackToLastAccessiblePageMarker()
    {
        var page = QqDesktopClient.ParsePageStateFromAccessibleTexts([
            "旧响应 第1页/共5页",
            "当前响应 药材背包2",
            "当前响应 第2页/共5页"
        ]);
        Assert.Equal((2, 5), page);
        Assert.Null(QqDesktopClient.ParsePageStateFromAccessibleTexts(["没有页脚"]));
        Assert.True(QqDesktopClient.HasAccessibleInventoryResponse(["一心的药材背包", "第2页/共5页"]));
        Assert.False(QqDesktopClient.HasAccessibleInventoryResponse(["坊市数据", "第2页/共5页"]));
    }

    [Fact]
    public async Task RecipeRules_KeepThresholdPingLeadProfitOrderAndCommandFormat()
    {
        var (calculator, store, paths) = await CreateCalculatorAsync();
        var catalog = await calculator.GenerateCatalogAsync();
        Assert.Equal(3072, calculator.NextAssistThreshold("生息", 2816, "炼气", 2816));
        Assert.True(calculator.IsPingLeadAllowed("性平", "平灵草"));
        Assert.False(calculator.IsPingLeadAllowed("性平", "不存在药引"));
        Assert.True(calculator.IsPingLeadAllowed("性寒", "不存在药引"));
        Assert.NotEmpty(catalog);
        Assert.True(catalog.Zip(catalog.Skip(1)).All(pair => pair.First.Profit >= pair.Second.Profit));
        Assert.All(catalog, recipe => Assert.Contains("丹炉寒铁铸心炉", recipe.Command));
        Assert.True(File.Exists(Path.Combine(paths.Data, "炼丹配方.txt")));
    }

    [Fact]
    public void AlchemyQueue_ConsumesSimulatedInventoryWithoutOverdrawing()
    {
        var herbA = Herb("甲草");
        var herbB = Herb("乙草");
        var herbC = Herb("丙草");
        var recipe = new Recipe(herbA, herbB, herbC, 2, 1, 3, 10, 100, "测试丹", 6, true);
        var calculator = new RecipeCalculator(new AppPaths(Path.Combine(_root, "queue")),
            new SqliteStore(new AppPaths(Path.Combine(_root, "queue-db"))));
        typeof(RecipeCalculator).GetField("_catalog", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(calculator, new[] { recipe });
        var queue = calculator.BuildAlchemyQueue(new Dictionary<string, int> { ["甲草"] = 4, ["乙草"] = 2, ["丙草"] = 7 });
        Assert.Equal(2, queue.Count);
    }

    private async Task<(RecipeCalculator Calculator, SqliteStore Store, AppPaths Paths)> CreateCalculatorAsync()
    {
        var paths = new AppPaths(Path.Combine(_root, "recipe"));
        var store = new SqliteStore(paths);
        await store.InitializeAsync();
        await store.SetSettingAsync("alchemy", new AlchemySettings { DanNumber = 6, Alchemy = true, AlchemyNumber = 0 });
        await File.WriteAllTextAsync(Path.Combine(paths.Properties, "elixirproperties.txt"), """
            -----药材列表-----
            赤心草	性热1	生息2816	性平1	养气2
            寒髓花	性寒1	生息2	性寒1	养气2
            平灵草	性平1	生息2	性平1	养气2
            炼气果	性平1	生息2	性平1	炼气2816
            -----丹药列表-----
            初阶丹	生息2816	炼气2816
            高阶丹	生息2816	炼气3072
            """);
        await File.WriteAllTextAsync(Path.Combine(paths.Properties, "性平.txt"), "平灵草\n");
        await File.WriteAllTextAsync(Path.Combine(paths.Properties, "药材价格.txt"), "1 赤心草\n1 寒髓花\n1 平灵草\n1 炼气果\n");
        await File.WriteAllTextAsync(Path.Combine(paths.Properties, "丹药炼金价值.txt"), "100 初阶丹\n200 高阶丹\n");
        await File.WriteAllTextAsync(Path.Combine(paths.Properties, "丹药坊市价值.txt"), "100 初阶丹\n200 高阶丹\n");
        return (new RecipeCalculator(paths, store), store, paths);
    }

    private static MarketListing Listing(string name, double price, string token) =>
        new(name, price, 1, new PixelRect(0, 0, 10, 10), "FRAME", token, "");

    private static Herb Herb(string name) => new()
    {
        Name = name, MainAttr1Type = "性平", MainAttr1Value = 1,
        MainAttr2Type = "生息", MainAttr2Value = 1, LeadAttrType = "性平", LeadAttrValue = 1,
        AssistAttrType = "养气", AssistAttrValue = 1, Price = 1
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
