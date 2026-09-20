using System.Text.RegularExpressions;

namespace QQAlchemyDesktop.Services;

public static partial class PurchaseCommandValidator
{
    [GeneratedRegex(@"坊市购买\s*(?<code>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})", RegexOptions.Compiled)]
    private static partial Regex CommandPattern();

    public static bool TryValidate(string text, string botDisplayName, out string command)
    {
        var normalized = text.Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        var match = CommandPattern().Match(normalized);
        if (!match.Success || string.IsNullOrWhiteSpace(botDisplayName) ||
            !normalized.Contains(botDisplayName.Trim(), StringComparison.Ordinal))
        {
            command = "";
            return false;
        }

        command = $"坊市购买{match.Groups["code"].Value.ToLowerInvariant()}";
        return true;
    }
}
