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

    public void Info(string message) => Append(InfoColor, "   ", message);
    public void Step(string message) => Append(StepColor, "▶ ", message);
    public void Ok(string message) => Append(OkColor, " ✓ ", message);
    public void Warn(string message) => Append(WarnColor, " ! ", message);
    public void Error(string message) => Append(ErrorColor, " ✗ ", message);
}
