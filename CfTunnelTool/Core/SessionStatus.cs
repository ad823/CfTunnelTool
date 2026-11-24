using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CfTunnelTool.Core;

/// <summary>被連線電腦上的一個 Windows 工作階段（只含判斷「有沒有人在用」需要的資訊）。</summary>
sealed record SessionInfo(
    string User,
    string Client,
    bool Remote,
    bool Active,
    bool? Locked,
    DateTime? SinceUtc,
    int? IdleMinutes);

/// <summary>被連線電腦回報的使用狀態。</summary>
sealed record MachineUsage(string Version, string Machine, List<SessionInfo> Sessions);

/// <summary>有人正在使用時的說明。</summary>
sealed record Occupant(SessionInfo Session, string Short, string Description);

/// <summary>
/// 使用狀態回報：被連線的電腦用 Windows 本機 API（WTS）讀取工作階段，經 TCP 連接埠回報；
/// 使用者端連線前查詢，避免把正在使用的人擠掉。
/// </summary>
static class SessionStatus
{
    /// <summary>狀態回報的 TCP 連接埠（WARP 的 Gateway 規則會放行這個連接埠）。</summary>
    public const int Port = 47389;

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string ToJson(MachineUsage usage) => JsonSerializer.Serialize(usage, Json);

    public static MachineUsage? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<MachineUsage>(json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>查詢被連線電腦的使用狀態；那台電腦沒有啟用狀態回報（或連不到）時回傳 null。</summary>
    public static async Task<MachineUsage?> QueryAsync(string ip, int port = Port, int timeoutMs = 1500)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var client = new TcpClient();
            await client.ConnectAsync(ip, port, cts.Token);
            using var reader = new StreamReader(client.GetStream());
            var text = await reader.ReadToEndAsync(cts.Token);
            return FromJson(text);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// 找出「連進去會被擠掉的人」：其他電腦連進來、正在進行中的遠端工作階段。
    /// 自己這台電腦連進去的工作階段不算。電腦前的工作階段不提醒：Windows 不會更新它的最後操作時間，
    /// 而且被連線的電腦通常開機自動登入、一直是未鎖定，無法可靠判斷是否有人坐在電腦前。
    /// </summary>
    public static Occupant? FindOccupant(MachineUsage usage, string myComputerName)
    {
        foreach (var s in usage.Sessions.Where(s => s.Active && s.Remote))
        {
            if (IsMe(s.Client, myComputerName)) continue;
            var from = string.IsNullOrEmpty(s.Client) ? "" : $"從 {s.Client} ";
            var details = new List<string>();
            if (s.SinceUtc is { } t) details.Add($"{t.ToLocalTime():HH:mm} 起");
            switch (s.IdleMinutes)
            {
                case 0: details.Add("目前有操作"); break;
                case > 0 and <= 24 * 60: details.Add($"已閒置 {s.IdleMinutes} 分鐘"); break;
            }
            var detail = details.Count > 0 ? $"（{string.Join("，", details)}）" : "";
            return new Occupant(s, $"使用中：{s.User}（遠端）", $"{s.User} 正{from}遠端連線使用中{detail}");
        }
        return null;
    }

    /// <summary>遠端桌面回報的用戶端名稱是 NetBIOS 名稱（最多 15 字元）。</summary>
    static bool IsMe(string client, string myComputerName)
    {
        if (string.IsNullOrEmpty(client)) return false;
        var me = myComputerName.Length > 15 ? myComputerName[..15] : myComputerName;
        return client.Equals(me, StringComparison.OrdinalIgnoreCase) ||
               client.Equals(myComputerName, StringComparison.OrdinalIgnoreCase);
    }

    // ================= 本機工作階段（WTS） =================

    /// <summary>讀取這台電腦已登入的工作階段（進行中與已中斷）。</summary>
    public static MachineUsage QueryLocal()
    {
        var list = new List<SessionInfo>();
        var console = WTSGetActiveConsoleSessionId();
        if (WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buffer, out var count))
        {
            try
            {
                var size = Marshal.SizeOf<WTS_SESSION_INFO>();
                for (var i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(buffer + i * size);
                    if (info.State is not (WTSActive or WTSDisconnected)) continue;
                    if (ReadSession(info.SessionId, console) is { } s) list.Add(s);
                }
            }
            finally { WTSFreeMemory(buffer); }
        }
        return new MachineUsage(
            typeof(SessionStatus).Assembly.GetName().Version?.ToString(3) ?? "",
            Environment.MachineName, list);
    }

    static SessionInfo? ReadSession(int sessionId, uint consoleSession)
    {
        if (!TryQuery(sessionId, WTSSessionInfoEx, out var exBuf)) return null;
        WTSINFOEX_LEVEL1 ex;
        try { ex = Marshal.PtrToStructure<WTSINFOEX>(exBuf).Data; }
        finally { WTSFreeMemory(exBuf); }
        if (string.IsNullOrEmpty(ex.UserName)) return null; // 登入畫面、服務

        ushort protocol = 0;
        if (TryQuery(sessionId, WTSClientProtocolType, out var pBuf))
        {
            try { protocol = (ushort)Marshal.ReadInt16(pBuf); }
            finally { WTSFreeMemory(pBuf); }
        }
        var client = "";
        if (TryQuery(sessionId, WTSClientName, out var cBuf))
        {
            try { client = Marshal.PtrToStringUni(cBuf) ?? ""; }
            finally { WTSFreeMemory(cBuf); }
        }

        var remote = protocol == 2 || (uint)sessionId != consoleSession;
        var active = ex.SessionState == WTSActive;
        bool? locked = ex.SessionFlags switch { WTS_SESSIONSTATE_LOCK => true, WTS_SESSIONSTATE_UNLOCK => false, _ => null };
        var sinceRaw = remote ? ex.ConnectTime : ex.LogonTime;
        DateTime? since = sinceRaw > 0 ? DateTime.FromFileTimeUtc(sinceRaw) : null;
        // 只有遠端工作階段的最後操作時間可靠（電腦前的不會更新）
        int? idle = remote && ex.LastInputTime > 0 && ex.CurrentTime >= ex.LastInputTime
            ? (int)TimeSpan.FromTicks(ex.CurrentTime - ex.LastInputTime).TotalMinutes
            : null;
        return new SessionInfo(ex.UserName, remote ? client : "", remote, active, locked, since, idle);
    }

    static bool TryQuery(int sessionId, int infoClass, out IntPtr buffer) =>
        WTSQuerySessionInformationW(IntPtr.Zero, sessionId, infoClass, out buffer, out _) && buffer != IntPtr.Zero;

    const int WTSActive = 0, WTSDisconnected = 4;
    const int WTSClientName = 10, WTSClientProtocolType = 16, WTSSessionInfoEx = 25;
    const int WTS_SESSIONSTATE_LOCK = 0, WTS_SESSIONSTATE_UNLOCK = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct WTS_SESSION_INFO
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WTSINFOEX_LEVEL1
    {
        public int SessionId;
        public int SessionState;
        public int SessionFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 18)] public string DomainName;
        public long LogonTime, ConnectTime, DisconnectTime, LastInputTime, CurrentTime;
        public uint IncomingBytes, OutgoingBytes, IncomingFrames, OutgoingFrames, IncomingCompressedBytes, OutgoingCompressedBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WTSINFOEX
    {
        public int Level;
        public WTSINFOEX_LEVEL1 Data;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("kernel32.dll")]
    static extern uint WTSGetActiveConsoleSessionId();
}
