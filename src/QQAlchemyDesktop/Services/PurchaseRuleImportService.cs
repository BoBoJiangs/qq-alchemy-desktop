using System.Globalization;
using QQAlchemyDesktop.Domain;

namespace QQAlchemyDesktop.Services;

public sealed record PurchaseRulePrice(string HerbName, int PriceWan);

public sealed record PurchaseRuleImportResult(
    IReadOnlyList<PurchaseRule> Rules,
    int ImportedCount,
    int AddedCount,
    int UpdatedCount,
    IReadOnlyList<int> InvalidLines);

public static class PurchaseRuleImportService
{
    public static PurchaseRuleImportResult Import(
        string content,
        IReadOnlyList<PurchaseRule> existingRules,
        int defaultInventoryLimit)
    {
        var parsed = Parse(content);
        if (parsed.Prices.Count == 0)
            throw new InvalidOperationException("文件中没有找到可识别的价格行");

        var rules = existingRules.Select((rule, index) => rule with { Order = index }).ToList();
        var indexes = rules
            .Select((rule, index) => (rule.HerbName, index))
            .GroupBy(item => item.HerbName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().index, StringComparer.Ordinal);
        var added = 0;
        var updated = 0;

        foreach (var price in parsed.Prices)
        {
            if (indexes.TryGetValue(price.HerbName, out var index))
            {
                rules[index] = rules[index] with { MaxPriceWan = price.PriceWan };
                updated++;
                continue;
            }

            indexes[price.HerbName] = rules.Count;
            rules.Add(new PurchaseRule(price.HerbName, price.PriceWan, Math.Max(0, defaultInventoryLimit), rules.Count));
            added++;
        }

        return new PurchaseRuleImportResult(rules, parsed.Prices.Count, added, updated, parsed.InvalidLines);
    }

    public static (IReadOnlyList<PurchaseRulePrice> Prices, IReadOnlyList<int> InvalidLines) Parse(string content)
    {
        var prices = new Dictionary<string, int>(StringComparer.Ordinal);
        var invalidLines = new List<int>();
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        for (var lineNumber = 1; lineNumber <= lines.Length; lineNumber++)
        {
            var line = lines[lineNumber - 1].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var separator = line.IndexOfAny([' ', '\t']);
            if (separator <= 0)
            {
                invalidLines.Add(lineNumber);
                continue;
            }

            var priceText = line[..separator].Trim();
            var herbName = line[separator..].Trim();
            if (!int.TryParse(priceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var price) ||
                price <= 0 || herbName.Length == 0)
            {
                invalidLines.Add(lineNumber);
                continue;
            }

            prices[herbName] = price;
        }

        return (prices.Select(pair => new PurchaseRulePrice(pair.Key, pair.Value)).ToList(), invalidLines);
    }
}
