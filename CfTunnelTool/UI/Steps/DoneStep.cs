namespace CfTunnelTool.UI.Steps;

sealed class DoneStep : StepPage
{
    readonly WizardState _state;
    readonly Label _status;
    readonly FlowLayoutPanel _links;

    public override string SidebarTitle => "完成";
    public override string Heading => "設定完成！";
    public override string NextText => "完成，開啟管理畫面";
    public override bool CanGoBack => false;

    public DoneStep(WizardState state)
    {
        _state = state;

        _status = AddText("", Theme.Sub, Theme.Success, bottom: 10);
        AddText("現在可以從任何地方用以下網址連到這台電腦（點一下即可開啟）：");

        _links = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(12, 4, 0, 10),
        };
        Add(_links, stretch: false);

        AddHelpBox("接下來你需要知道的事",
            "•  連線服務已安裝為 Windows 服務並設定開機自動啟動，關閉這個程式也不影響連線。\n" +
            "•  開始功能表和桌面已建立「CfTunnelTool」捷徑，之後可以用它查看狀態、修改網址或移除設定。\n" +
            "•  新網址通常幾秒到幾分鐘內就能連線；如果打不開，可以等一下再試，或到管理畫面按「重新整理」。");

        AddHelpBox("資安提醒",
            "你的網站和 API 現在已經公開在網路上，任何人都能連線，請務必做好登入驗證。\n" +
            "只給內部人員使用的網址，可以到 Cloudflare 後台 → Zero Trust → Access → Applications 加上登入保護" +
            "（例如只允許公司 Email 登入），不需要修改程式。",
            Theme.WarnBack, Theme.Danger);
    }

    public override void OnEnter()
    {
        var result = _state.Result;
        if (result is { Healthy: true, DnsFailures: 0 })
        {
            _status.Text = "✓ Tunnel 已連線，一切正常";
            _status.ForeColor = Theme.Success;
        }
        else if (result is { DnsFailures: > 0 })
        {
            _status.Text = $"! 有 {result.DnsFailures} 個網址的 DNS 沒有設定成功，請回到管理畫面查看";
            _status.ForeColor = Theme.Warning;
        }
        else
        {
            _status.Text = $"! Tunnel 尚未顯示為已連線（狀態：{result?.Status}），通常再等幾十秒即可";
            _status.ForeColor = Theme.Warning;
        }

        _links.Controls.Clear();
        foreach (var r in _state.Routes)
        {
            var url = $"https://{r.Hostname}";
            var link = new LinkLabel
            {
                Text = url,
                AutoSize = true,
                Font = Theme.Sub,
                LinkColor = Theme.AccentDark,
                ActiveLinkColor = Theme.Accent,
                Margin = new Padding(0, 4, 0, 0),
            };
            link.LinkClicked += (_, _) => Theme.OpenUrl(url);
            _links.Controls.Add(link);
            _links.Controls.Add(new Label
            {
                Text = $"→ 這台電腦的 {r.Service}",
                AutoSize = true,
                ForeColor = Theme.Muted,
                Margin = new Padding(2, 0, 0, 6),
            });
        }
    }
}
