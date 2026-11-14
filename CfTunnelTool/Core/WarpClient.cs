using System.Diagnostics;
using System.ServiceProcess;
using System.Text;

namespace CfTunnelTool.Core;

/// <summary>
/// 使用者端：安裝 Cloudflare WARP（Cloudflare One Client）、加入組織、查詢狀態，並建立遠端桌面捷徑。
/// 安裝程式一律從 Cloudflare 官方網址下載。
/// </summary>
static class WarpClient
{
    /// <summary>Cloudflare 官方 Windows 安裝程式（會轉址到最新正式版 .msi）。</summary>
    public const string DownloadUrl = "https://downloads.cloudflareclient.com/v1/download/windows/ga";

    const string ServiceName = "CloudflareWARP";

    static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Cloudflare", "Cloudflare WARP");

    static string CliPath => Path.Combine(InstallDir, "warp-cli.exe");
    static string GuiPath => Path.Combine(InstallDir, "Cloudflare WARP.exe");

    static string MdmPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Cloudflare", "mdm.xml");

    public static bool IsInstalled => File.Exists(CliPath);

    /// <summary>下載並安裝 WARP，安裝時就設定好組織名稱。</summary>
    public static async Task InstallAsync(string team, ILog log)
    {
        var dir = Path.Combine(Path.GetTempPath(), "CfTunnelTool");
        Directory.CreateDirectory(dir);
        var msi = Path.Combine(dir, "Cloudflare_WARP.msi");

        log.Info("從 Cloudflare 官方網站下載 WARP 安裝程式…");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CfTunnelTool/" + typeof(WarpClient).Assembly.GetName().Version);
            using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(msi);
            var buffer = new byte[81920];
            long done = 0;
            var next = 25;
            int read;
            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0 && done * 100 / total.Value >= next)
                {
                    log.Info($"下載中… {next}%（{done / 1024 / 1024} MB）");
                    next += 25;
                }
            }
        }

        log.Info("安裝 WARP（約 1 分鐘）…");
        var r = await Task.Run(() => Cloudflared.Run("msiexec.exe", "/i", msi, "/qn", "/norestart",
            $"ORGANIZATION={team}", "SUPPORT_URL="));
        try { File.Delete(msi); } catch (IOException) { }

        // 0 成功、3010 成功但需要重新開機
        if (r.ExitCode is not (0 or 3010))
            throw new InvalidOperationException(r.ExitCode == 1618
                ? "目前有其他程式正在安裝，請稍後再試。"
                : $"WARP 安裝失敗（msiexec 錯誤碼 {r.ExitCode}）。");
        log.Ok(r.ExitCode == 3010 ? "WARP 已安裝（建議之後重新開機）" : "WARP 已安裝");
    }

    /// <summary>已安裝 WARP 時，改用設定檔指定組織名稱，並重新啟動 WARP 服務讓它生效。</summary>
    public static async Task SetOrganizationAsync(string team, ILog log)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MdmPath)!);
        var xml = $"<dict>\r\n  <key>organization</key>\r\n  <string>{System.Security.SecurityElement.Escape(team)}</string>\r\n</dict>\r\n";
        await File.WriteAllTextAsync(MdmPath, xml, new UTF8Encoding(false));
        log.Ok($"已設定組織名稱：{team}");

        await Task.Run(() =>
        {
            try
            {
                using var sc = new ServiceController(ServiceName);
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
            catch (InvalidOperationException) { }
            catch (System.ServiceProcess.TimeoutException) { }
        });
        log.Ok("已重新啟動 WARP 服務");
    }

    /// <summary>WARP 狀態文字（例如 Connected、Disconnected、Registration Missing）。</summary>
    public static string GetStatus()
    {
        if (!IsInstalled) return "未安裝";
        var r = Cloudflared.Run(CliPath, "--accept-tos", "status");
        var line = r.Output.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("Status update:", StringComparison.OrdinalIgnoreCase));
        return line is null ? r.Output.Trim() : line["Status update:".Length..].Trim();
    }

    public static bool IsConnected(string status) => status.StartsWith("Connected", StringComparison.OrdinalIgnoreCase);

    public static bool NeedsRegistration(string status) => status.Contains("Registration", StringComparison.OrdinalIgnoreCase);

    public static void Connect()
    {
        if (IsInstalled) Cloudflared.Run(CliPath, "--accept-tos", "connect");
    }

    /// <summary>開啟 WARP 視窗（第一次會引導使用者登入組織）。</summary>
    public static void OpenGui()
    {
        if (File.Exists(GuiPath))
            Process.Start(new ProcessStartInfo(GuiPath) { UseShellExecute = true });
    }

    // ---------- 遠端桌面捷徑 ----------

    /// <summary>.rdp 檔內容：詢問帳密、自動偵測頻寬（可用 UDP 與 H.264），並開啟剪貼簿。</summary>
    public static string RdpFileContent(ClientMachine m) => string.Join("\r\n",
        $"full address:s:{m.Ip}:{m.Port}",
        "prompt for credentials:i:1",
        "administrative session:i:0",
        "screen mode id:i:2",
        "use multimon:i:0",
        "networkautodetect:i:1",
        "bandwidthautodetect:i:1",
        "connection type:i:7",
        "redirectclipboard:i:1",
        "authentication level:i:2",
        "") ;

    static string DesktopDir => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static string RdpFilePath(ClientMachine m)
    {
        var safe = string.Concat(m.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(DesktopDir, $"遠端桌面 - {safe}.rdp");
    }

    public static void CreateRdpShortcuts(IEnumerable<ClientMachine> machines, ILog log)
    {
        foreach (var m in machines)
        {
            var path = RdpFilePath(m);
            File.WriteAllText(path, RdpFileContent(m), new UTF8Encoding(false));
            log.Ok($"已在桌面建立「{Path.GetFileName(path)}」");
        }
    }

    public static void OpenRdp(ClientMachine m)
    {
        var path = RdpFilePath(m);
        if (!File.Exists(path)) File.WriteAllText(path, RdpFileContent(m), new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo("mstsc.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
