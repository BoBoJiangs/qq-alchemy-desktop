using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using QQAlchemyDesktop.Domain;

namespace QQAlchemyDesktop.Services;

public sealed class HerbNameResolver
{
    private readonly string[] _names;

    public HerbNameResolver(IEnumerable<string> names) =>
        _names = names.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();

    public string? Resolve(string raw)
    {
        var candidate = CleanName(raw);
        var direct = _names.FirstOrDefault(x => x == candidate);
        if (direct is not null) return direct;
        var contained = _names.Where(x => candidate.Contains(x, StringComparison.Ordinal)).ToArray();
        if (contained.Length == 1) return contained[0];
        if (contained.Length > 1) return null;
        var fuzzy = _names.Where(x => Math.Abs(x.Length - candidate.Length) <= 1 && Levenshtein(x, candidate) <= 1).ToArray();
        return fuzzy.Length == 1 ? fuzzy[0] : null;
    }

    private static string CleanName(string value) => Regex.Replace(value, @"[^\u4e00-\u9fff0-9]", "");

    private static int Levenshtein(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1];
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            previous = current;
        }
        return previous[^1];
    }
}

public static partial class InventoryParser
{
    [GeneratedRegex(@"[【\[]?(?<name>[\u4e00-\u9fff0-9]{2,})[】\]]?(?:\([^)]*\))?\s*[-－—]?\s*(?:数量|拥有数量)\s*[:：]?\s*(?<count>\d+)", RegexOptions.Compiled)]
    private static partial Regex InlinePattern();

    [GeneratedRegex(@"名字\s*[:：]?\s*[【\[]?(?<name>[\u4e00-\u9fff0-9]{2,})[】\]]?(?:\([^)]*\))?", RegexOptions.Compiled)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"(?:拥有数量|数量)\s*[:：]?\s*(?<count>\d+)", RegexOptions.Compiled)]
    private static partial Regex CountPattern();

    public static IReadOnlyList<InventoryEntry> Parse(string text, HerbNameResolver names)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        string? pendingName = null;
        foreach (var rawLine in Normalize(text).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var explicitName = NamePattern().Match(rawLine);
            if (explicitName.Success) pendingName = names.Resolve(explicitName.Groups["name"].Value);
            var inline = InlinePattern().Match(rawLine);
            if (inline.Success && int.TryParse(inline.Groups["count"].Value, out var inlineCount))
            {
                var resolved = pendingName ?? names.Resolve(inline.Groups["name"].Value);
                if (resolved is not null) result[resolved] = inlineCount;
                pendingName = null;
                continue;
            }
            var countMatch = CountPattern().Match(rawLine);
            if (pendingName is not null && countMatch.Success && int.TryParse(countMatch.Groups["count"].Value, out var count))
            {
                result[pendingName] = count;
                pendingName = null;
            }
        }
        return result.Select(x => new InventoryEntry(x.Key, x.Value)).ToArray();
    }

    public static string Normalize(string value) => value
        .Replace("：", ":", StringComparison.Ordinal)
        .Replace("－", "-", StringComparison.Ordinal)
        .Replace("—", "-", StringComparison.Ordinal)
        .Replace("O", "0", StringComparison.Ordinal)
        .Replace("l", "1", StringComparison.Ordinal)
        .Replace("|", "1", StringComparison.Ordinal)
        .Replace("\r", "", StringComparison.Ordinal)
        .Trim();
}

public static partial class MarketParser
{
    [GeneratedRegex(@"(?:价格\s*[:：]?)?\s*(?<price>\d+(?:\.\d+)?)\s*(?<unit>万|亿)", RegexOptions.Compiled)]
    private static partial Regex PricePattern();

    public static IReadOnlyList<MarketListing> Parse(OcrObservation observation, int page, HerbNameResolver resolver)
    {
        var lines = GroupLines(observation.Words);
        var listings = new List<MarketListing>();
        foreach (var line in lines)
        {
            var text = string.Join(" ", line.Select(x => x.Text));
            var priceMatch = PricePattern().Match(text);
            if (!priceMatch.Success || !double.TryParse(priceMatch.Groups["price"].Value,
                    NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var price)) continue;
            if (priceMatch.Groups["unit"].Value == "亿") price *= 10_000d;

            var herbWord = line.Select(word => (Word: word, Name: resolver.Resolve(word.Text)))
                .FirstOrDefault(x => x.Name is not null);
            if (herbWord.Name is null) continue;
            var rect = herbWord.Word.Bounds;
            var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{herbWord.Name}|{price:0.####}|{page}|{rect.X}|{rect.Y}|{rect.Width}|{rect.Height}|{observation.FrameHash}")));
            listings.Add(new MarketListing(herbWord.Name, price, page, rect, observation.FrameHash, token, text));
        }
        return listings;
    }

    private static IReadOnlyList<IReadOnlyList<OcrWordData>> GroupLines(IReadOnlyList<OcrWordData> words)
    {
        var result = new List<List<OcrWordData>>();
        foreach (var word in words.OrderBy(x => x.Bounds.Y).ThenBy(x => x.Bounds.X))
        {
            var line = result.FirstOrDefault(x =>
            {
                var averageY = x.Average(w => w.Bounds.Y + w.Bounds.Height / 2.0);
                var center = word.Bounds.Y + word.Bounds.Height / 2.0;
                return Math.Abs(averageY - center) <= Math.Max(8, word.Bounds.Height * 0.7);
            });
            if (line is null) result.Add([word]); else line.Add(word);
        }
        return result.Select(x => (IReadOnlyList<OcrWordData>)x.OrderBy(w => w.Bounds.X).ToArray()).ToArray();
    }
}

public static class MessageClassifier
{
    public static bool IsCaptcha(string text) => text.Contains("验证码", StringComparison.Ordinal) ||
        text.Contains("请点击", StringComparison.Ordinal) || text.Contains("表情对应", StringComparison.Ordinal);

    public static bool IsInventoryPage(string text) => text.Contains("药材背包", StringComparison.Ordinal) ||
        text.Contains("坊市数据", StringComparison.Ordinal) && text.Contains("数量", StringComparison.Ordinal);

    public static bool HasNextPage(string text) => text.Contains("下一页", StringComparison.Ordinal);

    public static bool IsMarketPage(string text) => text.Contains("不鼓励不保障任何第三方交易行为", StringComparison.Ordinal) ||
        text.Contains("价格", StringComparison.Ordinal) && (text.Contains("万", StringComparison.Ordinal) || text.Contains("亿", StringComparison.Ordinal));

    public static bool IsPurchaseSuccess(string text) => text.Contains("道友成功购买", StringComparison.Ordinal);
    public static bool IsPurchaseNotFound(string text) => text.Contains("未查询到该物品", StringComparison.Ordinal) || text.Contains("未查询", StringComparison.Ordinal);
    public static bool IsPurchaseTerminal(string text) => text.Contains("没钱还来买东西", StringComparison.Ordinal) || text.Contains("今天已经很努力了", StringComparison.Ordinal);
    public static bool IsTemporaryFailure(string text) => text.Contains("卖家正在进行其他操作", StringComparison.Ordinal) ||
        text.Contains("坊市现在太繁忙了", StringComparison.Ordinal) || text.Contains("上一条指令还没执行完", StringComparison.Ordinal);
    public static bool IsAlchemySuccess(string text) => text.Contains("成功炼成丹药", StringComparison.Ordinal);
    public static bool IsAlchemyFatal(string text) => text.Contains("炼丹炉是否在背包中", StringComparison.Ordinal) ||
        text.Contains("药材是否在背包中", StringComparison.Ordinal);
}

public static class OcrResponseGate
{
    public static bool TryExtractAfterCommand(OcrObservation observation, string command, out OcrObservation response)
    {
        var lines = GroupLines(observation.Words);
        var normalizedCommand = Normalize(command);
        var marker = normalizedCommand[..Math.Min(normalizedCommand.Length, 10)];
        var commandLine = lines.LastOrDefault(line => Normalize(string.Concat(line.Select(x => x.Text)))
            .Contains(marker, StringComparison.Ordinal));
        if (commandLine is null)
        {
            response = observation with { RawText = "", Words = Array.Empty<OcrWordData>() };
            return false;
        }

        var floor = commandLine.Max(x => x.Bounds.Y + x.Bounds.Height);
        var responseWords = observation.Words.Where(x => x.Bounds.Y >= floor - 2).ToArray();
        var responseLines = GroupLines(responseWords);
        var raw = string.Join('\n', responseLines.Select(line =>
            string.Join(' ', line.OrderBy(x => x.Bounds.X).Select(x => x.Text))));
        response = observation with { RawText = raw, Words = responseWords };
        return responseWords.Length > 0;
    }

    public static string LatestText(OcrObservation observation, int lineCount = 6)
    {
        var lines = GroupLines(observation.Words);
        return string.Join('\n', lines.TakeLast(Math.Max(1, lineCount)).Select(line =>
            string.Join(' ', line.OrderBy(x => x.Bounds.X).Select(x => x.Text))));
    }

    private static IReadOnlyList<IReadOnlyList<OcrWordData>> GroupLines(IReadOnlyList<OcrWordData> words)
    {
        var result = new List<List<OcrWordData>>();
        foreach (var word in words.OrderBy(x => x.Bounds.Y).ThenBy(x => x.Bounds.X))
        {
            var line = result.FirstOrDefault(x =>
            {
                var averageY = x.Average(w => w.Bounds.Y + w.Bounds.Height / 2.0);
                var center = word.Bounds.Y + word.Bounds.Height / 2.0;
                return Math.Abs(averageY - center) <= Math.Max(8, word.Bounds.Height * 0.7);
            });
            if (line is null) result.Add([word]); else line.Add(word);
        }
        return result.Select(x => (IReadOnlyList<OcrWordData>)x.OrderBy(w => w.Bounds.X).ToArray()).ToArray();
    }

    private static string Normalize(string value) => string.Concat(value.Where(char.IsLetterOrDigit));
}
