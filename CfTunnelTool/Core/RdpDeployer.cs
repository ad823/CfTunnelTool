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
        if (current.LauncherAppId is null)
        {
            current.LauncherAppId = previous.LauncherAppId;
            current.LauncherOwned = previous.LauncherOwned;
        }
        current.LauncherPolicyId ??= previous.LauncherPolicyId;
        current.WarpAppId ??= previous.WarpAppId;
        current.WarpPolicyId ??= previous.WarpPolicyId;
        current.GatewayRuleId ??= previous.GatewayRuleId;
        if (current.WarpEnrollAppId is null)
        {
            current.WarpEnrollAppId = previous.WarpEnrollAppId;
            current.WarpEnrollOwned = previous.WarpEnrollOwned;
        }
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
        log.Ok($"Zero Trust 組織：{org.AuthDomain}，Email 驗證碼登入已啟用");
        await EnsureOrganizationMfaAsync(acc, org, rdp);

        // ---- 2. 本機到各台電腦的連線檢查（只提醒） ----
        log.Step("檢查區網電腦的遠端桌面連接埠");
        foreach (var m in rdp.Machines)
        {
            if (await RdpHost.TestTcpAsync(m.Ip, m.Port))
                log.Ok($"{m.Name}（{m.Ip}:{m.Port}）可以連線");
            else
                log.Warn($"{m.Name}（{m.Ip}:{m.Port}）目前連不到，請確認該電腦已開機、已啟用遠端桌面、防火牆允許 {m.Port}");
        }
        await HardenLocalHostAsync(rdp);

        // ---- 3. 私有網路路由 ----
        log.Step("設定私有網路路由");
        var vnetId = await WithPermission(PermZeroTrust, () => EnsureVirtualNetworkAsync(acc, rdp, config.TunnelName, previous));

        // 虛擬網路變了（例如改成這個地點獨立使用）：舊虛擬網路中的路由要刪掉，再到新的虛擬網路建立
        if (previous?.VnetId is { } oldVnet && oldVnet != vnetId)
        {
            foreach (var old in previous.Machines.Where(o => o.RouteId is not null))
                await cf.DeleteRouteAsync(acc, old.RouteId!);
            foreach (var m in rdp.Machines) m.RouteId = null;
            log.Ok("虛擬網路已變更，已移除舊虛擬網路中的路由");
            if (previous is { UseOwnVnet: true, VnetOwned: true } && await cf.DeleteVirtualNetworkAsync(acc, oldVnet))
                log.Ok("已刪除之前建立的虛擬網路");
        }
        rdp.VnetId = vnetId;

        foreach (var m in rdp.Machines)
            await WithPermission(PermZeroTrust, () => EnsureRouteAsync(acc, m, tunnelId, vnetId, config.TunnelName));

        // ---- 4. 基礎架構目標 ----
        log.Step("設定遠端桌面目標");
        foreach (var m in rdp.Machines)
            await WithPermission(PermZeroTrust, () => EnsureTargetAsync(acc, rdp, m, vnetId));

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

        // ---- 5. App Launcher：使用者註冊 2FA 裝置（與註冊 WARP）的入口 ----
        log.Step("設定 2FA 註冊入口（App Launcher）");
        await WithPermission(PermApps, () => EnsureAppLauncherAsync(acc, rdp, config.TunnelName));

        // ---- 6. 瀏覽器 RDP ----
        var dnsOk = true;
        if (rdp.BrowserEnabled)
            dnsOk = await DeployBrowserAsync(config, zone, previous);
        else
            await RemoveBrowserAsync(rdp, zone);

        // ---- 7. WARP ＋ 遠端桌面連線 ----
        if (rdp.WarpEnabled)
            await DeployWarpAsync(config, acc);
        else
            await RemoveWarpAsync(rdp, acc);

        if (rdp.BrowserEnabled)
            foreach (var m in rdp.Machines)
                log.Info($"{m.Name}（瀏覽器）：{rdp.UrlFor(m)}");
        if (rdp.MfaEnrollUrl is { } enroll)
            log.Info($"使用者第一次登入前，先到這裡註冊 2FA 裝置：{enroll}");
        if (rdp.WarpEnabled)
            log.Info($"WARP 使用者：在自己的電腦執行 CfTunnelTool，選「我要連到公司的電腦」，貼上「複製連線資訊」裡的連線代碼。");

        return dnsOk;
    }

    async Task<bool> DeployBrowserAsync(ToolConfig config, ZoneInfo zone, RdpConfig? previous)
    {
        var rdp = config.Rdp!;
        var acc = zone.AccountId;

        log.Step("設定遠端桌面網址的 DNS");
        var dnsOk = await EnsureRdpDnsAsync(zone.ZoneId, rdp.Hostname);
        if (previous is { Enabled: true, BrowserEnabled: true } && previous.Hostname != rdp.Hostname)
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
        return dnsOk;
    }

    /// <summary>取消勾選瀏覽器 RDP 時，刪除先前建立的應用程式、政策與 DNS。</summary>
    async Task RemoveBrowserAsync(RdpConfig rdp, ZoneInfo zone)
    {
        var acc = zone.AccountId;
        if (rdp.AppId is null && rdp.PolicyId is null) return;
        log.Step("移除瀏覽器遠端桌面（已取消勾選）");
        if (rdp.AppId is not null && await cf.DeleteAppAsync(acc, rdp.AppId)) log.Ok("已刪除瀏覽器遠端桌面應用程式");
        if (rdp.PolicyId is not null && await cf.DeletePolicyAsync(acc, rdp.PolicyId)) log.Ok("已刪除瀏覽器登入政策");
        if (!string.IsNullOrEmpty(rdp.Hostname)) await DeleteRdpDnsAsync(zone.ZoneId, rdp.Hostname);
        rdp.AppId = rdp.PolicyId = null;
    }

    /// <summary>
    /// 中繼電腦本身也在開放清單時：把它的遠端桌面防火牆來源限制為內部網路，
    /// 避免路由器轉送或 UPnP 讓網際網路繞過 Cloudflare 直接連進來；並強制開啟 NLA（只加入 Entra ID 的電腦只寫警告、不修改）。
    /// 其他電腦請在該電腦上用「這台電腦要被遠端連線」設定。
    /// </summary>
    async Task HardenLocalHostAsync(RdpConfig rdp)
    {
        var thisIp = RdpHost.GetLanIPv4();
        if (thisIp is null || rdp.Machines.FirstOrDefault(m => m.Ip == thisIp) is not { } self) return;
        var host = RdpHost.GetStatus();
        if (!host.Enabled) return;

        await RestrictLocalFirewallAsync(self, host);
        try
        {
            switch (await Task.Run(RdpHost.EnsureNla))
            {
                case NlaResult.AlreadyOn: log.Ok($"{self.Name}（這台電腦）的 NLA 已開啟"); break;
                case NlaResult.Enabled: log.Ok($"{self.Name}（這台電腦）已開啟 NLA：帳號密碼驗證通過才建立遠端桌面工作階段"); break;
                case NlaResult.SkippedEntra:
                    log.Warn($"{self.Name}（這台電腦）的 NLA 目前{(host.NlaEffective ? "已開啟" : "未開啟")}，程式沒有修改。" +
                             RdpHost.EntraNlaHelp + "可以在「這台電腦要被遠端連線」畫面的 NLA 那一列手動開啟或關閉。");
                    break;
                default: log.Warn($"{self.Name}（這台電腦）的群組原則強制關閉 NLA，程式不會覆寫。" + RdpHost.NlaPolicyHelp); break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.Warn("無法開啟 NLA：" + ex.Message);
        }
    }

    async Task RestrictLocalFirewallAsync(RdpMachine self, RdpHostStatus host)
    {
        try
        {
            var open = await Task.Run(() => RdpHost.GetOpenFirewallRules(host.Port));
            if (open is null)
            {
                log.Warn("讀不到這台電腦的防火牆規則，請手動確認遠端桌面只允許區網連線");
                return;
            }
            if (open.Count == 0)
            {
                log.Ok($"{self.Name}（這台電腦）的遠端桌面防火牆只允許內部網路連線");
                return;
            }
            var n = await Task.Run(() => RdpHost.RestrictFirewallToInternal(host.Port));
            log.Ok($"{self.Name}（這台電腦）的遠端桌面防火牆已改為只允許內部網路連線（修改 {n} 條規則：" +
                   string.Join("、", open.Select(r => r.DisplayName)) + "）");
        }
        catch (InvalidOperationException ex)
        {
            log.Warn(ex.Message);
        }
    }

    // ================= WARP ＋ 遠端桌面連線 =================

    async Task DeployWarpAsync(ToolConfig config, string acc)
    {
        var rdp = config.Rdp!;

        log.Step("設定 WARP（讓使用者電腦能連到內網）");
        await WithPermission(PermZeroTrust, () => cf.EnableGatewayProxyAsync(acc));
        log.Ok("已開啟 Gateway 代理（TCP／UDP）");

        // WARP 政策（Email ＋ 2FA）：註冊裝置與連線前都使用
        log.Step("設定 WARP 的登入政策與 2FA");
        var policyName = $"CfTunnelTool WARP RDP - {config.TunnelName}";
        var appName = $"遠端桌面 WARP - {config.TunnelName}";
        await WithPermission(PermApps, async () =>
        {
            if (rdp.WarpPolicyId is not null && await cf.PolicyExistsAsync(acc, rdp.WarpPolicyId))
                await cf.UpdateWarpPolicyAsync(acc, rdp.WarpPolicyId, policyName, rdp);
            else
                rdp.WarpPolicyId = await cf.CreateWarpPolicyAsync(acc, policyName, rdp);
        });

        // 誰可以把電腦註冊到組織：同一批 Email，而且要通過 2FA
        await WithPermission(PermApps, () => EnsureWarpEnrollAsync(acc, rdp));

        // Split Tunnel：讓要連的電腦 IP 走 WARP
        await WithPermission(PermZeroTrust, () => EnsureSplitTunnelAsync(acc, rdp));

        // 連線前 2FA：私有網路應用程式
        var warpPolicyId = rdp.WarpPolicyId!;
        await WithPermission(PermApps, async () =>
        {
            if (rdp.WarpAppId is not null && await cf.AppExistsAsync(acc, rdp.WarpAppId))
                await cf.UpdatePrivateRdpAppAsync(acc, rdp.WarpAppId, appName, rdp, warpPolicyId);
            else
                rdp.WarpAppId = await cf.CreatePrivateRdpAppAsync(acc, appName, rdp, warpPolicyId);
        });

        // 只開放遠端桌面連接埠：其他連接埠由 Gateway 封鎖（預設是允許）
        await WithPermission(PermZeroTrust, () => EnsureGatewayRuleAsync(acc, rdp, config.TunnelName));

        log.Ok("WARP 遠端桌面已設定：" + string.Join("、", rdp.Machines.Select(m => $"{m.Name} {m.Ip}:{m.Port}")));
        log.Info($"WARP 組織名稱：{rdp.TeamName}");
    }

    /// <summary>
    /// Gateway 網路規則：被開放的電腦透過 WARP 只能連遠端桌面連接埠。
    /// 每次部署都依目前的電腦清單更新；Cloudflare 不接受時提醒，不中斷部署。
    /// </summary>
    async Task EnsureGatewayRuleAsync(string acc, RdpConfig rdp, string tunnelName)
    {
        var name = $"CfTunnelTool 只開放遠端桌面 - {tunnelName}";
        try
        {
            if (rdp.GatewayRuleId is not null && await cf.GatewayRuleExistsAsync(acc, rdp.GatewayRuleId))
                await cf.UpdateGatewayRuleAsync(acc, rdp.GatewayRuleId, name, rdp);
            else
                rdp.GatewayRuleId = await cf.CreateGatewayRuleAsync(acc, name, rdp);
            log.Ok("Gateway 規則：被開放的電腦只開放遠端桌面連接埠（" +
                   string.Join("、", rdp.Machines.Select(m => $"{m.Ip}:{m.Port}")) +
                   $"）與使用狀態回報（{SessionStatus.Port}），其他連接埠封鎖");
        }
        catch (CloudflareApiException ex) when (!ex.IsForbidden)
        {
            log.Warn("無法建立 Gateway 規則，被開放的電腦上其他連接埠仍可透過 WARP 連線。" +
                     "可以到 Zero Trust → Traffic policies → Firewall policies → Network 手動新增封鎖規則：" +
                     CloudflareClient.GatewayBlockTraffic(rdp));
            log.Info(ex.Message);
        }
    }

    /// <summary>
    /// WARP 裝置註冊權限：使用 WARP 政策（Email ＋ 2FA），註冊裝置也要通過 2FA。
    /// Cloudflare 不接受時退回 App Launcher 政策（只檢查 Email）並提醒。
    /// 舊版會把 App Launcher 政策加到註冊權限，這裡會把它換掉。
    /// </summary>
    async Task EnsureWarpEnrollAsync(string acc, RdpConfig rdp)
    {
        try
        {
            await EnsureWarpEnrollWithPolicyAsync(acc, rdp, rdp.WarpPolicyId!);
        }
        catch (CloudflareApiException ex) when (!ex.IsForbidden)
        {
            log.Warn("無法在 WARP 註冊權限要求 2FA，改為只檢查 Email（連線到電腦時仍要 2FA）。");
            log.Info(ex.Message);
            string? fallbackError = "沒有 App Launcher 政策可以使用";
            if (rdp.LauncherPolicyId is not null)
            {
                try
                {
                    await EnsureWarpEnrollWithPolicyAsync(acc, rdp, rdp.LauncherPolicyId);
                    fallbackError = null;
                }
                catch (CloudflareApiException ex2) when (!ex2.IsForbidden) { fallbackError = ex2.Message; }
            }
            if (fallbackError is not null)
            {
                log.Warn("無法自動設定 WARP 註冊權限，請到 Zero Trust → Team & Resources → Devices → " +
                         "Device enrollment permissions 確認允許這些 Email：" + string.Join("、", rdp.AllowedEmails));
                log.Info(fallbackError);
            }
            return;
        }

        // 舊版加入的 App Launcher 政策（不要求 2FA）：任何一條政策符合就能註冊，有了 2FA 政策後要移除
        if (rdp.WarpEnrollAppId is { } appId && rdp.LauncherPolicyId is { } launcher &&
            (await cf.GetAppPolicyIdsAsync(acc, appId)).Contains(launcher))
        {
            await cf.DetachPolicyAsync(acc, appId, launcher);
            log.Ok("WARP 註冊權限已改為要求 2FA（移除舊版不需 2FA 的政策）");
        }
        else
        {
            log.Ok("WARP 註冊權限：允許的 Email 註冊裝置時也要通過 2FA");
        }

        await WarnOtherEnrollPoliciesAsync(acc, rdp);
    }

    async Task EnsureWarpEnrollWithPolicyAsync(string acc, RdpConfig rdp, string policyId)
    {
        // 已記錄的應用程式（自己建立的，或之前加入的共用應用程式）：確保本地點的政策在裡面，保留其他地點的政策
        if (rdp.WarpEnrollAppId is not null && await cf.AppExistsAsync(acc, rdp.WarpEnrollAppId))
        {
            await cf.AttachPolicyAsync(acc, rdp.WarpEnrollAppId, policyId);
            return;
        }

        var existing = await cf.FindAppIdByTypeAsync(acc, "warp");
        if (existing is null)
        {
            rdp.WarpEnrollAppId = await cf.CreateWarpEnrollAppAsync(acc, policyId);
            rdp.WarpEnrollOwned = true;
            return;
        }

        // 帳號已經有 WARP 註冊設定（其他地點或手動建立的）：把本地點的政策加進去，不影響原本的設定
        await cf.AttachPolicyAsync(acc, existing, policyId);
        rdp.WarpEnrollAppId = existing;
        rdp.WarpEnrollOwned = false;
        log.Ok("已把本地點的政策加入現有的 WARP 註冊權限（與其他地點共用）");
    }

    /// <summary>
    /// 註冊權限裡其他地點（或手動建立）的政策若不要求 2FA，那些 Email 仍可不經 2FA 註冊裝置：只提醒，不修改。
    /// </summary>
    async Task WarnOtherEnrollPoliciesAsync(string acc, RdpConfig rdp)
    {
        if (rdp.WarpEnrollAppId is not { } appId) return;
        List<string> ids;
        try { ids = await cf.GetAppPolicyIdsAsync(acc, appId); }
        catch (CloudflareApiException) { return; }

        var weak = 0;
        foreach (var id in ids.Where(id => id != rdp.WarpPolicyId))
            if (await cf.PolicyRequiresMfaAsync(acc, id) != true) weak++;
        if (weak > 0)
            log.Warn($"WARP 註冊權限裡還有 {weak} 條政策不要求 2FA（可能是其他地點用舊版部署，或手動建立）。" +
                     "請用新版重新部署其他地點，或到 Zero Trust → Team & Resources → Devices → Device enrollment permissions 檢查。");
    }

    /// <summary>
    /// 預設 WARP 設定檔的 Split Tunnel：
    /// Exclude 模式（預設排除 192.168.0.0/16 等私有網段）→ 把包含電腦 IP 的項目拆開，只讓那些 /32 走 WARP；
    /// Include 模式 → 直接加入 /32。
    /// </summary>
    async Task EnsureSplitTunnelAsync(string acc, RdpConfig rdp)
    {
        var (mode, entries) = await cf.GetSplitTunnelAsync(acc);
        var hosts = rdp.Machines.Select(m => (m, cidr: Cidr.TryParse(m.Ip, out var c) ? c : default)).ToList();

        if (mode == "include")
        {
            var changed = false;
            foreach (var (m, cidr) in hosts)
            {
                if (entries.Any(e => e.Address is { } a && Cidr.TryParse(a, out var ec) && ec.Contains(cidr))) continue;
                entries.Add(new SplitTunnelEntry(cidr.ToString(), null, $"CfTunnelTool RDP: {m.Name}"));
                changed = true;
            }
            if (changed) await cf.PutSplitTunnelAsync(acc, "include", entries);
            log.Ok($"Split Tunnel（Include 模式）已包含：{string.Join("、", hosts.Select(h => h.cidr.ToString()))}");
            return;
        }

        var result = new List<SplitTunnelEntry>();
        var split = new List<string>();
        foreach (var e in entries)
        {
            if (e.Address is null || !Cidr.TryParse(e.Address, out var ec))
            {
                result.Add(e);
                continue;
            }
            var pieces = new List<Cidr> { ec };
            foreach (var (_, cidr) in hosts)
                pieces = pieces.SelectMany(p => p.Contains(cidr) ? p.Subtract(cidr) : [p]).ToList();
            if (pieces.Count == 1 && pieces[0] == ec)
            {
                result.Add(e);
                continue;
            }
            split.Add(e.Address);
            var desc = string.IsNullOrEmpty(e.Description) ? "" : e.Description + " ";
            result.AddRange(pieces.Select(p => new SplitTunnelEntry(p.ToString(), null, desc + "(CfTunnelTool split)")));
        }

        if (split.Count > 0)
        {
            await cf.PutSplitTunnelAsync(acc, "exclude", result);
            log.Ok($"Split Tunnel：已讓 {string.Join("、", hosts.Select(h => h.cidr.ToString()))} 走 WARP" +
                   $"（拆開原本排除的 {string.Join("、", split)}）");
        }
        else
        {
            log.Ok("Split Tunnel：電腦 IP 已經會走 WARP，不需變更");
        }
    }

    /// <summary>取消勾選 WARP 時，刪除本工具建立的 WARP 應用程式與政策（Split Tunnel 不還原，對其他使用者無影響）。</summary>
    async Task RemoveWarpAsync(RdpConfig rdp, string acc)
    {
        if (rdp.WarpAppId is null && rdp.WarpPolicyId is null && rdp.WarpEnrollAppId is null && rdp.GatewayRuleId is null) return;
        log.Step("移除 WARP 遠端桌面設定");
        if (rdp.GatewayRuleId is not null && await cf.DeleteGatewayRuleAsync(acc, rdp.GatewayRuleId)) log.Ok("已刪除 Gateway 規則（只開放遠端桌面連接埠）");
        rdp.GatewayRuleId = null;
        if (rdp.WarpAppId is not null && await cf.DeleteAppAsync(acc, rdp.WarpAppId)) log.Ok("已刪除 WARP 遠端桌面應用程式");
        // 註冊權限使用 WARP 政策（舊版是 App Launcher 政策），先從註冊權限移除，政策才刪得掉
        if (rdp.WarpEnrollAppId is not null)
            await ReleaseSharedAppAsync(acc, rdp.WarpEnrollAppId, [rdp.WarpPolicyId, rdp.LauncherPolicyId], rdp.WarpEnrollOwned, "WARP 註冊權限");
        if (rdp.WarpPolicyId is not null && await cf.DeletePolicyAsync(acc, rdp.WarpPolicyId)) log.Ok("已刪除 WARP 登入政策");
        rdp.WarpAppId = rdp.WarpPolicyId = rdp.WarpEnrollAppId = null;
        rdp.WarpEnrollOwned = true;
        log.Info("Split Tunnel 中讓電腦 IP 走 WARP 的設定保留不動（不影響其他使用者）。");
    }

    /// <summary>
    /// 啟用組織層級的獨立 MFA。API 更新失敗時：
    /// 組織已經允許部分方式 → 只用那些方式繼續部署並提醒；完全沒啟用 → 引導到後台手動啟用。
    /// </summary>
    async Task EnsureOrganizationMfaAsync(string acc, AccessOrganization org, RdpConfig rdp)
    {
        try
        {
            try
            {
                await WithPermission(PermOrg, () => cf.EnsureOrganizationMfaAsync(acc, org, rdp.MfaMethods, rdp.LoginSessionDuration));
                log.Ok($"組織 2FA 已啟用：{string.Join("、", rdp.MfaMethods.Select(MfaMethodName))}");
                log.Ok($"Email 驗證碼：{LoginName(rdp.LoginSessionDuration)}輸入一次；2FA：{SessionName(rdp.MfaSessionDuration)}");
                log.Info("（Email 驗證碼的有效時間是 Zero Trust 組織的全域登入時間，同一個 Cloudflare 帳號的所有應用程式共用。）");
            }
            catch (CloudflareApiException ex) when (!ex.IsForbidden && !string.IsNullOrEmpty(rdp.LoginSessionDuration))
            {
                // 全域登入時間設不進去時，至少先把 2FA 設好
                await WithPermission(PermOrg, () => cf.EnsureOrganizationMfaAsync(acc, org, rdp.MfaMethods));
                log.Ok($"組織 2FA 已啟用：{string.Join("、", rdp.MfaMethods.Select(MfaMethodName))}");
                log.Warn("無法自動設定 Email 驗證碼的有效時間（全域登入時間），請到 Zero Trust 後台 → Access settings → " +
                         $"Global session timeout 設為 {LoginName(rdp.LoginSessionDuration)}。");
                log.Info(ex.Message);
            }
            return;
        }
        catch (CloudflareApiException ex) when (!ex.IsForbidden)
        {
            var enabled = CloudflareClient.GetOrganizationMfaMethods(org);
            var usable = rdp.MfaMethods.Where(enabled.Contains).ToList();
            if (usable.Count > 0)
            {
                log.Warn($"無法自動更新組織的 2FA 設定，改用組織已允許的方式：{string.Join("、", usable.Select(MfaMethodName))}");
                log.Info(ex.Message);
                rdp.MfaMethods = usable;
                return;
            }

            throw new InvalidOperationException(
                "無法自動啟用 Zero Trust 組織的 2FA（獨立 MFA）。請先在後台手動啟用一次：\n" +
                "  1. 開啟 https://one.dash.cloudflare.com/ → Access controls（存取控制）→ Access settings（存取設定）\n" +
                "  2. 在「Allow multi-factor authentication (MFA)」勾選 Authenticator application" +
                "（以及需要的 Security key、Biometrics）\n" +
                "  3. 設定 Authentication duration（例如 24 小時），不要勾選「Apply global MFA settings by default」，按 Save\n" +
                "  4. 回到這裡按「重新部署」\n\n" +
                "Cloudflare 回應：" + ex.Message);
        }
    }

    /// <summary>
    /// 建立或更新 App Launcher，讓允許的使用者能登入 team 網域註冊 2FA 裝置。
    /// 每個帳號只能有一個 App Launcher：已經存在（其他地點或手動建立）時，把本地點的政策加進去，
    /// 保留其他地點的政策，多個地點的允許 Email 會自動合併。
    /// </summary>
    async Task EnsureAppLauncherAsync(string acc, RdpConfig rdp, string tunnelName)
    {
        var policyName = $"CfTunnelTool App Launcher - {tunnelName}";
        if (rdp.LauncherPolicyId is not null && await cf.PolicyExistsAsync(acc, rdp.LauncherPolicyId))
            await cf.UpdateLauncherPolicyAsync(acc, rdp.LauncherPolicyId, policyName, rdp);
        else
            rdp.LauncherPolicyId = await cf.CreateLauncherPolicyAsync(acc, policyName, rdp);

        if (rdp.LauncherAppId is not null && await cf.AppExistsAsync(acc, rdp.LauncherAppId))
        {
            await cf.AttachPolicyAsync(acc, rdp.LauncherAppId, rdp.LauncherPolicyId);
            log.Ok(rdp.LauncherOwned ? "2FA 註冊入口已更新" : "2FA 註冊入口已更新（與其他地點共用）");
            return;
        }

        var existing = await cf.FindAppLauncherIdAsync(acc);
        if (existing is null)
        {
            rdp.LauncherAppId = await cf.CreateAppLauncherAsync(acc, rdp.LauncherPolicyId);
            rdp.LauncherOwned = true;
            log.Ok("已建立 2FA 註冊入口（App Launcher）");
            return;
        }

        try
        {
            await cf.AttachPolicyAsync(acc, existing, rdp.LauncherPolicyId);
            rdp.LauncherAppId = existing;
            rdp.LauncherOwned = false;
            log.Ok("已把本地點允許的 Email 加入現有的 2FA 註冊入口（與其他地點共用）");
        }
        catch (CloudflareApiException ex) when (!ex.IsForbidden)
        {
            log.Warn("無法自動加入現有的 App Launcher。請到 Zero Trust → Access controls → Applications → App Launcher " +
                     "確認它的政策允許這些 Email：" + string.Join("、", rdp.AllowedEmails) + "，否則使用者無法註冊 2FA。");
            log.Info(ex.Message);
        }
    }

    /// <summary>
    /// 從共用的應用程式（App Launcher、WARP 註冊權限）移除本地點的政策。
    /// 是自己建立的、而且已經沒有其他地點的政策時，才刪除整個應用程式。
    /// </summary>
    Task ReleaseSharedAppAsync(string acc, string appId, string? policyId, bool owned, string label)
        => ReleaseSharedAppAsync(acc, appId, [policyId], owned, label);

    async Task ReleaseSharedAppAsync(string acc, string appId, string?[] policyIds, bool owned, string label)
    {
        var ids = policyIds.Where(id => id is not null).ToList();
        var remaining = ids.Count == 0 ? 0 : await cf.DetachPoliciesAsync(acc, appId, ids);
        if (owned && remaining == 0)
        {
            if (await cf.DeleteAppAsync(acc, appId)) log.Ok($"已刪除{label}");
        }
        else if (remaining > 0)
        {
            log.Ok($"已從{label}移除本地點的 Email（其他地點仍在使用，保留{label}）");
        }
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
        if (!confirm($"{network}（{m.Name}）目前已經透過「另一個 Tunnel」開放，可能是其他地點的電腦在使用。\n\n" +
                     "• 如果是同一台電腦要改由這裡中繼：選「是」\n" +
                     "• 如果是「不同地點、剛好 IP 一樣」的電腦：選「否」，回到遠端桌面步驟勾選「這個地點使用獨立的虛擬網路」\n\n" +
                     "要改成透過這台電腦中繼嗎？"))
            throw new OperationCanceledException(
                $"已取消部署：{network} 由其他 Tunnel 使用中。如果是不同地點的相同 IP，請在遠端桌面步驟勾選「這個地點使用獨立的虛擬網路」。");
        await cf.UpdateRouteTunnelAsync(acc, route.Id, network, tunnelId, vnetId, comment);
        m.RouteId = route.Id;
        log.Ok($"{m.Name}：路由 {network} 已改由這台電腦中繼");
    }

    async Task EnsureTargetAsync(string acc, RdpConfig rdp, RdpMachine m, string vnetId)
    {
        var name = rdp.TargetName(m);
        var target = m.TargetId is not null ? await cf.GetTargetAsync(acc, m.TargetId) : null;
        target ??= await cf.FindTargetByHostnameAsync(acc, name);
        if (target is null)
        {
            m.TargetId = await cf.CreateTargetAsync(acc, name, m.Ip, vnetId);
            log.Ok($"{m.Name}：已建立遠端桌面目標");
        }
        else
        {
            m.TargetId = target.Id;
            await cf.UpdateTargetAsync(acc, target.Id, name, m.Ip, vnetId);
            log.Ok($"{m.Name}：遠端桌面目標已更新（{m.Ip}）");
        }
    }

    /// <summary>
    /// 取得這個地點要用的虛擬網路：
    /// 獨立虛擬網路 → 找或建立「cft-Tunnel名稱」；否則用帳號的預設虛擬網路。
    /// </summary>
    async Task<string> EnsureVirtualNetworkAsync(string acc, RdpConfig rdp, string tunnelName, RdpConfig? previous)
    {
        if (!rdp.UseOwnVnet)
        {
            rdp.VnetName = null;
            rdp.SiteName = null;
            rdp.VnetOwned = false;
            return await cf.GetDefaultVirtualNetworkIdAsync(acc);
        }

        var name = $"cft-{tunnelName}";
        rdp.VnetName = tunnelName;
        rdp.SiteName = tunnelName;
        var id = await cf.FindVirtualNetworkIdAsync(acc, name);
        if (id is null)
        {
            id = await cf.CreateVirtualNetworkAsync(acc, name, $"CfTunnelTool：{tunnelName} 地點");
            rdp.VnetOwned = true;
            log.Ok($"已建立這個地點的虛擬網路「{name}」（不同地點的相同 IP 不會衝突）");
        }
        else
        {
            // 名稱是本工具的命名規則；之前有記錄就沿用當時的擁有狀態
            rdp.VnetOwned = previous?.VnetId == id ? previous.VnetOwned : true;
            log.Ok($"使用這個地點的虛擬網路「{name}」");
        }
        return id;
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
        // WARP 的註冊權限可能還掛著 App Launcher 政策（舊版），要先移除
        await RemoveWarpAsync(rdp, acc);
        log.Step("移除遠端桌面設定");
        if (rdp.AppId is not null && await cf.DeleteAppAsync(acc, rdp.AppId))
            log.Ok("已刪除遠端桌面應用程式");
        if (rdp.PolicyId is not null && await cf.DeletePolicyAsync(acc, rdp.PolicyId))
            log.Ok("已刪除登入政策");
        if (rdp.LauncherAppId is not null)
            await ReleaseSharedAppAsync(acc, rdp.LauncherAppId, rdp.LauncherPolicyId, rdp.LauncherOwned, "2FA 註冊入口（App Launcher）");
        if (rdp.LauncherPolicyId is not null && await cf.DeletePolicyAsync(acc, rdp.LauncherPolicyId))
            log.Ok("已刪除 App Launcher 政策");
        rdp.LauncherAppId = rdp.LauncherPolicyId = null;
        rdp.LauncherOwned = true;
        foreach (var m in rdp.Machines)
            await RemoveMachineAsync(acc, m);
        if (!string.IsNullOrEmpty(rdp.Hostname))
            await DeleteRdpDnsAsync(zone.ZoneId, rdp.Hostname);
        rdp.AppId = rdp.PolicyId = null;

        // 這個地點自己建立的虛擬網路（路由都已刪除後才能刪）
        if (rdp is { UseOwnVnet: true, VnetOwned: true, VnetId: { } vnet })
        {
            try
            {
                if (await cf.DeleteVirtualNetworkAsync(acc, vnet)) log.Ok("已刪除這個地點的虛擬網路");
            }
            catch (CloudflareApiException ex)
            {
                log.Warn("虛擬網路暫時無法刪除（可能還有其他路由在使用），可以之後到 Cloudflare 後台刪除。");
                log.Info(ex.Message);
            }
            rdp.VnetId = null;
            rdp.VnetOwned = false;
        }
    }

    // ================= 狀態 =================

    public async Task<RdpOnlineStatus> GetStatusAsync(RdpConfig rdp, string accountId)
    {
        var appOk = (!rdp.BrowserEnabled || (rdp.AppId is not null && await cf.AppExistsAsync(accountId, rdp.AppId))) &&
                    (!rdp.WarpEnabled || (rdp.WarpAppId is not null && await cf.AppExistsAsync(accountId, rdp.WarpAppId)));
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

    /// <summary>Email 驗證碼有效時間的顯示文字，例如「每 8 小時」。</summary>
    public static string LoginName(string duration) => duration switch
    {
        "1h" => "每 1 小時",
        "8h" => "每 8 小時",
        "24h" => "每 24 小時",
        "168h" => "每 7 天",
        _ => duration,
    };
}
