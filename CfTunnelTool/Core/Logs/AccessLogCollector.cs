using System.Text.Json.Nodes;

namespace CfTunnelTool.Core.Logs;

/// <summary>收集 Cloudflare Access 登入紀錄（免費方案只保留 24 小時，所以要定期收集）。</summary>
static class AccessLogCollector
{
    const int PerPage = 1000;
    const int MaxPages = 20;

    public static LoginRecord? Parse(JsonNode n)
    {
        var time = n["created_at"]?.GetValue<DateTime>();
        if (time is null) return null;
        return new LoginRecord(
            time.Value.ToLocalTime(),
            n["user_email"]?.ToString() ?? "",
            n["ip_address"]?.ToString() ?? "",
            n["app_domain"]?.ToString() ?? "",
            n["action"]?.ToString() ?? "",
            n["allowed"]?.GetValue<bool>() ?? false,
            n["connection"]?.ToString() ?? "",
            n["ray_id"]?.ToString() ?? "");
    }

    /// <summary>去除已經存過的紀錄（以 ray_id ＋時間判斷）。</summary>
    public static List<LoginRecord> Dedupe(IEnumerable<LoginRecord> incoming, IEnumerable<LoginRecord> existing)
    {
        static string Key(LoginRecord r) => $"{r.RayId}|{r.Time:O}|{r.Email}";
        var seen = existing.Select(Key).ToHashSet();
        return incoming.Where(r => seen.Add(Key(r))).ToList();
    }

    public static async Task<int> CollectAsync(CloudflareClient cf, string accountId, LogState state, DateTime now)
    {
        var since = state.LastAccess ?? now.AddHours(-24);
        if (since < now.AddDays(-7)) since = now.AddDays(-7);

        var records = new List<LoginRecord>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var (items, totalPages) = await cf.GetAccessRequestsPageAsync(accountId, since, now, page, PerPage);
            records.AddRange(items.Select(Parse).Where(r => r is not null).Select(r => r!));
            if (items.Count < PerPage || page >= totalPages) break;
        }

        // 和最近兩天已存的紀錄比對，避免時間邊界重複
        var existing = LogStore.Query<LoginRecord>(LogKinds.Login, since.AddDays(-1), now, r => r.Time);
        var fresh = Dedupe(records, existing);
        if (fresh.Count > 0) LogStore.Append(LogKinds.Login, fresh, r => r.Time);
        if (records.Count > 0) state.LastAccess = records.Max(r => r.Time);
        return fresh.Count;
    }
}
