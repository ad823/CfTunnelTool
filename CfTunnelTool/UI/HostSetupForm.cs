namespace CfTunnelTool.UI;

/// <summary>
/// 「這台電腦要被遠端連線」：給區網內被開放的電腦使用（不需要 Cloudflare 帳號，也不用部署），
/// 啟用遠端桌面、設定遠端登入密碼並保留開機自動登入、建立「結束遠端並交還畫面」捷徑。
/// </summary>
sealed class HostSetupForm : Form
{
    readonly HostAccountPanel _panel;

    public HostSetupForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"CfTunnelTool － 這台電腦要被遠端連線　v{Theme.Version}";
        Icon = Theme.AppIcon;
        Font = Theme.Base;
        BackColor = Color.White;
        ClientSize = new Size(900, 640);
        MinimumSize = new Size(820, 560);
        StartPosition = FormStartPosition.CenterScreen;

        var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(28, 18, 28, 8) };
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(0, 0, 12, 0),
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Label Lbl(string t, Font f, Color c, int bottom) => new()
        {
            Text = t, AutoSize = true, Font = f, ForeColor = c, Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 0, 0, bottom),
        };
        stack.Controls.Add(Lbl("這台電腦要被遠端連線", Theme.Heading, Theme.Text, 2));
        stack.Controls.Add(Lbl("在公司內要讓別人遠端連進來的電腦上做這些設定。不需要 Cloudflare 帳號，也不用部署；" +
                                "Cloudflare 的設定由中繼電腦的管理者負責（把這台電腦的 IP 加進「要開放的電腦」）。",
                                Theme.Base, Theme.Muted, 6));
        _panel = new HostAccountPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        stack.Controls.Add(_panel);

        var ip = Core.RdpHost.GetLanIPv4();
        stack.Controls.Add(new Label
        {
            Text = $"這台電腦的區網 IP：{ip ?? "（找不到）"}　　電腦名稱：{Environment.MachineName}\n" +
                   "請把這個 IP 提供給管理者；建議在路由器設定 DHCP 保留，讓 IP 不會改變。",
            AutoSize = true,
            BackColor = Theme.HelpBack,
            Padding = new Padding(14, 8, 14, 8),
            Margin = new Padding(0, 14, 0, 8),
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        });

        // ---- 遠端桌面使用紀錄 ----
        stack.Controls.Add(Lbl("遠端桌面使用紀錄：", Theme.Bold, Theme.Text, 2));
        stack.Controls.Add(Lbl("記錄誰在什麼時候遠端連進這台電腦、使用多久、登入失敗等。啟用後每小時自動收集（Windows 排程），" +
                               "紀錄存在這台電腦的 C:\\ProgramData\\CfTunnelTool\\logs。", Theme.Small, Theme.Muted, 4));
        var logs = Theme.SecondaryButton("查看與設定使用紀錄…");
        logs.Click += (_, _) =>
        {
            using var f = new LogsForm(LogsMode.Host);
            f.ShowDialog(this);
        };
        stack.Controls.Add(logs);
        scroller.Controls.Add(stack);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Theme.Surface };
        var close = Theme.PrimaryButton("完成");
        close.MinimumSize = new Size(120, 40);
        close.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        close.Location = new Point(footer.Width - 150, 12);
        close.Click += (_, _) => Close();
        footer.Controls.Add(close);
        footer.Resize += (_, _) => close.Left = footer.ClientSize.Width - close.Width - 28;

        Controls.Add(scroller);
        Controls.Add(footer);
        ResumeLayout(false);
        PerformLayout();

        Shown += async (_, _) => await _panel.LoadAsync();
    }
}
