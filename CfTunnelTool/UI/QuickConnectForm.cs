using CfTunnelTool.Core;

namespace CfTunnelTool.UI;

/// <summary>
/// 桌面捷徑（CfTunnelTool.exe --connect "名稱"）：不開使用者畫面，只顯示一個小視窗跑連線流程，
/// 需要時自動開驗證頁，連上後開啟遠端桌面就結束。以一般權限執行，不會跳出 UAC。
/// </summary>
sealed class QuickConnectForm : Form, ILog
{
    readonly ClientConfig _config;
    readonly ClientMachine _machine;
    readonly Label _status;

    QuickConnectForm(ClientConfig config, ClientMachine machine)
    {
        _config = config;
        _machine = machine;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"CfTunnelTool － 連線到 {machine.Name}";
        Icon = Theme.AppIcon;
        Font = Theme.Base;
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(24, 18, 24, 18) };
        layout.Controls.Add(new Label
        {
            Text = $"連線到 {machine.Name}（{machine.Ip}）", AutoSize = true, Font = Theme.Sub, Margin = new Padding(0, 0, 0, 8),
        });
        _status = new Label
        {
            Text = "準備中…", AutoSize = true, ForeColor = Theme.Muted, MinimumSize = new Size(380, 0), MaximumSize = new Size(440, 0),
        };
        layout.Controls.Add(_status);
        Controls.Add(layout);
        ResumeLayout(false);
        PerformLayout();

        Shown += async (_, _) => await RunAsync();
    }

    async Task RunAsync()
    {
        var flow = new RdpConnectFlow(_config, this, this, () => AppStorage.SaveClient(_config));
        bool ok;
        try
        {
            ok = await flow.ConnectAsync(_machine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ok = false;
            MessageBox.Show(this, "無法開啟遠端桌面：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        if (!ok && flow.LastError is { } error)
        {
            WarpClient.OpenGui();
            MessageBox.Show(this, error + "\n\n也可以開啟 CfTunnelTool 查看連線狀態。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        Close();
    }

    void SetStatus(string message, Color color)
    {
        if (IsDisposed) return;
        _status.Text = message;
        _status.ForeColor = color;
    }

    public void Info(string message) => SetStatus(message, Theme.Muted);
    public void Step(string message) => SetStatus(message + "…", Theme.Text);
    public void Ok(string message) => SetStatus(message, Theme.Success);
    public void Warn(string message) => SetStatus(message, Theme.Warning);
    public void Error(string message) => SetStatus(message, Theme.Danger);

    /// <summary>執行桌面捷徑的連線；找不到連線清單或電腦時顯示說明。</summary>
    public static int Run(ConnectRequest request)
    {
        ClientConfig? config;
        try { config = AppStorage.LoadClient(); }
        catch (UnauthorizedAccessException) { config = null; }

        if (config is null)
        {
            MessageBox.Show("這台電腦還沒有匯入公司的連線代碼。\n請開啟 CfTunnelTool，選「我要連到公司的電腦」並貼上連線資訊。",
                "CfTunnelTool", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
        if (ConnectArgs.Find(config.Machines, request) is not { } machine)
        {
            MessageBox.Show($"連線清單裡找不到「{request.Name}」，可能已被刪除或改名。\n請開啟 CfTunnelTool，按「建立桌面捷徑」重新建立捷徑。",
                "CfTunnelTool", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
        if (!WarpClient.IsInstalled)
        {
            MessageBox.Show("這台電腦沒有安裝 Cloudflare WARP。\n請開啟 CfTunnelTool 重新匯入連線代碼，程式會自動安裝。",
                "CfTunnelTool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }

        Application.Run(new QuickConnectForm(config, machine));
        return 0;
    }
}
