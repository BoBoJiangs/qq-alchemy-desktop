using System.Text.RegularExpressions;

namespace QQAlchemyDesktop.Domain;

public sealed class Herb
{
    private static readonly Regex Digits = new(@"\d", RegexOptions.Compiled);
    private static readonly Regex NonDigits = new(@"\D", RegexOptions.Compiled);

    public required string Name { get; init; }
    public required string MainAttr1Type { get; init; }
    public int MainAttr1Value { get; init; }
    public required string MainAttr2Type { get; init; }
    public int MainAttr2Value { get; init; }
    public required string LeadAttrType { get; init; }
    public int LeadAttrValue { get; init; }
    public required string AssistAttrType { get; init; }
    public int AssistAttrValue { get; init; }
    public int Price { get; set; }

    public static Herb Parse(string line)
    {
        var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 5) throw new FormatException($"药材属性格式错误: {line}");
        return new Herb
        {
            Name = parts[0],
            MainAttr1Type = Digits.Replace(parts[1], ""),
            MainAttr1Value = int.Parse(NonDigits.Replace(parts[1], "")),
            MainAttr2Type = Digits.Replace(parts[2], ""),
            MainAttr2Value = int.Parse(NonDigits.Replace(parts[2], "")),
            LeadAttrType = Digits.Replace(parts[3], ""),
            LeadAttrValue = int.Parse(NonDigits.Replace(parts[3], "")),
            AssistAttrType = Digits.Replace(parts[4], ""),
            AssistAttrValue = int.Parse(NonDigits.Replace(parts[4], ""))
        };
    }
}

public sealed record HerbCatalogItem(string Name, int Price, int Grade)
{
    public IReadOnlyList<string> Attributes { get; init; } = [];
}

public sealed class Dan
{
    private static readonly Regex Digits = new(@"\d", RegexOptions.Compiled);
    private static readonly Regex NonDigits = new(@"\D", RegexOptions.Compiled);

    public required string Name { get; init; }
    public required IReadOnlyDictionary<string, int> Requirements { get; init; }
    public int Priority => Requirements.Values.Sum();
    public int MarketValue { get; set; }
    public int AlchemyValue { get; set; }

    public static Dan Parse(string line)
    {
        var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) throw new FormatException($"丹药属性格式错误: {line}");
        var requirements = new Dictionary<string, int>();
        foreach (var part in parts.Skip(1))
        {
            requirements[Digits.Replace(part, "")] = int.Parse(NonDigits.Replace(part, ""));
        }
        return new Dan { Name = parts[0], Requirements = requirements };
    }
}

public sealed record Recipe(
    Herb Main,
    Herb Lead,
    Herb Assist,
    int MainCount,
    int LeadCount,
    int AssistCount,
    int Spend,
    int Profit,
    string DanName,
    int DanNumber,
    bool IsAlchemy)
{
    public string CatalogLine =>
        $"主药{Main.Name}-{MainCount}&{Main.Price} 药引{Lead.Name}-{LeadCount}&{Lead.Price} " +
        $"辅药{Assist.Name}-{AssistCount}&{Assist.Price} 花费{Spend} " +
        $"{(IsAlchemy ? "炼金收益" : "坊市收益")}{Profit} {DanNumber}丹 {DanName}";

    public string Command =>
        $"配方主药{Main.Name}{MainCount}药引{Lead.Name}{LeadCount}辅药{Assist.Name}{AssistCount}" +
        $"丹炉寒铁铸心炉 == {DanName}({(IsAlchemy ? "炼金" : "坊市")}收益{Profit})";

    public IReadOnlyDictionary<string, int> RequiredHerbs()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        Add(Main.Name, MainCount);
        Add(Lead.Name, LeadCount);
        Add(Assist.Name, AssistCount);
        return result;

        void Add(string name, int count) => result[name] = result.GetValueOrDefault(name) + Math.Abs(count);
    }
}
