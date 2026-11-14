using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CfTunnelTool.Core;

/// <summary>一台要透過瀏覽器 RDP 開放的電腦（這台或區網內其他電腦）。</summary>
sealed class RdpMachine
{
    /// <summary>顯示名稱，同時作為 Cloudflare 基礎架構目標（Target）的名稱。</summary>
    public string Name { get; set; } = "";

    /// <summary>從中繼電腦（安裝 CfTunnelTool 的電腦）連得到的 IPv4。</summary>
    public string Ip { get; set; } = "";

    public int Port { get; set; } = 3389;

    // ---- 部署後由程式記錄，用來更新與移除 ----
    public string? TargetId { get; set; }
    public string? RouteId { get; set; }
}

/// <summary>瀏覽器 RDP + 2FA 的設定。</summary>
sealed partial class RdpConfig
{
    public static readonly string[] AllMfaMethods = ["totp", "security_key", "biometrics"];
    public static readonly string[] SessionDurations = ["0m", "1h", "8h", "24h"];

    /// <summary>停用時保留這份設定，部署時才知道要清掉哪些 Cloudflare 資源。</summary>
    public bool Enabled { get; set; }

    /// <summary>瀏覽器 RDP 的網址，例如 rdp.example.com</summary>
    public string Hostname { get; set; } = "";

    /// <summary>允許登入的 Email，或以 @ 開頭的整個網域（例如 @company.com）。</summary>
    public List<string> AllowedEmails { get; set; } = [];

    /// <summary>允許的 2FA 方式：totp、security_key、biometrics。</summary>
    public List<string> MfaMethods { get; set; } = [.. AllMfaMethods];

    /// <summary>多久需要重新做一次 2FA，0m 代表每次都要。</summary>
    public string MfaSessionDuration { get; set; } = "8h";

    public bool ClipboardText { get; set; } = true;
    public bool FileUpload { get; set; } = true;
    public bool FileDownload { get; set; } = true;

    public List<RdpMachine> Machines { get; set; } = [];

    // ---- 部署後由程式記錄 ----
    public string? AppId { get; set; }
    public string? PolicyId { get; set; }
    public string? VnetId { get; set; }

    /// <summary>由本工具建立的 App Launcher（使用者註冊 2FA 的入口）與其政策。</summary>
    public string? LauncherAppId { get; set; }
    public string? LauncherPolicyId { get; set; }

    /// <summary>Zero Trust 組織網域，例如 myteam.cloudflareaccess.com，用來組 2FA 註冊網址。</summary>
    public string? AuthDomain { get; set; }

    public string? MfaEnrollUrl => string.IsNullOrEmpty(AuthDomain) ? null : $"https://{AuthDomain}/AddMfaDevice";

    public string UrlFor(RdpMachine m) => string.IsNullOrEmpty(VnetId)
        ? $"https://{Hostname}"
        : $"https://{Hostname}/rdp/{VnetId}/{m.Ip}/{m.Port}";

    public RdpConfig Clone() => JsonSerializer.Deserialize<RdpConfig>(JsonSerializer.Serialize(this))!;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$")]
    public static partial Regex MachineNamePattern();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailDomainPattern();

    public static bool IsValidEmailEntry(string s) => EmailPattern().IsMatch(s) || EmailDomainPattern().IsMatch(s);

    public void Validate(string domain)
    {
        Hostname = Hostname.Trim().TrimEnd('.').ToLowerInvariant();
        AllowedEmails = AllowedEmails.Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Length > 0).Distinct().ToList();
        MfaMethods = MfaMethods.Where(AllMfaMethods.Contains).Distinct().ToList();

        if (Hostname != domain && !Hostname.EndsWith("." + domain))
            throw new InvalidOperationException($"遠端桌面網址 {Hostname} 不屬於網域 {domain}。");
        if (Machines.Count == 0)
            throw new InvalidOperationException("請至少加入一台要開放遠端桌面的電腦。");
        if (AllowedEmails.Count == 0)
            throw new InvalidOperationException("請至少輸入一個允許登入的 Email（或 @公司網域）。");
        foreach (var e in AllowedEmails)
            if (!IsValidEmailEntry(e))
                throw new InvalidOperationException($"「{e}」不是有效的 Email，也不是 @網域 的格式。");
        if (MfaMethods.Count == 0)
            throw new InvalidOperationException("請至少選擇一種 2FA 驗證方式。");
        if (!SessionDurations.Contains(MfaSessionDuration))
            MfaSessionDuration = "8h";

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var endpoints = new HashSet<string>();
        foreach (var m in Machines)
        {
            m.Name = m.Name.Trim();
            m.Ip = m.Ip.Trim();
            if (!MachineNamePattern().IsMatch(m.Name))
                throw new InvalidOperationException($"電腦名稱「{m.Name}」只能使用英文、數字、.、- 和 _。");
            if (!names.Add(m.Name))
                throw new InvalidOperationException($"電腦名稱「{m.Name}」重複。");
            if (!IPAddress.TryParse(m.Ip, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || m.Ip.Count(c => c == '.') != 3)
                throw new InvalidOperationException($"{m.Name} 的 IP「{m.Ip}」不是有效的 IPv4 位址。");
            if (IPAddress.IsLoopback(ip))
                throw new InvalidOperationException($"{m.Name} 不能使用 127.0.0.1，請改用這台電腦的區網 IP（按「加入這台電腦」會自動帶入）。");
            if (m.Port is < 1 or > 65535)
                throw new InvalidOperationException($"{m.Name} 的連接埠必須介於 1～65535。");
            if (!endpoints.Add($"{m.Ip}:{m.Port}"))
                throw new InvalidOperationException($"{m.Ip}:{m.Port} 重複設定。");
        }
        if (Machines.GroupBy(m => m.Ip).Any(g => g.Count() > 1))
            throw new InvalidOperationException("同一個 IP 只能設定一台電腦（Cloudflare 的目標以 IP 區分）。");
    }
}
