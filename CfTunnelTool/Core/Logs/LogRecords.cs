namespace CfTunnelTool.Core.Logs;

/// <summary>紀錄種類（同時是 logs 底下的資料夾名稱）。</summary>
static class LogKinds
{
    public const string Login = "login";
    public const string RdpEvents = "rdp";
    public const string Network = "network";
    public const string Operation = "operations";
}

/// <summary>Cloudflare Access 登入紀錄。</summary>
sealed record LoginRecord(
    DateTime Time,
    string Email,
    string Ip,
    string App,
    string Action,
    bool Allowed,
    string Connection,
    string RayId);

/// <summary>被連電腦的 Windows 遠端桌面事件（原始資料，查詢時再組成工作階段）。</summary>
sealed record RdpEventRecord(
    DateTime Time,
    int EventId,
    string Machine,
    string User,
    int SessionId,
    string Source,
    long RecordId);

/// <summary>由事件組成的一段遠端桌面工作階段（顯示用）。</summary>
sealed record RdpSessionView(
    DateTime Start,
    DateTime? End,
    string Machine,
    string User,
    int SessionId,
    string Status,
    string Source)
{
    public TimeSpan? Duration => End - Start;
}

/// <summary>Cloudflare Gateway 網路工作階段（抽樣統計）。</summary>
sealed record NetworkRecord(
    DateTime Time,
    string User,
    string Destination,
    long BytesSent,
    long BytesReceived,
    long Count);

/// <summary>CfTunnelTool 自己的操作紀錄。</summary>
sealed record OperationRecord(
    DateTime Time,
    string Level,
    string Message,
    string Machine);
