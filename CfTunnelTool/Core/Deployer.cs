namespace CfTunnelTool.Core;

record DeployResult(string TunnelId, bool Healthy, string Status, int DnsFailures);
record LocalCheck(RouteConfig Route, bool Ok, string Message);

enum DnsState { Ok, Missing, Other }
record RouteStatus(RouteConfig Route, DnsState Dns, string Detail);
record OnlineStatus(TunnelInfo? Tunnel, IReadOnlyList<RouteStatus> Routes, RdpOnlineStatus? Rdp);

/// <summary>
/// 部署流程：Zone → Tunnel → 路由 → DNS → cloudflared 服務。
/// 由 UI 執行緒呼叫；會阻塞的步驟都已丟到背景執行緒。
/// <paramref name="confirm"/> 會在 UI 執行緒上被呼叫，用來詢問使用者。
/// </summary>
sealed class Deployer(CloudflareClient cf, ILog log, Func<string, bool> confirm)
{
    // ================= 部署 =================

    static bool HasRdpResources(RdpConfig rdp) =>
        rdp.AppId is not null || rdp.PolicyId is not null ||
        rdp.LauncherAppId is not null || rdp.LauncherPolicyId is not null ||
        rdp.WarpAppId is not null || rdp.WarpPolicyId is not null || rdp.WarpEnrollAppId is not null ||
        rdp.GatewayRuleId is not null ||
        (rdp.UseOwnVnet && rdp.VnetOwned && rdp.VnetId is not null) ||
        rdp.Machines.Any(m => m.TargetId is not null || m.RouteId is not null);

    /// <summary>讀取上一次部署的設定，用來找出需要清掉的舊資源。</summary>
    static ToolConfig? LoadPrevious()
    {
        try { return AppStorage.LoadConfig(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or IOException)
        {
            return null;
        }
    }

    public async Task<DeployResult> DeployAsync(ToolConfig config)
    {
        config.Validate();
        var previous = LoadPrevious();

        log.Step($"查詢網域 {config.Domain}");
        var zone = await cf.GetZoneAsync(config.Domain);
        log.Ok($"Zone ID {zone.ZoneId}");

        if (config.Routes.Count > 0)
        {
            log.Step("檢查本機服務");
            foreach (var c in await CheckLocalServicesAsync(config.Routes))
            {
                if (c.Ok) log.Ok(c.Message);
                else log.Warn(c.Message + "（Tunnel 仍會建立，服務啟動後即可連線）");
            }
        }

        log.Step($"準備 Tunnel「{config.TunnelName}」");
        var tunnel = await cf.FindTunnelAsync(zone.AccountId, config.TunnelName);
        string tunnelId;
        List<string> previousHostnames = [];
        if (tunnel is null)
        {
            tunnelId = await cf.CreateTunnelAsync(zone.AccountId, config.TunnelName);
            log.Ok($"已建立 Tunnel {tunnelId}");
        }
        else
        {
            if (tunnel.Id != config.TunnelId &&
                !confirm($"Cloudflare 上已經有名為「{config.TunnelName}」的 Tunnel，可能是其他電腦正在使用。\n\n" +
                         "沿用它的話，這台電腦會和那台電腦共用同一個 Tunnel，連線會被分散到兩台電腦。\n\n" +
                         "確定要沿用嗎？（選「否」可回上一步改用其他名稱）"))
                throw new OperationCanceledException("已取消部署。請回到「網域」步驟改用其他 Tunnel 名稱。");

            tunnelId = tunnel.Id;
            previousHostnames = await cf.GetTunnelHostnamesAsync(zone.AccountId, tunnelId);
            log.Ok($"沿用既有 Tunnel {tunnelId}");
        }
        var target = CloudflareClient.TunnelTarget(tunnelId);
        config.TunnelId = tunnelId; // 中途失敗時，重新部署才認得這是自己的 Tunnel

        log.Step("寫入路由設定");
        await cf.PutTunnelConfigAsync(zone.AccountId, tunnelId, config.Routes);
        foreach (var r in config.Routes)
            log.Ok($"{r.Hostname} → {r.Service}");

        var dnsFailures = 0;
        if (config.Routes.Count > 0)
        {
            log.Step("設定 DNS 紀錄");
            foreach (var r in config.Routes)
                if (!await EnsureDnsAsync(zone.ZoneId, r.Hostname, target))
                    dnsFailures++;
        }

        // 已從設定中移除的網址：刪掉指向此 Tunnel 的舊 DNS 紀錄
        var current = config.Routes.Select(r => r.Hostname).ToHashSet();
        foreach (var old in previousHostnames.Where(h => !current.Contains(h)))
            await DeleteTunnelDnsAsync(zone.ZoneId, old, target);

        await SyncBrowserCheckAsync(zone.ZoneId, config, previous);

        // 瀏覽器遠端桌面 + 2FA
        var rdpDeployer = new RdpDeployer(cf, log, confirm);
        if (config.RdpEnabled)
        {
            if (!await rdpDeployer.DeployAsync(config, zone, tunnelId, previous?.Rdp))
                dnsFailures++;
        }
        else if (previous?.Rdp is { } oldRdp && HasRdpResources(oldRdp))
        {
            await rdpDeployer.RemoveAsync(oldRdp, zone);
            if (config.Rdp is { } r)
            {
                r.AppId = r.PolicyId = r.LauncherAppId = r.LauncherPolicyId = null;
                r.WarpAppId = r.WarpPolicyId = r.WarpEnrollAppId = r.GatewayRuleId = null;
                foreach (var m in r.Machines) m.TargetId = m.RouteId = null;
            }
        }

        log.Step("安裝連線服務 cloudflared");
        var exe = await Cloudflared.EnsureInstalledAsync(config.CloudflaredPath, log);
        var tunnelToken = await cf.GetTunnelTokenAsync(zone.AccountId, tunnelId);
        await Task.Run(() => Cloudflared.InstallService(exe, tunnelToken, log));

        log.Step("等待 Tunnel 連線（最多 45 秒）");
        var info = await WaitForHealthyAsync(zone.AccountId, tunnelId, TimeSpan.FromSeconds(45));
        if (info.Status == "healthy")
            log.Ok($"Tunnel 已連線（Cloudflare 機房：{string.Join(", ", info.Connections.Select(c => c.ColoName).Distinct())}）");
        else
            log.Warn($"Tunnel 目前狀態為「{info.Status}」，通常再等幾十秒就會連上。");

        if (dnsFailures > 0)
            log.Warn($"有 {dnsFailures} 個網址的 DNS 沒有設定成功，請看上方訊息處理。");

        return new DeployResult(tunnelId, info.Status == "healthy", info.Status, dnsFailures);
    }

    async Task<bool> EnsureDnsAsync(string zoneId, string hostname, string target)
    {
        var records = await cf.FindDnsRecordsAsync(zoneId, hostname);

        if (records.Count == 0)
        {
            await cf.CreateCnameAsync(zoneId, hostname, target);
            log.Ok($"{hostname}：已新增 DNS 紀錄");
            return true;
        }

        if (records.Count == 1 && records[0].Type == "CNAME")
        {
            var rec = records[0];
            if (rec.Content.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                log.Ok($"{hostname}：DNS 已正確，不需變更");
                return true;
            }

            var isOtherTunnel = rec.Content.EndsWith(".cfargotunnel.com", StringComparison.OrdinalIgnoreCase);
            var question = isOtherTunnel
                ? $"{hostname} 目前指向「另一個 Tunnel」，可能是別台電腦在使用。\n\n要改成指向這台電腦嗎？"
                : $"{hostname} 目前已有 DNS 紀錄（CNAME → {rec.Content}）。\n\n要覆寫成指向這台電腦嗎？";
            if (confirm(question))
            {
                await cf.UpdateCnameAsync(zoneId, rec.Id, hostname, target);
                log.Ok($"{hostname}：已更新 DNS 紀錄");
                return true;
            }
            log.Warn($"{hostname}：保留原本的 DNS 紀錄，此網址不會連到這台電腦");
            return false;
        }

        // A / AAAA / 多筆紀錄等情況不自動處理，避免誤刪正在使用的設定
        var desc = string.Join("、", records.Select(r => $"{r.Type} {r.Content}"));
        log.Error($"{hostname}：已有其他 DNS 紀錄（{desc}）。為了安全不會自動覆寫，" +
                  "請到 Cloudflare 後台 → DNS 刪除這筆紀錄後，再重新部署。");
        return false;
    }

    async Task DeleteTunnelDnsAsync(string zoneId, string hostname, string target)
    {
        foreach (var rec in await cf.FindDnsRecordsAsync(zoneId, hostname))
        {
            if (rec.Type == "CNAME" && rec.Content.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                await cf.DeleteDnsRecordAsync(zoneId, rec.Id);
                log.Ok($"{hostname}：已刪除 DNS 紀錄");
            }
        }
    }

    /// <summary>勾選「API 用途」的網址：關閉 Browser Integrity Check。</summary>
    static List<string> BrowserCheckHosts(ToolConfig config) =>
        config.Routes.Where(r => r.DisableBrowserCheck && !r.IsTcp).Select(r => r.Hostname).Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// 同步這個 Tunnel 的「關閉瀏覽器檢查」規則。這一步失敗不影響網址本身，只顯示警告與手動設定方式。
    /// </summary>
    async Task SyncBrowserCheckAsync(string zoneId, ToolConfig config, ToolConfig? previous)
    {
        var hosts = BrowserCheckHosts(config);
        var hadBefore = previous is not null && BrowserCheckHosts(previous).Count > 0;
        if (hosts.Count == 0 && !hadBefore) return;

        log.Step("設定 API 網址（關閉瀏覽器檢查）");
        try
        {
            // 改了 Tunnel 名稱：舊名稱的規則要清掉
            if (hadBefore && previous!.TunnelName != config.TunnelName)
                await cf.SyncBrowserCheckRuleAsync(zoneId, previous.TunnelName, []);

            var result = await cf.SyncBrowserCheckRuleAsync(zoneId, config.TunnelName, hosts);
            if (result == BrowserCheckSync.Deleted)
                log.Ok("已沒有 API 網址，已移除關閉瀏覽器檢查的規則");
            foreach (var h in hosts)
                log.Ok($"{h}：已關閉瀏覽器檢查，Python、curl 等程式可以直接呼叫");
        }
        catch (CloudflareApiException ex)
        {
            log.Warn(ex.IsForbidden
                ? "API Token 沒有「Zone → Config Rules → Edit」權限，無法替 API 網址關閉瀏覽器檢查。"
                : $"無法設定關閉瀏覽器檢查的規則：{ex.Message}");
            if (hosts.Count > 0)
                log.Warn(BrowserCheckManualHint(hosts));
        }
    }

    internal static string BrowserCheckManualHint(IEnumerable<string> hosts) =>
        "程式呼叫這些網址可能會被 Cloudflare 擋下（403 Error 1010）：" + string.Join("、", hosts) + "。" +
        "請替 Token 加上「Zone → Config Rules → Edit」後重新部署；或到 Cloudflare 主控台 → 網域 → Rules → Configuration Rules " +
        "新增規則，條件為 Hostname 等於上述網址，將 Browser Integrity Check 設為 Off。";

    async Task<TunnelInfo> WaitForHealthyAsync(string accountId, string tunnelId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var info = await cf.GetTunnelAsync(accountId, tunnelId);
            if (info.Status == "healthy" || DateTime.UtcNow >= deadline)
                return info;
            await Task.Delay(3000);
        }
    }

    // ================= 狀態 =================

    public async Task<OnlineStatus> GetOnlineStatusAsync(ToolConfig config)
    {
        var zone = await cf.GetZoneAsync(config.Domain);
        var found = await cf.FindTunnelAsync(zone.AccountId, config.TunnelName);
        if (found is null)
            return new OnlineStatus(null, config.Routes.Select(r => new RouteStatus(r, DnsState.Missing, "Tunnel 不存在")).ToList(), null);

        var tunnel = await cf.GetTunnelAsync(zone.AccountId, found.Id);
        var target = CloudflareClient.TunnelTarget(tunnel.Id);

        RdpOnlineStatus? rdpStatus = null;
        if (config.RdpEnabled)
        {
            try { rdpStatus = await new RdpDeployer(cf, log, confirm).GetStatusAsync(config.Rdp!, zone.AccountId); }
            catch (CloudflareApiException ex) when (ex.IsForbidden)
            {
                log.Warn("API Token 沒有 Zero Trust 權限，無法查詢遠端桌面的線上狀態。");
            }
        }

        var routes = new List<RouteStatus>();
        foreach (var r in config.Routes)
        {
            var recs = await cf.FindDnsRecordsAsync(zone.ZoneId, r.Hostname);
            if (recs.Any(x => x.Type == "CNAME" && x.Content.Equals(target, StringComparison.OrdinalIgnoreCase)))
                routes.Add(new RouteStatus(r, DnsState.Ok, "正常"));
            else if (recs.Count == 0)
                routes.Add(new RouteStatus(r, DnsState.Missing, "沒有 DNS 紀錄"));
            else
                routes.Add(new RouteStatus(r, DnsState.Other, "指向其他地方"));
        }
        return new OnlineStatus(tunnel, routes, rdpStatus);
    }

    public static async Task<List<LocalCheck>> CheckLocalServicesAsync(IEnumerable<RouteConfig> routes)
    {
        using var handler = new HttpClientHandler
        {
            // 只是檢查本機服務有沒有回應，不驗證憑證
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            AllowAutoRedirect = false,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };

        var tasks = routes.Select(async r =>
        {
            if (r.IsTcp)
            {
                var uri = new Uri(r.Service);
                return await RdpHost.TestTcpAsync(uri.Host, uri.Port)
                    ? new LocalCheck(r, true, $"{r.Service} 可以連線")
                    : new LocalCheck(r, false, $"{r.Service} 沒有回應");
            }
            try
            {
                using var resp = await http.GetAsync(r.Service, HttpCompletionOption.ResponseHeadersRead);
                return new LocalCheck(r, true, $"{r.Service} 有回應（HTTP {(int)resp.StatusCode}）");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                return new LocalCheck(r, false, $"{r.Service} 沒有回應");
            }
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    // ================= 移除 =================

    public async Task RemoveAsync(ToolConfig config)
    {
        log.Step("移除 Windows 服務");
        await Task.Run(() => Cloudflared.UninstallService(Cloudflared.Locate(config.CloudflaredPath)));
        log.Ok("服務已移除");

        var zone = await cf.GetZoneAsync(config.Domain);

        // 遠端桌面的資源（應用程式、政策、目標、路由）要在刪 Tunnel 之前移除
        if (config.Rdp is { } rdp && HasRdpResources(rdp))
            await new RdpDeployer(cf, log, confirm).RemoveAsync(rdp, zone);

        var tunnel = await cf.FindTunnelAsync(zone.AccountId, config.TunnelName);

        // 同名 Tunnel 是別台電腦的時候，規則也是它的，不要刪
        if (BrowserCheckHosts(config).Count > 0 && (tunnel is null || config.TunnelId is null || tunnel.Id == config.TunnelId))
        {
            log.Step("移除關閉瀏覽器檢查的規則");
            try
            {
                if (await cf.SyncBrowserCheckRuleAsync(zone.ZoneId, config.TunnelName, []) == BrowserCheckSync.Deleted)
                    log.Ok("規則已移除");
                else
                    log.Ok("找不到規則，不需處理");
            }
            catch (CloudflareApiException ex)
            {
                log.Warn($"無法移除規則（{ex.Message}），請到 Cloudflare 主控台 → Rules → Configuration Rules 手動刪除。");
            }
        }

        if (tunnel is null)
        {
            log.Warn("Cloudflare 上找不到這個 Tunnel，略過 Tunnel 與 DNS 刪除");
            return;
        }
        if (config.TunnelId is not null && tunnel.Id != config.TunnelId)
        {
            log.Warn($"Cloudflare 上同名的 Tunnel（{tunnel.Id}）不是這台電腦建立的，為了安全不會刪除它。");
            return;
        }

        log.Step("刪除 DNS 紀錄（只刪除指向此 Tunnel 的紀錄）");
        var target = CloudflareClient.TunnelTarget(tunnel.Id);
        var hostnames = (await cf.GetTunnelHostnamesAsync(zone.AccountId, tunnel.Id))
            .Concat(config.Routes.Select(r => r.Hostname))
            .Distinct();
        foreach (var h in hostnames)
            await DeleteTunnelDnsAsync(zone.ZoneId, h, target);

        log.Step("刪除 Tunnel");
        try { await cf.CleanupTunnelConnectionsAsync(zone.AccountId, tunnel.Id); }
        catch (CloudflareApiException) { /* 沒有連線時可能回錯誤，忽略 */ }
        await cf.DeleteTunnelAsync(zone.AccountId, tunnel.Id);
        log.Ok($"Tunnel 已刪除");
    }
}
