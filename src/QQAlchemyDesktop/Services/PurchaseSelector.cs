using QQAlchemyDesktop.Domain;

namespace QQAlchemyDesktop.Services;

public static class PurchaseSelector
{
    public static MarketListing? Select(
        IReadOnlyList<MarketListing> listings,
        IReadOnlyList<PurchaseRule> rules,
        IReadOnlyDictionary<string, int> inventory)
    {
        return SelectAll(listings, rules, inventory).FirstOrDefault();
    }

    /// <summary>
    /// Selects every listing that can be purchased from the current page.
    /// Inventory limits are reserved while building the queue so multiple
    /// listings for the same herb cannot overshoot the configured limit.
    /// </summary>
    public static IReadOnlyList<MarketListing> SelectAll(
        IReadOnlyList<MarketListing> listings,
        IReadOnlyList<PurchaseRule> rules,
        IReadOnlyDictionary<string, int> inventory)
    {
        var choices = new List<(MarketListing Listing, PurchaseRule Rule)>();
        foreach (var listing in listings)
        {
            var matching = rules
                .Where(rule => rule.HerbName == listing.HerbName &&
                               listing.PriceWan <= rule.MaxPriceWan)
                .OrderByDescending(rule => rule.RepeatPurchase)
                .ThenBy(rule => rule.Order)
                .FirstOrDefault();
            if (matching is not null) choices.Add((listing, matching));
        }

        var reserved = new Dictionary<string, int>(StringComparer.Ordinal);
        var selected = new List<MarketListing>();
        foreach (var choice in choices
                     .OrderByDescending(x => x.Rule.RepeatPurchase)
                     .ThenByDescending(x => x.Rule.MaxPriceWan - x.Listing.PriceWan)
                     .ThenBy(x => x.Rule.Order)
                     .ThenBy(x => x.Listing.ClickRect.Y)
                     .ThenBy(x => x.Listing.ClickRect.X))
        {
            var current = inventory.GetValueOrDefault(choice.Listing.HerbName);
            var planned = reserved.GetValueOrDefault(choice.Listing.HerbName);
            if (current + planned >= choice.Rule.InventoryLimit) continue;
            reserved[choice.Listing.HerbName] = planned + 1;
            selected.Add(choice.Listing);
        }

        return selected;
    }

    public static bool HasReachedTaskLimit(int purchased, int limit) => purchased >= Math.Max(1, limit);
}
