using System.Text.Json;
using QQAlchemyDesktop.Domain;
using QQAlchemyDesktop.Infrastructure;

namespace QQAlchemyDesktop.Services;

public sealed class LegacyImportService
{
    private static readonly string[] PropertyFiles =
    [
        "elixirproperties.txt", "性平.txt", "丹药坊市价值.txt", "丹药炼金价值.txt", "药材价格.txt", "丹方查询.txt"
    ];

    private static readonly string[] AccountFiles =
    [
        "炼丹配置.txt", "药材价格.txt", "背包药材.txt", "重复采购药材.txt", "炼丹配方.txt"
    ];

    private readonly AppPaths _paths;
    private readonly SqliteStore _store;

    public LegacyImportService(AppPaths paths, SqliteStore store)
    {
        _paths = paths;
        _store = store;
    }

    public async Task<object> ImportAsync(ImportRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.SourceRoot) || string.IsNullOrWhiteSpace(request.AccountId))
            throw new ArgumentException("SourceRoot 和 AccountId 不能为空");

        var sourceRoot = Path.GetFullPath(request.SourceRoot);
        if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException(sourceRoot);
        if (request.AccountId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("账号目录名称无效");

        var sourceProperties = Path.Combine(sourceRoot, "properties");
        var sourceAccount = Path.Combine(sourceRoot, request.AccountId);
        if (!Directory.Exists(sourceProperties)) throw new DirectoryNotFoundException(sourceProperties);
        if (!Directory.Exists(sourceAccount)) throw new DirectoryNotFoundException(sourceAccount);

        var accountTarget = Path.Combine(_paths.Data, "account");
        Directory.CreateDirectory(accountTarget);
        var copied = new List<string>();

        foreach (var name in PropertyFiles)
        {
            var source = Path.Combine(sourceProperties, name);
            if (!File.Exists(source)) continue;
            File.Copy(source, Path.Combine(_paths.Properties, name), true);
            copied.Add($"properties/{name}");
        }
        foreach (var name in AccountFiles)
        {
            var source = Path.Combine(sourceAccount, name);
            if (!File.Exists(source)) continue;
            File.Copy(source, Path.Combine(accountTarget, name), true);
            copied.Add($"account/{name}");
        }

        var settings = await ReadAlchemySettingsAsync(accountTarget, cancellationToken);
        settings.AlchemyQq = long.TryParse(request.AccountId, out var accountId) ? accountId : settings.AlchemyQq;
        await _store.SetSettingAsync("alchemy", settings, cancellationToken);

        var rules = ReadPurchaseRules(accountTarget, settings.LimitHerbsCount);
        await _store.SetSettingAsync("purchaseRules", rules, cancellationToken);
        await _store.SetSettingAsync("legacyImport", new
        {
            sourceRoot, request.AccountId, importedAt = DateTimeOffset.Now, copied
        }, cancellationToken);
        await _store.AuditAsync("info", "legacy_import", $"导入 {copied.Count} 个文件，采购规则 {rules.Count} 条", cancellationToken: cancellationToken);
        return new { copied, purchaseRuleCount = rules.Count, dataRoot = _paths.Data };
    }

    private static async Task<AlchemySettings> ReadAlchemySettingsAsync(string accountTarget, CancellationToken cancellationToken)
    {
        var path = Path.Combine(accountTarget, "炼丹配置.txt");
        if (!File.Exists(path)) return new AlchemySettings();
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<AlchemySettings>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new AlchemySettings();
    }

    private static List<PurchaseRule> ReadPurchaseRules(string accountTarget, int defaultLimit)
    {
        var normal = new Dictionary<string, PurchaseRule>(StringComparer.Ordinal);
        var normalPath = Path.Combine(accountTarget, "药材价格.txt");
        var order = 0;
        if (File.Exists(normalPath))
        {
            foreach (var line in File.ReadLines(normalPath))
            {
                var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !int.TryParse(parts[0], out var price) || price <= 0) continue;
                normal[parts[1].Trim()] = new PurchaseRule(parts[1].Trim(), price, defaultLimit, order++);
            }
        }

        var result = normal.Values.ToList();
        var repeatPath = Path.Combine(accountTarget, "重复采购药材.txt");
        if (!File.Exists(repeatPath)) return result;
        foreach (var line in File.ReadLines(repeatPath))
        {
            var parts = line.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], out var price) || price <= 0) continue;
            var limit = defaultLimit;
            string name;
            if (parts.Length == 3 && int.TryParse(parts[1], out var parsedLimit))
            {
                limit = Math.Max(0, parsedLimit);
                name = parts[2].Trim();
            }
            else
            {
                name = parts[1].Trim();
            }
            result.Add(new PurchaseRule(name, price, limit, order++, true));
        }
        return result;
    }
}
