using CfTunnelTool.Core;

namespace CfTunnelTool.UI;

/// <summary>有顏色的記錄視窗；可從任何執行緒寫入。</summary>
sealed class LogBox : RichTextBox, ILog
{
    static readonly Color InfoColor = Color.FromArgb(200, 204, 210);
    static readonly Color StepColor = Color.FromArgb(110, 190, 255);
    static readonly Color OkColor = Color.FromArgb(110, 215, 140);
    static readonly Color WarnColor = Color.FromArgb(255, 200, 90);
    static readonly Color ErrorColor = Color.FromArgb(255, 120, 120);

    public LogBox()
    {
        ReadOnly = true;
        BackColor = Color.FromArgb(28, 31, 38);
        ForeColor = InfoColor;
        Font = Theme.Mono;
        BorderStyle = BorderStyle.None;
        DetectUrls = false;
        ScrollBars = RichTextBoxScrollBars.Vertical;
        WordWrap = true;
    }

    void Append(Color color, string prefix, string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => Append(color, prefix, message)));
            return;
        }
        SelectionStart = TextLength;
        SelectionLength = 0;
        SelectionColor = color;
        AppendText(prefix);
        SelectionColor = color == StepColor ? StepColor : InfoColor;
        AppendText(message + Environment.NewLine);
        ScrollToCaret();
    }

    /// <summary>是否同時寫入程式操作紀錄（截圖、自我測試時關閉，避免寫入假資料）。</summary>
    public static bool PersistToFile = true;

    void Log(string level, Color color, string prefix, string message)
    {
        if (PersistToFile) Core.Logs.OperationLog.Write(level, message);
        Append(color, prefix, message);
    }

    public void Info(string message) => Log("資訊", InfoColor, "   ", message);
    public void Step(string message) => Log("步驟", StepColor, "▶ ", message);
    public void Ok(string message) => Log("成功", OkColor, " ✓ ", message);
    public void Warn(string message) => Log("警告", WarnColor, " ! ", message);
    public void Error(string message) => Log("錯誤", ErrorColor, " ✗ ", message);
}
