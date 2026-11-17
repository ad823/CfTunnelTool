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

    /// <summary>
    /// 把 WARP 切換到指定的虛擬網路（地點）。同一時間只能在一個虛擬網路，
    /// 切換時連到其他地點的連線會中斷。回傳是否成功（舊版 WARP 沒有這個指令時回傳 false，
    /// 由畫面引導使用者在 WARP 設定中手動選擇）。
    /// </summary>
    public static bool SwitchVirtualNetwork(string vnetId, ILog log)
    {
        if (!IsInstalled) return false;
        var r = Cloudflared.Run(CliPath, "--accept-tos", "vnet", vnetId);
        if (r.ExitCode == 0)
        {
            log.Ok("已切換到這個地點的虛擬網路");
            return true;
        }
        log.Warn("無法自動切換虛擬網路：" + r.Output.Trim());
        return false;
    }

    /// <summary>中斷 WARP（之後一般上網不經過公司）。</summary>
    public static void Disconnect()
    {
        if (IsInstalled) Cloudflared.Run(CliPath, "--accept-tos", "disconnect");
    }

    /// <summary>登出組織：刪除這台電腦在組織的註冊，並移除組織設定檔。</summary>
    public static void LeaveOrganization(ILog log)
    {
        if (IsInstalled)
        {
            Cloudflared.Run(CliPath, "--accept-tos", "disconnect");
            var r = Cloudflared.Run(CliPath, "--accept-tos", "registration", "delete");
            if (r.ExitCode == 0) log.Ok("已登出 WARP 組織");
            else log.Warn("登出 WARP 組織時回報：" + r.Output.Trim());
        }
        if (File.Exists(MdmPath))
        {
            File.Delete(MdmPath);
            log.Ok("已移除 WARP 組織設定檔");
        }
    }

    /// <summary>找出 WARP 的解除安裝指令（MsiExec /X{GUID}）。</summary>
    static string? FindUninstallProductCode()
    {
        foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(root);
            if (key is null) continue;
            foreach (var name in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(name);
                var display = sub?.GetValue("DisplayName") as string ?? "";
                if ((display.Contains("Cloudflare WARP", StringComparison.OrdinalIgnoreCase) ||
                     display.Contains("Cloudflare One", StringComparison.OrdinalIgnoreCase)) &&
                    name.StartsWith('{') && name.EndsWith('}'))
                    return name;
            }
        }
        return null;
    }

    /// <summary>解除安裝 WARP（會阻塞，請在背景執行緒呼叫）。</summary>
    public static void Uninstall(ILog log)
    {
        var code = FindUninstallProductCode();
        if (code is null)
        {
            log.Warn("找不到 WARP 的安裝資訊，請到「設定 → 應用程式」手動解除安裝。");
            return;
        }
        var r = Cloudflared.Run("msiexec.exe", "/x", code, "/qn", "/norestart");
        if (r.ExitCode is 0 or 3010) log.Ok("WARP 已解除安裝" + (r.ExitCode == 3010 ? "（建議重新開機）" : ""));
        else log.Warn($"WARP 解除安裝失敗（msiexec 錯誤碼 {r.ExitCode}），請到「設定 → 應用程式」手動解除安裝。");
    }

    /// <summary>
    /// 請 WARP 重新做 Cloudflare Access 驗證（warp-cli debug access-reauth），
    /// 等同 WARP 視窗的「個人資料 → 帳戶資訊 → 重新驗證」，WARP 會開啟瀏覽器的驗證頁。
    /// 舊版 WARP 沒有這個指令時回傳 false。會阻塞，請在背景執行緒呼叫。
    /// </summary>
    public static bool RequestAccessReauth()
    {
        if (!IsInstalled) return false;
        try { return Cloudflared.Run(CliPath, "--accept-tos", "debug", "access-reauth").ExitCode == 0; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>
    /// 開啟重新驗證：優先用 WARP 的 access-reauth；不支援時改開 WARP 視窗與官方 refresh-identity 網址。
    /// 回傳是否由 WARP 直接開啟驗證頁。會阻塞，請在背景執行緒呼叫。
    /// </summary>
    public static bool OpenReauthenticate(string team)
    {
        if (RequestAccessReauth()) return true;
        OpenRefreshIdentity(team);
        return false;
    }

    /// <summary>舊方法：開啟 WARP 視窗與 refresh-identity 網址，由使用者在 WARP 視窗按「重新驗證」。</summary>
    static void OpenRefreshIdentity(string team)
    {
        OpenGui();
        if (!string.IsNullOrEmpty(team))
        {
            try
            {
                Process.Start(new ProcessStartInfo($"https://{team}.cloudflareaccess.com/cdn-cgi/access/refresh-identity")
                    { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    /// <summary>開啟 WARP 視窗（第一次會引導使用者登入組織）。</summary>
    public static void OpenGui()
    {
        if (File.Exists(GuiPath))
            Process.Start(new ProcessStartInfo(GuiPath) { UseShellExecute = true });
    }

    // ---------- 遠端桌面捷徑 ----------

    /// <summary>.rdp 檔內容：詢問帳密、自動偵測頻寬（可用 UDP 與 H.264），並開啟剪貼簿。</summary>
    public static string RdpFileContent(ClientMachine m)
    {
        var lines = new List<string>
        {
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
        };
        if (!string.IsNullOrWhiteSpace(m.UserName)) lines.Add($"username:s:{m.UserName.Trim()}");
        lines.Add("");
        return string.Join("\r\n", lines);
    }

    // 桌面捷徑是 .lnk，執行 CfTunnelTool.exe --connect：先檢查驗證、需要時自動開驗證頁，再開遠端桌面。
    // 舊版的桌面捷徑是 .rdp 檔（直接開 mstsc，會繞過檢查），開啟使用者畫面時自動換成 .lnk。

    static string DesktopDir => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>連線時暫存的 .rdp 檔資料夾（不放在桌面）。</summary>
    public static string RdpTempDir => Path.Combine(Path.GetTempPath(), "CfTunnelTool", "rdp");

    /// <summary>檔名用的名稱：有地點（獨立虛擬網路）時加在後面，避免不同地點的同名電腦互相覆蓋。</summary>
    public static string FileLabel(ClientMachine m)
    {
        var label = m.VnetName is { Length: > 0 } site ? $"{m.Name}（{site}）" : m.Name;
        return string.Concat(label.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    }

    /// <summary>桌面捷徑：遠端桌面 - 名稱（地點）.lnk</summary>
    public static string ShortcutPath(ClientMachine m) => Path.Combine(DesktopDir, $"遠端桌面 - {FileLabel(m)}.lnk");

    /// <summary>舊版的桌面 .rdp 捷徑。</summary>
    public static string LegacyShortcutPath(ClientMachine m) => Path.Combine(DesktopDir, $"遠端桌面 - {FileLabel(m)}.rdp");

    /// <summary>連線時使用的 .rdp 檔（暫存資料夾）。</summary>
    public static string RdpFilePath(ClientMachine m) => Path.Combine(RdpTempDir, $"遠端桌面 - {FileLabel(m)}.rdp");

    public static bool HasShortcut(ClientMachine m) => File.Exists(ShortcutPath(m)) || File.Exists(LegacyShortcutPath(m));

    public static void DeleteRdpShortcut(ClientMachine m)
    {
        foreach (var path in new[] { ShortcutPath(m), LegacyShortcutPath(m) })
            if (File.Exists(path)) File.Delete(path);
    }

    public static void CreateRdpShortcuts(IEnumerable<ClientMachine> machines, ILog log)
    {
        var mstsc = Path.Combine(Environment.SystemDirectory, "mstsc.exe");
        foreach (var m in machines)
        {
            var legacy = LegacyShortcutPath(m);
            if (File.Exists(legacy)) File.Delete(legacy);
            var path = ShortcutPath(m);
            SelfInstaller.CreateShortcut(path, SelfInstaller.ShortcutTarget, ConnectArgs.Build(m),
                $"連到 {m.Name}（{m.Ip}）：先確認 2FA 驗證，再開啟遠端桌面", mstsc + ",0");
            log.Ok($"已在桌面建立「{Path.GetFileName(path)}」");
        }
    }

    /// <summary>把舊版的桌面 .rdp 捷徑換成新的 .lnk 捷徑，回傳換了幾個。</summary>
    public static int MigrateLegacyShortcuts(IEnumerable<ClientMachine> machines, ILog log)
    {
        var legacy = machines.Where(m => File.Exists(LegacyShortcutPath(m))).ToList();
        if (legacy.Count > 0) CreateRdpShortcuts(legacy, log);
        return legacy.Count;
    }

    public static void OpenRdp(ClientMachine m)
    {
        // 每次都重寫，讓修改過的帳號或連接埠立即生效
        Directory.CreateDirectory(RdpTempDir);
        var path = RdpFilePath(m);
        File.WriteAllText(path, RdpFileContent(m), new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo("mstsc.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
