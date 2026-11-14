using System.Text;
using System.Text.Json;

namespace CfTunnelTool.Core;

/// <summary>連線代碼裡的一台電腦。</summary>
sealed class ClientMachine
{
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; } = 3389;
}

/// <summary>
/// 使用者模式需要的資訊：WARP 組織名稱與可以連的電腦。
/// 管理者「複製連線資訊」時產生連線代碼（CFT1: + base64 JSON），使用者貼到 CfTunnelTool 即可。
/// 代碼不含任何密碼或金鑰，只有組織名稱和內網 IP。
/// </summary>
sealed class ClientConfig
{
    const string Prefix = "CFT1:";

    public string Team { get; set; } = "";
    public List<ClientMachine> Machines { get; set; } = [];

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static ClientConfig FromRdp(RdpConfig rdp) => new()
    {
        Team = rdp.TeamName ?? "",
        Machines = rdp.Machines.Select(m => new ClientMachine { Name = m.Name, Ip = m.Ip, Port = m.Port }).ToList(),
    };

    public string ToCode() =>
        Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { t = Team, m = Machines.Select(x => new[] { x.Name, x.Ip, x.Port.ToString() }) })));

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
            foreach (var m in root.GetProperty("m").EnumerateArray())
            {
                var a = m.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                if (a.Length >= 2)
                    cfg.Machines.Add(new ClientMachine { Name = a[0], Ip = a[1], Port = a.Length > 2 && int.TryParse(a[2], out var p) ? p : 3389 });
            }
            return cfg.Team.Length > 0 && cfg.Machines.Count > 0 ? cfg : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
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
