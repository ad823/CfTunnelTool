namespace CfTunnelTool.Core;

/// <summary>桌面捷徑要連的電腦：名稱，加上地點（虛擬網路 ID，沒有地點時為 null）。</summary>
record ConnectRequest(string Name, string? VnetId);

/// <summary>
/// 桌面捷徑的參數：CfTunnelTool.exe --connect "電腦名稱" [--site 虛擬網路ID]。
/// 這個模式不需要系統管理員權限，不會跳出 UAC。
/// </summary>
static class ConnectArgs
{
    public const string Flag = "--connect";
    public const string SiteFlag = "--site";

    /// <summary>名稱裡的雙引號會破壞命令列，一律拿掉（建立捷徑與比對時都這樣處理）。</summary>
    static string Clean(string name) => name.Replace("\"", "").Trim();

    public static string Build(ClientMachine m) =>
        $"{Flag} \"{Clean(m.Name)}\"" + (m.VnetId is { Length: > 0 } vnet ? $" {SiteFlag} {vnet}" : "");

    /// <summary>解析命令列；沒有 --connect 或缺少名稱時回傳 null。</summary>
    public static ConnectRequest? Parse(IReadOnlyList<string> args)
    {
        var i = IndexOf(args, Flag);
        if (i < 0 || i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal)) return null;
        var name = Clean(args[i + 1]);
        if (name.Length == 0) return null;

        var s = IndexOf(args, SiteFlag);
        var vnet = s >= 0 && s + 1 < args.Count && !args[s + 1].StartsWith("--", StringComparison.Ordinal) ? args[s + 1].Trim() : null;
        return new ConnectRequest(name, string.IsNullOrEmpty(vnet) ? null : vnet);
    }

    /// <summary>依名稱（不分大小寫）與地點找出電腦；捷徑有指定地點時地點也要相同。</summary>
    public static ClientMachine? Find(IEnumerable<ClientMachine> machines, ConnectRequest r) =>
        machines.FirstOrDefault(m => string.Equals(Clean(m.Name), r.Name, StringComparison.OrdinalIgnoreCase) &&
                                     (r.VnetId is null || string.Equals(m.VnetId, r.VnetId, StringComparison.OrdinalIgnoreCase)));

    static int IndexOf(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
