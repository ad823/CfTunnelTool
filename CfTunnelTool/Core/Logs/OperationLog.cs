namespace CfTunnelTool.Core.Logs;

/// <summary>
/// 程式操作紀錄：記錄視窗（LogBox）的每一行都會寫進來。
/// 寫入失敗不影響程式運作（例如權限不足、磁碟滿）。
/// </summary>
static class OperationLog
{
    public static void Write(string level, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        try
        {
            LogStore.Append(LogKinds.Operation,
                [new OperationRecord(DateTime.Now, level, message.Trim(), Environment.MachineName)], r => r.Time);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }
}
