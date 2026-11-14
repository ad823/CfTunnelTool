using CfTunnelTool.Core;
using CfTunnelTool.Core.Logs;

namespace CfTunnelTool.UI;

/// <summary>紀錄視窗的使用情境：決定顯示哪些分頁。</summary>
enum LogsMode
{
    /// <summary>管理者（中繼）電腦：登入、遠端桌面使用、網路傳輸、程式操作。</summary>
    Admin,
    /// <summary>被連線的電腦：遠端桌面使用、程式操作。</summary>
    Host,
    /// <summary>使用者端：程式操作。</summary>
    Client,
}

/// <summary>查詢、篩選、匯出本機收集的紀錄，並管理每小時自動收集的排程。</summary>
sealed class LogsForm : Form
{
    readonly LogsMode _mode;
    readonly Func<string?>? _tokenProvider;
    readonly TabControl _tabs;
    readonly DateTimePicker _from, _to;
    readonly TextBox _search;
    readonly Label _status, _netNotice;
    readonly Button _collect, _export, _schedule;
    readonly ListView? _login, _rdp, _net;
    readonly ListView _ops;
    bool _busy;

    public LogsForm(LogsMode mode, Func<string?>? tokenProvider = null)
    {
        _mode = mode;
        _tokenProvider = tokenProvider;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"CfTunnelTool － 紀錄　v{Theme.Version}";
        Icon = Theme.AppIcon;
        Font = Theme.Base;
        BackColor = Color.White;
        ClientSize = new Size(1080, 720);
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterParent;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(24, 18, 24, 14) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void AddRow(Control c, bool fill = false)
        {
            root.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            if (fill) c.Dock = DockStyle.Fill;
            root.Controls.Add(c, 0, root.RowCount++);
        }
        static Label Inline(string t) => new() { Text = t, AutoSize = true, Margin = new Padding(0, 8, 6, 0) };

        AddRow(new Label { Text = "紀錄", AutoSize = true, Font = Theme.Heading, Margin = new Padding(0, 0, 0, 8) });

        // ---- 篩選列 ----
        _from = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 130, Value = DateTime.Today.AddDays(-6), Margin = new Padding(0, 4, 6, 0) };
        _to = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 130, Value = DateTime.Today, Margin = new Padding(0, 4, 14, 0) };
        _search = new TextBox { Width = 240, PlaceholderText = "搜尋 Email、帳號、IP、訊息", Margin = new Padding(0, 4, 8, 0) };
        _search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { LoadData(); e.SuppressKeyPress = true; } };
        var query = Theme.PrimaryButton("查詢");
        query.Click += (_, _) => LoadData();
        var filter = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        filter.Controls.AddRange([Inline("日期："), _from, Inline("～"), _to, Inline("搜尋："), _search, query]);
        AddRow(filter);

        // ---- 分頁 ----
        _tabs = new TabControl { Margin = new Padding(0, 0, 0, 8) };
        if (mode == LogsMode.Admin)
        {
            _login = AddTab("登入", ["時間", "Email", "結果", "應用程式", "IP", "登入方式", "動作"], [15, 22, 8, 20, 13, 12, 10]);
        }
        if (mode is LogsMode.Admin or LogsMode.Host)
        {
            _rdp = AddTab("遠端桌面使用", ["開始", "結束", "時長", "電腦", "Windows 帳號", "狀態", "來源 IP"], [15, 15, 9, 14, 20, 11, 16]);
        }
        _netNotice = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 52,
            ForeColor = Theme.Warning,
            BackColor = Theme.HelpBack,
            Padding = new Padding(10, 6, 10, 6),
            Visible = false,
        };
        if (mode == LogsMode.Admin)
        {
            _net = AddTab("網路傳輸", ["時間", "使用者", "目的（電腦 IP:Port）", "傳送", "接收", "連線數"], [16, 26, 22, 12, 12, 12]);
            _net.Parent!.Controls.Add(_netNotice);
            _netNotice.BringToFront();
            _net.BringToFront();
        }
        _ops = AddTab("程式操作", ["時間", "等級", "訊息", "電腦"], [15, 8, 62, 15]);
        AddRow(_tabs, fill: true);

        // ---- 按鈕列 ----
        _collect = Theme.SecondaryButton("立即收集");
        _collect.Click += async (_, _) => await CollectNowAsync();
        _export = Theme.SecondaryButton("匯出 CSV…");
        _export.Click += (_, _) => ExportCsv();
        _schedule = Theme.SecondaryButton("啟用每小時自動收集");
        _schedule.Click += async (_, _) => await ToggleScheduleAsync();
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        if (mode != LogsMode.Client) buttons.Controls.AddRange([_collect, _schedule]);
        buttons.Controls.Add(_export);
        AddRow(buttons);

        _status = new Label { AutoSize = true, ForeColor = Theme.Muted };
        AddRow(_status);

        Controls.Add(root);
        ResumeLayout(false);
        PerformLayout();

        Shown += (_, _) => { RefreshStatus(); LoadData(); };
    }

    ListView AddTab(string title, string[] columns, int[] weights)
    {
        var page = new TabPage(title) { BackColor = Color.White, Padding = new Padding(4) };
        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Clickable,
            BorderStyle = BorderStyle.FixedSingle,
        };
        foreach (var c in columns) list.Columns.Add(c);
        list.Resize += (_, _) =>
        {
            var w = list.ClientSize.Width;
            if (w <= 0) return;
            var total = weights.Sum();
            for (int i = 0; i < weights.Length; i++) list.Columns[i].Width = w * weights[i] / total;
        };
        page.Controls.Add(list);
        _tabs.TabPages.Add(page);
        return list;
    }

    static ListViewItem Row(Color? color, params string[] values)
    {
        var item = new ListViewItem(values[0]) { UseItemStyleForSubItems = true };
        for (int i = 1; i < values.Length; i++) item.SubItems.Add(values[i]);
        if (color is { } c) item.ForeColor = c;
        return item;
    }

    static string Bytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{b / 1024.0:0.0} KB",
        _ => $"{b} B",
    };

    static string Duration(TimeSpan? d) => d is not { } t ? "—"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours} 小時 {t.Minutes} 分" : $"{Math.Max(0, t.Minutes)} 分 {t.Seconds} 秒";

    bool Match(params string[] values)
    {
        var q = _search.Text.Trim();
        return q.Length == 0 || values.Any(v => v.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    void Fill(ListView? list, IEnumerable<ListViewItem> items)
    {
        if (list is null) return;
        list.BeginUpdate();
        list.Items.Clear();
        list.Items.AddRange(items.ToArray());
        list.EndUpdate();
        var page = (TabPage)list.Parent!;
        var baseTitle = page.Text.Split('（')[0];
        page.Text = $"{baseTitle}（{list.Items.Count}）";
    }

    void LoadData()
    {
        var from = _from.Value.Date;
        var to = _to.Value.Date.AddDays(1).AddTicks(-1);
        if (from > to) (from, to) = (to.Date, from.AddDays(1).AddTicks(-1));

        if (_login is not null)
        {
            var rows = LogStore.Query<LoginRecord>(LogKinds.Login, from, to, r => r.Time)
                .Where(r => Match(r.Email, r.Ip, r.App, r.Connection, r.Action))
                .OrderByDescending(r => r.Time)
                .Select(r => Row(r.Allowed ? null : Theme.Danger,
                    r.Time.ToString("yyyy-MM-dd HH:mm:ss"), r.Email, r.Allowed ? "允許" : "擋下", r.App, r.Ip, r.Connection, r.Action));
            Fill(_login, rows);
        }

        if (_rdp is not null)
        {
            // 往前多讀一天，才能接上跨日的工作階段
            var events = LogStore.Query<RdpEventRecord>(LogKinds.RdpEvents, from.AddDays(-1), to, r => r.Time);
            var rows = RdpEventCollector.BuildSessions(events)
                .Where(s => s.Start >= from && s.Start <= to)
                .Where(s => Match(s.User, s.Machine, s.Source, s.Status))
                .Select(s => Row(s.Status switch
                    {
                        "登入失敗" => Theme.Danger,
                        "使用中" => Theme.Success,
                        _ => null,
                    },
                    s.Start.ToString("yyyy-MM-dd HH:mm:ss"),
                    s.End?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—",
                    s.Status == "登入失敗" ? "—" : Duration(s.Duration ?? (s.Status == "使用中" ? DateTime.Now - s.Start : null)),
                    s.Machine, s.User, s.Status, s.Source));
            Fill(_rdp, rows);
        }

        if (_net is not null)
        {
            var state = LogState.Load();
            _netNotice.Text = state.NetworkUnavailable ?? "";
            _netNotice.Visible = state.NetworkUnavailable is not null;
            var rows = LogStore.Query<NetworkRecord>(LogKinds.Network, from, to, r => r.Time)
                .Where(r => Match(r.User, r.Destination))
                .OrderByDescending(r => r.Time)
                .Select(r => Row(null, r.Time.ToString("yyyy-MM-dd HH:mm"), r.User, r.Destination,
                    Bytes(r.BytesSent), Bytes(r.BytesReceived), r.Count.ToString()));
            Fill(_net, rows);
        }

        var ops = LogStore.Query<OperationRecord>(LogKinds.Operation, from, to, r => r.Time)
            .Where(r => Match(r.Message, r.Level, r.Machine))
            .OrderByDescending(r => r.Time)
            .Select(r => Row(r.Level switch { "錯誤" => Theme.Danger, "警告" => Theme.Warning, _ => null },
                r.Time.ToString("yyyy-MM-dd HH:mm:ss"), r.Level, r.Message, r.Machine));
        Fill(_ops, ops);
    }

    void RefreshStatus()
    {
        var state = LogState.Load();
        var enabled = _mode != LogsMode.Client && CollectorSchedule.IsEnabled;
        _schedule.Text = enabled ? "停用每小時自動收集" : "啟用每小時自動收集";
        var last = state.LastCollected is { } t ? $"{t:yyyy-MM-dd HH:mm}（{state.LastResult}）" : "尚未收集";
        _status.Text = _mode == LogsMode.Client
            ? $"紀錄位置：{LogStore.Root}　　保留 {state.RetentionDays} 天"
            : $"最後收集：{last}　　自動收集：{(enabled ? "每小時（SYSTEM 排程）" : "未啟用")}　　保留 {state.RetentionDays} 天　　位置：{LogStore.Root}";
    }

    sealed class CollectLog : ILog
    {
        public List<string> Warnings { get; } = [];
        public void Info(string message) { }
        public void Step(string message) { }
        public void Ok(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message) => Warnings.Add(message);
    }

    async Task CollectNowAsync()
    {
        if (_busy) return;
        _busy = true;
        _collect.Enabled = false;
        UseWaitCursor = true;
        try
        {
            var token = _mode == LogsMode.Admin ? _tokenProvider?.Invoke() : null;
            var log = new CollectLog();
            var result = await LogCollector.RunAsync(token, log);
            RefreshStatus();
            LoadData();
            if (log.Warnings.Count > 0)
                MessageBox.Show(this, "已收集：" + result + "\n\n注意：\n• " + string.Join("\n• ", log.Warnings), "收集紀錄",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _busy = false;
            _collect.Enabled = true;
            UseWaitCursor = false;
        }
    }

    async Task ToggleScheduleAsync()
    {
        try
        {
            if (CollectorSchedule.IsEnabled)
            {
                await Task.Run(CollectorSchedule.Disable);
                MessageBox.Show(this, "已停用每小時自動收集。", "自動收集", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                string? token = null;
                if (_mode == LogsMode.Admin)
                {
                    token = _tokenProvider?.Invoke();
                    if (token is null &&
                        MessageBox.Show(this, "沒有 API Token，自動收集只會記錄這台電腦的遠端桌面使用，不會收集 Cloudflare 的登入與網路傳輸紀錄。\n\n要繼續嗎？",
                            "自動收集", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                        return;
                }
                await Task.Run(() => CollectorSchedule.Enable(token));
                MessageBox.Show(this,
                    "已啟用每小時自動收集（Windows 排程，以 SYSTEM 身分執行，不需要登入）。\n\n" +
                    (token is null ? "" : "排程使用的 API Token 已用 Windows 加密，存在只有系統管理員能讀取的資料夾。\n\n") +
                    "Cloudflare 免費方案的登入與網路紀錄只保留 24 小時，請讓這台電腦保持開機，紀錄才不會漏掉。",
                    "自動收集", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                       or System.Security.Cryptography.CryptographicException)
        {
            MessageBox.Show(this, ex.Message, "自動收集", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        RefreshStatus();
    }

    void ExportCsv()
    {
        if (_tabs.SelectedTab?.Controls.OfType<ListView>().FirstOrDefault() is not { } list) return;
        var title = _tabs.SelectedTab.Text.Split('（')[0];
        using var dlg = new SaveFileDialog
        {
            Filter = "CSV 檔案 (*.csv)|*.csv",
            FileName = $"CfTunnelTool-{title}-{_from.Value:yyyyMMdd}-{_to.Value:yyyyMMdd}.csv",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var rows = new List<string[]> { list.Columns.Cast<ColumnHeader>().Select(c => c.Text).ToArray() };
        rows.AddRange(list.Items.Cast<ListViewItem>().Select(i => i.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text).ToArray()));
        try
        {
            LogStore.WriteCsv(dlg.FileName, rows);
            MessageBox.Show(this, $"已匯出 {rows.Count - 1} 筆到：\n{dlg.FileName}", "匯出 CSV", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "匯出失敗：" + ex.Message, "匯出 CSV", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
