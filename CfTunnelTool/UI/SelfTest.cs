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
