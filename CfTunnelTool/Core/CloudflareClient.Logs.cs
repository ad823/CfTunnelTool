using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CfTunnelTool.Core;

/// <summary>紀錄相關 API：Access 登入紀錄（REST）與 Gateway 統計（GraphQL）。</summary>
sealed partial class CloudflareClient
{
    /// <summary>
    /// Access 登入紀錄的一頁。需要 Token 權限：Account → Access: Audit Logs → Read。
    /// </summary>
    public async Task<(List<JsonNode> Items, int TotalPages)> GetAccessRequestsPageAsync(
        string accountId, DateTime since, DateTime until, int page, int perPage)
    {
        var path = $"accounts/{accountId}/access/logs/access_requests" +
                   $"?direction=asc&since={Q(since.ToUniversalTime().ToString("o"))}&until={Q(until.ToUniversalTime().ToString("o"))}" +
                   $"&page={page}&per_page={perPage}&limit={perPage}";
        var root = await SendRawAsync(HttpMethod.Get, path);
        var items = root["result"]?.AsArray().Where(x => x is not null).Select(x => x!).ToList() ?? [];
        var total = root["result_info"]?["total_pages"]?.GetValue<int>() ?? 1;
        return (items, total);
    }

    /// <summary>
    /// GraphQL Analytics API。需要 Token 權限：Account → Account Analytics → Read。
    /// 回傳 data 節點；有 errors 時拋出 CloudflareApiException。
    /// </summary>
    public async Task<JsonNode> GraphQLAsync(string query, object? variables = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "graphql")
        {
            Content = JsonContent.Create(new { query, variables }),
        };
        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();

        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch (JsonException) { throw new CloudflareApiException($"GraphQL 回應無法解析 (HTTP {(int)resp.StatusCode})。", (int)resp.StatusCode); }

        var errors = root?["errors"]?.AsArray();
        if (errors is { Count: > 0 })
        {
            var messages = errors.Select(e => e?["message"]?.ToString()).Where(m => !string.IsNullOrEmpty(m));
            var statusCode = (int)resp.StatusCode;
            var forbidden = statusCode is 401 or 403 ||
                            messages.Any(m => m!.Contains("not authorized", StringComparison.OrdinalIgnoreCase) ||
                                              m!.Contains("permission", StringComparison.OrdinalIgnoreCase));
            throw new CloudflareApiException("GraphQL 失敗：" + string.Join("; ", messages), forbidden ? 403 : statusCode);
        }
        if (!resp.IsSuccessStatusCode || root?["data"] is null)
            throw new CloudflareApiException($"GraphQL 失敗 (HTTP {(int)resp.StatusCode})", (int)resp.StatusCode);
        return root["data"]!;
    }
}
