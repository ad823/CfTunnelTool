namespace CfTunnelTool.UI;

enum AppMode { None, Admin, User, Host }

/// <summary>第一次執行時選擇：管理者（設定這台電腦對外）或使用者（連到公司電腦）。</summary>
sealed class ModeChooserForm : Form
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public AppMode Mode { get; private set; } = AppMode.None;

    public ModeChooserForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"CfTunnelTool　v{Theme.Version}";
        Icon = Theme.AppIcon;
        Font = Theme.Base;
        BackColor = Color.White;
        ClientSize = new Size(1040, 480);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            Padding = new Padding(36, 28, 36, 28),
        };
        for (int i = 0; i < 3; i++) root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var title = new Label { Text = "歡迎使用 CfTunnelTool", AutoSize = true, Font = Theme.Heading, Margin = new Padding(0, 0, 0, 4) };
        root.Controls.Add(title, 0, 0);
        root.SetColumnSpan(title, 3);
        var sub = new Label { Text = "請選擇你要做的事：", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(2, 0, 0, 18) };
        root.Controls.Add(sub, 0, 1);
        root.SetColumnSpan(sub, 3);

        root.Controls.Add(Card("我是管理者",
            "設定這台電腦，讓外網可以用網址連到這台電腦的網站、API，或透過這台電腦遠端桌面連到公司內的電腦。",
            "開始設定  >", AppMode.Admin, primary: true), 0, 2);
        root.Controls.Add(Card("我要連到公司的電腦",
            "在自己的電腦安裝連線程式（WARP），用管理者給你的連線代碼加入公司，之後點桌面捷徑就能遠端桌面。",
            "連到公司電腦  >", AppMode.User, primary: false), 1, 2);
        root.Controls.Add(Card("這台電腦要被遠端連線",
            "公司內要讓別人遠端連進來的電腦：啟用遠端桌面、設定遠端登入密碼（保留開機自動登入），並建立「結束遠端並交還畫面」捷徑。",
            "設定這台電腦  >", AppMode.Host, primary: false), 2, 2);

        Controls.Add(root);
        ResumeLayout(false);
        PerformLayout();
    }

    Control Card(string heading, string body, string button, AppMode mode, bool primary)
    {
        var card = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Surface,
            Padding = new Padding(20, 18, 20, 18),
            Margin = new Padding(0, 0, 12, 0),
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.Controls.Add(new Label { Text = heading, AutoSize = true, Font = Theme.Sub, Margin = new Padding(0, 0, 0, 8) }, 0, 0);
        card.Controls.Add(new Label
        {
            Text = body,
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            ForeColor = Theme.Text,
        }, 0, 1);
        var b = primary ? Theme.PrimaryButton(button) : Theme.SecondaryButton(button);
        b.MinimumSize = new Size(170, 40);
        b.Anchor = AnchorStyles.Left;
        b.Click += (_, _) => { Mode = mode; Close(); };
        card.Controls.Add(b, 0, 2);
        return card;
    }
}
