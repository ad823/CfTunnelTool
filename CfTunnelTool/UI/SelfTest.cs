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
        LogBox.PersistToFile = false; // 自我測試不寫入真正的操作紀錄
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

        // ---- v1.6.0：獨立虛擬網路（不同地點相同 IP） ----
        var siteA = Good();
        siteA.AuthDomain = "myteam.cloudflareaccess.com";
        siteA.UseOwnVnet = true; siteA.VnetId = "vnetA"; siteA.VnetName = "factory-a"; siteA.SiteName = "factory-a";
        var siteB = Good();
        siteB.AuthDomain = "myteam.cloudflareaccess.com";
        siteB.UseOwnVnet = true; siteB.VnetId = "vnetB"; siteB.VnetName = "factory-b"; siteB.SiteName = "factory-b";
        Check("虛擬網路：獨立時目標名稱加上地點", siteA.TargetName(siteA.Machines[0]) == "factory-a-pc1");
        var underscoreSite = Good();
        underscoreSite.UseOwnVnet = true; underscoreSite.SiteName = "ApiServer_1F_test";
        underscoreSite.Machines[0].Name = "HS";
        Check("虛擬網路：目標名稱符合 RFC 1123（底線換成 -）", underscoreSite.TargetName(underscoreSite.Machines[0]) == "ApiServer-1F-test-HS",
            underscoreSite.TargetName(underscoreSite.Machines[0]));
        Check("主機名稱：其他不合法字元與長度", RdpConfig.ToHostName("_a__b_") == "a-b" && RdpConfig.ToHostName("___") == "pc"
                                            && RdpConfig.ToHostName(new string('x', 80)).Length == 63 && RdpConfig.ToHostName("pc.1") == "pc.1");
        var defaultSite = Good(); defaultSite.SiteName = "x";
        Check("虛擬網路：預設時目標名稱不變（相容舊版）", defaultSite.TargetName(defaultSite.Machines[0]) == "pc1");
        var appA = JsonSerializer.Serialize(CloudflareClient.RdpAppBody("a", siteA, "P"));
        Check("虛擬網路：瀏覽器應用程式用地點化的目標名稱", appA.Contains("\"hostname\":[\"factory-a-pc1\"]"));

        var codeA = ClientConfig.TryParse(ClientConfig.FromRdp(siteA).ToCode())!;
        var codeB = ClientConfig.TryParse(ClientConfig.FromRdp(siteB).ToCode())!;
        Check("虛擬網路：連線代碼帶地點", codeA.Machines[0].VnetId == "vnetA" && codeA.Machines[0].VnetName == "factory-a");
        var mergedSites = codeA.Merge(codeB, out var addB, out _);
        Check("虛擬網路：不同地點的相同 IP 合併後是不同電腦", mergedSites && addB == 2 && codeA.Machines.Count == 4,
            $"added={addB} total={codeA.Machines.Count}");
        Check("虛擬網路：Sites() 列出兩個地點", codeA.Sites().Count == 2);
        var legacy = ClientConfig.TryParse(oldCode)!; // 舊版代碼沒有地點
        var legacyMerge = legacy.Merge(new ClientConfig
        {
            Team = "myteam",
            Machines = [new ClientMachine { Name = "pc1", Ip = "192.168.1.10", Port = 3389, VnetId = "vdef", VnetName = null }],
        }, out var addL, out var updL);
        Check("虛擬網路：舊版清單匯入新代碼時補上地點、不重複", legacyMerge && addL == 0 && updL == 1 && legacy.Machines[0].VnetId == "vdef");

        // ---- v1.7.0：紀錄（全部寫在暫存資料夾，不碰真正的紀錄） ----
        var realRoot = Core.Logs.LogStore.Root;
        var tempRoot = Path.Combine(Path.GetTempPath(), "cftunnel-selftest-logs-" + Guid.NewGuid().ToString("N"));
        Core.Logs.LogStore.Root = tempRoot;
        try
        {
            var t0 = new DateTime(2026, 10, 31, 23, 50, 0);
            Core.Logs.LogStore.Append(Core.Logs.LogKinds.Operation, new[]
            {
                new Core.Logs.OperationRecord(t0, "資訊", "十月最後", "PC"),
                new Core.Logs.OperationRecord(t0.AddMinutes(20), "錯誤", "十一月第一筆, 含逗號", "PC"),
            }, r => r.Time);
            Check("紀錄：依月份分檔", Directory.GetFiles(Path.Combine(tempRoot, "operations"), "*.jsonl").Length == 2);
            var q = Core.Logs.LogStore.Query<Core.Logs.OperationRecord>(Core.Logs.LogKinds.Operation, t0.AddMinutes(-1), t0.AddHours(1), r => r.Time);
            Check("紀錄：跨月份查詢並依時間排序", q.Count == 2 && q[0].Message == "十月最後");
            var q2 = Core.Logs.LogStore.Query<Core.Logs.OperationRecord>(Core.Logs.LogKinds.Operation, t0.AddMinutes(10), t0.AddHours(1), r => r.Time);
            Check("紀錄：時間範圍過濾", q2.Count == 1);
            var removedFiles = Core.Logs.LogStore.Cleanup(10, new DateTime(2026, 11, 20));
            Check("紀錄：清除超過保留天數的整月檔案", removedFiles == 1, $"removed={removedFiles}");

            var accessJson = System.Text.Json.Nodes.JsonNode.Parse(
                "{\"user_email\":\"a@x.com\",\"ip_address\":\"1.2.3.4\",\"app_domain\":\"rdp.x.com\",\"action\":\"login\"," +
                "\"allowed\":false,\"connection\":\"onetimepin\",\"created_at\":\"2026-11-14T10:00:00Z\",\"ray_id\":\"r1\"}")!;
            var login = Core.Logs.AccessLogCollector.Parse(accessJson);
            Check("登入紀錄：解析欄位", login is { Email: "a@x.com", Allowed: false, RayId: "r1", App: "rdp.x.com" });
            var deduped = Core.Logs.AccessLogCollector.Dedupe([login!, login!], [login!]);
            Check("登入紀錄：已存在的與重複的都會去除", deduped.Count == 0);

            Core.Logs.RdpEventRecord Ev(int min, int id, int session, string user = "PC\\u") =>
                new(t0.AddMinutes(min), id, "PC", user, session, "192.168.5.10", min);
            var sessions = Core.Logs.RdpEventCollector.BuildSessions(
            [
                Ev(0, 21, 2), Ev(30, 24, 2), Ev(40, 25, 2), Ev(70, 23, 2),
                Ev(80, 21, 3),
                new(t0.AddMinutes(5), 4625, "PC", "PC\\bad", 0, "192.168.5.10", 999),
            ]);
            var s2 = sessions.Where(s => s.SessionId == 2).OrderBy(s => s.Start).ToList();
            Check("使用紀錄：21→24、25→23 組成兩段", s2.Count == 2 && s2[0].Status == "已中斷" && s2[1].Status == "已登出"
                                               && s2[0].Duration == TimeSpan.FromMinutes(30) && s2[1].Duration == TimeSpan.FromMinutes(30));
            Check("使用紀錄：只有開始的是使用中", sessions.Any(s => s.SessionId == 3 && s.Status == "使用中" && s.End is null));
            Check("使用紀錄：4625 為登入失敗", sessions.Any(s => s.Status == "登入失敗" && s.User == "PC\\bad"));

            var csv = Core.Logs.LogStore.ToCsv([["時間", "訊息"], ["t", "a,b \"c\"\n第二行"]]);
            Check("CSV：逗號、引號、換行跳脫", csv == "時間,訊息\r\nt,\"a,b \"\"c\"\"\n第二行\"\r\n", csv.Replace("\r", "\\r").Replace("\n", "\\n"));
            var csvPath = Path.Combine(tempRoot, "x.csv");
            Core.Logs.LogStore.WriteCsv(csvPath, [["a"]]);
            var bytes = File.ReadAllBytes(csvPath);
            Check("CSV：含 UTF-8 BOM（Excel 不亂碼）", bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

            var fields = Core.Logs.NetworkLogCollector.ChooseFields(
                ["AccountGatewayL4SessionsAdaptiveGroups", "AccountGatewayL4SessionsAdaptiveGroupsDimensions"],
                ["datetime", "email", "destinationIp", "destinationPort", "transport"],
                ["bytesSent", "bytesReceived"],
                ["datetime_geq", "datetime_lt"], hasCount: true);
            Check("網路：從 schema 選出欄位", fields is { TimeField: "datetime", UserField: "email", DestIpField: "destinationIp",
                                                  DestPortField: "destinationPort", SentField: "bytesSent", ReceivedField: "bytesReceived" });
            Check("網路：沒有資料集時回傳 null", Core.Logs.NetworkLogCollector.ChooseFields(["Other"], [], [], [], false) is null);
            var gql = Core.Logs.NetworkLogCollector.BuildQuery(fields!);
            Check("網路：GraphQL 查詢含欄位與時間篩選", gql.Contains("datetime_geq: $since") && gql.Contains("sum { bytesSent bytesReceived }"));
            var gqlData = System.Text.Json.Nodes.JsonNode.Parse(
                "{\"viewer\":{\"accounts\":[{\"rows\":[" +
                "{\"count\":3,\"dimensions\":{\"datetime\":\"2026-11-14T10:00:00Z\",\"email\":\"a@x.com\",\"destinationIp\":\"192.168.5.200\",\"destinationPort\":3389},\"sum\":{\"bytesSent\":1000,\"bytesReceived\":5000}}," +
                "{\"count\":1,\"dimensions\":{\"datetime\":\"2026-11-14T10:01:00Z\",\"email\":\"a@x.com\",\"destinationIp\":\"8.8.8.8\",\"destinationPort\":443},\"sum\":{\"bytesSent\":1,\"bytesReceived\":1}}" +
                "]}]}}")!;
            var netRows = Core.Logs.NetworkLogCollector.ParseRows(gqlData, fields!, new HashSet<string> { "192.168.5.200" });
            Check("網路：只保留被開放電腦的流量", netRows.Count == 1 && netRows[0].Destination == "192.168.5.200:3389"
                                             && netRows[0].BytesReceived == 5000 && netRows[0].Count == 3);

            var realEvents = Core.Logs.RdpEventCollector.Collect(new Core.Logs.LogState());
            Check("使用紀錄：可讀取本機 Windows 事件（只讀）", true, $"讀到 {realEvents} 筆遠端桌面事件");
        }
        finally
        {
            Core.Logs.LogStore.Root = realRoot;
            try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

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

        // ---- Email 驗證碼（全域登入）與 2FA 分開 ----
        var everyTime = codeSrc.Clone();
        everyTime.MfaSessionDuration = "0m";
        var everyPrivate = JsonSerializer.Serialize(CloudflareClient.PrivateRdpAppBody("a", everyTime, "POL"));
        var everyBrowser = JsonSerializer.Serialize(CloudflareClient.RdpAppBody("b", everyTime, "POL"));
        Check("2FA 每次：WARP 應用程式工作階段 5 分鐘＋應用程式層 mfa_config",
            everyPrivate.Contains("\"session_duration\":\"5m\"") && everyPrivate.Contains("\"mfa_config\":{\"mfa_disabled\":false") &&
            everyPrivate.Contains("\"session_duration\":\"0m\""), everyPrivate);
        Check("2FA 每次：瀏覽器應用程式工作階段 5 分鐘＋mfa_config",
            everyBrowser.Contains("\"session_duration\":\"5m\"") && everyBrowser.Contains("\"mfa_config\""));
        everyTime.MfaSessionDuration = "8h";
        Check("2FA 8 小時：應用程式工作階段 8 小時", JsonSerializer.Serialize(CloudflareClient.PrivateRdpAppBody("a", everyTime, "POL")).Contains("\"session_duration\":\"8h\""));
        Check("舊設定檔沒有 Email 驗證碼時間 → 預設 8 小時",
            JsonSerializer.Deserialize<RdpConfig>("{\"Enabled\":true}")!.LoginSessionDuration == "8h");

        var orgRaw = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(
            "{\"auth_domain\":\"t.cloudflareaccess.com\",\"name\":\"t\",\"session_duration\":\"24h\",\"created_at\":\"x\"," +
            "\"mfa_config\":{\"allowed_authenticators\":[\"totp\"],\"session_duration\":\"24h\"}}")!;
        var org = new AccessOrganization("t.cloudflareaccess.com", orgRaw);
        var orgBody = CloudflareClient.OrganizationBody(org, ["totp"], "8h");
        Check("組織：全域登入時間改為 8h、移除唯讀欄位",
            orgBody?["session_duration"]?.ToString() == "8h" && orgBody["created_at"] is null &&
            orgBody["mfa_config"]?["allowed_authenticators"]?.AsArray().Count == 1, orgBody?.ToJsonString() ?? "null");
        orgRaw["session_duration"] = "8h";
        Check("組織：已經是 8h 且 2FA 已允許 → 不需要更新", CloudflareClient.OrganizationBody(org, ["totp"], "8h") is null);
        Check("組織：新增 2FA 方式時仍保留 8h", CloudflareClient.OrganizationBody(org, ["totp", "security_key"], "8h")?["session_duration"]?.ToString() == "8h");

        // ---- v1.7.2：WARP 裝置註冊也要 2FA ----
        Check("註冊 2FA：WARP 政策判定為要求 MFA",
            CloudflareClient.PolicyRequiresMfa(System.Text.Json.Nodes.JsonNode.Parse(warpPolicyJson)));
        Check("註冊 2FA：App Launcher 政策判定為不要求 MFA",
            !CloudflareClient.PolicyRequiresMfa(System.Text.Json.Nodes.JsonNode.Parse(launcherJson)));
        Check("註冊 2FA：mfa_disabled=true 判定為不要求 MFA",
            !CloudflareClient.PolicyRequiresMfa(System.Text.Json.Nodes.JsonNode.Parse(
                "{\"mfa_config\":{\"mfa_disabled\":true,\"allowed_authenticators\":[\"totp\"]}}")));
        Check("註冊 2FA：沒有允許的驗證方式判定為不要求 MFA",
            !CloudflareClient.PolicyRequiresMfa(System.Text.Json.Nodes.JsonNode.Parse(
                "{\"mfa_config\":{\"mfa_disabled\":false,\"allowed_authenticators\":[]}}")));

        // ---- v1.7.3：Gateway 規則只開放遠端桌面連接埠 ----
        var gwSrc = Good(); // pc1 192.168.1.10:3389、pc2 192.168.1.11:3390
        gwSrc.Machines.Add(new RdpMachine { Name = "pc3", Ip = "192.168.1.12" });
        var gwTraffic = CloudflareClient.GatewayBlockTraffic(gwSrc);
        sb.AppendLine("INFO  GatewayBlockTraffic = " + gwTraffic);
        Check("Gateway：依連接埠分組、封鎖其他連接埠",
            gwTraffic == "(net.dst.ip in {192.168.1.10/32 192.168.1.12/32} and not(net.dst.port in {3389 47389}))" +
                         " or (net.dst.ip in {192.168.1.11/32} and not(net.dst.port in {3390 47389}))", gwTraffic);
        Check("Gateway：預設虛擬網路不加 vnet 條件", !gwTraffic.Contains("net.vnet_id"));
        gwSrc.UseOwnVnet = true;
        gwSrc.VnetId = "v-1";
        var gwVnet = CloudflareClient.GatewayBlockTraffic(gwSrc);
        Check("Gateway：獨立虛擬網路限定在該虛擬網路",
            gwVnet.StartsWith("net.vnet_id == \"v-1\" and ((net.dst.ip in {") && gwVnet.EndsWith("))"), gwVnet);
        var gwJson = JsonSerializer.Serialize(CloudflareClient.GatewayRuleBody("g", gwSrc));
        sb.AppendLine("INFO  GatewayRuleBody = " + gwJson);
        Check("Gateway：block、l4、啟用", gwJson.Contains("\"action\":\"block\"") && gwJson.Contains("\"enabled\":true") &&
                                          gwJson.Contains("\"filters\":[\"l4\"]"), gwJson);
        Check("Gateway：舊設定檔的 Gateway 規則 ID 會帶入",
            new Func<bool>(() => { var cur = Good(); RdpDeployer.CarryOverIds(cur, new RdpConfig { GatewayRuleId = "gw" }); return cur.GatewayRuleId == "gw"; })());

        // ---- v1.7.4：遠端桌面防火牆只允許內部網路（只讀取，不修改本機防火牆） ----
        var fwScript = RdpHost.OpenRulesScript(3389);
        Check("防火牆：同時找內建規則群組與開放同連接埠的規則",
            fwScript.Contains("@FirewallAPI.dll,-28752") && fwScript.Contains("-contains '3389'"), fwScript);
        Check("防火牆：只挑啟用、輸入、允許且來源為任何的規則",
            fwScript.Contains("'Inbound'") && fwScript.Contains("'Allow'") && fwScript.Contains("-contains 'Any'"));
        Check("防火牆：允許來源不含任何公網位址",
            !RdpHost.InternalAddresses.Contains("Any") && RdpHost.InternalAddresses.Contains("LocalSubnet") &&
            RdpHost.InternalAddresses.Contains("192.168.0.0/16"));
        var fwOpen = RdpHost.GetOpenFirewallRules(status.Port);
        sb.AppendLine("INFO  GetOpenFirewallRules = " + (fwOpen is null ? "null" : string.Join(" | ", fwOpen.Select(r => $"{r.Name}={r.DisplayName}"))));
        Check("防火牆：讀得到本機規則", fwOpen is not null);

        // ---- v1.8.0：NLA 強制開啟與群組原則（只讀取，不修改本機設定） ----
        RdpHostStatus Nla(bool local, int layer, int? nlaPolicy, int? layerPolicy) =>
            new(true, local, layer, 3389, "Professional", nlaPolicy, layerPolicy);
        Check("NLA：沒有群組原則時依本機設定", Nla(true, 2, null, null).NlaEffective && !Nla(false, 2, null, null).NlaEffective);
        Check("NLA：群組原則強制開啟優先於本機設定", Nla(false, 2, 1, null).NlaEffective && !Nla(false, 2, 1, null).NlaBlockedByPolicy);
        Check("NLA：群組原則強制關閉會被偵測", !Nla(true, 2, 0, null).NlaEffective && Nla(true, 2, 0, null).NlaBlockedByPolicy);
        Check("NLA：舊 RDP 安全性層無法使用 NLA", !Nla(true, 0, null, null).NlaEffective && !Nla(true, 0, null, null).NlaBlockedByPolicy);
        Check("NLA：群組原則強制舊 RDP 安全性層視為封鎖", Nla(true, 2, null, 0).NlaBlockedByPolicy);
        var nlaNow = RdpHost.GetStatus();
        sb.AppendLine($"INFO  本機 NLA：Effective={nlaNow.NlaEffective} Local={nlaNow.NlaRequired} Policy={nlaNow.NlaPolicy?.ToString() ?? "未設定"}");

        // ---- v1.10.1：Entra ID 電腦不自動開啟 NLA（只讀取，不修改本機設定） ----
        const string dsregEntra = """
            +----------------------------------------------------------------------+
            | Device State                                                         |
            +----------------------------------------------------------------------+

                         AzureAdJoined : YES
                      EnterpriseJoined : NO
                          DomainJoined : NO
                           Device Name : PC01
            """;
        const string dsregHybrid = "  AzureAdJoined : YES\r\n  EnterpriseJoined : NO\r\n  DomainJoined : YES\r\n  DomainName : CORP\r\n";
        const string dsregNone = "  AzureAdJoined : NO\r\n  EnterpriseJoined : NO\r\n  DomainJoined : NO\r\n";
        Check("Entra：dsregcmd 只加入 Entra ID", RdpHost.ParseDsregStatus(dsregEntra) == new JoinState(true, false) &&
                                                 RdpHost.ParseDsregStatus(dsregEntra).EntraOnly);
        Check("Entra：dsregcmd 混合加入不算例外", RdpHost.ParseDsregStatus(dsregHybrid) == new JoinState(true, true) &&
                                                 !RdpHost.ParseDsregStatus(dsregHybrid).EntraOnly);
        Check("Entra：dsregcmd 沒有加入", RdpHost.ParseDsregStatus(dsregNone) == new JoinState(false, false));
        Check("Entra：dsregcmd 空白或不認得的輸出視為沒有加入", RdpHost.ParseDsregStatus("") == new JoinState(false, false) &&
                                                          RdpHost.ParseDsregStatus("AzureAdJoinedX : YES") == new JoinState(false, false));
        Check("Entra：登錄檔 JoinInfo 有子機碼、沒有 AD 網域", RdpHost.JoinStateFrom(["ABCDEF0123"], 2).EntraOnly);
        Check("Entra：登錄檔 JoinInfo 有子機碼且加入 AD 網域是混合加入",
            RdpHost.JoinStateFrom(["ABCDEF0123"], RdpHost.NetSetupDomainName) == new JoinState(true, true));
        Check("Entra：登錄檔 JoinInfo 沒有子機碼", !RdpHost.JoinStateFrom([], 2).EntraJoined && !RdpHost.JoinStateFrom([""], 0).EntraJoined);
        var entraOnly = new JoinState(true, false);
        var notJoined = new JoinState(false, false);
        Check("Entra：只加入 Entra ID 時不自動開啟 NLA",
            RdpHost.PlanNla(Nla(false, 2, null, null), entraOnly) == NlaResult.SkippedEntra &&
            RdpHost.PlanNla(Nla(true, 2, null, null), entraOnly) == NlaResult.SkippedEntra &&
            RdpHost.PlanNla(Nla(true, 2, 0, null), entraOnly) == NlaResult.SkippedEntra);
        Check("Entra：沒有加入或混合加入時照常強制開啟 NLA",
            RdpHost.PlanNla(Nla(false, 2, null, null), notJoined) == NlaResult.Enabled &&
            RdpHost.PlanNla(Nla(false, 2, null, null), new JoinState(true, true)) == NlaResult.Enabled &&
            RdpHost.PlanNla(Nla(true, 2, null, null), notJoined) == NlaResult.AlreadyOn &&
            RdpHost.PlanNla(Nla(true, 2, 0, null), notJoined) == NlaResult.BlockedByPolicy);
        Check("Entra：說明文字提到瀏覽器需要關閉 NLA 與建議 WARP",
            RdpHost.EntraNlaHelp.Contains("Entra ID") && RdpHost.EntraNlaHelp.Contains("關閉 NLA") && RdpHost.EntraNlaHelp.Contains("WARP"));
        var joinNow = RdpHost.GetJoinState();
        sb.AppendLine($"INFO  本機加入狀態（登錄檔）：{joinNow}");
        var dsregExe = Path.Combine(Environment.SystemDirectory, "dsregcmd.exe");
        var dsreg = File.Exists(dsregExe) ? Cloudflared.Run(dsregExe, "/status") : new Cloudflared.RunResult(-1, "");
        if (dsreg.ExitCode == 0 && dsreg.Output.Contains("AzureAdJoined", StringComparison.OrdinalIgnoreCase))
        {
            var dsregNow = RdpHost.ParseDsregStatus(dsreg.Output);
            Check("Entra：登錄檔判斷與 dsregcmd /status 一致", dsregNow == joinNow, $"dsregcmd={dsregNow} 登錄檔={joinNow}");
        }
        else
        {
            sb.AppendLine($"INFO  dsregcmd /status 無法執行（ExitCode={dsreg.ExitCode}），略過一致性比對");
        }

        // ---- v1.8.0：管理帳號密碼（暫存路徑，不影響本機設定） ----
        var authPath = Path.Combine(Path.GetTempPath(), $"cftt-admin-{Guid.NewGuid():N}.dat");
        try
        {
            var auth = new AdminAuth(authPath);
            Check("管理密碼：尚未設定時 Exists 為 false", !auth.Exists);
            Check("管理密碼：規則 — 太短", AdminAuth.ValidateNew("admin", "short1", "short1") is not null);
            Check("管理密碼：規則 — 兩次不同", AdminAuth.ValidateNew("admin", "LongPassword1", "LongPassword2") is not null);
            Check("管理密碼：規則 — 不能和帳號相同", AdminAuth.ValidateNew("administrator1", "Administrator1", "Administrator1") is not null);
            Check("管理密碼：規則 — 合格", AdminAuth.ValidateNew("admin", "LongPassword1", "LongPassword1") is null);
            auth.Create("admin", "LongPassword1");
            var raw = File.ReadAllText(authPath);
            Check("管理密碼：檔案不含明文密碼", auth.Exists && !raw.Contains("LongPassword1") && auth.StoredHash is { Length: > 20 });
            Check("管理密碼：正確密碼可登入（帳號不分大小寫）", auth.Verify("ADMIN", "LongPassword1").Ok);
            Check("管理密碼：帳號錯誤不能登入", !auth.Verify("other", "LongPassword1").Ok);
            var t0 = DateTime.Now;
            AdminLoginResult last = new(false, 0, TimeSpan.Zero);
            for (var i = 0; i < AdminAuth.MaxFailures - 1; i++) last = auth.Verify("admin", "wrong", t0); // 加上前一次共 5 次
            Check("管理密碼：連續失敗 5 次鎖定", last.Locked, $"remaining={last.Remaining}");
            Check("管理密碼：鎖定期間正確密碼也不能登入", auth.Verify("admin", "LongPassword1", t0.AddMinutes(1)).Locked);
            Check("管理密碼：鎖定期滿後可以登入", auth.Verify("admin", "LongPassword1", t0.AddMinutes(6)).Ok);
            var again = auth.Verify("admin", "wrong", t0.AddMinutes(7));
            Check("管理密碼：成功登入後失敗次數重新計算", !again.Ok && again.Remaining == AdminAuth.MaxFailures - 1, $"remaining={again.Remaining}");
            File.WriteAllText(authPath, "{broken");
            Check("管理密碼：檔案損毀視為未設定", !auth.Exists);
        }
        finally
        {
            try { File.Delete(authPath); } catch (IOException) { }
        }

        // ---- v1.9.0：桌面捷徑經過程式（--connect），連線前先檢查驗證 ----
        var pc = ConnectArgs.Parse(["--connect", "office pc"]);
        Check("捷徑參數：解析名稱（含空白）", pc is { Name: "office pc", VnetId: null }, pc?.ToString() ?? "null");
        var pcSite = ConnectArgs.Parse(["--connect", "office-pc", "--site", "a1b2"]);
        Check("捷徑參數：解析地點", pcSite is { Name: "office-pc", VnetId: "a1b2" }, pcSite?.ToString() ?? "null");
        Check("捷徑參數：缺少名稱時不解析",
            ConnectArgs.Parse(["--connect"]) is null && ConnectArgs.Parse(["--connect", "--site", "x"]) is null &&
            ConnectArgs.Parse(["--connect", "  "]) is null);
        Check("捷徑參數：沒有 --connect 時不進入捷徑模式", ConnectArgs.Parse([]) is null && ConnectArgs.Parse(["--uninstall"]) is null);
        var siteMachine = new ClientMachine { Name = "office-pc", Ip = "192.168.10.20", VnetId = "v2", VnetName = "factory/b" };
        var plainMachine = new ClientMachine { Name = "office-pc", Ip = "192.168.10.20" };
        Check("捷徑參數：建立的參數含名稱與地點",
            ConnectArgs.Build(siteMachine) == "--connect \"office-pc\" --site v2" &&
            ConnectArgs.Build(plainMachine) == "--connect \"office-pc\"", ConnectArgs.Build(siteMachine));
        Check("捷徑參數：名稱裡的雙引號會被移除",
            ConnectArgs.Build(new ClientMachine { Name = "a\"b" }) == "--connect \"ab\"");
        var bothSites = new[] { plainMachine, siteMachine };
        Check("捷徑參數：依地點找到同名的電腦",
            ConnectArgs.Find(bothSites, new ConnectRequest("OFFICE-PC", "v2")) == siteMachine &&
            ConnectArgs.Find(bothSites, new ConnectRequest("office-pc", null)) == plainMachine &&
            ConnectArgs.Find(bothSites, new ConnectRequest("other", null)) is null);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Check("捷徑：桌面捷徑是 .lnk，含地點且替換非法字元",
            WarpClient.ShortcutPath(siteMachine) == Path.Combine(desktop, "遠端桌面 - office-pc（factory_b）.lnk"),
            WarpClient.ShortcutPath(siteMachine));
        Check("捷徑：舊版 .rdp 捷徑路徑（遷移用）",
            WarpClient.LegacyShortcutPath(plainMachine) == Path.Combine(desktop, "遠端桌面 - office-pc.rdp"));
        Check("捷徑：連線用的 .rdp 檔放在暫存資料夾，不在桌面",
            WarpClient.RdpFilePath(plainMachine).StartsWith(WarpClient.RdpTempDir, StringComparison.OrdinalIgnoreCase) &&
            !WarpClient.RdpFilePath(plainMachine).StartsWith(desktop, StringComparison.OrdinalIgnoreCase));
        Check("等候驗證：最多等 3 分鐘", AuthWaitForm.WaitLimit == TimeSpan.FromMinutes(3));

        // ---- v1.10.0：網址轉發支援 tcp://（例如 MySQL） ----
        static string? TcpError(string service)
        {
            var c = new ToolConfig { Domain = "example.com", TunnelName = "t",
                Routes = [new RouteConfig { Hostname = "db.example.com", Service = service }] };
            try { c.Validate(); return null; }
            catch (InvalidOperationException ex) { return ex.Message; }
        }
        var tcpCfg = new ToolConfig { Domain = "example.com", TunnelName = "t",
            Routes = [new RouteConfig { Hostname = "db.example.com", Service = " tcp://localhost:3306/ ", NoTlsVerify = true }] };
        tcpCfg.Validate();
        var tcpRoute = tcpCfg.Routes[0];
        Check("TCP：接受 tcp://主機:連接埠並整理格式、清掉自簽憑證",
            tcpRoute is { Service: "tcp://localhost:3306", NoTlsVerify: false, IsTcp: true, TcpPort: 3306 }, tcpRoute.Service);
        Check("TCP：接受區網 IP", TcpError("tcp://192.168.1.20:3306") is null);
        Check("TCP：沒有連接埠會被拒絕", TcpError("tcp://localhost") is { } e1 && e1.Contains("tcp://主機:連接埠"), TcpError("tcp://localhost") ?? "null");
        Check("TCP：帶路徑會被拒絕", TcpError("tcp://localhost:3306/db") is not null);
        Check("TCP：其他協定仍會被拒絕", TcpError("ftp://localhost:21") is { } e2 && e2.Contains("tcp://"));
        Check("TCP：http 網址不受影響", TcpError("http://localhost:8080") is null &&
            !new RouteConfig { Hostname = "w.example.com", Service = "http://localhost:8080" }.IsTcp);
        Check("TCP：顯示位址與使用者端指令",
            tcpRoute.PublicUrl == "tcp://db.example.com" &&
            tcpRoute.ClientCommand() == "cloudflared access tcp --hostname db.example.com --url 127.0.0.1:3306" &&
            tcpRoute.ClientCommand(13306).EndsWith("127.0.0.1:13306"), tcpRoute.ClientCommand());
        var ingressJson = JsonSerializer.Serialize(CloudflareClient.IngressRules(
            [tcpRoute, new RouteConfig { Hostname = "w.example.com", Service = "https://localhost:8443", NoTlsVerify = true }]));
        sb.AppendLine("INFO  IngressRules = " + ingressJson);
        Check("TCP：ingress 直接轉 tcp://、不帶 originRequest，最後是 404",
            ingressJson.StartsWith("[{\"hostname\":\"db.example.com\",\"service\":\"tcp://localhost:3306\"}") &&
            ingressJson.Contains("\"noTLSVerify\":true") && ingressJson.EndsWith("{\"service\":\"http_status:404\"}]"), ingressJson);
        Check("TCP：設定檔不寫入計算出來的欄位",
            !JsonSerializer.Serialize(tcpRoute).Contains("IsTcp") && !JsonSerializer.Serialize(tcpRoute).Contains("PublicUrl"));
        Check("TCP：給使用者的連線說明含指令與連接埠",
            Steps.DoneStep.TcpClientInfo(tcpRoute) is var info && info.Contains(tcpRoute.ClientCommand()) && info.Contains("127.0.0.1"));

        // ---- v1.11.0：API 網址關閉 Browser Integrity Check（避免 403 Error 1010） ----
        var bicCfg = new ToolConfig { Domain = "example.com", TunnelName = "Office-1", Routes =
        [
            new RouteConfig { Hostname = "api.example.com", Service = "http://localhost:5000", DisableBrowserCheck = true },
            new RouteConfig { Hostname = "db.example.com", Service = "tcp://localhost:3306", DisableBrowserCheck = true },
        ] };
        bicCfg.Validate();
        Check("API 用途：http 網址保留勾選、TCP 網址自動取消",
            bicCfg.Routes[0].DisableBrowserCheck && !bicCfg.Routes[1].DisableBrowserCheck);
        Check("API 用途：Clone 保留勾選", bicCfg.Routes[0].Clone().DisableBrowserCheck);
        var bicJson = JsonSerializer.Serialize(bicCfg.Routes[0]);
        Check("API 用途：設定檔寫入與讀回",
            bicJson.Contains("\"DisableBrowserCheck\":true") &&
            JsonSerializer.Deserialize<RouteConfig>(bicJson)!.DisableBrowserCheck &&
            !JsonSerializer.Deserialize<RouteConfig>("{\"Hostname\":\"a.example.com\",\"Service\":\"http://localhost\"}")!.DisableBrowserCheck, bicJson);
        Check("API 用途：規則 ref 依 Tunnel 名稱", CloudflareClient.BrowserCheckRuleRef("Office-1") == "cftunneltool_bic_office_1",
            CloudflareClient.BrowserCheckRuleRef("Office-1"));
        var bicExpr = CloudflareClient.BrowserCheckExpression(["b.example.com", "a.example.com"]);
        Check("API 用途：規則條件排序後列出網址", bicExpr == "(http.host in {\"a.example.com\" \"b.example.com\"})", bicExpr);
        var bicRule = CloudflareClient.BrowserCheckRule("Office-1", ["api.example.com"]).ToJsonString();
        sb.AppendLine("INFO  BrowserCheckRule = " + bicRule);
        Check("API 用途：規則內容 set_config、bic=false、帶 ref",
            bicRule.Contains("\"ref\":\"cftunneltool_bic_office_1\"") && bicRule.Contains("\"action\":\"set_config\"") &&
            bicRule.Contains("\"action_parameters\":{\"bic\":false}") && bicRule.Contains("\"enabled\":true"), bicRule);
        Check("API 用途：手動設定說明列出網址與權限",
            Deployer.BrowserCheckManualHint(["api.example.com"]) is var hint && hint.Contains("api.example.com") &&
            hint.Contains("Config Rules") && hint.Contains("1010"));

        // ---- v1.11.1：程式已開啟時再開一次，喚醒原本的視窗（用測試專用名稱，不影響正在執行的程式） ----
        var siMutex = $@"Local\CfTunnelTool.SelfTest.M.{Guid.NewGuid():N}";
        var siEvent = $@"Local\CfTunnelTool.SelfTest.E.{Guid.NewGuid():N}";
        Check("單一執行個體：沒有 mutex 時 IsRunning 為 false", !SingleInstance.IsRunning(siMutex));
        using (new Mutex(true, siMutex, out _))
            Check("單一執行個體：已有 mutex 時 IsRunning 為 true", SingleInstance.IsRunning(siMutex));
        Check("單一執行個體：找不到喚醒事件時回傳 OtherSession（才顯示提示）",
            SingleInstance.SignalExisting(siEvent) == ActivateResult.OtherSession);
        using (var signaled = new ManualResetEventSlim())
        using (SingleInstance.Listen(signaled.Set, siEvent))
        {
            Check("單一執行個體：有喚醒事件時回傳 Activated",
                SingleInstance.SignalExisting(siEvent) == ActivateResult.Activated);
            Check("單一執行個體：原本的程式收到通知", signaled.Wait(3000));
            signaled.Reset();
            Check("單一執行個體：第二次通知也收得到",
                SingleInstance.SignalExisting(siEvent) == ActivateResult.Activated && signaled.Wait(3000));

            using var opened = System.Threading.EventWaitHandleAcl.OpenExisting(siEvent,
                System.Security.AccessControl.EventWaitHandleRights.ReadPermissions);
            var authUsers = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null);
            var allowed = opened.GetAccessControl()
                .GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
                .Cast<System.Security.AccessControl.EventWaitHandleAccessRule>()
                .Where(r => r.IdentityReference.Equals(authUsers) && r.AccessControlType == System.Security.AccessControl.AccessControlType.Allow)
                .Aggregate((System.Security.AccessControl.EventWaitHandleRights)0, (a, r) => a | r.EventWaitHandleRights);
            Check("單一執行個體：一般權限的程式可以觸發喚醒事件（不能改權限）",
                allowed.HasFlag(System.Security.AccessControl.EventWaitHandleRights.Modify) &&
                !allowed.HasFlag(System.Security.AccessControl.EventWaitHandleRights.ChangePermissions), allowed.ToString());
        }
        Check("單一執行個體：程式結束後事件消失", SingleInstance.SignalExisting(siEvent) == ActivateResult.OtherSession);

        // ---- v1.12.0：使用狀態回報，避免搶連線 ----
        var localUsage = SessionStatus.QueryLocal();
        sb.AppendLine("INFO  QueryLocal = " + SessionStatus.ToJson(localUsage));
        Check("使用狀態：讀得到本機電腦名稱與目前的工作階段",
            localUsage.Machine == Environment.MachineName &&
            localUsage.Sessions.Any(x => x.Active && x.User.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase)),
            string.Join(", ", localUsage.Sessions.Select(x => $"{x.User}/{(x.Remote ? "remote" : "console")}/{(x.Active ? "active" : "disc")}")));
        var usageT0 = new DateTime(2026, 11, 24, 2, 20, 0, DateTimeKind.Utc);
        SessionInfo Remote(string client, bool active = true, int? idle = 3) => new("alice", client, true, active, false, usageT0, idle);
        SessionInfo Console(bool? locked, int? idle) => new("bob", "", false, true, locked, usageT0, idle);
        MachineUsage Usage(params SessionInfo[] ss) => new("1.12.0", "OFFICE-PC", [.. ss]);
        var occRemote = SessionStatus.FindOccupant(Usage(Remote("ACCT-PC")), "MY-PC");
        Check("使用狀態：別台電腦的遠端工作階段算使用中",
            occRemote is { } o1 && o1.Short == "使用中：alice（遠端）" && o1.Description.Contains("從 ACCT-PC") && o1.Description.Contains("閒置 3 分鐘"),
            occRemote?.Description ?? "null");
        Check("使用狀態：自己這台電腦連進去的不算（含 15 字元截斷）",
            SessionStatus.FindOccupant(Usage(Remote("MY-PC")), "my-pc") is null &&
            SessionStatus.FindOccupant(Usage(Remote("VERYLONGCOMPUTE")), "VERYLONGCOMPUTERNAME1") is null);
        Check("使用狀態：已中斷的遠端工作階段不算", SessionStatus.FindOccupant(Usage(Remote("ACCT-PC", active: false)), "MY-PC") is null);
        Check("使用狀態：電腦前的工作階段不提醒（開機自動登入、無法判斷是否有人）",
            SessionStatus.FindOccupant(Usage(Console(false, 0), Console(true, null)), "MY-PC") is null);
        Check("使用狀態：電腦前與遠端同時存在時，提醒遠端的人",
            SessionStatus.FindOccupant(Usage(Console(false, 1), Remote("ACCT-PC")), "MY-PC")?.Session.User == "alice");
        Check("使用狀態：沒有閒置資訊時不顯示閒置時間",
            SessionStatus.FindOccupant(Usage(Remote("ACCT-PC", idle: null)), "MY-PC")?.Description is { } dNoIdle &&
            !dNoIdle.Contains("閒置") && dNoIdle.EndsWith(" 起）"), SessionStatus.FindOccupant(Usage(Remote("ACCT-PC", idle: null)), "MY-PC")?.Description ?? "null");
        Check("使用狀態：本機電腦前的工作階段不帶閒置時間（不可靠）",
            localUsage.Sessions.Where(x => !x.Remote).All(x => x.IdleMinutes is null));
        var demoUsage = Usage(Remote("ACCT-PC"), Console(true, null));
        var usageJson = SessionStatus.ToJson(demoUsage);
        sb.AppendLine("INFO  MachineUsage JSON = " + usageJson);
        var usageBack = SessionStatus.FromJson(usageJson);
        Check("使用狀態：JSON 讀回內容相同",
            usageBack is not null && usageBack.Machine == "OFFICE-PC" && usageBack.Sessions.SequenceEqual(demoUsage.Sessions), usageJson);
        Check("使用狀態：壞掉的回應視為查不到", SessionStatus.FromJson("not json") is null);
        using (var server = new StatusServer(0, System.Net.IPAddress.Loopback, () => demoUsage))
        {
            var got = SessionStatus.QueryAsync("127.0.0.1", server.Port).GetAwaiter().GetResult();
            Check("使用狀態：TCP 伺服器回報、用戶端查詢", got is not null && got.Sessions.SequenceEqual(demoUsage.Sessions), server.Port.ToString());
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Check("使用狀態：沒有啟用的電腦查不到（照舊直接連線）",
            SessionStatus.QueryAsync("127.0.0.1", 1, 1500).GetAwaiter().GetResult() is null && sw.ElapsedMilliseconds < 2500, $"{sw.ElapsedMilliseconds} ms");
        Check("使用狀態：防火牆只允許內部網路、TCP 47389",
            StatusService.FirewallScript() is var statusFw && statusFw.Contains("-LocalPort 47389") &&
            statusFw.Contains("-RemoteAddress LocalSubnet,10.0.0.0/8") && !statusFw.Contains("Any,") && statusFw.Contains("-Protocol TCP"), statusFw);
        var gwSame = Good();
        gwSame.Machines = [new RdpMachine { Name = "p", Ip = "192.168.1.20", Port = SessionStatus.Port }];
        Check("使用狀態：Gateway 連接埠和遠端桌面相同時只列一次",
            CloudflareClient.GatewayBlockTraffic(gwSame).Contains("not(net.dst.port in {47389}))"), CloudflareClient.GatewayBlockTraffic(gwSame));

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
