using QQAlchemyDesktop.Domain;

namespace QQAlchemyDesktop.Services;

public static class OcrConsensus
{
    public static bool AreEquivalent(OcrObservation first, OcrObservation second) =>
        AreEquivalent(first.RawText, second.RawText);

    public static bool AreEquivalent(string first, string second) =>
        string.Equals(Normalize(first), Normalize(second), StringComparison.Ordinal);

    public static string Normalize(string value) =>
        string.Concat(value.Where(c => !char.IsWhiteSpace(c)))
            .Replace("：", ":", StringComparison.Ordinal)
            .Trim();

    public static string Compact(string value, int maxLength = 180)
    {
        var compact = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= maxLength ? compact : compact[..maxLength] + "…";
    }
}
