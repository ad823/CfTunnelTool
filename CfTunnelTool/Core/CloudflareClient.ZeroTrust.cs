using System.Text.Json.Nodes;

namespace CfTunnelTool.Core;

record AccessOrganization(string AuthDomain, JsonObject Raw);
record TeamnetRoute(string Id, string Network, string TunnelId, string? VnetId);
record InfraTarget(string Id, string Hostname, string? Ip);
record SplitTunnelEntry(string? Address, string? Host, string? Description);

/// <summary>
/// Zero Trust（Access、私有網路路由、基礎架構目標）相關 API，供瀏覽器 RDP 使用。
/// 需要的 Token 權限：
///   Account → Access: Apps and Policies → Edit
///   Account → Access: Organizations, Identity Providers, and Groups → Edit
///   Account → Zero Trust → Edit
/// </summary>
sealed partial class CloudflareClient
{
    /// <summary>刪除資源；已經不存在（404）時視為成功。</summary>
    async Task<bool> TryDeleteAsync(string path)
    {
        try
        {
            await SendAsync(HttpMethod.Delete, path);
            return true;
        }
        catch (CloudflareApiException ex) when (ex.IsNotFound)
        {
            return false;
        }
    }

    // ---------- Zero Trust 組織 ----------

    /// <summary>取得 Zero Trust 組織；帳號尚未啟用 Zero Trust 時回傳 null。</summary>
    public async Task<AccessOrganization?> GetAccessOrganizationAsync(string accountId)
    {
        try
        {
            var result = await SendAsync(HttpMethod.Get, $"accounts/{accountId}/access/organizations");
            if (result is not JsonObject obj || obj["auth_domain"] is null) return null;
            return new AccessOrganization(obj["auth_domain"]!.GetValue<string>(), obj);
        }
        catch (CloudflareApiException ex) when (ex.IsNotFound)
        {
            return null;
        }
    }

    /// <summary>組織目前允許的 MFA 方式（尚未啟用獨立 MFA 時為空）。</summary>
    public static HashSet<string> GetOrganizationMfaMethods(AccessOrganization org) =>
        (org.Raw["mfa_config"]?["allowed_authenticators"] as JsonArray)?
            .Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToHashSet() ?? [];

    /// <summary>
    /// 確保組織已啟用獨立 MFA，且允許指定的驗證方式。
    /// 依官方文件，PUT 時要把 GET 回來的欄位帶回去，避免覆寫其他設定；
    /// 但 GET 會回傳唯讀欄位與空值，原封不動送回會被拒絕（12062 invalid_org_config），所以先清掉。
    /// 失敗時拋出的例外訊息會列出送出的欄位名稱（不含值），方便排查。
    /// </summary>
    public async Task EnsureOrganizationMfaAsync(string accountId, AccessOrganization org, IEnumerable<string> methods,
        string? loginSessionDuration = null)
    {
        var body = OrganizationBody(org, methods, loginSessionDuration);
        if (body is null) return; // 已經是想要的設定
        var mfa = body["mfa_config"] as JsonObject ?? new JsonObject();

        try
        {
            await SendAsync(HttpMethod.Put, $"accounts/{accountId}/access/organizations", body);
        }
        catch (CloudflareApiException ex) when (!ex.IsForbidden)
        {
            throw new CloudflareApiException(
                $"{ex.Message}\n（送出的組織欄位：{string.Join(", ", body.Select(p => p.Key))}；mfa_config：{string.Join(", ", mfa.Select(p => p.Key))}）",
                ex.StatusCode, ex.Codes);
        }
    }

    /// <summary>組織目前的全域登入時間（Email 驗證碼的有效時間）。</summary>
    public static string? GetOrganizationSessionDuration(AccessOrganization org) =>
        org.Raw["session_duration"] is JsonValue v ? v.ToString() : null;

    /// <summary>
    /// 組出要 PUT 的組織設定；已經符合時回傳 null（不需要更新）。
    /// <paramref name="loginSessionDuration"/> 是全域登入時間：在這段時間內，使用者不必重新輸入 Email 驗證碼。
    /// </summary>
    internal static JsonObject? OrganizationBody(AccessOrganization org, IEnumerable<string> methods, string? loginSessionDuration)
    {
        var allowed = GetOrganizationMfaMethods(org);
        var wanted = methods.ToList();
        var sessionOk = string.IsNullOrEmpty(loginSessionDuration) || GetOrganizationSessionDuration(org) == loginSessionDuration;
        if (wanted.All(allowed.Contains) && org.Raw["mfa_config"] is not null && sessionOk) return null;

        var body = (JsonObject)org.Raw.DeepClone();
        string[] readOnly = ["created_at", "updated_at", "is_ui_read_only", "ui_read_only_toggle_reason"];
        foreach (var key in readOnly) body.Remove(key);
        foreach (var key in body.Where(p => p.Value is null || (p.Value is JsonValue v && v.ToString() == ""))
                                .Select(p => p.Key).ToList())
            body.Remove(key);

        foreach (var m in wanted) allowed.Add(m);
        var mfa = body["mfa_config"] as JsonObject ?? new JsonObject();
        foreach (var key in mfa.Where(p => p.Value is null).Select(p => p.Key).ToList()) mfa.Remove(key);
        mfa["allowed_authenticators"] = new JsonArray(allowed.Select(a => (JsonNode)a).ToArray());
        if (mfa["session_duration"] is null || mfa["session_duration"]!.ToString() == "") mfa["session_duration"] = "24h";
        body["mfa_config"] = mfa;
        body["mfa_required_for_all_apps"] ??= false;
        if (!string.IsNullOrEmpty(loginSessionDuration)) body["session_duration"] = loginSessionDuration;
        return body;
    }

    // ---------- 登入方式（Identity Provider） ----------

    /// <summary>確保有「Email 一次性驗證碼」登入方式，回傳其 ID。</summary>
    public async Task<string> EnsureOneTimePinIdpAsync(string accountId)
    {
        var list = await SendAsync(HttpMethod.Get, $"accounts/{accountId}/access/identity_providers");
        var otp = list?.AsArray().FirstOrDefault(x => x?["type"]?.GetValue<string>() == "onetimepin");
        if (otp is not null) return otp["id"]!.GetValue<string>();

        var created = await SendAsync(HttpMethod.Post, $"accounts/{accountId}/access/identity_providers",
            new { name = "One-time PIN", type = "onetimepin", config = new { } });
        return created!["id"]!.GetValue<string>();
    }

    // ---------- 私有網路（虛擬網路、路由） ----------

    public async Task<string> GetDefaultVirtualNetworkIdAsync(string accountId)
    {
        var list = await SendAsync(HttpMethod.Get, $"accounts/{accountId}/teamnet/virtual_networks?is_deleted=false");
        var vnet = list?.AsArray().FirstOrDefault(v => v?["is_default_network"]?.GetValue<bool>() == true)
                   ?? list?.AsArray().FirstOrDefault()
                   ?? throw new CloudflareApiException("找不到預設的虛擬網路（Virtual Network），請確認 Zero Trust 已啟用。");
        return vnet["id"]!.GetValue<string>();
    }

    /// <summary>用名稱找虛擬網路，找不到回傳 null。</summary>
    public async Task<string?> FindVirtualNetworkIdAsync(string accountId, string name)
    {
        var list = await SendAsync(HttpMethod.Get, $"accounts/{accountId}/teamnet/virtual_networks?is_deleted=false&name={Q(name)}");
        return list?.AsArray().FirstOrDefault(v => v?["name"]?.GetValue<string>() == name)?["id"]?.GetValue<string>();
    }

    public async Task<string> CreateVirtualNetworkAsync(string accountId, string name, string comment)
        => (await SendAsync(HttpMethod.Post, $"accounts/{accountId}/teamnet/virtual_networks",
            new { name, comment, is_default_network = false }))!["id"]!.GetValue<string>();

    public Task<bool> DeleteVirtualNetworkAsync(string accountId, string vnetId)
        => TryDeleteAsync($"accounts/{accountId}/teamnet/virtual_networks/{vnetId}");

    static TeamnetRoute ParseRoute(JsonNode r) => new(
        r["id"]!.GetValue<string>(),
        r["network"]!.GetValue<string>(),
        r["tunnel_id"]?.GetValue<string>() ?? "",
        r["virtual_network_id"]?.GetValue<string>());

    public async Task<TeamnetRoute?> FindRouteAsync(string accountId, string network, string vnetId)
    {
        // 只列出包含這個 IP 的路由，再挑出完全相同的 /32
        var list = await SendAsync(HttpMethod.Get,
            $"accounts/{accountId}/teamnet/routes?is_deleted=false&network_superset={Q(network)}&virtual_network_id={Q(vnetId)}");
        return list?.AsArray()
            .Where(r => r is not null)
            .Select(r => ParseRoute(r!))
            .FirstOrDefault(r => r.Network == network && (r.VnetId is null || r.VnetId == vnetId));
    }

    public async Task<string> CreateRouteAsync(string accountId, string network, string tunnelId, string vnetId, string comment)
    {
        var r = await SendAsync(HttpMethod.Post, $"accounts/{accountId}/teamnet/routes",
            new { network, tunnel_id = tunnelId, virtual_network_id = vnetId, comment });
        return r!["id"]!.GetValue<string>();
    }

    public async Task UpdateRouteTunnelAsync(string accountId, string routeId, string network, string tunnelId, string vnetId, string comment)
        => await SendAsync(HttpMethod.Patch, $"accounts/{accountId}/teamnet/routes/{routeId}",
            new { network, tunnel_id = tunnelId, virtual_network_id = vnetId, comment });

    public Task<bool> DeleteRouteAsync(string accountId, string routeId)
        => TryDeleteAsync($"accounts/{accountId}/teamnet/routes/{routeId}");

    // ---------- 基礎架構目標（Infrastructure Target） ----------

    static InfraTarget ParseTarget(JsonNode t) => new(
        t["id"]!.GetValue<string>(),
        t["hostname"]!.GetValue<string>(),
        t["ip"]?["ipv4"]?["ip_addr"]?.GetValue<string>());

    public async Task<InfraTarget?> GetTargetAsync(string accountId, string targetId)
    {
        try { return ParseTarget((await SendAsync(HttpMethod.Get, $"accounts/{accountId}/infrastructure/targets/{targetId}"))!); }
        catch (CloudflareApiException ex) when (ex.IsNotFound) { return null; }
    }

    public async Task<InfraTarget?> FindTargetByHostnameAsync(string accountId, string hostname)
    {
        var list = await SendAsync(HttpMethod.Get, $"accounts/{accountId}/infrastructure/targets?hostname={Q(hostname)}");
        var t = list?.AsArray().FirstOrDefault(x => x?["hostname"]?.GetValue<string>() == hostname);
        return t is null ? null : ParseTarget(t);
    }

    static object TargetBody(string hostname, string ip, string vnetId) =>
        new { hostname, ip = new { ipv4 = new { ip_addr = ip, virtual_network_id = vnetId } } };

    public async Task<string> CreateTargetAsync(string accountId, string hostname, string ip, string vnetId)
        => (await SendAsync(HttpMethod.Post, $"accounts/{accountId}/infrastructure/targets",
            TargetBody(hostname, ip, vnetId)))!["id"]!.GetValue<string>();

    public async Task UpdateTargetAsync(string accountId, string targetId, string hostname, string ip, string vnetId)
        => await SendAsync(HttpMethod.Put, $"accounts/{accountId}/infrastructure/targets/{targetId}",
            TargetBody(hostname, ip, vnetId));

    public Task<bool> DeleteTargetAsync(string accountId, string targetId)
        => TryDeleteAsync($"accounts/{accountId}/infrastructure/targets/{targetId}");

    // ---------- Access 政策 ----------

    static List<object> EmailIncludes(RdpConfig rdp) => rdp.AllowedEmails.Select(e => e.StartsWith('@')
        ? (object)new { email_domain = new { domain = e[1..] } }
        : new { email = new { email = e } }).ToList();

    internal static object PolicyBody(string name, RdpConfig rdp)
    {
        var include = EmailIncludes(rdp);

        var localToRemote = new List<string>();
        var remoteToLocal = new List<string>();
        if (rdp.ClipboardText) { localToRemote.Add("text"); remoteToLocal.Add("text"); }
        if (rdp.FileUpload) localToRemote.Add("file");
        if (rdp.FileDownload) remoteToLocal.Add("file");

        return new
        {
            name,
            decision = "allow",
            include,
            mfa_config = new
            {
                mfa_disabled = false,
                allowed_authenticators = rdp.MfaMethods,
                session_duration = rdp.MfaSessionDuration,
            },
            connection_rules = new
            {
                rdp = new
                {
                    allowed_clipboard_local_to_remote_formats = localToRemote,
                    allowed_clipboard_remote_to_local_formats = remoteToLocal,
                },
            },
        };
    }

    /// <summary>App Launcher 的政策：只允許同一批 Email 登入，不要求 MFA（使用者要先能進來才能註冊 2FA 裝置）。</summary>
    internal static object LauncherPolicyBody(string name, RdpConfig rdp) => new
    {
        name,
        decision = "allow",
        include = EmailIncludes(rdp),
    };

    public async Task<string> CreateLauncherPolicyAsync(string accountId, string name, RdpConfig rdp)
        => (await SendAsync(HttpMethod.Post, $"accounts/{accountId}/access/policies", LauncherPolicyBody(name, rdp)))!["id"]!.GetValue<string>();

    public async Task UpdateLauncherPolicyAsync(string accountId, string policyId, string name, RdpConfig rdp)
        => await SendAsync(HttpMethod.Put, $"accounts/{accountId}/access/policies/{policyId}", LauncherPolicyBody(name, rdp));

    // ---------- WARP：Gateway 代理、Split Tunnel、裝置註冊、私有網路應用程式 ----------

    /// <summary>開啟 Gateway 的 TCP／UDP 代理（WARP 連私有網路必須）。PATCH 只改這兩項。</summary>
    public async Task EnableGatewayProxyAsync(string accountId)
        => await SendAsync(HttpMethod.Patch, $"accounts/{accountId}/devices/settings",
            new { gateway_proxy_enabled = true, gateway_udp_proxy_enabled = true });

    /// <summary>預設 WARP 設定檔的 Split Tunnel；有 include 清單就是 Include 模式，否則是 Exclude 模式。</summary>
    public async Task<(string Mode, List<SplitTunnelEntry> Entries)> GetSplitTunnelAsync(string accountId)
    {
        var policy = await SendAsync(HttpMethod.Get, $"accounts/{accountId}/devices/policy");
        static List<SplitTunnelEntry> Parse(JsonNode? arr) => arr?.AsArray()
            .Where(e => e is not null)
            .Select(e => new SplitTunnelEntry(e!["address"]?.ToString(), e["host"]?.ToString(), e["description"]?.ToString()))
            .ToList() ?? [];
        var include = Parse(policy?["include"]);
        return include.Count > 0 ? ("include", include) : ("exclude", Parse(policy?["exclude"]));
    }

    public async Task PutSplitTunnelAsync(string accountId, string mode, List<SplitTunnelEntry> entries)
    {
        var body = entries.Select(e => e.Address is not null
            ? (object)new { address = e.Address, description = e.Description ?? "" }
            : new { host = e.Host, description = e.Description ?? "" }).ToList();
        await SendAsync(HttpMethod.Put, $"accounts/{accountId}/devices/policy/{mode}", body);
    }

    /// <summary>找出指定類型的 Access 應用程式（例如 warp、app_launcher，每個帳號最多一個）。</summary>
    public async Task<string?> FindAppIdByTypeAsync(string accountId, string type)
    {
        var list = await SendAsync(HttpMethod.Get, $"accounts/{accountId}/access/apps");
        return list?.AsArray().FirstOrDefault(a => a?["type"]?.GetValue<string>() == type)?["id"]?.GetValue<string>();
    }

    static object WarpEnrollBody(string policyId) => new
    {
        type = "warp",
        name = "Warp Login App",
        policies = new[] { new { id = policyId, precedence = 1 } },
        session_duration = "24h",
    };

    public async Task<string> CreateWarpEnrollAppAsync(string accountId, string policyId)
        => (await SendAsync(HttpMethod.Post, $"accounts/{accountId}/access/apps", WarpEnrollBody(policyId)))!["id"]!.GetValue<string>();


    /// <summary>
    /// WARP 政策：同一批 Email ＋ 2FA（沒有瀏覽器 RDP 的剪貼簿規則）。
    /// 同時用在「私有網路應用程式」（連線前 2FA）與「WARP 裝置註冊權限」（註冊裝置也要 2FA）。
    /// </summary>
    internal static object WarpPolicyBody(string name, RdpConfig rdp) => new
    {
        name,
        decision = "allow",
        include = EmailIncludes(rdp),
        mfa_config = new
        {
            mfa_disabled = false,
            allowed_authenticators = rdp.MfaMethods,
            session_duration = rdp.MfaSessionDuration,
        },
    };

    public async Task<string> CreateWarpPolicyAsync(string accountId, string name, RdpConfig rdp)
        => (await SendAsync(HttpMethod.Post, $"accounts/{accountId}/access/policies", WarpPolicyBody(name, rdp)))!["id"]!.GetValue<string>();

    public async Task UpdateWarpPolicyAsync(string accountId, string policyId, string name, RdpConfig rdp)
        => await SendAsync(HttpMethod.Put, $"accounts/{accountId}/access/policies/{policyId}", WarpPolicyBody(name, rdp));

    /// <summary>私有網路應用程式：每台電腦一個 IP/32 ＋ RDP 連接埠，透過 WARP 連線前要通過 Access 與 2FA。</summary>
    internal static object PrivateRdpAppBody(string name, RdpConfig rdp, string policyId) => new
    {
        name,
        type = "self_hosted",
        destinations = rdp.Machines.Select(m => new
        {
            type = "private",
            cidr = $"{m.Ip}/32",
            port_range = m.Port.ToString(),
            l4_protocol = "tcp",
            vnet_id = rdp.VnetId,
        }).ToArray(),
        policies = new[] { new { id = policyId, precedence = 1 } },
        // 應用程式工作階段短 → 每次新連線都回到 Access 檢查；Email 驗證碼由組織全域登入時間決定
        session_duration = rdp.AppSessionDuration,
        mfa_config = AppMfaConfig(rdp),
        app_launcher_visible = false,
    };

    /// <summary>應用程式層的 2FA 設定（與政策層相同；政策層優先，兩層都設比較保險）。</summary>
    static object AppMfaConfig(RdpConfig rdp) => new
    {
        mfa_disabled = false,
        allowed_authenticators = rdp.MfaMethods,
        session_duration = rdp.MfaSessionDuration,
    };

    public async Task<string> CreatePrivateRdpAppAsync(string accountId, string name, RdpConfig rdp, string policyId)
        => (await SendAsync(HttpMethod.Post, $"accounts/{accountId}/access/apps", PrivateRdpAppBody(name, rdp, policyId)))!["id"]!.GetValue<string>();

    public async Task UpdatePrivateRdpAppAsync(string accountId, string appId, string name, RdpConfig rdp, string policyId)
        => await SendAsync(HttpMethod.Put, $"accounts/{accountId}/access/apps/{appId}", PrivateRdpAppBody(name, rdp, policyId));

    // ---------- Gateway 網路規則：被開放的電腦只開放遠端桌面連接埠 ----------

    /// <summary>
    /// 封鎖條件：連到被開放電腦的 IP、但不是它的遠端桌面連接埠。同一個 port 的電腦放在同一組。
    /// 使用獨立虛擬網路時限定在該虛擬網路，不影響其他地點 IP 相同的電腦。
    /// 例如 <c>(net.dst.ip in {192.168.1.10/32 192.168.1.11/32} and not(net.dst.port in {3389}))</c>
    /// </summary>
    internal static string GatewayBlockTraffic(RdpConfig rdp)
    {
        var groups = rdp.Machines
            .GroupBy(m => m.Port)
            .OrderBy(g => g.Key)
            .Select(g => $"(net.dst.ip in {{{string.Join(' ', g.Select(m => $"{m.Ip}/32"))}}} and not(net.dst.port in {{{g.Key}}}))")
            .ToList();
        var hosts = string.Join(" or ", groups);
        return rdp.UseOwnVnet && !string.IsNullOrEmpty(rdp.VnetId)
            ? $"net.vnet_id == \"{rdp.VnetId}\" and ({hosts})"
            : hosts;
    }

    internal static object GatewayRuleBody(string name, RdpConfig rdp) => new
    {
        name,
        description = "CfTunnelTool：被開放的電腦透過 WARP 只能連遠端桌面連接埠，其他連接埠一律封鎖",
        action = "block",
        enabled = true,
        filters = new[] { "l4" },
        traffic = GatewayBlockTraffic(rdp),
        identity = "",
        device_posture = "",
    };

    public async Task<string> CreateGatewayRuleAsync(string accountId, string name, RdpConfig rdp)
        => (await SendAsync(HttpMethod.Post, $"accounts/{accountId}/gateway/rules", GatewayRuleBody(name, rdp)))!["id"]!.GetValue<string>();

    public async Task UpdateGatewayRuleAsync(string accountId, string ruleId, string name, RdpConfig rdp)
        => await SendAsync(HttpMethod.Put, $"accounts/{accountId}/gateway/rules/{ruleId}", GatewayRuleBody(name, rdp));

    public async Task<bool> GatewayRuleExistsAsync(string accountId, string ruleId)
    {
        try { await SendAsync(HttpMethod.Get, $"accounts/{accountId}/gateway/rules/{ruleId}"); return true; }
        catch (CloudflareApiException ex) when (ex.IsNotFound) { return false; }
    }

    public Task<bool> DeleteGatewayRuleAsync(string accountId, string ruleId)
        => TryDeleteAsync($"accounts/{accountId}/gateway/rules/{ruleId}");

    // ---------- 共用應用程式的政策（多地點） ----------

    /// <summary>應用程式目前套用的政策 ID（依優先順序）。</summary>
    public async Task<List<string>> GetAppPolicyIdsAsync(string accountId, string appId)
    {
        var app = await SendAsync(HttpMethod.Get, $"accounts/{accountId}/access/apps/{appId}");
        return app?["policies"]?.AsArray()
            .Where(p => p?["id"] is not null)
            .OrderBy(p => p?["precedence"]?.GetValue<int>() ?? int.MaxValue)
            .Select(p => p!["id"]!.GetValue<string>())
            .ToList() ?? [];
    }

    /// <summary>
    /// 只更換應用程式的政策清單，其他設定保持原樣：先 GET 完整設定，移除唯讀欄位與空值後再 PUT 回去。
    /// </summary>
    async Task SetAppPoliciesAsync(string accountId, string appId, List<string> policyIds)
    {
        var app = await SendAsync(HttpMethod.Get, $"accounts/{accountId}/access/apps/{appId}") as JsonObject
                  ?? throw new CloudflareApiException("讀不到應用程式設定。");
        await SendAsync(HttpMethod.Put, $"accounts/{accountId}/access/apps/{appId}", WithPolicies(app, policyIds));
    }

    /// <summary>複製應用程式設定，移除唯讀欄位與空值，並換上新的政策清單（依順序設定 precedence）。</summary>
    internal static JsonObject WithPolicies(JsonObject app, List<string> policyIds)
    {
        var body = (JsonObject)app.DeepClone();
        foreach (var key in new[] { "id", "uid", "aud", "created_at", "updated_at", "policies" }) body.Remove(key);
        foreach (var key in body.Where(p => p.Value is null).Select(p => p.Key).ToList()) body.Remove(key);
        body["policies"] = new JsonArray(policyIds
            .Select((id, i) => (JsonNode)new JsonObject { ["id"] = id, ["precedence"] = i + 1 }).ToArray());
        return body;
    }

    /// <summary>把政策加到應用程式（保留其他地點的政策）。已經有了就不動，回傳是否有變更。</summary>
    public async Task<bool> AttachPolicyAsync(string accountId, string appId, string policyId)
    {
        var ids = await GetAppPolicyIdsAsync(accountId, appId);
        if (ids.Contains(policyId)) return false;
        ids.Add(policyId);
        await SetAppPoliciesAsync(accountId, appId, ids);
        return true;
    }

    /// <summary>
    /// 從應用程式移除政策，回傳剩下的政策數。
    /// 移除後會變成沒有任何政策時不更新（由呼叫端決定要不要刪除整個應用程式）。
    /// </summary>
    public Task<int> DetachPolicyAsync(string accountId, string appId, string policyId)
        => DetachPoliciesAsync(accountId, appId, [policyId]);

    /// <summary>從應用程式移除多個政策（規則同 <see cref="DetachPolicyAsync"/>），回傳剩下的政策數。</summary>
    public async Task<int> DetachPoliciesAsync(string accountId, string appId, IEnumerable<string?> policyIds)
    {
        List<string> ids;
        try { ids = await GetAppPolicyIdsAsync(accountId, appId); }
        catch (CloudflareApiException ex) when (ex.IsNotFound) { return 0; }
        var removed = ids.RemoveAll(id => policyIds.Contains(id));
        if (removed > 0 && ids.Count > 0) await SetAppPoliciesAsync(accountId, appId, ids);
        return ids.Count;
    }

    /// <summary>政策是否要求獨立 MFA（政策本身有 mfa_config 且沒有停用）。讀不到時回傳 null。</summary>
    public async Task<bool?> PolicyRequiresMfaAsync(string accountId, string policyId)
    {
        try
        {
            var p = await SendAsync(HttpMethod.Get, $"accounts/{accountId}/access/policies/{policyId}");
            return PolicyRequiresMfa(p);
        }
        catch (CloudflareApiException) { return null; }
    }

    internal static bool PolicyRequiresMfa(JsonNode? policy)
        => policy?["mfa_config"] is JsonObject mfa
           && mfa["mfa_disabled"]?.GetValue<bool>() != true
           && mfa["allowed_authenticators"] is JsonArray { Count: > 0 };

    // ---------- App Launcher（使用者註冊 2FA 裝置的入口） ----------

    static object LauncherAppBody(string policyId) => new
    {
        type = "app_launcher",
        policies = new[] { new { id = policyId, precedence = 1 } },
        session_duration = "24h",
    };

    /// <summary>找出帳號現有的 App Launcher（每個帳號最多一個）。</summary>
    public async Task<string?> FindAppLauncherIdAsync(string accountId)
    {
        var list = await SendAsync(HttpMethod.Get, $"accounts/{accountId}/access/apps");
        return list?.AsArray().FirstOrDefault(a => a?["type"]?.GetValue<string>() == "app_launcher")?["id"]?.GetValue<string>();
    }

    public async Task<string> CreateAppLauncherAsync(string accountId, string policyId)
        => (await SendAsync(HttpMethod.Post, $"accounts/{accountId}/access/apps", LauncherAppBody(policyId)))!["id"]!.GetValue<string>();


    public async Task<string> CreatePolicyAsync(string accountId, string name, RdpConfig rdp)
        => (await SendAsync(HttpMethod.Post, $"accounts/{accountId}/access/policies", PolicyBody(name, rdp)))!["id"]!.GetValue<string>();

    public async Task UpdatePolicyAsync(string accountId, string policyId, string name, RdpConfig rdp)
        => await SendAsync(HttpMethod.Put, $"accounts/{accountId}/access/policies/{policyId}", PolicyBody(name, rdp));

    public async Task<bool> PolicyExistsAsync(string accountId, string policyId)
    {
        try { await SendAsync(HttpMethod.Get, $"accounts/{accountId}/access/policies/{policyId}"); return true; }
        catch (CloudflareApiException ex) when (ex.IsNotFound) { return false; }
    }

    public Task<bool> DeletePolicyAsync(string accountId, string policyId)
        => TryDeleteAsync($"accounts/{accountId}/access/policies/{policyId}");

    // ---------- Access 應用程式（瀏覽器 RDP） ----------

    internal static object RdpAppBody(string name, RdpConfig rdp, string policyId)
    {
        // 依連接埠分組：同一個 port 的電腦放在同一條 target_criteria
        var criteria = rdp.Machines
            .GroupBy(m => m.Port)
            .Select(g => new
            {
                port = g.Key,
                protocol = "RDP",
                target_attributes = new Dictionary<string, string[]> { ["hostname"] = g.Select(rdp.TargetName).ToArray() },
            })
            .ToList();

        return new
        {
            name,
            type = "rdp",
            domain = rdp.Hostname,
            destinations = new[] { new { type = "public", uri = rdp.Hostname } },
            target_criteria = criteria,
            policies = new[] { new { id = policyId, precedence = 1 } },
            session_duration = rdp.AppSessionDuration,
            mfa_config = AppMfaConfig(rdp),
            app_launcher_visible = true,
            allow_authenticate_via_warp = false,
        };
    }

    public async Task<string> CreateRdpAppAsync(string accountId, string name, RdpConfig rdp, string policyId)
        => (await SendAsync(HttpMethod.Post, $"accounts/{accountId}/access/apps", RdpAppBody(name, rdp, policyId)))!["id"]!.GetValue<string>();

    public async Task UpdateRdpAppAsync(string accountId, string appId, string name, RdpConfig rdp, string policyId)
        => await SendAsync(HttpMethod.Put, $"accounts/{accountId}/access/apps/{appId}", RdpAppBody(name, rdp, policyId));

    public async Task<bool> AppExistsAsync(string accountId, string appId)
    {
        try { await SendAsync(HttpMethod.Get, $"accounts/{accountId}/access/apps/{appId}"); return true; }
        catch (CloudflareApiException ex) when (ex.IsNotFound) { return false; }
    }

    /// <summary>用網域找既有的 Access 應用程式（避免重複建立）。</summary>
    public async Task<(string Id, string Type)?> FindAppByDomainAsync(string accountId, string domain)
    {
        var list = await SendAsync(HttpMethod.Get, $"accounts/{accountId}/access/apps?domain={Q(domain)}");
        var app = list?.AsArray().FirstOrDefault(a => a?["domain"]?.GetValue<string>() == domain);
        return app is null ? null : (app["id"]!.GetValue<string>(), app["type"]?.GetValue<string>() ?? "");
    }

    public Task<bool> DeleteAppAsync(string accountId, string appId)
        => TryDeleteAsync($"accounts/{accountId}/access/apps/{appId}");

    // ---------- DNS（A 紀錄） ----------

    public async Task CreateProxiedARecordAsync(string zoneId, string name, string ip)
        => await SendAsync(HttpMethod.Post, $"zones/{zoneId}/dns_records",
            new { type = "A", name, content = ip, proxied = true, ttl = 1, comment = ManagedComment });

    public async Task UpdateProxiedARecordAsync(string zoneId, string recordId, string name, string ip)
        => await SendAsync(HttpMethod.Put, $"zones/{zoneId}/dns_records/{recordId}",
            new { type = "A", name, content = ip, proxied = true, ttl = 1, comment = ManagedComment });

    /// <summary>讀取 DNS 紀錄的 comment，用來判斷是不是本工具建立的。</summary>
    public async Task<string?> GetDnsRecordCommentAsync(string zoneId, string recordId)
        => (await SendAsync(HttpMethod.Get, $"zones/{zoneId}/dns_records/{recordId}"))?["comment"]?.GetValue<string>();
}
