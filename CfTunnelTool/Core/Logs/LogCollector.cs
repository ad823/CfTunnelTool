namespace CfTunnelTool.Core.Logs;

/// <summary>
/// 依這台電腦的角色收集紀錄：
///   - 一律收集本機遠端桌面使用紀錄（Windows 事件）
///   - 有 CfTunnelTool 部署設定、且有 Token 時，再收集 Cloudflare 登入紀錄與網路傳輸統計
/// 排程（--collect-logs）與畫面上的「立即收集」都呼叫這裡。
/// </summary>
static class LogCollector
{
    public static async Task<string> RunAsync(string? token, ILog? log = null)
    {
        var now = DateTime.Now;
        var state = LogState.Load();
        var parts = new List<string>();

        // 1. 本機遠端桌面使用（Windows 事件）
        try
        {
            var rdp = await Task.Run(() => RdpEventCollector.Collect(state));
            parts.Add($"遠端桌面事件 {rdp} 筆");
            log?.Ok($"遠端桌面使用：新增 {rdp} 筆事件");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            parts.Add("遠端桌面事件讀取失敗");
            log?.Warn("讀取遠端桌面事件失敗：" + ex.Message);
        }

        // 2. Cloudflare 登入與網路傳輸（需要部署設定與 Token）
        ToolConfig? config = null;
        try { config = AppStorage.LoadConfig(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or IOException) { }

        if (config is not null && token is not null)
        {
            try
            {
                using var cf = new CloudflareClient(token);
                var zone = await cf.GetZoneAsync(config.Domain);

                try
                {
                    var logins = await AccessLogCollector.CollectAsync(cf, zone.AccountId, state, now);
                    parts.Add($"登入 {logins} 筆");
                    log?.Ok($"登入紀錄：新增 {logins} 筆");
                }
                catch (CloudflareApiException ex)
                {
                    var msg = ex.IsForbidden
                        ? "API Token 缺少「Account → Access: Audit Logs → Read」權限，無法讀取登入紀錄。"
                        : "讀取登入紀錄失敗：" + ex.Message;
                    parts.Add("登入紀錄失敗");
                    log?.Warn(msg);
                }

                if (config.Rdp is { UsesWarp: true })
                {
                    var ips = config.Rdp.Machines.Select(m => m.Ip).ToHashSet();
                    var net = await NetworkLogCollector.CollectAsync(cf, zone.AccountId, state, now, ips);
                    if (state.NetworkUnavailable is { } reason)
                    {
                        parts.Add("網路傳輸無法取得");
                        log?.Warn(reason);
                    }
                    else
                    {
                        parts.Add($"網路傳輸 {net} 筆");
                        log?.Ok($"網路傳輸：新增 {net} 筆統計");
                    }
                }
            }
            catch (Exception ex) when (ex is CloudflareApiException or HttpRequestException or TaskCanceledException)
            {
                parts.Add("Cloudflare 連線失敗");
                log?.Warn("連線 Cloudflare 失敗：" + ex.Message);
            }
        }
        else if (config is not null)
        {
            log?.Info("沒有可用的 API Token，略過 Cloudflare 登入與網路傳輸紀錄。");
        }

        var removed = LogStore.Cleanup(state.RetentionDays, now);
        if (removed > 0) log?.Info($"已刪除超過 {state.RetentionDays} 天的紀錄檔 {removed} 個");

        state.LastCollected = now;
        state.LastResult = string.Join("、", parts);
        state.Save();
        return state.LastResult;
    }
}
