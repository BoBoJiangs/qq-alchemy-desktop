using System.Text;
using QQAlchemyDesktop.Domain;
using QQAlchemyDesktop.Infrastructure;

namespace QQAlchemyDesktop.Services;

public sealed class RecipeCalculator
{
    private readonly AppPaths _paths;
    private readonly SqliteStore _store;
    private readonly SemaphoreSlim _generationGate = new(1, 1);
    private List<Herb> _herbs = [];
    private List<Dan> _dans = [];
    private HashSet<string> _pingLeadNames = new(StringComparer.Ordinal);
    private Dictionary<string, List<DanProfile>> _profilesByPair = new(StringComparer.Ordinal);
    private IReadOnlyList<Recipe> _catalog = Array.Empty<Recipe>();

    public RecipeCalculator(AppPaths paths, SqliteStore store)
    {
        _paths = paths;
        _store = store;
    }

    public IReadOnlyCollection<string> HerbNames => _herbs.Select(x => x.Name).ToArray();
    public IReadOnlyList<Recipe> Catalog => _catalog;

    public async Task<IReadOnlyList<HerbCatalogItem>> GetHerbCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        await _generationGate.WaitAsync(cancellationToken);
        try
        {
            await LoadDataAsync(cancellationToken);
            return _herbs
                .Select(herb => new HerbCatalogItem(herb.Name, herb.Price, GetHerbGrade(herb)))
                .OrderBy(item => item.Grade)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            _generationGate.Release();
        }
    }

    internal static int GetHerbGrade(Herb herb)
    {
        var value = Math.Max(1, herb.MainAttr1Value);
        return Math.Clamp((int)Math.Log2(value) + 1, 1, 9);
    }

    public async Task<IReadOnlyList<Recipe>> GenerateCatalogAsync(CancellationToken cancellationToken = default)
    {
        await _generationGate.WaitAsync(cancellationToken);
        try
        {
            await LoadDataAsync(cancellationToken);
            var settings = await _store.GetSettingAsync<AlchemySettings>("alchemy", cancellationToken)
                           ?? new AlchemySettings();
            var recipes = new Dictionary<string, Recipe>(StringComparer.Ordinal);
            var priced = _herbs.Where(h => h.Price > 0).ToArray();

            await Task.Run(() =>
            {
                foreach (var main in priced)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var lead in priced)
                    {
                        if (!CheckBalance(main, lead) || !IsPingLeadAllowed(lead.LeadAttrType, lead.Name)) continue;
                        foreach (var assist in priced)
                        {
                            if (main.Name == lead.Name || main.Name == assist.Name || lead.Name == assist.Name) continue;
                            foreach (var recipe in FindRecipes(main, lead, assist, settings))
                            {
                                recipes.TryAdd(recipe.CatalogLine, recipe);
                            }
                        }
                    }
                }
            }, cancellationToken);

            _catalog = recipes.Values
                .OrderByDescending(x => x.Profit)
                .ThenBy(x => DanPriority(x.DanName))
                .ThenBy(x => x.CatalogLine, StringComparer.Ordinal)
                .ToArray();

            var catalogPath = Path.Combine(_paths.Data, "炼丹配方.txt");
            var header = settings.Alchemy ? "炼金丹配方" : "坊市丹配方";
            await File.WriteAllLinesAsync(catalogPath,
                new[] { "", header }.Concat(_catalog.Select(x => x.CatalogLine)), Encoding.UTF8, cancellationToken);
            await _store.AuditAsync("info", "recipe_catalog_generated", $"生成配方 {_catalog.Count} 条", cancellationToken: cancellationToken);
            return _catalog;
        }
        finally
        {
            _generationGate.Release();
        }
    }

    public IReadOnlyList<string> BuildAlchemyQueue(IReadOnlyDictionary<string, int> inventory)
    {
        var remaining = new Dictionary<string, int>(inventory, StringComparer.Ordinal);
        var queue = new List<string>();
        foreach (var recipe in _catalog)
        {
            var requirements = recipe.RequiredHerbs();
            while (requirements.All(x => remaining.GetValueOrDefault(x.Key) >= x.Value))
            {
                queue.Add(recipe.Command);
                foreach (var requirement in requirements)
                    remaining[requirement.Key] = remaining.GetValueOrDefault(requirement.Key) - requirement.Value;
            }
        }
        return queue;
    }

    public int NextAssistThreshold(string mainType, int mainValue, string assistType, int assistValue)
    {
        var key = PairKey(mainType, assistType);
        var best = int.MaxValue;
        foreach (var profile in _profilesByPair.GetValueOrDefault(key) ?? [])
        {
            var candidateAssist = profile.GetValue(assistType);
            var candidateMain = profile.GetValue(mainType);
            if (candidateAssist is > 0 && candidateMain >= mainValue && candidateAssist > assistValue)
                best = Math.Min(best, candidateAssist.Value);
        }
        return best;
    }

    public bool IsPingLeadAllowed(string leadAttrType, string leadName) =>
        !leadAttrType.StartsWith("性平", StringComparison.Ordinal) ||
        _pingLeadNames.Count == 0 || _pingLeadNames.Contains(leadName);

    public static double FeeRate(int price) => price switch
    {
        <= 500 => 0.05,
        <= 1000 => 0.10,
        <= 1500 => 0.15,
        <= 2000 => 0.20,
        _ => 0.30
    };

    private async Task LoadDataAsync(CancellationToken cancellationToken)
    {
        var propertyFile = Path.Combine(_paths.Properties, "elixirproperties.txt");
        if (!File.Exists(propertyFile)) throw new FileNotFoundException("请先导入 elixirproperties.txt", propertyFile);

        var herbs = new List<Herb>();
        var dans = new List<Dan>();
        var herbSection = false;
        foreach (var raw in await File.ReadAllLinesAsync(propertyFile, cancellationToken))
        {
            var line = raw.Trim();
            if (line.StartsWith("-----药材列表-----", StringComparison.Ordinal)) { herbSection = true; continue; }
            if (line.StartsWith("-----丹药列表-----", StringComparison.Ordinal)) { herbSection = false; continue; }
            if (line.Length == 0) continue;
            if (herbSection) herbs.Add(Herb.Parse(raw)); else dans.Add(Dan.Parse(raw));
        }

        var herbPrices = ReadPriceMap(Path.Combine(_paths.Data, "account", "药材价格.txt"));
        if (herbPrices.Count == 0) herbPrices = ReadPriceMap(Path.Combine(_paths.Properties, "药材价格.txt"));
        var marketValues = ReadPriceMap(Path.Combine(_paths.Properties, "丹药坊市价值.txt"));
        var alchemyValues = ReadPriceMap(Path.Combine(_paths.Properties, "丹药炼金价值.txt"));

        foreach (var herb in herbs) herb.Price = herbPrices.GetValueOrDefault(herb.Name);
        foreach (var dan in dans)
        {
            dan.MarketValue = marketValues.GetValueOrDefault(dan.Name);
            dan.AlchemyValue = alchemyValues.GetValueOrDefault(dan.Name);
        }

        _herbs = herbs;
        _dans = dans.OrderByDescending(x => x.Priority).ToList();
        _pingLeadNames = File.Exists(Path.Combine(_paths.Properties, "性平.txt"))
            ? (await File.ReadAllLinesAsync(Path.Combine(_paths.Properties, "性平.txt"), cancellationToken))
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        _profilesByPair = BuildProfiles(_dans);
    }

    private IEnumerable<Recipe> FindRecipes(Herb main, Herb lead, Herb assist, AlchemySettings settings)
    {
        foreach (var dan in _dans)
        {
            var mainCount = 0;
            var assistCount = 0;
            string? mainReqType = null;
            string? assistReqType = null;
            var mainNeeded = 0;
            var assistNeeded = 0;
            var valid = true;

            foreach (var requirement in dan.Requirements)
            {
                if (main.MainAttr2Type == requirement.Key)
                {
                    if (main.MainAttr2Value == 0) { valid = false; break; }
                    mainReqType = requirement.Key;
                    mainNeeded = requirement.Value;
                    mainCount = Math.Max(mainCount, DivideRoundUp(requirement.Value, main.MainAttr2Value));
                }
                else if (assist.AssistAttrType == requirement.Key)
                {
                    if (assist.AssistAttrValue == 0 || assist.AssistAttrValue / requirement.Value >= 2)
                    {
                        valid = false;
                        break;
                    }
                    assistReqType = requirement.Key;
                    assistNeeded = requirement.Value;
                    assistCount = Math.Max(assistCount, DivideRoundUp(requirement.Value, assist.AssistAttrValue));
                }
                else
                {
                    valid = false;
                    break;
                }
            }
            if (!valid || mainReqType is null || assistReqType is null) continue;

            int leadCount;
            if (main.MainAttr1Type == "性平")
            {
                leadCount = 1;
            }
            else
            {
                if (lead.LeadAttrValue == 0) continue;
                var leadNumber = main.MainAttr1Value * mainCount;
                if (leadNumber < lead.LeadAttrValue || leadNumber % lead.LeadAttrValue != 0) continue;
                leadCount = leadNumber / lead.LeadAttrValue;
            }

            var assistProvided = assistCount * assist.AssistAttrValue;
            if (assistProvided >= NextAssistThreshold(mainReqType, mainNeeded, assistReqType, assistNeeded)) continue;

            var spend = mainCount * main.Price + leadCount * lead.Price + assistCount * assist.Price;
            var value = settings.Alchemy
                ? dan.AlchemyValue * settings.DanNumber
                : (int)(dan.MarketValue * (1 - FeeRate(dan.MarketValue)) * settings.DanNumber);
            if (settings.Alchemy && spend > value - settings.AlchemyNumber) continue;
            if (!settings.Alchemy && spend > value - settings.MakeNumber) continue;
            if (mainCount + leadCount + assistCount >= 100) continue;

            yield return new Recipe(main, lead, assist, mainCount, leadCount, assistCount,
                spend, value - spend, dan.Name, settings.DanNumber, settings.Alchemy);
        }
    }

    private int DanPriority(string name) => -(_dans.FirstOrDefault(x => x.Name == name)?.Priority ?? 0);
    private static int DivideRoundUp(int value, int divisor) => (int)Math.Ceiling((double)value / divisor);

    private static bool CheckBalance(Herb main, Herb lead) =>
        main.MainAttr1Type.StartsWith("性寒", StringComparison.Ordinal) && lead.LeadAttrType.StartsWith("性热", StringComparison.Ordinal) ||
        main.MainAttr1Type.StartsWith("性热", StringComparison.Ordinal) && lead.LeadAttrType.StartsWith("性寒", StringComparison.Ordinal) ||
        main.MainAttr1Type.StartsWith("性平", StringComparison.Ordinal) && lead.LeadAttrType.StartsWith("性平", StringComparison.Ordinal);

    private static Dictionary<string, int> ReadPriceMap(string path)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var value)) result[parts[1].Trim()] = value;
        }
        return result;
    }

    private static string PairKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";

    private static Dictionary<string, List<DanProfile>> BuildProfiles(IEnumerable<Dan> dans)
    {
        var result = new Dictionary<string, List<DanProfile>>(StringComparer.Ordinal);
        foreach (var dan in dans.Where(x => x.Requirements.Count == 2))
        {
            var requirements = dan.Requirements.ToArray();
            var profile = new DanProfile(requirements[0].Key, requirements[0].Value,
                requirements[1].Key, requirements[1].Value);
            var key = PairKey(profile.Type1, profile.Type2);
            if (!result.TryGetValue(key, out var list)) result[key] = list = [];
            list.Add(profile);
        }
        return result;
    }

    private sealed record DanProfile(string Type1, int Value1, string Type2, int Value2)
    {
        public int? GetValue(string type) => type == Type1 ? Value1 : type == Type2 ? Value2 : null;
    }
}
