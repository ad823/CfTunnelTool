using CfTunnelTool.Core;

namespace CfTunnelTool.UI;

/// <summary>
/// 使用者模式：在使用者自己的電腦安裝 WARP、加入公司組織，並用原生遠端桌面連到公司電腦。
/// </summary>
sealed class ClientForm : Form
{
    ClientConfig? _config;
    readonly bool _demo;
    bool _busy;

    readonly TextBox _code;
    readonly Label _parsed;
    readonly Button _install;
    readonly Label _warpStatus;
    readonly Button _openWarp;
    readonly FlowLayoutPanel _machines;
    readonly LogBox _log;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 4000 };

    public ClientForm(ClientConfig? config, bool demo = false)
    {
        _config = config;
        _demo = demo;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"CfTunnelTool － 連到公司的電腦　v{Theme.Version}";
        Icon = Theme.AppIcon;
        Font = Theme.Base;
        BackColor = Color.White;
        ClientSize = new Size(900, 720);
        MinimumSize = new Size(820, 640);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(28, 20, 28, 16) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void AddRow(Control c, SizeType type = SizeType.AutoSize, float h = 0)
        {
            root.RowStyles.Add(new RowStyle(type, h));
            if (type == SizeType.AutoSize && c is not TableLayoutPanel) c.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            root.Controls.Add(c, 0, root.RowCount++);
        }
        static Label Lbl(string t, Font? f = null, Color? c = null, int top = 0, int bottom = 4) => new()
        {
            Text = t, AutoSize = true, Font = f ?? Theme.Base, ForeColor = c ?? Theme.Text, Margin = new Padding(0, top, 0, bottom),
        };
        static FlowLayoutPanel Row(params Control[] cs)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 6) };
            f.Controls.AddRange(cs);
            return f;
        }

        AddRow(Lbl("連到公司的電腦", Theme.Heading, bottom: 2));
        AddRow(Lbl("使用 Cloudflare WARP ＋ Windows 遠端桌面連線，連線前需要通過 Email 驗證碼與 2FA。", null, Theme.Muted, bottom: 14));

        // ---- 1. 連線代碼 ----
        AddRow(Lbl("1.  貼上管理者給你的連線資訊（整段貼上即可）", Theme.Bold));
        _code = new TextBox { Multiline = true, Height = 70, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono, Margin = new Padding(0, 4, 0, 4) };
        _code.TextChanged += (_, _) => ParseCode();
        AddRow(_code);
        _parsed = Lbl("", Theme.Small, Theme.Muted, bottom: 10);
        AddRow(_parsed);

        // ---- 2. 安裝 WARP ----
        AddRow(Lbl("2.  安裝 WARP 並加入公司（第一次需要，約 2 分鐘）", Theme.Bold));
        _install = Theme.PrimaryButton("安裝並加入組織");
        _install.MinimumSize = new Size(150, 38);
        _install.Click += async (_, _) => await InstallAsync();
        _openWarp = Theme.SecondaryButton("開啟 WARP 登入");
        _openWarp.Click += (_, _) => WarpClient.OpenGui();
        _warpStatus = Lbl("WARP 狀態：檢查中…", null, Theme.Muted, top: 10);
        AddRow(Row(_install, _openWarp, _warpStatus));

        // ---- 3. 連線 ----
        AddRow(Lbl("3.  連線（WARP 顯示「已連線」後）", Theme.Bold, top: 8));
        _machines = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 6) };
        AddRow(_machines);

        var help = new Label
        {
            Text = "•  安裝後 WARP 會開啟登入畫面：輸入你的 Email，收信取得驗證碼；第一次會要求註冊 2FA（例如用手機 Authenticator App 掃 QR Code）\n" +
                   "•  連線時，如果右下角跳出「Authentication required」通知，點它完成 2FA，再重新連線一次\n" +
                   "•  遠端桌面要求輸入帳密時，輸入「公司那台電腦」的 Windows 帳號密碼（格式如 電腦名稱\\帳號）\n" +
                   "•  桌面上會有「遠端桌面 - 名稱」捷徑，之後直接點它就能連線",
            AutoSize = true,
            BackColor = Theme.HelpBack,
            Padding = new Padding(14, 10, 14, 10),
            Margin = new Padding(0, 6, 0, 10),
        };
        AddRow(help);

        _log = new LogBox { Dock = DockStyle.Fill, Margin = new Padding(0) };
        AddRow(_log, SizeType.Percent, 100);

        Controls.Add(root);
        ResumeLayout(false);
        PerformLayout();

        if (_config is not null)
        {
            _code.Text = _config.ToCode();
        }
        RenderMachines();
        _timer.Tick += async (_, _) => await RefreshStatusAsync();
        Shown += async (_, _) =>
        {
            await RefreshStatusAsync();
            if (!_demo) _timer.Start();
        };
        FormClosed += (_, _) => _timer.Dispose();
    }

    void ParseCode()
    {
        var cfg = ClientConfig.TryParse(_code.Text);
        if (cfg is null)
        {
            _parsed.Text = _code.Text.Trim().Length == 0 ? "" : "✗ 找不到有效的連線代碼（以 CFT1: 開頭），請確認整段複製完整。";
            _parsed.ForeColor = Theme.Danger;
            return;
        }
        _config = cfg;
        _parsed.Text = $"✓ 組織：{cfg.Team}　　電腦：{string.Join("、", cfg.Machines.Select(m => $"{m.Name}（{m.Ip}）"))}";
        _parsed.ForeColor = Theme.Success;
        RenderMachines();
    }

    void RenderMachines()
    {
        _machines.Controls.Clear();
        if (_config is null) return;
        foreach (var m in _config.Machines)
        {
            var b = Theme.SecondaryButton($"連線到 {m.Name}（{m.Ip}）");
            b.Margin = new Padding(0, 0, 8, 6);
            b.Click += (_, _) =>
            {
                var status = WarpClient.GetStatus();
                if (!WarpClient.IsConnected(status) &&
                    MessageBox.Show(this, $"WARP 目前狀態是「{status}」，可能連不上。\n\n仍要開啟遠端桌面嗎？", "WARP 尚未連線",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                WarpClient.OpenRdp(m);
            };
            _machines.Controls.Add(b);
        }
    }

    async Task RefreshStatusAsync()
    {
        if (_busy) return;
        var status = await Task.Run(WarpClient.GetStatus);
        if (IsDisposed) return;
        if (WarpClient.IsConnected(status))
        {
            _warpStatus.Text = "WARP 狀態：● 已連線";
            _warpStatus.ForeColor = Theme.Success;
        }
        else if (status == "未安裝")
        {
            _warpStatus.Text = "WARP 狀態：尚未安裝";
            _warpStatus.ForeColor = Theme.Muted;
        }
        else if (WarpClient.NeedsRegistration(status))
        {
            _warpStatus.Text = "WARP 狀態：尚未登入，請按「開啟 WARP 登入」";
            _warpStatus.ForeColor = Theme.Warning;
        }
        else
        {
            _warpStatus.Text = $"WARP 狀態：{status}";
            _warpStatus.ForeColor = Theme.Warning;
        }
        _openWarp.Enabled = WarpClient.IsInstalled;
    }

    async Task InstallAsync()
    {
        if (_config is null)
        {
            MessageBox.Show(this, "請先貼上管理者給你的連線資訊。", "連到公司的電腦", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _busy = true;
        _install.Enabled = false;
        UseWaitCursor = true;
        try
        {
            AppStorage.SaveClient(_config);
            if (WarpClient.IsInstalled)
            {
                _log.Step("WARP 已安裝，設定組織名稱");
                await WarpClient.SetOrganizationAsync(_config.Team, _log);
            }
            else
            {
                _log.Step("安裝 WARP");
                await WarpClient.InstallAsync(_config.Team, _log);
            }

            _log.Step("建立遠端桌面捷徑");
            WarpClient.CreateRdpShortcuts(_config.Machines, _log);

            _log.Step("安裝 CfTunnelTool 捷徑（之後可再開啟這個畫面）");
            try { SelfInstaller.Install(_log); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                           or System.Runtime.InteropServices.COMException)
            {
                _log.Warn("建立捷徑失敗（不影響連線）：" + ex.Message);
            }

            _log.Step("登入 WARP");
            _log.Info("接下來 WARP 會開啟登入畫面：輸入你的 Email、收信取得驗證碼；第一次會要求註冊 2FA。");
            _log.Info("如果沒有自動出現，請按「開啟 WARP 登入」，或點右下角的 WARP 圖示 → 登入。");
            WarpClient.OpenGui();
            await Task.Run(WarpClient.Connect);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException
                                       or UnauthorizedAccessException or TaskCanceledException
                                       or System.ComponentModel.Win32Exception)
        {
            _log.Error(ex.Message);
        }
        finally
        {
            _busy = false;
            _install.Enabled = true;
            UseWaitCursor = false;
            await RefreshStatusAsync();
        }
    }
}
