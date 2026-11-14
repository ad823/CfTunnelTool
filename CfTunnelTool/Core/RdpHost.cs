using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

namespace CfTunnelTool.Core;

/// <summary>這台電腦的遠端桌面狀態。</summary>
record RdpHostStatus(bool Enabled, bool NlaRequired, int SecurityLayer, int Port, string Edition)
{
    /// <summary>Windows 家用版（EditionID 以 Core 開頭）不能當遠端桌面主機。</summary>
    public bool IsHomeEdition => Edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase);

    /// <summary>瀏覽器 RDP 需要 TLS，安全性層不能是舊的「RDP」(0)。</summary>
    public bool SecurityLayerOk => SecurityLayer != 0;
}

/// <summary>本機 Windows 遠端桌面的檢查、啟用，以及網路工具。</summary>
static class RdpHost
{
    const string TsKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    const string RdpTcpKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp";
    const string VersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <summary>「遠端桌面」防火牆規則群組的資源字串，不受系統語言影響。</summary>
    const string FirewallGroup = "@FirewallAPI.dll,-28752";

    public static RdpHostStatus GetStatus()
    {
        using var ts = Registry.LocalMachine.OpenSubKey(TsKey);
        using var tcp = Registry.LocalMachine.OpenSubKey(RdpTcpKey);
        using var ver = Registry.LocalMachine.OpenSubKey(VersionKey);
        return new RdpHostStatus(
            Enabled: ts?.GetValue("fDenyTSConnections") is int deny && deny == 0,
            NlaRequired: tcp?.GetValue("UserAuthentication") is int nla && nla == 1,
            SecurityLayer: tcp?.GetValue("SecurityLayer") is int sl ? sl : 2,
            Port: tcp?.GetValue("PortNumber") is int port ? port : 3389,
            Edition: ver?.GetValue("EditionID") as string ?? "");
    }

    /// <summary>啟用這台電腦的遠端桌面並開放防火牆規則（需要系統管理員權限，會阻塞數秒）。</summary>
    public static void EnableRemoteDesktop()
    {
        using (var ts = Registry.LocalMachine.OpenSubKey(TsKey, writable: true)
                        ?? throw new InvalidOperationException("找不到遠端桌面的系統設定。"))
            ts.SetValue("fDenyTSConnections", 0, RegistryValueKind.DWord);

        var r = Cloudflared.Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
            $"Enable-NetFirewallRule -Group '{FirewallGroup}'");
        if (r.ExitCode != 0)
            throw new InvalidOperationException("遠端桌面已啟用，但開放防火牆規則失敗：\n" + r.Output);
    }

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
