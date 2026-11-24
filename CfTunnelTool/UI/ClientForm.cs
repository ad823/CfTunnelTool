using CfTunnelTool.Core;

namespace CfTunnelTool.UI;

/// <summary>
/// 使用者模式：用 Cloudflare WARP ＋ Windows 遠端桌面連線連到公司的電腦。
/// 功能：連線清單（新增／編輯／刪除／連線）、WARP 中斷與重新連線、2FA 狀態與重新驗證、移除公司連線。
/// </summary>
sealed class ClientForm : Form
{
    const int ColName = 0, ColSite = 1, ColAddress = 2, ColUser = 3, ColLast = 4, ColStatus = 5;

    ClientConfig? _config;
    readonly bool _demo;
    bool _busy;
    bool _warpConnected;

    readonly Label _warpStatus, _mfaStatus, _parsed;
    readonly Button _toggleWarp, _openWarp, _reauth, _import, _toggleImport, _remove;
    readonly ListView _list;
    readonly TableLayoutPanel _importPanel;
    readonly TextBox _code;
    readonly LogBox _log;
    readonly List<Button> _actions = [];
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };

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
        ClientSize = new Size(960, 800);
        MinimumSize = new Size(880, 700);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(28, 18, 28, 14) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void AddRow(Control c, SizeType type = SizeType.AutoSize, float h = 0)
        {
            root.RowStyles.Add(new RowStyle(type, h));
            if (type == SizeType.Percent) c.Dock = DockStyle.Fill;
            root.Controls.Add(c, 0, root.RowCount++);
        }
        static Label Lbl(string t, Font? f = null, Color? c = null, int top = 0, int bottom = 4) => new()
        {
            Text = t, AutoSize = true, Font = f ?? Theme.Base, ForeColor = c ?? Theme.Text, Margin = new Padding(0, top, 0, bottom),
        };
        static FlowLayoutPanel Row(params Control[] cs)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 4) };
            f.Controls.AddRange(cs);
            return f;
        }
        Button Act(Button b, Func<Task> handler)
        {
            b.Margin = new Padding(0, 0, 8, 4);
            b.Click += async (_, _) => await handler();
            _actions.Add(b);
            return b;
        }

        AddRow(Lbl("連到公司的電腦", Theme.Heading, bottom: 2));
        AddRow(Lbl("使用 Cloudflare WARP ＋ Windows 遠端桌面連線，連線前需要通過 Email 驗證碼與 2FA。", null, Theme.Muted, bottom: 10));

        // ---- 狀態列 ----
        var status = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            BackColor = Theme.Surface,
            Padding = new Padding(14, 8, 14, 8),
            Margin = new Padding(0, 0, 0, 10),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
        };
        _warpStatus = Lbl("WARP：檢查中…", Theme.Bold, Theme.Muted, top: 9, bottom: 0);
        _warpStatus.MinimumSize = new Size(380, 0);
        _toggleWarp = Act(Theme.SecondaryButton("中斷連線"), ToggleWarpAsync);
        _openWarp = Act(Theme.SecondaryButton("開啟 WARP"), () => { WarpClient.OpenGui(); return Task.CompletedTask; });
        status.Controls.Add(Row(_warpStatus, _toggleWarp, _openWarp));
        _mfaStatus = Lbl("2FA：—", null, Theme.Muted, top: 9, bottom: 0);
        _mfaStatus.MinimumSize = new Size(380, 0);
        _reauth = Act(Theme.SecondaryButton("立即重新驗證"), ReauthenticateAsync);
        status.Controls.Add(Row(_mfaStatus, _reauth));
        AddRow(status);

        // ---- 連線清單 ----
        AddRow(Lbl("我的連線（點兩下連線）", Theme.Sub, bottom: 4));
        _list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 0, 6),
        };
        _list.Columns.Add("名稱");
        _list.Columns.Add("地點");
        _list.Columns.Add("位址");
        _list.Columns.Add("預設帳號");
        _list.Columns.Add("上次連線");
        _list.Columns.Add("狀態");
        _list.Resize += (_, _) => ResizeColumns();
        _list.DoubleClick += async (_, _) => await ConnectSelectedAsync();
        AddRow(_list, SizeType.Percent, 45);

        var buttons = Row(
            Act(Theme.PrimaryButton("連線"), ConnectSelectedAsync),
            Act(Theme.SecondaryButton("新增…"), () => { AddMachine(); return Task.CompletedTask; }),
            Act(Theme.SecondaryButton("編輯…"), () => { EditSelected(); return Task.CompletedTask; }),
            Act(Theme.SecondaryButton("刪除"), () => { DeleteSelected(); return Task.CompletedTask; }),
            Act(Theme.SecondaryButton("測試全部"), TestAllAsync),
            Act(Theme.SecondaryButton("建立桌面捷徑"), () => { CreateShortcuts(); return Task.CompletedTask; }));
        buttons.WrapContents = true;
        AddRow(buttons);

        // ---- 加入公司／匯入連線代碼（可摺疊） ----
        _toggleImport = Theme.SecondaryButton("＋ 加入公司／匯入連線代碼");
        _toggleImport.Margin = new Padding(0, 8, 8, 4);
        _toggleImport.Click += (_, _) => _importPanel!.Visible = !_importPanel.Visible;
        _remove = Act(Theme.DangerButton("移除這個公司連線…"), RemoveCompanyAsync);
        _remove.Margin = new Padding(0, 8, 8, 4);
        var logs = Theme.SecondaryButton("紀錄…");
        logs.Margin = new Padding(0, 8, 8, 4);
        logs.Click += (_, _) =>
        {
            using var f = new LogsForm(LogsMode.Client);
            f.ShowDialog(this);
        };
        AddRow(Row(_toggleImport, _remove, logs));

        _importPanel = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Margin = new Padding(0, 0, 0, 6),
        };
        _importPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _importPanel.Controls.Add(Lbl("貼上管理者給你的連線資訊（整段貼上即可，會合併進上面的清單）：", Theme.Bold));
        _code = new TextBox
        {
            Multiline = true,
            Height = 60,
            ScrollBars = ScrollBars.Vertical,
            Font = Theme.Mono,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 2, 0, 2),
        };
        _code.TextChanged += (_, _) => ParseCode();
        _importPanel.Controls.Add(_code);
        _parsed = Lbl("", Theme.Small, Theme.Muted, bottom: 4);
        _importPanel.Controls.Add(_parsed);
        _import = Act(Theme.PrimaryButton("匯入"), ImportAsync);
        _importPanel.Controls.Add(_import);
        AddRow(_importPanel);

        AddRow(new Label
        {
            Text = "•  按「連線」或桌面捷徑時，程式會先檢查；需要重新驗證時會自動開啟驗證網頁，完成後自動開啟遠端桌面\n" +
                   "•  2FA 到期後，已經連上的遠端桌面不會中斷，只有新的連線需要重新驗證\n" +
                   "•  遠端桌面要求帳密時，輸入公司那台電腦的 Windows 帳號密碼；「預設帳號」只預先填好帳號名稱\n" +
                   "•  不需要連公司時可以按「中斷連線」，平常上網就不會經過公司的 Cloudflare\n" +
                   "•  有多個地點時，WARP 同一時間只能連一個地點；連到另一個地點時會自動切換，原本地點的遠端桌面會中斷",
            AutoSize = true,
            BackColor = Theme.HelpBack,
            Padding = new Padding(14, 8, 14, 8),
            Margin = new Padding(0, 4, 0, 8),
        });

        _log = new LogBox { Margin = new Padding(0) };
        AddRow(_log, SizeType.Percent, 55);

        Controls.Add(root);
        ResumeLayout(false);
        PerformLayout();

        RenderList();
        _importPanel.Visible = _config is null || _config.Machines.Count == 0;
        _timer.Tick += async (_, _) => await RefreshStatusAsync();
        Shown += async (_, _) =>
        {
            ResizeColumns();
            MigrateShortcuts();
            await RefreshStatusAsync();
            if (!_demo) _timer.Start();
        };
        FormClosed += (_, _) => _timer.Dispose();
    }

    // ================= 清單 =================

    void ResizeColumns()
    {
        var w = _list.ClientSize.Width;
        if (w <= 0) return;
        int[] pct = [20, 14, 18, 16, 14];
        for (int i = 0; i < pct.Length; i++) _list.Columns[i].Width = w * pct[i] / 100;
        _list.Columns[ColStatus].Width = w - pct.Sum(p => w * p / 100) - 2;
    }

    void RenderList()
    {
        var selected = SelectedMachine();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var m in _config?.Machines ?? [])
        {
            var item = new ListViewItem(m.Name) { Tag = m, UseItemStyleForSubItems = false };
            item.SubItems.Add(m.VnetId is null ? "—" : m.VnetName ?? "預設");
            item.SubItems.Add($"{m.Ip}:{m.Port}");
            item.SubItems.Add(string.IsNullOrEmpty(m.UserName) ? "—" : m.UserName);
            item.SubItems.Add(m.LastConnectedAt is { } t ? t.ToString("M/d HH:mm") : "—");
            item.SubItems.Add("");
            _list.Items.Add(item);
            if (m == selected) item.Selected = true;
        }
        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0) _list.Items[0].Selected = true;
        _list.EndUpdate();
        _remove.Enabled = _config is not null;
    }

    ClientMachine? SelectedMachine() =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as ClientMachine : null;

    ListViewItem? ItemOf(ClientMachine m) => _list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag == m);

    void SetItemStatus(ClientMachine m, string text, Color color)
    {
        if (ItemOf(m) is not { } item) return;
        item.SubItems[ColStatus].Text = text;
        item.SubItems[ColStatus].ForeColor = color;
    }

    void Save()
    {
        if (_config is not null) AppStorage.SaveClient(_config);
    }

    void AddMachine()
    {
        if (_config is null)
        {
            MessageBox.Show(this, "請先在下方「加入公司／匯入連線代碼」貼上管理者給你的連線資訊。", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            _importPanel.Visible = true;
            return;
        }
        using var dlg = new MachineEditForm(null, _config.Sites());
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _config.Machines.Add(dlg.Result);
        Save();
        RenderList();
        _log.Ok($"已新增 {dlg.Result.Name}（{dlg.Result.Ip}）");
    }

    void EditSelected()
    {
        if (SelectedMachine() is not { } m || _config is null) return;
        using var dlg = new MachineEditForm(m, _config.Sites());
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var hadShortcut = WarpClient.HasShortcut(m);
        if (hadShortcut) WarpClient.DeleteRdpShortcut(m);
        var i = _config.Machines.IndexOf(m);
        _config.Machines[i] = dlg.Result;
        if (hadShortcut) WarpClient.CreateRdpShortcuts([dlg.Result], _log);
        Save();
        RenderList();
        _log.Ok($"已更新 {dlg.Result.Name}");
    }

    void DeleteSelected()
    {
        if (SelectedMachine() is not { } m || _config is null) return;
        if (MessageBox.Show(this, $"確定要從清單刪除「{m.Name}（{m.Ip}）」嗎？\n桌面上的捷徑也會一併刪除。", "刪除連線",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        WarpClient.DeleteRdpShortcut(m);
        _config.Machines.Remove(m);
        Save();
        RenderList();
        _log.Ok($"已刪除 {m.Name}");
    }

    void CreateShortcuts()
    {
        if (_config is null || _config.Machines.Count == 0) return;
        try { WarpClient.CreateRdpShortcuts(_config.Machines, _log); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            _log.Error("建立桌面捷徑失敗：" + ex.Message);
        }
    }

    /// <summary>舊版的桌面捷徑（.rdp，會繞過驗證檢查）換成新捷徑。</summary>
    void MigrateShortcuts()
    {
        if (_demo || _config is null) return;
        try
        {
            if (WarpClient.MigrateLegacyShortcuts(_config.Machines, _log) > 0)
                _log.Info("桌面捷徑已更新：連線前會先檢查驗證，需要時自動開啟驗證頁。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            _log.Warn("更新桌面捷徑失敗，請按「建立桌面捷徑」：" + ex.Message);
        }
    }

    // ================= 狀態 =================

    async Task RefreshStatusAsync()
    {
        if (_demo)
        {
            _warpConnected = true;
            _warpStatus.Text = $"WARP：● 已連線（組織 {_config?.Team}）";
            _warpStatus.ForeColor = Theme.Success;
            _toggleWarp.Text = "中斷連線";
            RefreshMfaStatus();
            return;
        }
        if (_busy) return;

        var status = await Task.Run(WarpClient.GetStatus);
        if (IsDisposed) return;
        _warpConnected = WarpClient.IsConnected(status);
        var org = _config is null ? "" : $"（組織 {_config.Team}）";
        if (_warpConnected)
        {
            _warpStatus.Text = $"WARP：● 已連線{org}";
            _warpStatus.ForeColor = Theme.Success;
        }
        else if (status == "未安裝")
        {
            _warpStatus.Text = "WARP：尚未安裝（請先匯入連線代碼）";
            _warpStatus.ForeColor = Theme.Muted;
        }
        else if (WarpClient.NeedsRegistration(status))
        {
            _warpStatus.Text = "WARP：尚未登入組織，請按「開啟 WARP」登入";
            _warpStatus.ForeColor = Theme.Warning;
        }
        else
        {
            _warpStatus.Text = $"WARP：○ 未連線（{status}）";
            _warpStatus.ForeColor = Theme.Warning;
        }
        _toggleWarp.Text = _warpConnected ? "中斷連線" : "重新連線";
        _toggleWarp.Enabled = _openWarp.Enabled = WarpClient.IsInstalled;
        RefreshMfaStatus();
    }

    TwoFactorStatus CurrentMfa() =>
        TwoFactorStatus.Compute(_config?.MfaSessionDuration, _config?.LastVerifiedAt, DateTime.Now);

    void RefreshMfaStatus()
    {
        if (_config is null)
        {
            _mfaStatus.Text = "2FA：—";
            _mfaStatus.ForeColor = Theme.Muted;
            return;
        }
        var s = CurrentMfa();
        _mfaStatus.Text = "2FA：" + s.Describe(DateTime.Now);
        _mfaStatus.ForeColor = s.State switch
        {
            TwoFactorState.Valid => Theme.Success,
            TwoFactorState.Expired => Theme.Warning,
            _ => Theme.Muted,
        };
    }

    async Task ToggleWarpAsync()
    {
        _busy = true;
        try
        {
            if (_warpConnected)
            {
                await Task.Run(WarpClient.Disconnect);
                _log.Ok("已中斷 WARP；平常上網不會經過公司，要連公司電腦時再按「重新連線」。");
            }
            else
            {
                await Task.Run(WarpClient.Connect);
                _log.Ok("已重新連線 WARP");
            }
        }
        finally
        {
            _busy = false;
            await RefreshStatusAsync();
        }
    }

    async Task ReauthenticateAsync()
    {
        if (_config is null) return;
        var viaWarp = await Task.Run(() => WarpClient.OpenReauthenticate(_config.Team));
        if (viaWarp)
        {
            _log.Info("已請 WARP 開啟驗證頁：請在瀏覽器完成 Email 驗證碼與 2FA。");
        }
        else
        {
            _log.Info("已開啟重新驗證：請在瀏覽器完成 Email 驗證碼與 2FA。");
            _log.Info("如果沒有出現，請在 WARP 視窗點「個人資料 → 帳戶資訊 → 重新驗證」。");
        }
    }

    // ================= 連線 =================

    RdpConnectFlow Flow() => new(_config!, _log, this, Save) { Status = SetItemStatus };

    async Task ConnectSelectedAsync()
    {
        if (SelectedMachine() is not { } m || _config is null || _busy) return;
        _busy = true;
        SetBusy(true);
        try
        {
            if (await Flow().ConnectAsync(m))
            {
                RenderList();
                SetItemStatus(m, "✓ 已開啟遠端桌面", Theme.Success);
                RefreshMfaStatus();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _log.Error("無法開啟遠端桌面：" + ex.Message);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    async Task TestAllAsync()
    {
        if (_config is null || _config.Machines.Count == 0) return;
        _busy = true;
        SetBusy(true);
        try
        {
            if (!await Flow().EnsureWarpConnectedAsync())
            {
                _log.Error("WARP 無法連線，無法測試。");
                return;
            }
            // WARP 同一時間只在一個虛擬網路：只測試目前地點的電腦，其他地點標示「切換後才能測試」
            var current = _config.CurrentVnetId;
            bool InCurrentSite(ClientMachine m) => m.VnetId is null || current is null || m.VnetId == current;
            var testable = _config.Machines.Where(InCurrentSite).ToList();
            foreach (var m in _config.Machines.Except(testable)) SetItemStatus(m, "其他地點（連線時自動切換）", Theme.Muted);
            foreach (var m in testable) SetItemStatus(m, "測試中…", Theme.Muted);

            var results = await Task.WhenAll(testable.Select(async m =>
            {
                var ok = await RdpHost.TestTcpAsync(m.Ip, m.Port);
                var usage = ok ? await SessionStatus.QueryAsync(m.Ip) : null;
                return (m, ok, usage);
            }));
            foreach (var (m, ok, usage) in results)
            {
                if (!ok) SetItemStatus(m, "✗ 連不到", Theme.Danger);
                else if (usage is null) SetItemStatus(m, "✓ 可以連線", Theme.Success);
                else if (SessionStatus.FindOccupant(usage, Environment.MachineName) is { } who)
                    SetItemStatus(m, "! " + who.Short, Theme.Warning);
                else SetItemStatus(m, "✓ 可以連線（沒人使用）", Theme.Success);
            }
            if (results.Any(r => r.ok))
            {
                _config.LastVerifiedAt = DateTime.Now;
                Save();
                RefreshMfaStatus();
            }
            else if (results.Length > 0)
            {
                _log.Warn("全部連不到：最可能是 2FA 需要重新驗證。直接按「連線」，程式會自動開啟驗證頁；也可以按「立即重新驗證」。");
            }
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    void SetBusy(bool busy)
    {
        foreach (var b in _actions) b.Enabled = !busy;
        UseWaitCursor = busy;
        if (!busy) _remove.Enabled = _config is not null;
    }

    // ================= 匯入與移除 =================

    void ParseCode()
    {
        var cfg = ClientConfig.TryParse(_code.Text);
        if (cfg is null)
        {
            _parsed.Text = _code.Text.Trim().Length == 0 ? "" : "✗ 找不到有效的連線代碼（以 CFT1: 開頭），請確認整段複製完整。";
            _parsed.ForeColor = Theme.Danger;
            _import.Text = "匯入";
            return;
        }
        _parsed.Text = $"✓ 組織：{cfg.Team}　　電腦：{string.Join("、", cfg.Machines.Select(m => $"{m.Name}（{m.Ip}）"))}";
        _parsed.ForeColor = Theme.Success;
        _import.Text = WarpClient.IsInstalled ? "匯入" : "安裝 WARP 並匯入";
    }

    async Task ImportAsync()
    {
        var incoming = ClientConfig.TryParse(_code.Text);
        if (incoming is null)
        {
            MessageBox.Show(this, "請貼上管理者給你的連線資訊（包含 CFT1: 開頭的連線代碼）。", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var orgChanged = false;
        if (_config is null)
        {
            _config = incoming;
            orgChanged = true;
        }
        else if (_config.Merge(incoming, out var added, out var updated))
        {
            _log.Ok($"已合併連線代碼：新增 {added} 台、更新 {updated} 台");
        }
        else
        {
            if (MessageBox.Show(this,
                    $"這個連線代碼屬於另一個組織（{incoming.Team}），目前是 {_config.Team}。\n\n" +
                    "WARP 同時只能加入一個組織。要改成新的組織嗎？（目前的連線清單會被取代）",
                    "切換組織", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            foreach (var m in _config.Machines) WarpClient.DeleteRdpShortcut(m);
            _config = incoming;
            orgChanged = true;
        }

        _busy = true;
        SetBusy(true);
        try
        {
            Save();
            RenderList();

            if (!WarpClient.IsInstalled)
            {
                _log.Step("安裝 WARP");
                await WarpClient.InstallAsync(_config.Team, _log);
                orgChanged = false; // 安裝時已經設定組織
            }
            if (orgChanged)
            {
                _log.Step("設定 WARP 組織");
                await WarpClient.SetOrganizationAsync(_config.Team, _log);
            }

            // 先安裝本程式：遠端桌面捷徑會指向安裝位置的 CfTunnelTool.exe --connect
            try { SelfInstaller.Install(_log); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                           or System.Runtime.InteropServices.COMException)
            {
                _log.Warn("建立 CfTunnelTool 捷徑失敗（不影響連線）：" + ex.Message);
            }

            _log.Step("建立遠端桌面捷徑");
            CreateShortcuts();

            _code.Clear();
            _importPanel.Visible = false;

            if (orgChanged || !WarpClient.IsConnected(await Task.Run(WarpClient.GetStatus)))
            {
                _log.Step("登入 WARP");
                _log.Info("WARP 會開啟登入畫面：輸入你的 Email、收信取得驗證碼；第一次會要求註冊 2FA。");
                _log.Info("如果沒有自動出現，請按「開啟 WARP」，或點右下角的 WARP 圖示 → 登入。");
                WarpClient.OpenGui();
                await Task.Run(WarpClient.Connect);
            }
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
            SetBusy(false);
            await RefreshStatusAsync();
        }
    }

    async Task RemoveCompanyAsync()
    {
        if (_config is null) return;
        if (MessageBox.Show(this,
                $"確定要移除與 {_config.Team} 的連線嗎？這會：\n\n" +
                "•  中斷 WARP 並登出這個組織\n" +
                "•  刪除連線清單與桌面上的遠端桌面捷徑\n\n" +
                "之後要再連線，需要重新匯入連線代碼。",
                "移除公司連線", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        _busy = true;
        SetBusy(true);
        try
        {
            _log.Step("移除公司連線");
            await Task.Run(() => WarpClient.LeaveOrganization(_log));
            foreach (var m in _config.Machines) WarpClient.DeleteRdpShortcut(m);
            _log.Ok("已刪除桌面捷徑");
            AppStorage.DeleteClient();
            _config = null;
            RenderList();
            _log.Ok("已移除連線清單");

            if (WarpClient.IsInstalled &&
                MessageBox.Show(this, "要一併解除安裝 Cloudflare WARP 嗎？\n（如果這台電腦還要連其他公司的組織，請選「否」）",
                    "解除安裝 WARP", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            {
                _log.Step("解除安裝 WARP");
                await Task.Run(() => WarpClient.Uninstall(_log));
            }
            _importPanel.Visible = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Error(ex.Message);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
            await RefreshStatusAsync();
        }
    }
}
