using System.Text;
using System.Text.Json.Nodes;

namespace CfTunnelTool.Core.Logs;

/// <summary>
/// 收集 Cloudflare Gateway 網路工作階段統計（WARP 方式的連線）。
/// 官方文件沒有列出這個資料集的欄位，所以先用 GraphQL introspection 找出實際欄位再查詢。
/// 這是 Cloudflare 的抽樣統計，不是每一筆的精確數字，也不含檔案名稱。
/// </summary>
static class NetworkLogCollector
{
    const string DatasetKeyword = "GatewayL4SessionsAdaptiveGroups";
    const string DatasetField = "gatewayL4SessionsAdaptiveGroups";

    static readonly string[] TimeCandidates = ["datetime", "datetimeMinute", "datetimeFiveMinutes", "datetimeHour"];
    static readonly string[] UserCandidates = ["email", "userEmail", "user", "userId"];
    static readonly string[] DestIpCandidates = ["destinationIp", "destinationIP", "destIp", "dstIp", "destinationAddress"];
    static readonly string[] DestPortCandidates = ["destinationPort", "destPort", "dstPort"];
    static readonly string[] SentCandidates = ["bytesSent", "bytesUploaded", "sentBytes", "uploadBytes", "bytesUp"];
    static readonly string[] ReceivedCandidates = ["bytesReceived", "bytesDownloaded", "receivedBytes", "downloadBytes", "bytesDown"];

    static string? Pick(IEnumerable<string> available, string[] candidates)
    {
        var set = available.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hit = candidates.FirstOrDefault(set.Contains);
        return hit is null ? null : available.First(a => a.Equals(hit, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>從 schema 資訊選出要用的欄位（純邏輯，可單獨測試）。找不到資料集或時間欄位時回傳 null。</summary>
    public static NetworkFields? ChooseFields(IEnumerable<string> typeNames, IEnumerable<string> dimensionFields,
                                              IEnumerable<string> sumFields, IEnumerable<string> filterFields, bool hasCount)
    {
        if (!typeNames.Any(t => t.Contains(DatasetKeyword, StringComparison.OrdinalIgnoreCase))) return null;
        var dims = dimensionFields.ToList();
        var filters = filterFields.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var time = TimeCandidates.FirstOrDefault(t => dims.Contains(t, StringComparer.OrdinalIgnoreCase) && filters.Contains(t + "_geq"));
        if (time is null) return null;
        var sums = sumFields.ToList();
        return new NetworkFields(
            DatasetField,
            time,
            Pick(dims, UserCandidates),
            Pick(dims, DestIpCandidates),
            Pick(dims, DestPortCandidates),
            Pick(sums, SentCandidates),
            Pick(sums, ReceivedCandidates),
            hasCount);
    }

    static async Task<List<string>> TypeFieldsAsync(CloudflareClient cf, string typeName, bool input = false)
    {
        var q = input
            ? $"{{ __type(name: \"{typeName}\") {{ inputFields {{ name }} }} }}"
            : $"{{ __type(name: \"{typeName}\") {{ fields {{ name }} }} }}";
        var data = await cf.GraphQLAsync(q);
        var arr = data["__type"]?[input ? "inputFields" : "fields"]?.AsArray();
        return arr?.Select(f => f?["name"]?.ToString()).Where(n => n is not null).Select(n => n!).ToList() ?? [];
    }

    public static async Task<NetworkFields?> DiscoverAsync(CloudflareClient cf)
    {
        var schema = await cf.GraphQLAsync("{ __schema { types { name } } }");
        var names = schema["__schema"]?["types"]?.AsArray()
            .Select(t => t?["name"]?.ToString()).Where(n => n is not null).Select(n => n!).ToList() ?? [];
        var group = names.FirstOrDefault(n => n.EndsWith(DatasetKeyword, StringComparison.OrdinalIgnoreCase));
        if (group is null) return null;

        var groupFields = await TypeFieldsAsync(cf, group);
        var dims = names.Contains(group + "Dimensions") ? await TypeFieldsAsync(cf, group + "Dimensions") : [];
        var sums = names.Contains(group + "Sum") ? await TypeFieldsAsync(cf, group + "Sum") : [];
        var filterType = names.FirstOrDefault(n => n.StartsWith(group + "Filter", StringComparison.OrdinalIgnoreCase));
        var filters = filterType is null ? [] : await TypeFieldsAsync(cf, filterType, input: true);
        return ChooseFields(names, dims, sums, filters, groupFields.Contains("count"));
    }

    public static string BuildQuery(NetworkFields f)
    {
        var dims = new[] { f.TimeField, f.UserField, f.DestIpField, f.DestPortField }.Where(x => x is not null);
        var sums = new[] { f.SentField, f.ReceivedField }.Where(x => x is not null).ToList();
        var sb = new StringBuilder();
        sb.Append("query($acc: String!, $since: Time!, $until: Time!) { viewer { accounts(filter: { accountTag: $acc }) { ");
        sb.Append($"rows: {f.Dataset}(limit: 10000, filter: {{ {f.TimeField}_geq: $since, {f.TimeField}_lt: $until }}, orderBy: [{f.TimeField}_ASC]) {{ ");
        if (f.HasCount) sb.Append("count ");
        sb.Append("dimensions { ").Append(string.Join(' ', dims)).Append(" } ");
        if (sums.Count > 0) sb.Append("sum { ").Append(string.Join(' ', sums)).Append(" } ");
        sb.Append("} } } }");
        return sb.ToString();
    }

    public static List<NetworkRecord> ParseRows(JsonNode data, NetworkFields f, ISet<string>? onlyIps)
    {
        var rows = data["viewer"]?["accounts"]?.AsArray().FirstOrDefault()?["rows"]?.AsArray();
        var list = new List<NetworkRecord>();
        foreach (var r in rows ?? [])
        {
            if (r is null) continue;
            var d = r["dimensions"];
            if (d?[f.TimeField]?.ToString() is not { } timeText || !DateTime.TryParse(timeText, out var time)) continue;
            var ip = f.DestIpField is null ? "" : d[f.DestIpField]?.ToString() ?? "";
            if (onlyIps is { Count: > 0 } && ip.Length > 0 && !onlyIps.Contains(ip)) continue;
            var port = f.DestPortField is null ? "" : d[f.DestPortField]?.ToString() ?? "";
            static long Num(JsonNode? n) => n is null ? 0 : long.TryParse(n.ToString(), out var v) ? v : 0;
            list.Add(new NetworkRecord(
                time.ToLocalTime(),
                f.UserField is null ? "" : d[f.UserField]?.ToString() ?? "",
                port.Length > 0 ? $"{ip}:{port}" : ip,
                f.SentField is null ? 0 : Num(r["sum"]?[f.SentField]),
                f.ReceivedField is null ? 0 : Num(r["sum"]?[f.ReceivedField]),
                f.HasCount ? Num(r["count"]) : 1));
        }
        return list;
    }

    public static async Task<int> CollectAsync(CloudflareClient cf, string accountId, LogState state, DateTime now, ISet<string> machineIps)
    {
        try
        {
            var fields = state.NetworkFieldsCache ?? await DiscoverAsync(cf);
            if (fields is null)
            {
                state.NetworkUnavailable = "這個 Cloudflare 帳號沒有提供網路工作階段統計（可能是方案不支援）。";
                return 0;
            }
            state.NetworkFieldsCache = fields;

            // 統計資料約有幾分鐘延遲，只收到 5 分鐘前
            var until = now.AddMinutes(-5);
            var since = state.LastNetwork ?? until.AddHours(-24);
            if (since < until.AddHours(-24)) since = until.AddHours(-24);
            if (since >= until) return 0;

            var data = await cf.GraphQLAsync(BuildQuery(fields), new
            {
                acc = accountId,
                since = since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                until = until.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            });
            var records = ParseRows(data, fields, machineIps);
            if (records.Count > 0) LogStore.Append(LogKinds.Network, records, r => r.Time);
            state.LastNetwork = until;
            state.NetworkUnavailable = null;
            return records.Count;
        }
        catch (CloudflareApiException ex) when (ex.IsForbidden)
        {
            state.NetworkUnavailable = "API Token 缺少「Account → Account Analytics → Read」權限，無法讀取網路傳輸統計。";
            return 0;
        }
        catch (CloudflareApiException ex)
        {
            state.NetworkFieldsCache = null; // 下次重新查 schema
            state.NetworkUnavailable = "讀取網路傳輸統計失敗：" + ex.Message;
            return 0;
        }
    }
}
