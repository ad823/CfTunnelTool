using System.ServiceProcess;
using CfTunnelTool.Core;

namespace CfTunnelTool.UI;

/// <summary>已設定完成後的管理畫面：查看狀態、修改設定、重新啟動、移除。</summary>
sealed class DashboardForm : Form
{
    const int ColUrl = 0, ColService = 1, ColDns = 2, ColLocal = 3;

    ToolConfig _config;
    string? _token;
    readonly bool _demo;
    bool _busy;

    readonly Label _subtitle;
    readonly StatusCard _serviceCard, _tunnelCard, _checkedCard;
    readonly ListView _routes;
    readonly ListViewGroup _webGroup = new("網站／API／TCP");
    readonly ListViewGroup _rdpGroup = new("遠端桌面（瀏覽器 RDP ＋ 2FA）");
    readonly LogBox _log;
    readonly List<Button> _actions = [];
    readonly Button _copyRdp;

    public DashboardForm(ToolConfig config, bool demo = false)
    {
        _config = config;
        _demo = demo;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"CfTunnelTool 管理　v{Theme.Version}";
        Icon = Theme.AppIcon;
        Font = Theme.Base;
        BackColor = Color.White;
        ClientSize = new Size(1000, 700);
        MinimumSize = new Size(860, 600);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(28, 22, 28, 18),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void AddRow(Control c, SizeType type = SizeType.AutoSize, float height = 0)
        {
            root.RowStyles.Add(new RowStyle(type, height));
            root.Controls.Add(c, 0, root.RowCount++);
        }

        // ---- 標題 ----
        AddRow(new Label
        {
            Text = "CfTunnelTool 管理",
            AutoSize = true,
            Font = Theme.Heading,
            Margin = new Padding(0, 0, 0, 2),
        });
        _subtitle = new Label { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(2, 0, 0, 14) };
        AddRow(_subtitle);

        // ---- 狀態卡片 ----
        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 14),
        };
        for (int i = 0; i < 3; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        _serviceCard = new StatusCard("本機連線服務");
        _tunnelCard = new StatusCard("Cloudflare Tunnel");
        _checkedCard = new StatusCard("最後檢查時間");
        cards.Controls.Add(_serviceCard, 0, 0);
        cards.Controls.Add(_tunnelCard, 1, 0);
        cards.Controls.Add(_checkedCard, 2, 0);
        AddRow(cards);

        // ---- 網址清單 ----
        AddRow(new Label { Text = "對外網址與遠端桌面（點兩下開啟）", AutoSize = true, Font = Theme.Sub, Margin = new Padding(0, 0, 0, 6) });
        _routes = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 0, 10),
            ShowGroups = true,
        };
        _routes.Columns.Add("對外網址");
        _routes.Columns.Add("本機服務");
        _routes.Columns.Add("Cloudflare 設定");
        _routes.Columns.Add("本機連線狀態");
        _routes.Groups.Add(_webGroup);
        _routes.Groups.Add(_rdpGroup);
        _routes.Resize += (_, _) => ResizeColumns();
        _routes.DoubleClick += (_, _) => OpenSelected();
        AddRow(_routes, SizeType.Percent, 50);

        // ---- 操作按鈕 ----
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 10),
        };
        Button Action(Button b, Func<Task> handler)
        {
            b.Click += async (_, _) => await handler();
            b.Margin = new Padding(0, 0, 8, 6);
            _actions.Add(b);
            buttons.Controls.Add(b);
            return b;
        }
        Action(Theme.PrimaryButton("重新整理"), () => RefreshAsync(promptForToken: true));
        Action(Theme.SecondaryButton("開啟選取的網址"), () => { OpenSelected(); return Task.CompletedTask; });
        _copyRdp = Action(Theme.SecondaryButton("複製 RDP 連線資訊"), () => { CopyRdpInfo(); return Task.CompletedTask; });
        Action(Theme.SecondaryButton("重新啟動連線服務"), RestartServiceAsync);
        Action(Theme.SecondaryButton("修改設定…"), EditAsync);
        Action(Theme.SecondaryButton("紀錄…"), () =>
        {
            using var logs = new LogsForm(LogsMode.Admin, () => GetToken(prompt: true));
            logs.ShowDialog(this);
            return Task.CompletedTask;
        });
        Action(Theme.SecondaryButton("變更管理密碼…"), () =>
        {
            using var change = new AdminLoginForm(AdminLoginMode.ChangePassword, _config);
            change.ShowDialog(this);
            return Task.CompletedTask;
        });
        Action(Theme.SecondaryButton("Cloudflare 後台 ↗"), () => { Theme.OpenUrl("https://one.dash.cloudflare.com/"); return Task.CompletedTask; });
        Action(Theme.DangerButton("移除全部設定…"), RemoveAsync);
        AddRow(buttons);

        // ---- 記錄 ----
        AddRow(new Label { Text = "記錄", AutoSize = true, Font = Theme.Sub, Margin = new Padding(0, 0, 0, 6) });
        _log = new LogBox { Dock = DockStyle.Fill, Margin = new Padding(0) };
        AddRow(_log, SizeType.Percent, 50);

        Controls.Add(root);
        ResumeLayout(false);
        PerformLayout();

        LoadRoutes();
        _log.Info("連線服務在背景執行，關閉這個視窗不影響外網連線。");
        _log.Info("之後可以從開始功能表或桌面的「CfTunnelTool」捷徑再次開啟這個畫面。");
        Shown += async (_, _) => await RefreshAsync(promptForToken: false);
    }

    void LoadRoutes()
    {
        _subtitle.Text = $"Tunnel：{_config.TunnelName}　　網域：{_config.Domain}" +
            (_config.Rdp is { UseOwnVnet: true, VnetName: { } site } ? $"　　地點（虛擬網路）：cft-{site}" : "") +
            $"　　設定檔：{AppStorage.Dir}";
        _routes.BeginUpdate();
        _routes.Items.Clear();
        foreach (var r in _config.Routes)
        {
            var item = new ListViewItem(r.PublicUrl + (r.DisableBrowserCheck ? "（API）" : ""), _webGroup)
            {
                Tag = r,
                UseItemStyleForSubItems = false,
                ToolTipText = r.IsTcp ? $"TCP，沒有登入保護。使用者端：{r.ClientCommand()}"
                    : r.DisableBrowserCheck ? $"{r.PublicUrl}（API 用途：已關閉瀏覽器檢查，Python、curl 等程式可直接呼叫）"
                    : r.PublicUrl,
            };
            item.SubItems.Add(r.Service);
            item.SubItems.Add("—");
            item.SubItems.Add("—");
            item.ForeColor = Theme.AccentDark;
            _routes.Items.Add(item);
        }
        if (_config.RdpEnabled)
        {
            foreach (var m in _config.Rdp!.Machines)
            {
                var modes = string.Join("＋", new[] { _config.Rdp.WarpEnabled ? "WARP" : null, _config.Rdp.BrowserEnabled ? "瀏覽器" : null }
                    .Where(x => x is not null));
                var item = new ListViewItem($"{m.Name}（{modes}）", _rdpGroup)
                {
                    Tag = m,
                    UseItemStyleForSubItems = false,
                    ToolTipText = _config.Rdp.BrowserEnabled ? _config.Rdp.UrlFor(m) : "WARP：用遠端桌面連線（mstsc）連 " + m.Ip,
                };
                item.SubItems.Add($"RDP {m.Ip}:{m.Port}");
                item.SubItems.Add("—");
                item.SubItems.Add("—");
                item.ForeColor = Theme.AccentDark;
                _routes.Items.Add(item);
            }
        }
        _routes.ShowItemToolTips = true;
        _routes.EndUpdate();
        _copyRdp.Visible = _config.RdpEnabled;
        ResizeColumns();
    }

    void CopyRdpInfo()
    {
        if (!_config.RdpEnabled) return;
        Clipboard.SetText(Steps.DoneStep.BuildRdpInfo(_config.Rdp!));
        _log.Ok("遠端桌面連線資訊已複製，可以直接貼給使用者。");
    }

    void ResizeColumns()
    {
        var w = _routes.ClientSize.Width;
        if (w <= 0) return;
        _routes.Columns[ColUrl].Width = (int)(w * 0.36);
        _routes.Columns[ColService].Width = (int)(w * 0.28);
        _routes.Columns[ColDns].Width = (int)(w * 0.16);
        _routes.Columns[ColLocal].Width = w - _routes.Columns[0].Width - _routes.Columns[1].Width - _routes.Columns[2].Width - 2;
    }

    ListViewItem? FindItem(RouteConfig r) =>
        _routes.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag is RouteConfig rc && rc.Hostname == r.Hostname);

    ListViewItem? FindItem(RdpMachine m) =>
        _routes.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag is RdpMachine rm && rm.Name == m.Name);

    static void SetSub(ListViewItem item, int col, string text, Color color)
    {
        item.SubItems[col].Text = text;
        item.SubItems[col].ForeColor = color;
    }

    void OpenSelected()
    {
        if (_routes.SelectedItems.Count == 0 && _routes.Items.Count > 0) _routes.Items[0].Selected = true;
        if (_routes.SelectedItems.Count == 0) return;
        var item = _routes.SelectedItems[0];
        if (item.Tag is RdpMachine m && _config.Rdp is { } rdp)
        {
            if (rdp.BrowserEnabled) Theme.OpenUrl(rdp.UrlFor(m));
            else WarpClient.OpenRdp(new ClientMachine { Name = m.Name, Ip = m.Ip, Port = m.Port }); // 區網內直接用 mstsc 測試
            return;
        }
        if (item.Tag is RouteConfig { IsTcp: true } tcp)
        {
            // TCP 不能用瀏覽器開，改成複製給使用者的連線方式
            Clipboard.SetText(Steps.DoneStep.TcpClientInfo(tcp));
            _log.Ok($"{tcp.Hostname} 是 TCP 服務，已複製使用者端的連線方式：{tcp.ClientCommand()}");
            return;
        }
        if (item.Tag is RouteConfig route) Theme.OpenUrl(route.PublicUrl);
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var b in _actions) b.Enabled = !busy;
        UseWaitCursor = busy;
    }

    bool Confirm(string q) =>
        MessageBox.Show(this, q, "請確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    /// <summary>取得 API Token：記憶體 → 已儲存 → 詢問使用者。</summary>
    string? GetToken(bool prompt)
    {
        if (_demo) return null;
        _token ??= AppStorage.LoadToken();
        if (_token is not null || !prompt) return _token;

        using var dlg = new TokenPromptForm();
        if (dlg.ShowDialog(this) != DialogResult.OK) return null;
        _token = dlg.Token;
        if (dlg.Remember) AppStorage.SaveToken(_token);
        return _token;
    }

    // ================= 重新整理 =================

    async Task RefreshAsync(bool promptForToken)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            // 1. 本機服務（Windows 服務）
            var svc = Cloudflared.GetServiceStatus();
            if (svc is null) _serviceCard.Set("未安裝", Theme.Danger);
            else if (svc == ServiceControllerStatus.Running) _serviceCard.Set("● 執行中", Theme.Success);
            else _serviceCard.Set(svc.Value == ServiceControllerStatus.Stopped ? "● 已停止" : svc.Value.ToString(), Theme.Warning);

            // 2. 本機網站 / API
            foreach (var c in await Deployer.CheckLocalServicesAsync(_config.Routes))
                if (FindItem(c.Route) is { } item)
                    SetSub(item, ColLocal, c.Ok ? (c.Route.IsTcp ? "✓ 可以連線" : "✓ 有回應") : "✗ 沒有回應", c.Ok ? Theme.Success : Theme.Danger);

            // 2b. 遠端桌面：從這台電腦測試各台電腦的 RDP 連接埠
            if (_config.RdpEnabled)
            {
                var checks = _config.Rdp!.Machines.Select(async m => (m, ok: await RdpHost.TestTcpAsync(m.Ip, m.Port)));
                foreach (var (m, ok) in await Task.WhenAll(checks))
                    if (FindItem(m) is { } item)
                        SetSub(item, ColLocal, ok ? "✓ 可以連線" : "✗ 連不到", ok ? Theme.Success : Theme.Danger);
            }

            // 3. Cloudflare 線上狀態（需要 Token）
            var token = GetToken(promptForToken);
            if (_demo)
            {
                _tunnelCard.Set("● 已連線（TPE、KHH）", Theme.Success);
                foreach (ListViewItem item in _routes.Items) SetSub(item, ColDns, "✓ 正常", Theme.Success);
            }
            else if (token is null)
            {
                _tunnelCard.Set("按「重新整理」並輸入 Token", Theme.Muted);
            }
            else
            {
                using var cf = new CloudflareClient(token);
                var status = await new Deployer(cf, _log, Confirm).GetOnlineStatusAsync(_config);
                if (status.Tunnel is null)
                    _tunnelCard.Set("✗ 找不到 Tunnel", Theme.Danger);
                else if (status.Tunnel.Status == "healthy")
                    _tunnelCard.Set($"● 已連線（{string.Join("、", status.Tunnel.Connections.Select(c => c.ColoName).Distinct())}）", Theme.Success);
                else
                    _tunnelCard.Set(status.Tunnel.Status switch
                    {
                        "inactive" => "● 未連線",
                        "down" => "● 連線中斷",
                        "degraded" => "● 部分連線",
                        var s => s,
                    }, Theme.Warning);

                foreach (var r in status.Routes)
                    if (FindItem(r.Route) is { } item)
                        SetSub(item, ColDns, r.Dns == DnsState.Ok ? "✓ 正常" : "✗ " + r.Detail,
                            r.Dns == DnsState.Ok ? Theme.Success : Theme.Danger);

                if (status.Rdp is { } rdpStatus)
                {
                    foreach (var ms in rdpStatus.Machines)
                    {
                        if (FindItem(ms.Machine) is not { } item) continue;
                        var problems = new List<string>();
                        if (!rdpStatus.AppOk) problems.Add("應用程式");
                        if (!ms.TargetOk) problems.Add("目標");
                        if (!ms.RouteOk) problems.Add("路由");
                        if (problems.Count == 0) SetSub(item, ColDns, "✓ 正常", Theme.Success);
                        else SetSub(item, ColDns, "✗ 缺少" + string.Join("、", problems), Theme.Danger);
                    }
                    if (!rdpStatus.AppOk || rdpStatus.Machines.Any(x => !x.TargetOk || !x.RouteOk))
                        _log.Warn("遠端桌面的 Cloudflare 設定不完整，請按「修改設定…」重新部署。");
                }

                if (status.Tunnel?.Status is not null and not "healthy" && svc == ServiceControllerStatus.Running)
                    _log.Warn("服務在執行但 Tunnel 未連線：請確認這台電腦能上網，或按「重新啟動連線服務」。");
            }
            _checkedCard.Set(DateTime.Now.ToString("HH:mm:ss"), Theme.Text);
        }
        catch (CloudflareApiException ex)
        {
            _tunnelCard.Set("✗ 查詢失敗", Theme.Danger);
            _log.Error(ex.Message);
            if (ex.Message.Contains("Token")) _token = null; // 讓下次重新輸入
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _tunnelCard.Set("✗ 無法連線", Theme.Danger);
            _log.Error("無法連線到 Cloudflare，請確認網路：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ================= 操作 =================

    async Task RestartServiceAsync()
    {
        if (Cloudflared.GetServiceStatus() is null)
        {
            MessageBox.Show(this, "連線服務尚未安裝，請按「修改設定…」重新部署。", "CfTunnelTool",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        SetBusy(true);
        try
        {
            _log.Step("重新啟動連線服務");
            await Task.Run(Cloudflared.RestartService);
            _log.Ok("服務已重新啟動");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException
                                       or System.ComponentModel.Win32Exception)
        {
            _log.Error("重新啟動失敗：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
        await Task.Delay(3000);
        await RefreshAsync(promptForToken: false);
    }

    async Task EditAsync()
    {
        var state = WizardState.From(_config, GetToken(prompt: false));
        using (var wizard = new WizardForm(state))
            wizard.ShowDialog(this);

        if (state.Result is null) return;
        if (state.RememberToken) _token = state.ApiToken;
        _config = AppStorage.LoadConfig() ?? _config;
        LoadRoutes();
        _log.Ok("設定已更新");
        await RefreshAsync(promptForToken: false);
    }

    async Task RemoveAsync()
    {
        if (MessageBox.Show(this,
                "確定要移除全部設定嗎？這會：\n\n" +
                "•  停止並移除這台電腦的 Cloudflared 連線服務\n" +
                "•  刪除 Cloudflare 上的 Tunnel 與它的 DNS 紀錄\n" +
                "•  刪除本機儲存的設定、Token 與捷徑\n\n" +
                "移除後外網將無法再透過網址連到這台電腦。",
                "移除全部設定", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2)
            != DialogResult.Yes)
            return;

        var token = GetToken(prompt: true);
        if (token is null) return;

        SetBusy(true);
        try
        {
            using var cf = new CloudflareClient(token);
            await new Deployer(cf, _log, Confirm).RemoveAsync(_config);
            AppStorage.DeleteAll();
            SelfInstaller.Uninstall(_log);
            _log.Ok("全部移除完成");
            MessageBox.Show(this, "已移除全部設定。程式將會關閉。", "CfTunnelTool",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            _busy = false;
            Close();
        }
        catch (Exception ex) when (ex is CloudflareApiException or InvalidOperationException or HttpRequestException
                                       or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            _log.Error(ex.Message);
            MessageBox.Show(this, $"移除時發生錯誤：\n\n{ex.Message}\n\n可以修正後再試一次。", "CfTunnelTool",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }

    /// <summary>狀態卡片：小標題 + 大字狀態。</summary>
    sealed class StatusCard : Panel
    {
        readonly Label _value;

        public StatusCard(string title)
        {
            Dock = DockStyle.Fill;
            Height = 78;
            BackColor = Theme.Surface;
            Padding = new Padding(16, 12, 12, 10);
            Margin = new Padding(0, 0, 12, 0);

            _value = new Label
            {
                Text = "檢查中…",
                Dock = DockStyle.Fill,
                Font = Theme.CardValue,
                ForeColor = Theme.Muted,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            Controls.Add(_value);
            Controls.Add(new Label
            {
                Text = title,
                Dock = DockStyle.Top,
                Height = 22,
                Font = Theme.Small,
                ForeColor = Theme.Muted,
            });
        }

        public void Set(string text, Color color)
        {
            _value.Text = text;
            _value.ForeColor = color;
        }
    }
}
