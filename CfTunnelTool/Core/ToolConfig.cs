using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CfTunnelTool.Core;

sealed class RouteConfig
{
    /// <summary>對外網址，例如 api.example.com</summary>
    public string Hostname { get; set; } = "";

    /// <summary>本機服務，例如 http://localhost:5000，或 TCP 服務 tcp://localhost:3306（MySQL）</summary>
    public string Service { get; set; } = "";

    /// <summary>本機服務為自簽憑證 https 時設為 true</summary>
    public bool NoTlsVerify { get; set; }

    /// <summary>
    /// API 用途：關閉 Cloudflare 的 Browser Integrity Check，讓 Python、curl 等程式可以呼叫
    /// （否則預設的 User-Agent 會被擋，回 403 Error 1010）。只對 http／https 網址有意義。
    /// </summary>
    public bool DisableBrowserCheck { get; set; }

    public RouteConfig Clone() => new() { Hostname = Hostname, Service = Service, NoTlsVerify = NoTlsVerify, DisableBrowserCheck = DisableBrowserCheck };

    /// <summary>TCP 轉發（例如 MySQL）：使用者端要用 cloudflared access tcp 連線，不能用瀏覽器開。</summary>
    [JsonIgnore]
    public bool IsTcp => Service.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase);

    /// <summary>顯示用的對外位址：網站是 https://網址，TCP 是 tcp://網址。</summary>
    [JsonIgnore]
    public string PublicUrl => (IsTcp ? "tcp://" : "https://") + Hostname;

    /// <summary>TCP 服務的連接埠（例如 3306）；不是 TCP 或沒有連接埠時為 null。</summary>
    [JsonIgnore]
    public int? TcpPort => IsTcp && Uri.TryCreate(Service, UriKind.Absolute, out var u) && u.Port is > 0 and <= 65535 ? u.Port : null;

    /// <summary>
    /// 使用者端的連線指令：在自己電腦開一個本機連接埠，經 Cloudflare 轉到這個服務。
    /// 本機連接埠預設和服務相同（MySQL 就是 127.0.0.1:3306）。
    /// </summary>
    public string ClientCommand(int? localPort = null) =>
        $"cloudflared access tcp --hostname {Hostname} --url 127.0.0.1:{localPort ?? TcpPort ?? 0}";
}

sealed partial class ToolConfig
{
    public string Domain { get; set; } = "";
    public string TunnelName { get; set; } = "";

    /// <summary>部署成功後記錄的 Tunnel ID，用來辨識「這台電腦自己的」Tunnel。</summary>
    public string? TunnelId { get; set; }

    public List<RouteConfig> Routes { get; set; } = [];

    /// <summary>瀏覽器 RDP + 2FA；null 代表從未設定。</summary>
    public RdpConfig? Rdp { get; set; }

    public string? CloudflaredPath { get; set; }

    public bool RdpEnabled => Rdp is { Enabled: true };

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    public static partial Regex TunnelNamePattern();

    [GeneratedRegex(@"^([a-z0-9]([a-z0-9-]*[a-z0-9])?\.)+[a-z0-9-]{2,}$")]
    private static partial Regex HostnamePattern();

    public static ToolConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize<ToolConfig>(File.ReadAllText(path), JsonOptions)
                     ?? throw new InvalidOperationException("設定檔內容是空的。");
        config.Validate();
        return config;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public void Validate()
    {
        Domain = Domain.Trim().TrimEnd('.').ToLowerInvariant();
        TunnelName = TunnelName.Trim();

        if (string.IsNullOrEmpty(Domain))
            throw new InvalidOperationException("請選擇網域。");
        if (!TunnelNamePattern().IsMatch(TunnelName))
            throw new InvalidOperationException("Tunnel 名稱只能使用英文、數字、- 和 _，且需以英文或數字開頭。");
        if (Routes.Count == 0 && !RdpEnabled)
            throw new InvalidOperationException("請至少設定一個對外網址，或啟用遠端桌面。");

        var seen = new HashSet<string>();
        foreach (var r in Routes)
        {
            r.Hostname = r.Hostname.Trim().TrimEnd('.').ToLowerInvariant();
            r.Service = r.Service.Trim();

            if (r.Hostname != Domain && !r.Hostname.EndsWith("." + Domain))
                throw new InvalidOperationException($"網址 {r.Hostname} 不屬於網域 {Domain}。");
            if (!HostnamePattern().IsMatch(r.Hostname))
                throw new InvalidOperationException($"網址 {r.Hostname} 格式不正確，子網域只能使用英文、數字和 -。");
            if (!seen.Add(r.Hostname))
                throw new InvalidOperationException($"網址 {r.Hostname} 重複設定。");
            if (!Uri.TryCreate(r.Service, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "tcp"))
                throw new InvalidOperationException($"{r.Hostname} 的本機服務位址必須是 http://、https:// 或 tcp:// 開頭，目前是「{r.Service}」。");
            if (uri.Scheme == "tcp")
            {
                if (r.TcpPort is null || uri.AbsolutePath is not ("" or "/") || uri.Query != "" || uri.UserInfo != "")
                    throw new InvalidOperationException($"{r.Hostname} 的 TCP 服務位址要寫成 tcp://主機:連接埠，例如 tcp://localhost:3306，目前是「{r.Service}」。");
                r.Service = $"tcp://{uri.Host}:{uri.Port}"; // 去掉結尾的 /
                r.NoTlsVerify = false; // 只對 https 有意義
                r.DisableBrowserCheck = false; // TCP 不經過瀏覽器檢查
            }
        }

        if (RdpEnabled)
        {
            Rdp!.Validate(Domain);
            if (!Rdp.BrowserEnabled) return; // 只用 WARP 時不需要瀏覽器網址
            if (!HostnamePattern().IsMatch(Rdp.Hostname))
                throw new InvalidOperationException($"遠端桌面網址 {Rdp.Hostname} 格式不正確，子網域只能使用英文、數字和 -。");
            if (seen.Contains(Rdp.Hostname))
                throw new InvalidOperationException($"遠端桌面網址 {Rdp.Hostname} 和網站／API 的網址重複，請換一個子網域。");
        }
    }

    /// <summary>
    /// 把使用者輸入的本機服務位址整理成完整網址：
    /// 「5000」→ http://localhost:5000，「localhost:8080」→ http://localhost:8080
    /// </summary>
    public static string NormalizeService(string input)
    {
        var s = input.Trim();
        if (s.Length == 0) return s;
        if (s.All(char.IsAsciiDigit)) return $"http://localhost:{s}";
        if (!s.Contains("://")) return "http://" + s;
        return s;
    }
}
