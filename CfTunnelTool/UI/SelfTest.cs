#if DEBUG
using System.Text;
using System.Text.Json;
using CfTunnelTool.Core;

namespace CfTunnelTool.UI;

/// <summary>
/// 開發用自我測試（CfTunnelTool.exe --selftest 輸出檔）：只讀取系統狀態，不會修改任何設定。
/// </summary>
static class SelfTest
{
    public static void Run(string outputPath)
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  | " + detail : "")}");
        }
        void Throws(string name, Action action, string expectContains)
        {
            try { action(); Check(name, false, "沒有拋出例外"); }
            catch (InvalidOperationException ex) { Check(name, ex.Message.Contains(expectContains), ex.Message); }
        }

        // ---- RdpHost（只讀） ----
        var status = RdpHost.GetStatus();
        sb.AppendLine($"INFO  RdpHost.GetStatus = {status}");
        Check("RdpHost.GetStatus 讀得到 Edition", status.Edition.Length > 0, status.Edition);
        var ip = RdpHost.GetLanIPv4();
        Check("RdpHost.GetLanIPv4 找到 IPv4", ip is not null && System.Net.IPAddress.TryParse(ip, out _), ip ?? "null");
        Check("TestTcpAsync 對關閉的連接埠回傳 false", !RdpHost.TestTcpAsync("127.0.0.1", 1, 1500).GetAwaiter().GetResult());
        if (ip is not null)
        {
            var open = RdpHost.TestTcpAsync(ip, status.Port, 1500).GetAwaiter().GetResult();
            Check("TestTcpAsync 結果與 RDP 啟用狀態一致", open == status.Enabled, $"{ip}:{status.Port} open={open} enabled={status.Enabled}");
        }

        // ---- 本機帳號（只讀，不會修改密碼或自動登入） ----
        var users = LocalAccount.ListUsers();
        var me = LocalAccount.CurrentUserName();
        Check("ListUsers 讀得到帳號", users.Count > 0, $"{users.Count} 個");
        Check("ListUsers 包含目前帳號", users.Any(u => u.Name.Equals(me, StringComparison.OrdinalIgnoreCase)));
        Check("ListUsers 有帳號來源", users.All(u => u.Source.Length > 0), string.Join(", ", users.Select(u => u.Source).Distinct()));
        var auto = LocalAccount.GetAutoLogon();
        Check("GetAutoLogon 可讀取", true, auto.Enabled ? "已啟用" : "未啟用");
        Check("RemoteLoginName 格式", LocalAccount.RemoteLoginName("u") == $"{Environment.MachineName}\\u");

        // ---- RdpConfig / ToolConfig 驗證 ----
        RdpConfig Good() => new()
        {
            Enabled = true,
            Hostname = "rdp.example.com",
            AllowedEmails = ["a@example.com", "@example.com"],
            Machines = [new RdpMachine { Name = "pc1", Ip = "192.168.1.10" }, new RdpMachine { Name = "pc2", Ip = "192.168.1.11", Port = 3390 }],
        };
        ToolConfig Cfg(RdpConfig? rdp, int routes = 0) => new()
        {
            Domain = "example.com",
            TunnelName = "test-pc",
            Routes = Enumerable.Range(0, routes).Select(i => new RouteConfig { Hostname = $"w{i}.example.com", Service = "http://localhost:80" }).ToList(),
            Rdp = rdp,
        };

        try { Cfg(Good()).Validate(); Check("只有 RDP、沒有網址也能通過驗證", true); }
        catch (InvalidOperationException ex) { Check("只有 RDP、沒有網址也能通過驗證", false, ex.Message); }
        Throws("沒有網址也沒有 RDP → 錯誤", () => Cfg(null).Validate(), "請至少設定一個對外網址");
        Throws("RDP 停用時，沒有網址 → 錯誤", () => Cfg(new RdpConfig { Enabled = false }).Validate(), "請至少設定一個對外網址");
        Throws("127.0.0.1 不允許", () => { var r = Good(); r.Machines[0].Ip = "127.0.0.1"; Cfg(r).Validate(); }, "127.0.0.1");
        Throws("IP 格式錯誤", () => { var r = Good(); r.Machines[0].Ip = "192.168.1"; Cfg(r).Validate(); }, "不是有效的 IPv4");
        Throws("Email 格式錯誤", () => { var r = Good(); r.AllowedEmails = ["not-an-email"]; Cfg(r).Validate(); }, "不是有效的 Email");
        Throws("沒有 Email", () => { var r = Good(); r.AllowedEmails = []; Cfg(r).Validate(); }, "允許登入的 Email");
        Throws("名稱重複", () => { var r = Good(); r.Machines[1].Name = "PC1"; Cfg(r).Validate(); }, "重複");
        Throws("同 IP 兩台", () => { var r = Good(); r.Machines[1].Ip = "192.168.1.10"; Cfg(r).Validate(); }, "同一個 IP");
        Throws("RDP 網址不屬於網域", () => { var r = Good(); r.Hostname = "rdp.other.com"; Cfg(r).Validate(); }, "不屬於網域");
        Throws("RDP 網址和網站網址重複", () => { var r = Good(); r.Hostname = "w0.example.com"; Cfg(r, 1).Validate(); }, "重複");
        Throws("沒有選 2FA 方式", () => { var r = Good(); r.MfaMethods = []; Cfg(r).Validate(); }, "2FA");

        // ---- 帶入舊 ID ----
        var prev = Good();
        prev.AppId = "app1"; prev.PolicyId = "pol1"; prev.VnetId = "vnet1";
        prev.Machines[0].TargetId = "t1"; prev.Machines[0].RouteId = "r1";
        prev.Machines[1].TargetId = "t2"; prev.Machines[1].RouteId = "r2";
        var cur = Good();
        cur.Machines[1].Ip = "192.168.1.99"; // IP 改變 → 路由不帶入
        RdpDeployer.CarryOverIds(cur, prev);
        Check("CarryOverIds 帶入 App/Policy/VNet", cur.AppId == "app1" && cur.PolicyId == "pol1" && cur.VnetId == "vnet1");
        Check("CarryOverIds 同 IP 帶入 Target 與 Route", cur.Machines[0].TargetId == "t1" && cur.Machines[0].RouteId == "r1");
        Check("CarryOverIds IP 改變時不帶入 Route", cur.Machines[1].TargetId == "t2" && cur.Machines[1].RouteId is null);

        // ---- 連線網址 ----
        Check("UrlFor 格式", prev.UrlFor(prev.Machines[1]) == "https://rdp.example.com/rdp/vnet1/192.168.1.11/3390", prev.UrlFor(prev.Machines[1]));

        // ---- 送給 Cloudflare 的 JSON ----
        var rdpForJson = Good();
        rdpForJson.FileDownload = false;
        var policyJson = JsonSerializer.Serialize(CloudflareClient.PolicyBody("p", rdpForJson));
        var appJson = JsonSerializer.Serialize(CloudflareClient.RdpAppBody("a", rdpForJson, "POLICY"));
        sb.AppendLine("INFO  PolicyBody = " + policyJson);
        sb.AppendLine("INFO  RdpAppBody = " + appJson);
        Check("政策：email 與 email_domain", policyJson.Contains("{\"email\":{\"email\":\"a@example.com\"}}") && policyJson.Contains("{\"email_domain\":{\"domain\":\"example.com\"}}"));
        Check("政策：不允許下載時 remote_to_local 只有 text", policyJson.Contains("\"allowed_clipboard_remote_to_local_formats\":[\"text\"]"));
        Check("政策：mfa_config", policyJson.Contains("\"mfa_config\":{\"mfa_disabled\":false"));
        Check("應用程式：type rdp 與依 port 分組", appJson.Contains("\"type\":\"rdp\"") && appJson.Contains("\"port\":3389") && appJson.Contains("\"port\":3390"));
        Check("應用程式：target_attributes hostname", appJson.Contains("\"target_attributes\":{\"hostname\":[\"pc1\"]}"));
        var launcherJson = JsonSerializer.Serialize(CloudflareClient.LauncherPolicyBody("l", rdpForJson));
        sb.AppendLine("INFO  LauncherPolicyBody = " + launcherJson);
        Check("App Launcher 政策：同一批 Email、不要求 MFA", launcherJson.Contains("a@example.com") && !launcherJson.Contains("mfa_config"));
        var prev2 = new RdpConfig { LauncherAppId = "la", LauncherPolicyId = "lp" };
        var cur2 = new RdpConfig();
        RdpDeployer.CarryOverIds(cur2, prev2);
        Check("CarryOverIds 帶入 App Launcher ID", cur2.LauncherAppId == "la" && cur2.LauncherPolicyId == "lp");

        // ---- CIDR 相減（WARP Split Tunnel） ----
        Cidr.TryParse("192.168.0.0/16", out var lan);
        Cidr.TryParse("192.168.5.200", out var host);
        var pieces = lan.Subtract(host);
        var hostIp = host.Network;
        Check("CIDR：/16 減 /32 得到 16 段", pieces.Count == 16, string.Join(" ", pieces.Take(4)) + " …");
        Check("CIDR：結果不包含該 IP", pieces.All(p => !p.Contains(hostIp)));
        Check("CIDR：結果都在原網段內", pieces.All(lan.Contains));
        var total = pieces.Sum(p => Math.Pow(2, 32 - p.Prefix)) + 1;
        Check("CIDR：數量加起來等於原網段", Math.Abs(total - Math.Pow(2, 16)) < 0.5, $"{total}");
        Cidr.TryParse("10.0.0.0/8", out var other);
        Check("CIDR：不包含時原樣回傳", other.Subtract(host) is [var only] && only == other);

        // ---- 連線方式與舊設定檔相容 ----
        var oldJson = "{\"Enabled\":true,\"Hostname\":\"rdp.example.com\",\"AllowedEmails\":[\"a@example.com\"],\"Machines\":[{\"Name\":\"pc1\",\"Ip\":\"192.168.1.10\"}]}";
        var oldCfg = JsonSerializer.Deserialize<RdpConfig>(oldJson)!;
        Check("舊設定檔沒有 BrowserEnabled 時為 true", oldCfg.BrowserEnabled && !oldCfg.WarpEnabled);
        Throws("兩種連線方式都沒勾 → 錯誤", () => { var r = Good(); r.BrowserEnabled = false; r.WarpEnabled = false; Cfg(r).Validate(); }, "連線方式");
        try
        {
            var r = Good(); r.BrowserEnabled = false; r.WarpEnabled = true; r.Hostname = "";
            Cfg(r).Validate();
            Check("只用 WARP 時不需要瀏覽器網址", true);
        }
        catch (InvalidOperationException ex) { Check("只用 WARP 時不需要瀏覽器網址", false, ex.Message); }

        // ---- 連線代碼 ----
        var codeSrc = Good();
        codeSrc.AuthDomain = "myteam.cloudflareaccess.com";
        var code = ClientConfig.FromRdp(codeSrc).ToCode();
        var parsed = ClientConfig.TryParse("前面的說明文字\n  連線代碼：" + code + "\n後面的文字");
        Check("連線代碼：TeamName 取自 AuthDomain", codeSrc.TeamName == "myteam");
        Check("連線代碼：可從整段文字中解析", parsed is not null && parsed.Team == "myteam" && parsed.Machines.Count == 2
                                       && parsed.Machines[1].Ip == "192.168.1.11" && parsed.Machines[1].Port == 3390, code);
        Check("連線代碼：無效代碼回傳 null", ClientConfig.TryParse("CFT1:not-base64!!") is null && ClientConfig.TryParse("hello") is null);

        // ---- 連線代碼 v1.4：2FA 間隔、舊代碼相容、合併 ----
        var parsedS = ClientConfig.TryParse(code);
        Check("連線代碼：含 2FA 驗證間隔", parsedS?.MfaSessionDuration == "8h");
        var oldCode = "CFT1:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"t\":\"myteam\",\"m\":[[\"pc1\",\"192.168.1.10\",\"3389\"]]}"));
        var oldParsed = ClientConfig.TryParse(oldCode);
        Check("連線代碼：舊版（沒有 s）仍可解析", oldParsed is not null && oldParsed.MfaSessionDuration is null && oldParsed.Machines.Count == 1);

        var mine = ClientConfig.TryParse(code)!;
        mine.Machines[0].UserName = @"PC1\me";
        mine.Machines[0].LastConnectedAt = new DateTime(2026, 1, 1);
        var incomingCfg = new ClientConfig
        {
            Team = "MyTeam",
            MfaSessionDuration = "1h",
            Machines =
            [
                new ClientMachine { Name = "pc1-renamed", Ip = "192.168.1.10", Port = 3389 },
                new ClientMachine { Name = "pc3", Ip = "192.168.1.30", Port = 3389 },
            ],
        };
        var merged = mine.Merge(incomingCfg, out var addedN, out var updatedN);
        Check("合併：同組織（不分大小寫）新增 1、更新 1", merged && addedN == 1 && updatedN == 1, $"added={addedN} updated={updatedN}");
        Check("合併：保留使用者自訂帳號與時間", mine.Machines[0].Name == "pc1-renamed" && mine.Machines[0].UserName == @"PC1\me"
                                         && mine.Machines[0].LastConnectedAt == new DateTime(2026, 1, 1));
        Check("合併：更新 2FA 間隔", mine.MfaSessionDuration == "1h");
        Check("合併：不同組織回傳 false 且不修改", !mine.Merge(new ClientConfig { Team = "other", Machines = [new ClientMachine { Ip = "1.2.3.4" }] }, out _, out _)
                                          && mine.Machines.Count == 3);

        // ---- 2FA 狀態估算 ----
        var now = new DateTime(2026, 11, 14, 12, 0, 0);
        Check("2FA：0m 為每次都要驗證", TwoFactorStatus.Compute("0m", now.AddHours(-1), now).State == TwoFactorState.EveryTime);
        var valid = TwoFactorStatus.Compute("8h", now.AddHours(-2), now);
        Check("2FA：8 小時內有效並算出到期時間", valid.State == TwoFactorState.Valid && valid.ExpiresAt == now.AddHours(6), valid.Describe(now));
        Check("2FA：超過間隔為已過期", TwoFactorStatus.Compute("1h", now.AddHours(-2), now).State == TwoFactorState.Expired);
        Check("2FA：沒有記錄時為未知", TwoFactorStatus.Compute("8h", null, now).State == TwoFactorState.Unknown);
        Check("2FA：舊代碼沒有間隔時顯示依管理者設定", TwoFactorStatus.Compute(null, now, now).IntervalText == "依管理者設定");
        Check("2FA：間隔文字", TwoFactorStatus.Compute("24h", now, now).IntervalText == "每 24 小時驗證一次"
                            && TwoFactorStatus.Compute("30m", now, now).IntervalText == "每 30 分鐘驗證一次");

        // ---- v1.5.0：密碼規則、發行商、多地點共用、交還畫面 ----
        Check("密碼：8 個字元以上即可（不限字元種類）", UI.HostAccountPanel.ValidatePassword("abcdefgh", "abcdefgh") is null);
        Check("密碼：少於 8 個字元 → 錯誤", UI.HostAccountPanel.ValidatePassword("abc1234", "abc1234") is not null);
        Check("密碼：兩次不一樣 → 錯誤", UI.HostAccountPanel.ValidatePassword("abcdefgh", "abcdefgi") is not null);
        Check("發行商：鴻森智能科技", SelfInstaller.Publisher.Contains("鴻森智能科技") && SelfInstaller.Publisher.Contains("HONGSEN"), SelfInstaller.Publisher);
        Check("交還畫面：可判斷是否為遠端工作階段", true, ConsoleSession.IsRemoteSession ? "目前是遠端工作階段" : "目前是本機工作階段");

        var oldOwned = JsonSerializer.Deserialize<RdpConfig>("{\"LauncherAppId\":\"la\",\"WarpEnrollAppId\":\"wa\"}")!;
        Check("多地點：舊設定檔的 App Launcher 視為自己建立", oldOwned.LauncherOwned && oldOwned.WarpEnrollOwned);
        var sharedPrev = new RdpConfig { LauncherAppId = "shared", LauncherOwned = false, WarpEnrollAppId = "wshared", WarpEnrollOwned = false };
        var sharedCur = new RdpConfig();
        RdpDeployer.CarryOverIds(sharedCur, sharedPrev);
        Check("多地點：CarryOverIds 帶入共用狀態", sharedCur.LauncherAppId == "shared" && !sharedCur.LauncherOwned
                                              && sharedCur.WarpEnrollAppId == "wshared" && !sharedCur.WarpEnrollOwned);
        var appJson2 = System.Text.Json.Nodes.JsonNode.Parse(
            "{\"id\":\"a1\",\"aud\":\"x\",\"created_at\":\"t\",\"type\":\"app_launcher\",\"domain\":\"team.cloudflareaccess.com\"," +
            "\"landing_page_design\":{\"title\":\"Hi\"},\"scim_config\":null,\"policies\":[{\"id\":\"p1\",\"precedence\":1}]}")!.AsObject();
        var withPol = CloudflareClient.WithPolicies(appJson2, ["p1", "p2"]).ToJsonString();
        Check("多地點：只換政策，保留其他設定、移除唯讀欄位與空值",
            withPol.Contains("\"landing_page_design\"") && withPol.Contains("\"type\":\"app_launcher\"") &&
            !withPol.Contains("\"id\":\"a1\"") && !withPol.Contains("\"aud\"") && !withPol.Contains("scim_config") &&
            withPol.Contains("{\"id\":\"p1\",\"precedence\":1},{\"id\":\"p2\",\"precedence\":2}"), withPol);

        // ---- .rdp 檔 ----
        Check(".rdp：有預設帳號時寫入 username",
            WarpClient.RdpFileContent(new ClientMachine { Ip = "1.2.3.4", UserName = @"PC\u" }).Contains(@"username:s:PC\u"));
        Check(".rdp：沒有預設帳號時不寫 username",
            !WarpClient.RdpFileContent(new ClientMachine { Ip = "1.2.3.4" }).Contains("username:s:"));
        var rdpFile = WarpClient.RdpFileContent(new ClientMachine { Name = "pc", Ip = "192.168.1.10", Port = 3389 });
        Check(".rdp：位址與詢問帳密", rdpFile.Contains("full address:s:192.168.1.10:3389") && rdpFile.Contains("prompt for credentials:i:1"));

        // ---- WARP 的 JSON ----
        codeSrc.VnetId = "vnet1";
        var warpPolicyJson = JsonSerializer.Serialize(CloudflareClient.WarpPolicyBody("w", codeSrc));
        var privateAppJson = JsonSerializer.Serialize(CloudflareClient.PrivateRdpAppBody("a", codeSrc, "POL"));
        sb.AppendLine("INFO  WarpPolicyBody = " + warpPolicyJson);
        sb.AppendLine("INFO  PrivateRdpAppBody = " + privateAppJson);
        Check("WARP 政策：有 mfa_config、沒有 connection_rules", warpPolicyJson.Contains("\"mfa_config\"") && !warpPolicyJson.Contains("connection_rules"));
        Check("WARP 應用程式：private destination", privateAppJson.Contains("\"type\":\"private\",\"cidr\":\"192.168.1.10/32\",\"port_range\":\"3389\",\"l4_protocol\":\"tcp\",\"vnet_id\":\"vnet1\""));

        // ---- 官方 WARP 安裝程式網址（只用 HEAD，不下載） ----
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(20) };
            using var req = new HttpRequestMessage(HttpMethod.Head, WarpClient.DownloadUrl);
            using var resp = http.Send(req);
            var name = resp.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "";
            Check("WARP 下載網址回應 200 且為 .msi", resp.IsSuccessStatusCode && name.EndsWith(".msi"), $"{(int)resp.StatusCode} {name}");
        }
        catch (HttpRequestException ex) { Check("WARP 下載網址回應 200 且為 .msi", false, ex.Message); }

        // ---- 無效 Token 呼叫新 API ----
        using (var cf = new CloudflareClient("invalid-token-selftest"))
        {
            try
            {
                cf.GetAccessOrganizationAsync("00000000000000000000000000000000").GetAwaiter().GetResult();
                Check("無效 Token 會拋出 CloudflareApiException", false, "沒有拋出例外");
            }
            catch (CloudflareApiException ex)
            {
                Check("無效 Token 會拋出 CloudflareApiException", ex.StatusCode is >= 400 and < 500, $"HTTP {ex.StatusCode}: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                Check("無效 Token 會拋出 CloudflareApiException", false, "網路錯誤：" + ex.Message);
            }

            // 帳號 ID 格式錯誤（Cloudflare 回 404 + 7003）不能被當成「尚未啟用 Zero Trust」
            try
            {
                cf.GetAccessOrganizationAsync("bad-id").GetAwaiter().GetResult();
                Check("ID 格式錯誤（7003）不會被當成資源不存在", false, "回傳 null");
            }
            catch (CloudflareApiException ex)
            {
                Check("ID 格式錯誤（7003）不會被當成資源不存在", ex.Codes.Contains("7003"), ex.Message);
            }
        }

        sb.AppendLine($"\n結果：{pass} 通過，{fail} 失敗");
        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
    }
}
#endif
