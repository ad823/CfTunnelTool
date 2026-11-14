using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CfTunnelTool.Core;

sealed class CloudflareApiException(string message) : Exception(message);

sealed record ZoneInfo(string Name, string ZoneId, string AccountId, string Status)
{
    public override string ToString() => Status == "active" ? Name : $"{Name}（狀態：{Status}）";
}

record DnsRecord(string Id, string Type, string Name, string Content);
record TunnelConnection(string ColoName, string OriginIp, string OpenedAt);
record TunnelInfo(string Id, string Name, string Status, IReadOnlyList<TunnelConnection> Connections);

/// <summary>
/// Cloudflare API v4 的最小封裝，只包含本工具用到的端點。
/// API Token 需要的權限：
///   Account → Cloudflare Tunnel → Edit
///   Zone    → DNS → Edit
///   Zone    → Zone → Read
/// </summary>
sealed class CloudflareClient : IDisposable
{
    public const string ManagedComment = "Managed by CfTunnelTool";

    readonly HttpClient _http;

    public CloudflareClient(string apiToken)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.cloudflare.com/client/v4/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());
    }

    public void Dispose() => _http.Dispose();

    async Task<JsonNode> SendRawAsync(HttpMethod method, string path, object? body = null)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
            req.Content = JsonContent.Create(body);

        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();

        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch (JsonException) { throw new CloudflareApiException($"Cloudflare 回應無法解析 (HTTP {(int)resp.StatusCode})。"); }

        if (root?["success"]?.GetValue<bool>() != true)
        {
            var errorNodes = root?["errors"]?.AsArray().ToList() ?? [];
            var errors = errorNodes.Select(e => $"[{e?["code"]}] {e?["message"]}").ToList();
            var codes = errorNodes.Select(e => e?["code"]?.ToString()).ToHashSet();
            var hint = codes.Contains("6003") || codes.Contains("6111")
                ? "（API Token 格式錯誤，請確認複製的 Token 是否完整）"
                : resp.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized
                    ? "（請確認 API Token 是否有效、權限是否足夠）" : "";
            throw new CloudflareApiException(
                $"Cloudflare API 失敗 (HTTP {(int)resp.StatusCode})：{string.Join("; ", errors)}{hint}");
        }
        return root;
    }

    async Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body = null)
        => (await SendRawAsync(method, path, body))["result"];

    static string Q(string s) => Uri.EscapeDataString(s);

    // ---------- Zone ----------

    static ZoneInfo ParseZone(JsonNode z) => new(
        z["name"]!.GetValue<string>(),
        z["id"]!.GetValue<string>(),
        z["account"]!["id"]!.GetValue<string>(),
        z["status"]?.GetValue<string>() ?? "unknown");

    /// <summary>列出 Token 能存取的所有網域。</summary>
    public async Task<List<ZoneInfo>> ListZonesAsync()
    {
        var zones = new List<ZoneInfo>();
        for (var page = 1; ; page++)
        {
            var root = await SendRawAsync(HttpMethod.Get, $"zones?per_page=50&page={page}");
            zones.AddRange(root["result"]!.AsArray().Select(z => ParseZone(z!)));
            var totalPages = root["result_info"]?["total_pages"]?.GetValue<int>() ?? 1;
            if (page >= totalPages) break;
        }
        return zones.OrderBy(z => z.Name).ToList();
    }

    public async Task<ZoneInfo> GetZoneAsync(string domain)
    {
        var result = await SendAsync(HttpMethod.Get, $"zones?name={Q(domain)}");
        var zone = result?.AsArray().FirstOrDefault()
            ?? throw new CloudflareApiException($"找不到網域 {domain}。請確認網域在此 Cloudflare 帳號下，且 Token 有 Zone:Read 權限。");
        return ParseZone(zone);
    }

    /// <summary>確認 Token 有 Tunnel 權限（讀取一筆 Tunnel 清單）。</summary>
    public async Task TestTunnelAccessAsync(string accountId)
        => await SendAsync(HttpMethod.Get, $"accounts/{accountId}/cfd_tunnel?per_page=1");

    /// <summary>確認 Token 有 DNS 權限（讀取一筆 DNS 紀錄）。</summary>
    public async Task TestDnsAccessAsync(string zoneId)
        => await SendAsync(HttpMethod.Get, $"zones/{zoneId}/dns_records?per_page=1");

    // ---------- Tunnel ----------

    static TunnelInfo ParseTunnel(JsonNode t) => new(
        t["id"]!.GetValue<string>(),
        t["name"]!.GetValue<string>(),
        t["status"]?.GetValue<string>() ?? "unknown",
        t["connections"]?.AsArray()
            .Select(c => new TunnelConnection(
                c?["colo_name"]?.GetValue<string>() ?? "?",
                c?["origin_ip"]?.GetValue<string>() ?? "?",
                c?["opened_at"]?.GetValue<string>() ?? "?"))
            .ToList() ?? []);

    public async Task<TunnelInfo?> FindTunnelAsync(string accountId, string name)
    {
        var result = await SendAsync(HttpMethod.Get, $"accounts/{accountId}/cfd_tunnel?name={Q(name)}&is_deleted=false");
        var t = result?.AsArray().FirstOrDefault(x => x?["name"]?.GetValue<string>() == name);
        return t is null ? null : ParseTunnel(t);
    }

    public async Task<TunnelInfo> GetTunnelAsync(string accountId, string tunnelId)
        => ParseTunnel((await SendAsync(HttpMethod.Get, $"accounts/{accountId}/cfd_tunnel/{tunnelId}"))!);

    /// <summary>建立由 Cloudflare 端管理設定（remotely-managed）的 Tunnel。</summary>
    public async Task<string> CreateTunnelAsync(string accountId, string name)
    {
        var result = await SendAsync(HttpMethod.Post, $"accounts/{accountId}/cfd_tunnel",
            new { name, config_src = "cloudflare" });
        return result!["id"]!.GetValue<string>();
    }

    public async Task<string> GetTunnelTokenAsync(string accountId, string tunnelId)
        => (await SendAsync(HttpMethod.Get, $"accounts/{accountId}/cfd_tunnel/{tunnelId}/token"))!.GetValue<string>();

    public async Task PutTunnelConfigAsync(string accountId, string tunnelId, IEnumerable<RouteConfig> routes)
    {
        var ingress = new List<object>();
        foreach (var r in routes)
        {
            if (r.NoTlsVerify)
                ingress.Add(new { hostname = r.Hostname, service = r.Service, originRequest = new { noTLSVerify = true } });
            else
                ingress.Add(new { hostname = r.Hostname, service = r.Service });
        }
        // 最後一條必須是不帶 hostname 的 catch-all 規則
        ingress.Add(new { service = "http_status:404" });

        await SendAsync(HttpMethod.Put, $"accounts/{accountId}/cfd_tunnel/{tunnelId}/configurations",
            new { config = new { ingress } });
    }

    public async Task<List<string>> GetTunnelHostnamesAsync(string accountId, string tunnelId)
    {
        var result = await SendAsync(HttpMethod.Get, $"accounts/{accountId}/cfd_tunnel/{tunnelId}/configurations");
        return result?["config"]?["ingress"]?.AsArray()
            .Select(i => i?["hostname"]?.GetValue<string>())
            .Where(h => !string.IsNullOrEmpty(h))
            .Select(h => h!)
            .ToList() ?? [];
    }

    public async Task CleanupTunnelConnectionsAsync(string accountId, string tunnelId)
        => await SendAsync(HttpMethod.Delete, $"accounts/{accountId}/cfd_tunnel/{tunnelId}/connections");

    public async Task DeleteTunnelAsync(string accountId, string tunnelId)
        => await SendAsync(HttpMethod.Delete, $"accounts/{accountId}/cfd_tunnel/{tunnelId}");

    // ---------- DNS ----------

    public async Task<List<DnsRecord>> FindDnsRecordsAsync(string zoneId, string name)
    {
        var result = await SendAsync(HttpMethod.Get, $"zones/{zoneId}/dns_records?name={Q(name)}");
        return result?.AsArray()
            .Select(r => new DnsRecord(
                r!["id"]!.GetValue<string>(),
                r["type"]!.GetValue<string>(),
                r["name"]!.GetValue<string>(),
                r["content"]?.GetValue<string>() ?? ""))
            .ToList() ?? [];
    }

    public async Task CreateCnameAsync(string zoneId, string name, string target)
        => await SendAsync(HttpMethod.Post, $"zones/{zoneId}/dns_records",
            new { type = "CNAME", name, content = target, proxied = true, ttl = 1, comment = ManagedComment });

    public async Task UpdateCnameAsync(string zoneId, string recordId, string name, string target)
        => await SendAsync(HttpMethod.Put, $"zones/{zoneId}/dns_records/{recordId}",
            new { type = "CNAME", name, content = target, proxied = true, ttl = 1, comment = ManagedComment });

    public async Task DeleteDnsRecordAsync(string zoneId, string recordId)
        => await SendAsync(HttpMethod.Delete, $"zones/{zoneId}/dns_records/{recordId}");

    public static string TunnelTarget(string tunnelId) => $"{tunnelId}.cfargotunnel.com";
}
