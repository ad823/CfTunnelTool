using CfTunnelTool.Core;

namespace CfTunnelTool.UI.Steps;

sealed class TokenStep : StepPage
{
    const string TokenPageUrl = "https://dash.cloudflare.com/profile/api-tokens";

    readonly WizardState _state;
    readonly TextBox _token;
    readonly CheckBox _show;
    readonly Button _verify;
    readonly Label _status;
    readonly CheckBox _remember;
    bool _verifying;
    bool _loading;

    public override string SidebarTitle => "API Token";
    public override string Heading => "步驟 1／5　建立 Cloudflare API Token";
    public override bool CanGoNext => _state.TokenVerified && !_verifying;
    public override bool IsBusy => _verifying;

    public TokenStep(WizardState state)
    {
        _state = state;

        AddText("程式需要一組 API Token，才能代替你在 Cloudflare 建立連線通道和 DNS 紀錄。" +
                "Token 只會存在這台電腦上（經 Windows 加密），不會傳送到 Cloudflare 以外的地方。");

        AddHelpBox("如何建立 Token（約 2 分鐘）",
            "1.  按下方「開啟 Cloudflare Token 頁面」，並登入 Cloudflare\n" +
            "2.  按「Create Token」，捲到最下方「Create Custom Token」，按「Get started」\n" +
            "3.  Token name 填：CfTunnelTool\n" +
            "4.  在 Permissions 依下表設定權限（按「+ Add more」新增一列）；只用網站／API 設前三列即可，要用遠端桌面再加後三列\n" +
            "5.  Zone Resources 選「Include → Specific zone → 你的網域」\n" +
            "6.  按「Continue to summary」→「Create Token」，把顯示的 Token 複製後貼到下方");

        Add(BuildPermissionTable(), stretch: false);

        var open = Theme.SecondaryButton("開啟 Cloudflare Token 頁面 ↗");
        open.Click += (_, _) => Theme.OpenUrl(TokenPageUrl);
        AddRow(open);

        AddText("貼上 API Token：", Theme.Bold, top: 12, bottom: 2);
        _token = new TextBox
        {
            Width = 470,
            UseSystemPasswordChar = true,
            Font = Theme.Mono,
            Margin = new Padding(0, 4, 8, 0),
        };
        _token.TextChanged += (_, _) =>
        {
            if (_loading) return;
            _state.TokenVerified = false;
            SetStatus("", Theme.Muted);
            NotifyStateChanged();
        };
        _show = new CheckBox { Text = "顯示", AutoSize = true, Margin = new Padding(0, 7, 12, 0) };
        _show.CheckedChanged += (_, _) => _token.UseSystemPasswordChar = !_show.Checked;
        _verify = Theme.PrimaryButton("驗證 Token");
        _verify.Click += async (_, _) => await VerifyAsync();
        AddRow(_token, _show, _verify);

        _status = AddText("", top: 6, bottom: 4);

        _remember = new CheckBox
        {
            Text = "記住這組 Token（以 Windows 加密儲存，之後開啟管理畫面不用重新輸入）",
            AutoSize = true,
            Checked = true,
            Margin = new Padding(0, 6, 0, 0),
        };
        Add(_remember, stretch: false);
    }

    static TableLayoutPanel BuildPermissionTable()
    {
        string[,] rows =
        {
            { "類型", "權限項目", "層級", "用途" },
            { "Account", "Cloudflare Tunnel", "Edit", "必要" },
            { "Zone", "DNS", "Edit", "必要" },
            { "Zone", "Zone", "Read", "必要" },
            { "Account", "Access: Apps and Policies", "Edit", "遠端桌面" },
            { "Account", "Access: Organizations, Identity Providers, and Groups", "Edit", "遠端桌面" },
            { "Account", "Zero Trust", "Edit", "遠端桌面" },
        };
        int rowCount = rows.GetLength(0), colCount = rows.GetLength(1);
        var table = new TableLayoutPanel
        {
            ColumnCount = colCount,
            RowCount = rowCount,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            CellBorderStyle = TableLayoutPanelCellBorderStyle.Single,
            BackColor = Color.White,
            Margin = new Padding(0, 0, 0, 8),
        };
        for (int r = 0; r < rowCount; r++)
        for (int c = 0; c < colCount; c++)
        {
            var optional = r > 0 && rows[r, 3] != "必要";
            table.Controls.Add(new Label
            {
                Text = rows[r, c],
                AutoSize = true,
                Dock = DockStyle.Fill,
                Font = r == 0 || c == 2 ? Theme.Bold : Theme.Base,
                ForeColor = r == 0 ? Theme.Text
                    : c == 2 ? Theme.AccentDark
                    : c == 3 || optional ? Theme.Muted : Theme.Text,
                BackColor = r == 0 ? Theme.Surface : Color.White,
                Padding = new Padding(10, 5, 14, 5),
                Margin = new Padding(0),
            }, c, r);
        }
        return table;
    }

    public override void OnEnter()
    {
        _loading = true;
        _token.Text = _state.ApiToken;
        _remember.Checked = _state.RememberToken;
        _loading = false;

        if (_state.TokenVerified)
            SetStatus($"✓ Token 已驗證（可管理 {_state.Zones.Count} 個網域）", Theme.Success);
        else if (_token.Text.Length > 0 && !_verifying)
            _ = VerifyAsync(); // 有記住的 Token 就自動驗證

        if (IsHandleCreated) BeginInvoke(new Action(() => _token.Focus()));
    }

    public override bool OnNext()
    {
        _state.RememberToken = _remember.Checked;
        return true;
    }

    async Task VerifyAsync()
    {
        var token = _token.Text.Trim();
        if (token.Length == 0)
        {
            SetStatus("請先貼上 API Token。", Theme.Danger);
            return;
        }

        _verifying = true;
        _verify.Enabled = _token.Enabled = false;
        SetStatus("驗證中，請稍候…", Theme.Muted);
        NotifyStateChanged();

        try
        {
            using var cf = new CloudflareClient(token);
            var zones = await cf.ListZonesAsync();
            if (zones.Count == 0)
                throw new CloudflareApiException(
                    "Token 有效，但看不到任何網域。請確認建立 Token 時「Zone Resources」有包含你的網域，並有 Zone → Zone → Read 權限。");

            try { await cf.TestTunnelAccessAsync(zones[0].AccountId); }
            catch (CloudflareApiException)
            {
                throw new CloudflareApiException("Token 缺少「Account → Cloudflare Tunnel → Edit」權限。請到 Cloudflare 編輯這組 Token 加上此權限。");
            }
            try { await cf.TestDnsAccessAsync(zones[0].ZoneId); }
            catch (CloudflareApiException)
            {
                throw new CloudflareApiException("Token 缺少「Zone → DNS → Edit」權限。請到 Cloudflare 編輯這組 Token 加上此權限。");
            }

            _state.ApiToken = token;
            _state.Zones = zones;
            _state.TokenVerified = true;
            var names = string.Join("、", zones.Take(4).Select(z => z.Name)) + (zones.Count > 4 ? "…" : "");
            SetStatus($"✓ 驗證成功！這組 Token 可管理 {zones.Count} 個網域：{names}。請按「下一步」。", Theme.Success);
        }
        catch (CloudflareApiException ex)
        {
            _state.TokenVerified = false;
            SetStatus("✗ " + ex.Message, Theme.Danger);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _state.TokenVerified = false;
            SetStatus("✗ 無法連線到 Cloudflare，請確認這台電腦可以上網後再試一次。", Theme.Danger);
        }
        finally
        {
            _verifying = false;
            _verify.Enabled = _token.Enabled = true;
            NotifyStateChanged();
        }
    }

    void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
    }
}
