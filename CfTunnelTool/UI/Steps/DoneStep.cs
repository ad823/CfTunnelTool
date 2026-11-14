using CfTunnelTool.Core;

namespace CfTunnelTool.UI.Steps;

sealed class DoneStep : StepPage
{
    readonly WizardState _state;
    readonly Label _status;
    readonly Label _webTitle;
    readonly FlowLayoutPanel _links;
    readonly Label _rdpTitle;
    readonly FlowLayoutPanel _rdpLinks;
    readonly FlowLayoutPanel _rdpButtons;

    public override string SidebarTitle => "完成";
    public override string Heading => "設定完成！";
    public override string NextText => "完成，開啟管理畫面";
    public override bool CanGoBack => false;

    public DoneStep(WizardState state)
    {
        _state = state;

        _status = AddText("", Theme.Sub, Theme.Success, bottom: 10);

        _webTitle = AddText("網站／API（點一下即可開啟）：", Theme.Bold);
        _links = AddLinkPanel();

        _rdpTitle = AddText("遠端桌面（把連結和 2FA 註冊網址提供給使用者）：", Theme.Bold, top: 6);
        _rdpLinks = AddLinkPanel();
        var copy = Theme.SecondaryButton("複製連線資訊（給使用者）");
        copy.Click += (_, _) => CopyRdpInfo();
        _rdpButtons = AddRow(copy);

        AddHelpBox("接下來你需要知道的事",
            "•  連線服務已安裝為 Windows 服務並設定開機自動啟動，關閉這個程式也不影響連線。\n" +
            "•  開始功能表和桌面已建立「CfTunnelTool」捷徑，之後可以用它查看狀態、修改設定或移除設定。\n" +
            "•  新網址通常幾秒到幾分鐘內就能連線；如果打不開，可以等一下再試，或到管理畫面按「重新整理」。\n" +
            "•  遠端桌面使用者第一次登入時，會用 Email 驗證碼登入並註冊 2FA 裝置，之後每次都要通過 2FA。");

        AddHelpBox("資安提醒",
            "公開的網站和 API 任何人都能連線，請務必做好登入驗證；只給內部人員使用的網址，" +
            "可以到 Cloudflare 後台 → Zero Trust → Access → Applications 加上登入保護。\n" +
            "遠端桌面已經有 Email ＋ 2FA 保護，但 Windows 帳號仍請使用強密碼。",
            Theme.WarnBack, Theme.Danger);
    }

    FlowLayoutPanel AddLinkPanel() => Add(new FlowLayoutPanel
    {
        FlowDirection = FlowDirection.TopDown,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = false,
        Margin = new Padding(12, 2, 0, 10),
    }, stretch: false);

    static void AddLink(FlowLayoutPanel panel, string text, string url, string note, Font font)
    {
        var link = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            Font = font,
            LinkColor = Theme.AccentDark,
            ActiveLinkColor = Theme.Accent,
            Margin = new Padding(0, 4, 0, 0),
        };
        link.LinkClicked += (_, _) => Theme.OpenUrl(url);
        panel.Controls.Add(link);
        panel.Controls.Add(new Label
        {
            Text = note,
            AutoSize = true,
            ForeColor = Theme.Muted,
            Margin = new Padding(2, 0, 0, 6),
        });
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
            AddLink(_links, $"https://{r.Hostname}", $"https://{r.Hostname}", $"→ 這台電腦的 {r.Service}", Theme.Sub);
        _webTitle.Visible = _links.Visible = _state.Routes.Count > 0;

        _rdpLinks.Controls.Clear();
        var rdp = _state.Rdp is { Enabled: true } r2 ? r2 : null;
        if (rdp is not null)
        {
            if (rdp.BrowserEnabled)
                foreach (var m in rdp.Machines)
                {
                    var url = rdp.UrlFor(m);
                    AddLink(_rdpLinks, $"{m.Name}（{m.Ip}）瀏覽器連線", url, url, Theme.Bold);
                }
            if (rdp.WarpEnabled)
                _rdpLinks.Controls.Add(new Label
                {
                    Text = "WARP 使用者：按下方「複製連線資訊」傳給使用者，裡面的連線代碼貼到 CfTunnelTool 的使用者模式即可。",
                    AutoSize = true,
                    ForeColor = Theme.Text,
                    Margin = new Padding(0, 4, 0, 6),
                });
            if (rdp.MfaEnrollUrl is { } enroll)
                AddLink(_rdpLinks, "註冊 2FA 裝置", enroll, enroll + "　（使用者第一次使用前先到這裡註冊）", Theme.Bold);
        }
        _rdpTitle.Visible = _rdpLinks.Visible = _rdpButtons.Visible = rdp is not null;
    }

    /// <summary>給遠端桌面使用者的連線說明，可以直接貼到 Email 或通訊軟體。</summary>
    public static string BuildRdpInfo(RdpConfig rdp)
    {
        var lines = new List<string> { "【遠端桌面連線方式】", "" };
        if (rdp.MfaEnrollUrl is { } enroll)
        {
            lines.Add("■ 第一次使用：先註冊 2FA");
            lines.Add("  開啟下面的網址，用你的 Email 收驗證碼登入，再依畫面註冊 2FA");
            lines.Add("  （建議用手機的 Google / Microsoft Authenticator 掃描 QR Code）");
            lines.Add($"  {enroll}");
            lines.Add("");
        }

        if (rdp.WarpEnabled)
        {
            lines.Add("■ 方式一：WARP ＋ 遠端桌面連線（建議，最流暢）");
            lines.Add("  1. 下載 CfTunnelTool（向管理者索取），在你的電腦執行，選「我要連到公司的電腦」");
            lines.Add("  2. 貼上整段連線資訊（或下面的連線代碼），按「安裝並加入組織」");
            lines.Add("  3. 依畫面用 Email 驗證碼登入 WARP，完成後桌面會出現遠端桌面捷徑");
            lines.Add("  4. 點捷徑連線，輸入那台電腦的 Windows 帳號密碼；右下角跳出驗證通知時，點它完成 2FA");
            lines.Add($"  連線代碼：{ClientConfig.FromRdp(rdp).ToCode()}");
            lines.Add("");
        }

        if (rdp.BrowserEnabled)
        {
            lines.Add(rdp.WarpEnabled ? "■ 方式二：瀏覽器（免安裝）" : "■ 用瀏覽器連線");
            lines.Add("  開啟對應電腦的網址，依序輸入 Email 驗證碼、2FA 驗證碼、Windows 帳號密碼：");
            foreach (var m in rdp.Machines)
                lines.Add($"  {m.Name}：{rdp.UrlFor(m)}");
            lines.Add("  建議使用 Chrome 或 Edge 瀏覽器。");
        }
        return string.Join(Environment.NewLine, lines);
    }

    void CopyRdpInfo()
    {
        if (_state.Rdp is not { Enabled: true } rdp) return;
        Clipboard.SetText(BuildRdpInfo(rdp));
        MessageBox.Show(FindForm(), "連線資訊已複製，可以直接貼到 Email 或通訊軟體提供給使用者。", "已複製",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
