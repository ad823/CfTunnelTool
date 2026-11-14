using System.Text.Json;

namespace CfTunnelTool.Core.Logs;

/// <summary>收集進度與結果（logs\state.json）。</summary>
sealed class LogState
{
    public DateTime? LastAccess { get; set; }
    public DateTime? LastNetwork { get; set; }

    /// <summary>每個 Windows 事件記錄最後收集到的 EventRecordID。</summary>
    public Dictionary<string, long> LastRdpRecordIds { get; set; } = [];

    public DateTime? LastCollected { get; set; }
    public string? LastResult { get; set; }

    /// <summary>網路傳輸紀錄不可用的原因（權限不足、資料集不支援等）；null 代表可用。</summary>
    public string? NetworkUnavailable { get; set; }

    /// <summary>上次查到的 GraphQL 欄位，避免每次都重新查 schema。</summary>
    public NetworkFields? NetworkFieldsCache { get; set; }

    public int RetentionDays { get; set; } = LogStore.DefaultRetentionDays;

    static string Path => System.IO.Path.Combine(LogStore.Root, "state.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static LogState Load()
    {
        try { return File.Exists(Path) ? JsonSerializer.Deserialize<LogState>(File.ReadAllText(Path), Json) ?? new() : new(); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(LogStore.Root);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, Json));
    }
}

/// <summary>GraphQL 網路工作階段資料集實際可用的欄位。</summary>
sealed record NetworkFields(
    string Dataset,
    string TimeField,
    string? UserField,
    string? DestIpField,
    string? DestPortField,
    string? SentField,
    string? ReceivedField,
    bool HasCount);
