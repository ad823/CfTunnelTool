namespace CfTunnelTool.Core;

record RdpMachineStatus(RdpMachine Machine, bool TargetOk, bool RouteOk);
record RdpOnlineStatus(bool AppOk, IReadOnlyList<RdpMachineStatus> Machines);

/// <summary>
/// 瀏覽器 RDP + 2FA 的部署、狀態與移除：
/// Zero Trust 組織（Email 驗證碼、獨立 MFA）→ 私有網路路由 → 基礎架構目標 → DNS → Access 政策 → Access 應用程式。
/// 只刪除設定檔裡記錄、由本工具建立的資源。
/// </summary>
sealed class RdpDeployer(CloudflareClient cf, ILog log, Func<string, bool> confirm)
{
    /// <summary>瀏覽器 RDP 的 DNS 紀錄只需要存在且經過 Cloudflare Proxy，官方文件使用這個保留位址。</summary>
    public const string PlaceholderIp = "240.0.0.0";

    const string PermOrg = "Account → Access: Organizations, Identity Providers, and Groups → Edit";
    const string PermApps = "Account → Access: Apps and Policies → Edit";
    const string PermZeroTrust = "Account → Zero Trust → Edit";

    static async Task<T> WithPermission<T>(string permission, Func<Task<T>> action)
    {
        try { return await action(); }
        catch (CloudflareApiException ex) when (ex.IsForbidden)
        {
            throw new CloudflareApiException(
                $"API Token 缺少「{permission}」權限。請到 Cloudflare → My Profile → API Tokens 編輯這組 Token 加上此權限，再重新部署。\n（{ex.Message}）",
                ex.StatusCode);
        }
    }

    static Task WithPermission(string permission, Func<Task> action)
        => WithPermission(permission, async () => { await action(); return true; });

    /// <summary>
    /// 從上一次的設定帶入資源 ID（使用者在畫面上重新編輯時，ID 可能沒有保留）。
    /// </summary>
    public static void CarryOverIds(RdpConfig current, RdpConfig? previous)
    {
        if (previous is null) return;
        current.AppId ??= previous.AppId;
        current.PolicyId ??= previous.PolicyId;
        current.VnetId ??= previous.VnetId;
        current.AuthDomain ??= previous.AuthDomain;
        foreach (var m in current.Machines)
        {
            var old = previous.Machines.FirstOrDefault(p => p.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase));
            if (old is null) continue;
            m.TargetId ??= old.TargetId;
            if (old.Ip == m.Ip) m.RouteId ??= old.RouteId;
        }
    }

    // ================= 部署 =================

    /// <summary>部署瀏覽器 RDP；回傳 DNS 是否設定成功。</summary>
    public async Task<bool> DeployAsync(ToolConfig config, ZoneInfo zone, string tunnelId, RdpConfig? previous)
    {
        var rdp = config.Rdp!;
        var acc = zone.AccountId;
        CarryOverIds(rdp, previous);

        // ---- 1. Zero Trust 組織、Email 驗證碼登入、獨立 MFA ----
        log.Step("檢查 Zero Trust 設定（登入方式與 2FA）");
        var org = await WithPermission(PermOrg, () => cf.GetAccessOrganizationAsync(acc))
                  ?? throw new InvalidOperationException(
                      "這個 Cloudflare 帳號還沒有啟用 Zero Trust。\n" +
                      "請到 https://one.dash.cloudflare.com/ 依畫面完成啟用（設定 team 名稱、選擇 Free 方案），然後再按一次「重新部署」。");
        rdp.AuthDomain = org.AuthDomain;
        await WithPermission(PermOrg, () => cf.EnsureOneTimePinIdpAsync(acc));
        await WithPermission(PermOrg, () => cf.EnsureOrganizationMfaAsync(acc, org, rdp.MfaMethods));
        log.Ok($"Zero Trust 組織：{org.AuthDomain}（Email 驗證碼登入、2FA 已啟用）");

        // ---- 2. 本機到各台電腦的連線檢查（只提醒） ----
        log.Step("檢查區網電腦的遠端桌面連接埠");
        foreach (var m in rdp.Machines)
        {
            if (await RdpHost.TestTcpAsync(m.Ip, m.Port))
                log.Ok($"{m.Name}（{m.Ip}:{m.Port}）可以連線");
            else
                log.Warn($"{m.Name}（{m.Ip}:{m.Port}）目前連不到，請確認該電腦已開機、已啟用遠端桌面、防火牆允許 {m.Port}");
        }

        // ---- 3. 私有網路路由 ----
        log.Step("設定私有網路路由");
        var vnetId = await WithPermission(PermZeroTrust, () => cf.GetDefaultVirtualNetworkIdAsync(acc));
        rdp.VnetId = vnetId;
        foreach (var m in rdp.Machines)
            await WithPermission(PermZeroTrust, () => EnsureRouteAsync(acc, m, tunnelId, vnetId, config.TunnelName));

        // ---- 4. 基礎架構目標 ----
        log.Step("設定遠端桌面目標");
        foreach (var m in rdp.Machines)
            await WithPermission(PermZeroTrust, () => EnsureTargetAsync(acc, m, vnetId));

        // 已從清單移除的電腦：刪掉它們的目標與路由
        if (previous is not null)
        {
            var currentNames = rdp.Machines.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var old in previous.Machines.Where(o => !currentNames.Contains(o.Name)))
                await RemoveMachineAsync(acc, old);
            // IP 改變的電腦：舊路由刪除
            foreach (var m in rdp.Machines)
            {
                var old = previous.Machines.FirstOrDefault(o => o.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase));
                if (old?.RouteId is { } oldRoute && old.Ip != m.Ip && oldRoute != m.RouteId)
                    await cf.DeleteRouteAsync(acc, oldRoute);
            }
        }

        // ---- 5. DNS ----
        log.Step("設定遠端桌面網址的 DNS");
        var dnsOk = await EnsureRdpDnsAsync(zone.ZoneId, rdp.Hostname);
        if (previous is { Enabled: true } && previous.Hostname != rdp.Hostname)
            await DeleteRdpDnsAsync(zone.ZoneId, previous.Hostname);

        // ---- 6. Access 政策（誰可以登入、2FA、剪貼簿與檔案傳輸） ----
        log.Step("設定登入政策與 2FA");
        var policyName = $"CfTunnelTool RDP - {config.TunnelName}";
        await WithPermission(PermApps, async () =>
        {
            if (rdp.PolicyId is not null && await cf.PolicyExistsAsync(acc, rdp.PolicyId))
                await cf.UpdatePolicyAsync(acc, rdp.PolicyId, policyName, rdp);
            else
                rdp.PolicyId = await cf.CreatePolicyAsync(acc, policyName, rdp);
        });
        log.Ok($"允許登入：{string.Join("、", rdp.AllowedEmails)}");
        log.Ok($"2FA：{string.Join("、", rdp.MfaMethods.Select(MfaMethodName))}，{SessionName(rdp.MfaSessionDuration)}");

        // ---- 7. Access 應用程式（瀏覽器 RDP） ----
        log.Step("設定瀏覽器遠端桌面應用程式");
        var appName = $"遠端桌面 - {config.TunnelName}";
        await WithPermission(PermApps, async () =>
        {
            if (rdp.AppId is not null && await cf.AppExistsAsync(acc, rdp.AppId))
            {
                await cf.UpdateRdpAppAsync(acc, rdp.AppId, appName, rdp, rdp.PolicyId!);
                return;
            }

            var existing = await cf.FindAppByDomainAsync(acc, rdp.Hostname);
            if (existing is { } app)
            {
                if (app.Type != "rdp")
                    throw new InvalidOperationException(
                        $"{rdp.Hostname} 已經被另一個 Access 應用程式（類型：{app.Type}）使用。請換一個遠端桌面網址，或到 Cloudflare 後台刪除那個應用程式。");
                if (!confirm($"{rdp.Hostname} 已經有一個瀏覽器遠端桌面應用程式（可能是其他電腦建立的）。\n\n要改由這台電腦管理並覆寫它的設定嗎？"))
                    throw new OperationCanceledException("已取消部署。請回到「遠端桌面」步驟換一個網址。");
                rdp.AppId = app.Id;
                await cf.UpdateRdpAppAsync(acc, rdp.AppId, appName, rdp, rdp.PolicyId!);
            }
            else
            {
                rdp.AppId = await cf.CreateRdpAppAsync(acc, appName, rdp, rdp.PolicyId!);
            }
        });
        log.Ok("瀏覽器遠端桌面已設定");
        foreach (var m in rdp.Machines)
            log.Info($"{m.Name}：{rdp.UrlFor(m)}");
        if (rdp.MfaEnrollUrl is { } enroll)
            log.Info($"使用者第一次登入前，先到這裡註冊 2FA 裝置：{enroll}");

        return dnsOk;
    }

    async Task EnsureRouteAsync(string acc, RdpMachine m, string tunnelId, string vnetId, string tunnelName)
    {
        var network = $"{m.Ip}/32";
        var comment = $"CfTunnelTool RDP: {m.Name} via {tunnelName}";
        var route = await cf.FindRouteAsync(acc, network, vnetId);
        if (route is null)
        {
            m.RouteId = await cf.CreateRouteAsync(acc, network, tunnelId, vnetId, comment);
            log.Ok($"{m.Name}：已新增路由 {network}");
            return;
        }
        if (route.TunnelId == tunnelId)
        {
            m.RouteId = route.Id;
            log.Ok($"{m.Name}：路由 {network} 已正確");
            return;
        }
        if (!confirm($"{network}（{m.Name}）目前已經透過「另一個 Tunnel」開放，可能是其他電腦在使用。\n\n要改成透過這台電腦中繼嗎？"))
            throw new OperationCanceledException($"已取消部署：{network} 由其他 Tunnel 使用中。");
        await cf.UpdateRouteTunnelAsync(acc, route.Id, network, tunnelId, vnetId, comment);
        m.RouteId = route.Id;
        log.Ok($"{m.Name}：路由 {network} 已改由這台電腦中繼");
    }

    async Task EnsureTargetAsync(string acc, RdpMachine m, string vnetId)
    {
        var target = m.TargetId is not null ? await cf.GetTargetAsync(acc, m.TargetId) : null;
        target ??= await cf.FindTargetByHostnameAsync(acc, m.Name);
        if (target is null)
        {
            m.TargetId = await cf.CreateTargetAsync(acc, m.Name, m.Ip, vnetId);
            log.Ok($"{m.Name}：已建立遠端桌面目標");
        }
        else
        {
            m.TargetId = target.Id;
            await cf.UpdateTargetAsync(acc, target.Id, m.Name, m.Ip, vnetId);
            log.Ok($"{m.Name}：遠端桌面目標已更新（{m.Ip}）");
        }
    }

    async Task<bool> EnsureRdpDnsAsync(string zoneId, string hostname)
    {
        var records = await cf.FindDnsRecordsAsync(zoneId, hostname);
        if (records.Count == 0)
        {
            await cf.CreateProxiedARecordAsync(zoneId, hostname, PlaceholderIp);
            log.Ok($"{hostname}：已新增 DNS 紀錄");
            return true;
        }
        if (records.Count == 1 && records[0].Type == "A" && records[0].Content == PlaceholderIp)
        {
            log.Ok($"{hostname}：DNS 已正確");
            return true;
        }
        if (records.Count == 1 && records[0].Type is "A" or "AAAA" or "CNAME" &&
            confirm($"{hostname} 目前已有 DNS 紀錄（{records[0].Type} {records[0].Content}），可能正在使用中。\n\n要改成遠端桌面專用嗎？"))
        {
            if (records[0].Type == "A")
                await cf.UpdateProxiedARecordAsync(zoneId, records[0].Id, hostname, PlaceholderIp);
            else
            {
                await cf.DeleteDnsRecordAsync(zoneId, records[0].Id);
                await cf.CreateProxiedARecordAsync(zoneId, hostname, PlaceholderIp);
            }
            log.Ok($"{hostname}：已改為遠端桌面專用的 DNS 紀錄");
            return true;
        }
        log.Error($"{hostname}：已有其他 DNS 紀錄（{string.Join("、", records.Select(r => $"{r.Type} {r.Content}"))}），沒有修改。請換一個遠端桌面網址。");
        return false;
    }

    async Task DeleteRdpDnsAsync(string zoneId, string hostname)
    {
        foreach (var rec in await cf.FindDnsRecordsAsync(zoneId, hostname))
        {
            if (rec.Type != "A" || rec.Content != PlaceholderIp) continue;
            if (await cf.GetDnsRecordCommentAsync(zoneId, rec.Id) != CloudflareClient.ManagedComment) continue;
            await cf.DeleteDnsRecordAsync(zoneId, rec.Id);
            log.Ok($"{hostname}：已刪除遠端桌面 DNS 紀錄");
        }
    }

    async Task RemoveMachineAsync(string acc, RdpMachine m)
    {
        if (m.TargetId is not null && await cf.DeleteTargetAsync(acc, m.TargetId))
            log.Ok($"{m.Name}：已刪除遠端桌面目標");
        if (m.RouteId is not null && await cf.DeleteRouteAsync(acc, m.RouteId))
            log.Ok($"{m.Name}：已刪除路由 {m.Ip}/32");
        m.TargetId = m.RouteId = null;
    }

    // ================= 移除 =================

    /// <summary>移除這台電腦建立的所有 RDP 資源（應用程式 → 政策 → 目標 → 路由 → DNS）。</summary>
    public async Task RemoveAsync(RdpConfig rdp, ZoneInfo zone)
    {
        var acc = zone.AccountId;
        log.Step("移除瀏覽器遠端桌面設定");
        if (rdp.AppId is not null && await cf.DeleteAppAsync(acc, rdp.AppId))
            log.Ok("已刪除遠端桌面應用程式");
        if (rdp.PolicyId is not null && await cf.DeletePolicyAsync(acc, rdp.PolicyId))
            log.Ok("已刪除登入政策");
        foreach (var m in rdp.Machines)
            await RemoveMachineAsync(acc, m);
        if (!string.IsNullOrEmpty(rdp.Hostname))
            await DeleteRdpDnsAsync(zone.ZoneId, rdp.Hostname);
        rdp.AppId = rdp.PolicyId = null;
    }

    // ================= 狀態 =================

    public async Task<RdpOnlineStatus> GetStatusAsync(RdpConfig rdp, string accountId)
    {
        var appOk = rdp.AppId is not null && await cf.AppExistsAsync(accountId, rdp.AppId);
        var routes = new List<RdpMachineStatus>();
        foreach (var m in rdp.Machines)
        {
            var targetOk = m.TargetId is not null && await cf.GetTargetAsync(accountId, m.TargetId) is not null;
            var routeOk = rdp.VnetId is not null && await cf.FindRouteAsync(accountId, $"{m.Ip}/32", rdp.VnetId) is not null;
            routes.Add(new RdpMachineStatus(m, targetOk, routeOk));
        }
        return new RdpOnlineStatus(appOk, routes);
    }

    // ================= 顯示用 =================

    public static string MfaMethodName(string method) => method switch
    {
        "totp" => "Authenticator App",
        "security_key" => "安全金鑰",
        "biometrics" => "生物辨識",
        _ => method,
    };

    public static string SessionName(string duration) => duration switch
    {
        "0m" => "每次連線都要驗證",
        "1h" => "每 1 小時重新驗證",
        "8h" => "每 8 小時重新驗證",
        "24h" => "每 24 小時重新驗證",
        _ => duration,
    };
}
