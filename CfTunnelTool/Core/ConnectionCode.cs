using System.Text;
using System.Text.Json;

namespace CfTunnelTool.Core;

/// <summary>使用者連線清單中的一台電腦。</summary>
sealed class ClientMachine
{
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; } = 3389;

    /// <summary>預先填入的 Windows 帳號（選填），密碼仍然每次輸入。</summary>
    public string? UserName { get; set; }

    public DateTime? LastConnectedAt { get; set; }

    /// <summary>這台電腦所在的 Cloudflare 虛擬網路（地點）；舊版代碼沒有時為 null。</summary>
    public string? VnetId { get; set; }

    /// <summary>地點名稱（顯示用）。</summary>
    public string? VnetName { get; set; }

    public ClientMachine Clone() => new()
    {
        Name = Name, Ip = Ip, Port = Port, UserName = UserName, LastConnectedAt = LastConnectedAt,
        VnetId = VnetId, VnetName = VnetName,
    };

    /// <summary>
    /// 是否為同一台電腦：IP 與連接埠相同，且地點相同。
    /// 任一方沒有地點資訊（舊版代碼）時，只比對 IP 與連接埠。
    /// </summary>
    public bool SameEndpoint(ClientMachine other) =>
        Ip == other.Ip &&
        (VnetId is null || other.VnetId is null || VnetId == other.VnetId);
}

/// <summary>
/// 使用者模式的設定：WARP 組織名稱、可以連的電腦、2FA 驗證間隔與上次確認時間。
/// 管理者「複製連線資訊」時產生連線代碼（CFT1: + base64 JSON），使用者貼到 CfTunnelTool 即可。
/// 代碼不含任何密碼或金鑰，只有組織名稱、內網 IP 與 2FA 驗證間隔。
/// </summary>
sealed class ClientConfig
{
    const string Prefix = "CFT1:";

    public string Team { get; set; } = "";
    public List<ClientMachine> Machines { get; set; } = [];

    /// <summary>管理者設定的 2FA 重新驗證間隔（例如 8h、0m）；舊版代碼沒有這個資訊時為 null。</summary>
    public string? MfaSessionDuration { get; set; }

    /// <summary>本機最後一次確認 2FA 有效（連線測試通過）的時間。</summary>
    public DateTime? LastVerifiedAt { get; set; }

    /// <summary>上次程式把 WARP 切換到的虛擬網路（用來避免每次連線都重新切換）。</summary>
    public string? CurrentVnetId { get; set; }

    /// <summary>清單中的不同地點（虛擬網路），供新增電腦時選擇。</summary>
    public List<(string? Id, string Name)> Sites() => Machines
        .Where(m => m.VnetId is not null)
        .GroupBy(m => m.VnetId)
        .Select(g => (g.Key, g.First().VnetName ?? "預設"))
        .ToList();

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static ClientConfig FromRdp(RdpConfig rdp) => new()
    {
        Team = rdp.TeamName ?? "",
        MfaSessionDuration = rdp.MfaSessionDuration,
        Machines = rdp.Machines.Select(m => new ClientMachine
        {
            Name = m.Name, Ip = m.Ip, Port = m.Port,
            VnetId = rdp.VnetId,
            VnetName = rdp.VnetName,
        }).ToList(),
    };

    public string ToCode()
    {
        // 每台電腦：[名稱, IP, 連接埠, 虛擬網路 ID, 地點名稱]；後兩項舊版代碼沒有
        var payload = new Dictionary<string, object>
        {
            ["t"] = Team,
            ["m"] = Machines.Select(x => x.VnetId is null
                ? new[] { x.Name, x.Ip, x.Port.ToString() }
                : new[] { x.Name, x.Ip, x.Port.ToString(), x.VnetId, x.VnetName ?? "" }),
        };
        if (!string.IsNullOrEmpty(MfaSessionDuration)) payload["s"] = MfaSessionDuration;
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
    }

    /// <summary>從一段文字（可以是整段連線資訊）中找出連線代碼並解析。</summary>
    public static ClientConfig? TryParse(string text)
    {
        var i = text.IndexOf(Prefix, StringComparison.Ordinal);
        if (i < 0) return null;
        var code = new string(text[(i + Prefix.Length)..].TakeWhile(c => char.IsLetterOrDigit(c) || c is '+' or '/' or '=').ToArray());
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(code)));
            var root = doc.RootElement;
            var cfg = new ClientConfig { Team = root.GetProperty("t").GetString() ?? "" };
            if (root.TryGetProperty("s", out var s)) cfg.MfaSessionDuration = s.GetString();
            foreach (var m in root.GetProperty("m").EnumerateArray())
            {
                var a = m.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                if (a.Length >= 2)
                    cfg.Machines.Add(new ClientMachine
                    {
                        Name = a[0],
                        Ip = a[1],
                        Port = a.Length > 2 && int.TryParse(a[2], out var p) ? p : 3389,
                        VnetId = a.Length > 3 && a[3].Length > 0 ? a[3] : null,
                        VnetName = a.Length > 4 && a[4].Length > 0 ? a[4] : null,
                    });
            }
            return cfg.Team.Length > 0 && cfg.Machines.Count > 0 ? cfg : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// 把另一份連線代碼合併進來：同一個組織時依 IP 合併電腦（更新名稱與連接埠，保留使用者自訂的帳號與時間），
    /// 並更新驗證間隔。不同組織時不做任何事並回傳 false。
    /// </summary>
    public bool Merge(ClientConfig other, out int added, out int updated)
    {
        added = updated = 0;
        if (!string.Equals(Team, other.Team, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(other.MfaSessionDuration)) MfaSessionDuration = other.MfaSessionDuration;
        foreach (var m in other.Machines)
        {
            // 同一個地點（虛擬網路）＋同 IP 才是同一台；不同地點的相同 IP 是不同電腦
            var existing = Machines.FirstOrDefault(x => x.SameEndpoint(m) && x.Port == m.Port)
                           ?? Machines.FirstOrDefault(x => x.SameEndpoint(m));
            if (existing is null)
            {
                Machines.Add(m.Clone());
                added++;
            }
            else if (existing.Name != m.Name || existing.Port != m.Port ||
                     (m.VnetId is not null && (existing.VnetId != m.VnetId || existing.VnetName != m.VnetName)))
            {
                existing.Name = m.Name;
                existing.Port = m.Port;
                if (m.VnetId is not null)
                {
                    existing.VnetId = m.VnetId;
                    existing.VnetName = m.VnetName;
                }
                updated++;
            }
        }
        return true;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public static ClientConfig? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(path), Json) : null; }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }
}
