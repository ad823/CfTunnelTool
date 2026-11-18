using System.Text.Json.Nodes;

namespace CfTunnelTool.Core;

enum BrowserCheckSync { Unchanged, Created, Updated, Deleted }

/// <summary>
/// Configuration Rules（網域層級，phase http_config_settings）：
/// 替勾選「API 用途」的網址關閉 Browser Integrity Check，避免 Python、curl 等程式被擋（403 Error 1010）。
/// 每個 Tunnel 只管理一條自己的規則（以 ref 辨識），不會動到使用者在主控台手動加的其他規則。
/// API Token 需要 Zone → Config Rules → Edit 權限。
/// </summary>
sealed partial class CloudflareClient
{
    const string ConfigPhase = "http_config_settings";

    /// <summary>這個 Tunnel 的規則 ref，例如 cftunneltool_bic_office_1。</summary>
    internal static string BrowserCheckRuleRef(string tunnelName) =>
        "cftunneltool_bic_" + tunnelName.ToLowerInvariant().Replace('-', '_');

    /// <summary>規則條件：網址等於其中一個 API 網址。網址已排序，內容相同時字串也相同。</summary>
    internal static string BrowserCheckExpression(IEnumerable<string> hostnames) =>
        "(http.host in {" + string.Join(" ", hostnames.Order(StringComparer.Ordinal).Select(h => $"\"{h}\"")) + "})";

    internal static JsonObject BrowserCheckRule(string tunnelName, IEnumerable<string> hostnames) => new()
    {
        ["ref"] = BrowserCheckRuleRef(tunnelName),
        ["description"] = $"{ManagedComment} ({tunnelName}): disable Browser Integrity Check for API hostnames",
        ["expression"] = BrowserCheckExpression(hostnames),
        ["action"] = "set_config",
        ["action_parameters"] = new JsonObject { ["bic"] = false },
        ["enabled"] = true,
    };

    /// <summary>
    /// 讓這個 Tunnel 的規則剛好涵蓋 <paramref name="hostnames"/>；清單為空時刪除規則。
    /// 只新增、修改或刪除自己那一條，不會整批覆寫 entrypoint。
    /// </summary>
    public async Task<BrowserCheckSync> SyncBrowserCheckRuleAsync(string zoneId, string tunnelName, IReadOnlyCollection<string> hostnames)
    {
        string? rulesetId = null;
        JsonArray rules = [];
        try
        {
            var entry = await SendAsync(HttpMethod.Get, $"zones/{zoneId}/rulesets/phases/{ConfigPhase}/entrypoint");
            rulesetId = entry?["id"]?.GetValue<string>();
            rules = entry?["rules"] as JsonArray ?? [];
        }
        catch (CloudflareApiException ex) when (ex.IsNotFound)
        {
            // 網域還沒有任何 Configuration Rule
        }

        var refName = BrowserCheckRuleRef(tunnelName);
        var existing = rules.FirstOrDefault(r => r?["ref"]?.ToString() == refName);
        var existingId = existing?["id"]?.ToString();

        if (hostnames.Count == 0)
        {
            if (existingId is null || rulesetId is null) return BrowserCheckSync.Unchanged;
            await SendAsync(HttpMethod.Delete, $"zones/{zoneId}/rulesets/{rulesetId}/rules/{existingId}");
            return BrowserCheckSync.Deleted;
        }

        var rule = BrowserCheckRule(tunnelName, hostnames);
        if (rulesetId is null)
        {
            await SendAsync(HttpMethod.Put, $"zones/{zoneId}/rulesets/phases/{ConfigPhase}/entrypoint",
                new JsonObject { ["rules"] = new JsonArray(rule) });
            return BrowserCheckSync.Created;
        }
        if (existingId is null)
        {
            await SendAsync(HttpMethod.Post, $"zones/{zoneId}/rulesets/{rulesetId}/rules", rule);
            return BrowserCheckSync.Created;
        }
        if (existing!["expression"]?.ToString() == rule["expression"]!.ToString() &&
            existing["action"]?.ToString() == "set_config" &&
            existing["action_parameters"]?["bic"]?.GetValue<bool>() == false &&
            existing["enabled"]?.GetValue<bool>() != false)
            return BrowserCheckSync.Unchanged;

        await SendAsync(HttpMethod.Patch, $"zones/{zoneId}/rulesets/{rulesetId}/rules/{existingId}", rule);
        return BrowserCheckSync.Updated;
    }
}
