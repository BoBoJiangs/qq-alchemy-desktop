using QQAlchemyDesktop.Domain;

namespace QQAlchemyDesktop.Services;

public static class PurchaseSelector
{
    public static MarketListing? Select(
        IReadOnlyList<MarketListing> listings,
        IReadOnlyList<PurchaseRule> rules,
        IReadOnlyDictionary<string, int> inventory)
    {
        var choices = new List<(MarketListing Listing, PurchaseRule Rule)>();
        foreach (var listing in listings)
        {
            var matching = rules
                .Where(rule => rule.HerbName == listing.HerbName &&
                               listing.PriceWan <= rule.MaxPriceWan &&
                               inventory.GetValueOrDefault(rule.HerbName) < rule.InventoryLimit)
                .OrderByDescending(rule => rule.RepeatPurchase)
                .ThenBy(rule => rule.Order)
                .FirstOrDefault();
            if (matching is not null) choices.Add((listing, matching));
        }

        return choices
            .OrderByDescending(x => x.Rule.RepeatPurchase)
            .ThenByDescending(x => x.Rule.MaxPriceWan - x.Listing.PriceWan)
            .ThenBy(x => x.Rule.Order)
            .Select(x => x.Listing)
            .FirstOrDefault();
    }

    public static bool HasReachedTaskLimit(int purchased, int limit) => purchased >= Math.Max(1, limit);
}

