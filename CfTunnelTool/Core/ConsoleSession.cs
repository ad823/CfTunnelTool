using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CfTunnelTool.Core;

/// <summary>
/// 「結束遠端並交還畫面」：在遠端桌面工作階段裡執行，用 Windows 內建的 tscon
/// 把目前的工作階段交還給電腦前面的螢幕（本機螢幕直接回到桌面，不需重新登入）。
/// 需要系統管理員權限。
/// </summary>
static class ConsoleSession
{
    const int SM_REMOTESESSION = 0x1000;

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    [DllImport("kernel32.dll")]
    static extern uint WTSGetActiveConsoleSessionId();

    /// <summary>目前是否在遠端桌面工作階段中。</summary>
    public static bool IsRemoteSession =>
        GetSystemMetrics(SM_REMOTESESSION) != 0 || Process.GetCurrentProcess().SessionId != WTSGetActiveConsoleSessionId();

    /// <summary>
    /// 把目前的工作階段交還給本機螢幕。成功時遠端連線會立即中斷，因此只回傳失敗訊息；成功回傳 null。
    /// </summary>
    public static string? ReturnToConsole()
    {
        if (!IsRemoteSession)
            return "目前不是透過遠端桌面連線，不需要交還畫面。";

        var tscon = Path.Combine(Environment.SystemDirectory, "tscon.exe");
        if (!File.Exists(tscon))
            return "這個 Windows 版本找不到 tscon.exe，無法交還畫面。";

        var sessionId = Process.GetCurrentProcess().SessionId;
        var r = Cloudflared.Run(tscon, sessionId.ToString(), "/dest:console");
        return r.ExitCode == 0
            ? null
            : $"交還畫面失敗（tscon 錯誤碼 {r.ExitCode}）。\n{r.Output.Trim()}\n\n" +
              "可以改用一般方式結束遠端桌面；電腦前面的人需要輸入 PIN 或密碼解鎖。";
    }
}
