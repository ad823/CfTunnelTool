using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

namespace CfTunnelTool.Core;

/// <summary>
/// 這台電腦的遠端桌面狀態。NlaRequired／SecurityLayer 是本機設定；NlaPolicy／SecurityLayerPolicy 是群組原則
/// （HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services），有設定時以群組原則為準。
/// </summary>
record RdpHostStatus(bool Enabled, bool NlaRequired, int SecurityLayer, int Port, string Edition,
                     int? NlaPolicy = null, int? SecurityLayerPolicy = null)
{
    /// <summary>實際生效的 NLA：群組原則優先，其次本機設定；安全性層是舊的「RDP」時 NLA 無法運作。</summary>
    public bool NlaEffective => (NlaPolicy is { } p ? p == 1 : NlaRequired) && EffectiveSecurityLayer != 0;

    /// <summary>群組原則強制關閉 NLA，或強制使用無法搭配 NLA 的舊「RDP」安全性層。</summary>
    public bool NlaBlockedByPolicy => NlaPolicy == 0 || SecurityLayerPolicy == 0;

    int EffectiveSecurityLayer => SecurityLayerPolicy ?? SecurityLayer;

    /// <summary>Windows 家用版（EditionID 以 Core 開頭）不能當遠端桌面主機。</summary>
    public bool IsHomeEdition => Edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase);

    /// <summary>瀏覽器 RDP 需要 TLS，安全性層不能是舊的「RDP」(0)。</summary>
    public bool SecurityLayerOk => EffectiveSecurityLayer != 0;
}

/// <summary>EnsureNla 的結果。</summary>
enum NlaResult { AlreadyOn, Enabled, BlockedByPolicy }

/// <summary>Windows 防火牆規則（Name 是內部名稱，DisplayName 是顯示名稱）。</summary>
record FirewallRule(string Name, string DisplayName);

/// <summary>本機 Windows 遠端桌面的檢查、啟用，以及網路工具。</summary>
static class RdpHost
{
    const string TsKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    const string RdpTcpKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp";
    const string VersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";

    /// <summary>「遠端桌面」防火牆規則群組的資源字串，不受系統語言影響。</summary>
    const string FirewallGroup = "@FirewallAPI.dll,-28752";

    public static RdpHostStatus GetStatus()
    {
        using var ts = Registry.LocalMachine.OpenSubKey(TsKey);
        using var tcp = Registry.LocalMachine.OpenSubKey(RdpTcpKey);
        using var ver = Registry.LocalMachine.OpenSubKey(VersionKey);
        using var policy = Registry.LocalMachine.OpenSubKey(PolicyKey);
        return new RdpHostStatus(
            Enabled: ts?.GetValue("fDenyTSConnections") is int deny && deny == 0,
            NlaRequired: tcp?.GetValue("UserAuthentication") is int nla && nla == 1,
            SecurityLayer: tcp?.GetValue("SecurityLayer") is int sl ? sl : 2,
            Port: tcp?.GetValue("PortNumber") is int port ? port : 3389,
            Edition: ver?.GetValue("EditionID") as string ?? "",
            NlaPolicy: policy?.GetValue("UserAuthentication") is int np ? np : null,
            SecurityLayerPolicy: policy?.GetValue("SecurityLayer") is int sp ? sp : null);
    }

    /// <summary>
    /// 強制開啟 NLA（網路層級驗證）：帳號密碼驗證通過才建立遠端桌面工作階段。
    /// 群組原則強制關閉時不覆寫（網域原則下次更新會改回來），回傳 BlockedByPolicy 由畫面提示。
    /// 安全性層是舊的「RDP」(0) 時一併改為 SSL (2)，NLA 才能運作。需要系統管理員權限。
    /// </summary>
    public static NlaResult EnsureNla()
    {
        var s = GetStatus();
        if (s.NlaBlockedByPolicy) return NlaResult.BlockedByPolicy;
        if (s.NlaEffective) return NlaResult.AlreadyOn;

        using var tcp = Registry.LocalMachine.OpenSubKey(RdpTcpKey, writable: true)
                        ?? throw new InvalidOperationException("找不到遠端桌面的系統設定。");
        tcp.SetValue("UserAuthentication", 1, RegistryValueKind.DWord);
        if (s.SecurityLayer == 0) tcp.SetValue("SecurityLayer", 2, RegistryValueKind.DWord);
        return NlaResult.Enabled;
    }

    /// <summary>群組原則強制關閉 NLA 時的說明（本機群組原則的設定路徑）。</summary>
    public const string NlaPolicyHelp =
        "請執行 gpedit.msc → 電腦設定 → 系統管理範本 → Windows 元件 → 遠端桌面服務 → 遠端桌面工作階段主機 → 安全性，" +
        "把「需要使用網路層級驗證對遠端連線進行使用者驗證」設為「已啟用」或「尚未設定」，" +
        "並確認「遠端 (RDP) 連線需要使用指定的安全性層」不是「RDP」；網域電腦請洽 IT 修改網域原則。";

    /// <summary>
    /// 啟用這台電腦的遠端桌面並開放防火牆規則，來源只允許內部網路，並強制開啟 NLA（需要系統管理員權限，會阻塞數秒）。
    /// </summary>
    public static void EnableRemoteDesktop()
    {
        using (var ts = Registry.LocalMachine.OpenSubKey(TsKey, writable: true)
                        ?? throw new InvalidOperationException("找不到遠端桌面的系統設定。"))
            ts.SetValue("fDenyTSConnections", 0, RegistryValueKind.DWord);

        var r = Cloudflared.Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
            $"Enable-NetFirewallRule -Group '{FirewallGroup}'");
        if (r.ExitCode != 0)
            throw new InvalidOperationException("遠端桌面已啟用，但開放防火牆規則失敗：\n" + r.Output);
        RestrictFirewallToInternal(GetStatus().Port);
        EnsureNla();
    }

    // ---------- 防火牆來源限制 ----------

    /// <summary>
    /// 遠端桌面只允許這些來源：同網段、私有 IP（10／172.16–31／192.168）、IPv6 內部位址。
    /// 經 Cloudflare Tunnel 連進來的連線，來源是中繼電腦的區網 IP，仍然可以連；
    /// 網際網路直接連進來（路由器轉送、DMZ、UPnP、電腦直接拿到公網 IP）的會被擋掉。
    /// </summary>
    public static readonly string[] InternalAddresses =
        ["LocalSubnet", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7", "fe80::/10"];

    /// <summary>
    /// 列出「允許任何來源」連到遠端桌面的輸入防火牆規則：Windows 內建的遠端桌面規則群組，
    /// 以及其他開放同一個連接埠的規則。只讀取本機設定的規則（群組原則派送的規則不在此列）。
    /// 讀取失敗回傳 null。
    /// </summary>
    public static List<FirewallRule>? GetOpenFirewallRules(int port)
    {
        var r = Cloudflared.Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
            "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + OpenRulesScript(port) +
            " | ForEach-Object { [pscustomobject]@{ Name=$_.Name; DisplayName=$_.DisplayName } } | ConvertTo-Json -Compress");
        if (r.ExitCode != 0) return null;
        var json = r.Output.Trim();
        if (json.Length == 0) return [];
        if (json.StartsWith('{')) json = $"[{json}]"; // 只有一條規則時不是陣列
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray()
                .Select(e => new FirewallRule(e.GetProperty("Name").GetString() ?? "", e.GetProperty("DisplayName").GetString() ?? ""))
                .Where(f => f.Name.Length > 0)
                .ToList();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>把允許任何來源的遠端桌面規則改為只允許內部網路，回傳修改的規則數（需要系統管理員權限，會阻塞數秒）。</summary>
    public static int RestrictFirewallToInternal(int port)
    {
        var r = Cloudflared.Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
            "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
            $"$rules = @({OpenRulesScript(port)}); " +
            $"$rules | Set-NetFirewallRule -RemoteAddress {string.Join(',', InternalAddresses)}; " +
            "$rules.Count");
        if (r.ExitCode != 0)
            throw new InvalidOperationException("限制遠端桌面防火牆來源失敗：\n" + r.Output.Trim());
        return int.TryParse(r.Output.Trim().Split('\n').LastOrDefault()?.Trim(), out var n) ? n : 0;
    }

    /// <summary>PowerShell：找出啟用中、輸入、允許，且來源包含「任何」的遠端桌面規則。</summary>
    internal static string OpenRulesScript(int port) =>
        "@(@(Get-NetFirewallRule -Group '" + FirewallGroup + "' -ErrorAction SilentlyContinue) + " +
        $"@(Get-NetFirewallPortFilter -ErrorAction SilentlyContinue | Where-Object {{ @($_.LocalPort) -contains '{port}' }} | " +
        "Get-NetFirewallRule -ErrorAction SilentlyContinue)) | " +
        "Where-Object { $_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow' } | " +
        "Sort-Object Name -Unique | " +
        "Where-Object { @(($_ | Get-NetFirewallAddressFilter).RemoteAddress) -contains 'Any' }";

    /// <summary>找出這台電腦的區網 IPv4（排除虛擬網卡與自動私人 IP）。</summary>
    public static string? GetLanIPv4()
    {
        string[] virtualHints = ["virtual", "hyper-v", "vmware", "virtualbox", "wsl", "vethernet", "loopback", "tap", "tunnel", "warp", "cloudflare"];

        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                        or NetworkInterfaceType.GigabitEthernet)
            .Where(n => !virtualHints.Any(h => (n.Description + " " + n.Name).Contains(h, StringComparison.OrdinalIgnoreCase)))
            .Select(n => (Nic: n, Props: n.GetIPProperties()))
            .OrderByDescending(x => x.Props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork));

        foreach (var (_, props) in candidates)
        {
            var ip = props.UnicastAddresses
                .Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork &&
                                     !IPAddress.IsLoopback(a) &&
                                     !a.ToString().StartsWith("169.254."));
            if (ip is not null) return ip.ToString();
        }
        return null;
    }

    /// <summary>從這台電腦用 TCP 連線測試目標的 RDP 連接埠。</summary>
    public static async Task<bool> TestTcpAsync(string ip, int port, int timeoutMs = 3000)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(ip, port, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>開啟 Windows「遠端桌面」設定頁。</summary>
    public static void OpenSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:remotedesktop") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
