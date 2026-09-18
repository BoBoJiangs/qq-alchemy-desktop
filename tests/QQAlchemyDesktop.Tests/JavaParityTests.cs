using QQAlchemyDesktop.Domain;
using QQAlchemyDesktop.Infrastructure;
using QQAlchemyDesktop.Services;

namespace QQAlchemyDesktop.Tests;

public sealed class JavaParityTests
{
    [Fact]
    public async Task CurrentJavaAccount_ProducesSameNormalizedRecipeSet()
    {
        var source = @"D:\Java\qqbot";
        var account = "3860863656";
        var legacyRecipes = Path.Combine(source, account, "炼丹配方.txt");
        if (!File.Exists(legacyRecipes)) return;

        var root = Path.Combine(Path.GetTempPath(), "qq-alchemy-parity", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            var store = new SqliteStore(paths);
            await store.InitializeAsync();
            var importer = new LegacyImportService(paths, store);
            await importer.ImportAsync(new ImportRequest(source, account));
            var calculator = new RecipeCalculator(paths, store);
            var actual = (await calculator.GenerateCatalogAsync()).Select(x => x.CatalogLine).ToHashSet(StringComparer.Ordinal);
            var expected = (await File.ReadAllLinesAsync(legacyRecipes))
                .Select(x => x.Trim()).Where(x => x.StartsWith("主药", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(expected.Count, actual.Count);
            Assert.Empty(expected.Except(actual));
            Assert.Empty(actual.Except(expected));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
