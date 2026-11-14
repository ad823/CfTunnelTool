using System.Text.Json.Nodes;

namespace CfTunnelTool.Core;

record AccessOrganization(string AuthDomain, JsonObject Raw);
record TeamnetRoute(string Id, string Network, string TunnelId, string? VnetId);
record InfraTarget(string Id, string Hostname, string? Ip);

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

    /// <summary>
    /// 確保組織已啟用獨立 MFA，且允許指定的驗證方式。
    /// 依官方文件，PUT 時要把 GET 回來的所有欄位帶回去，避免覆寫其他設定。
    /// </summary>
    public async Task EnsureOrganizationMfaAsync(string accountId, AccessOrganization org, IEnumerable<string> methods)
    {
        var body = (JsonObject)org.Raw.DeepClone();
        foreach (var readOnly in new[] { "created_at", "updated_at" }) body.Remove(readOnly);

        var mfa = body["mfa_config"] as JsonObject ?? new JsonObject();
        var allowed = (mfa["allowed_authenticators"] as JsonArray)?
            .Select(x => x?.GetValue<string>()).Where(x => x is not null).Select(x => x!).ToHashSet() ?? [];
        var before = allowed.Count;
        foreach (var m in methods) allowed.Add(m);
        if (allowed.Count == before && body["mfa_config"] is not null) return; // 已經都允許

        mfa["allowed_authenticators"] = new JsonArray(allowed.Select(a => (JsonNode)a).ToArray());
        mfa["session_duration"] ??= "24h";
        body["mfa_config"] = mfa;
        body["mfa_required_for_all_apps"] ??= false;
        await SendAsync(HttpMethod.Put, $"accounts/{accountId}/access/organizations", body);
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

    internal static object PolicyBody(string name, RdpConfig rdp)
    {
        var include = rdp.AllowedEmails.Select(e => e.StartsWith('@')
            ? (object)new { email_domain = new { domain = e[1..] } }
            : new { email = new { email = e } }).ToList();

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
                target_attributes = new Dictionary<string, string[]> { ["hostname"] = g.Select(m => m.Name).ToArray() },
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
            session_duration = "8h",
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
