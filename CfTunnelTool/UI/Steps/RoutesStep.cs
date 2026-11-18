using CfTunnelTool.Core;

namespace CfTunnelTool.UI.Steps;

sealed class RoutesStep : StepPage
{
    const int ColSub = 0, ColDomain = 1, ColService = 2, ColTls = 3, ColApi = 4, ColLocal = 5;

    readonly WizardState _state;
    readonly DataGridView _grid;
    readonly Button _test;
    readonly Label _preview;
    bool _loaded;

    public override string SidebarTitle => "網址";
    public override string Heading => "步驟 3／5　設定對外網址（網站／API／TCP）";

    public RoutesStep(WizardState state)
    {
        _state = state;

        AddText("每一列代表一個對外網址，以及它要轉到這台電腦上的哪個服務。網站和 API 各一列，可以自由新增或刪除；" +
                "MySQL 等非網頁服務用 tcp:// 開頭。只需要遠端桌面的話，可以刪除所有列，直接到下一步。");

        _grid = BuildGrid();
        Add(_grid);

        var addWeb = Theme.SecondaryButton("+ 新增網站");
        addWeb.Click += (_, _) => AddGridRow(NextFreeSub("www"), "http://localhost:80", false, false, focus: true);
        var addApi = Theme.SecondaryButton("+ 新增 API");
        addApi.Click += (_, _) => AddGridRow(NextFreeSub("api"), "http://localhost:5000", false, true, focus: true);
        var addTcp = Theme.SecondaryButton("+ 新增 TCP");
        addTcp.Click += (_, _) => AddGridRow(NextFreeSub("mysql"), "tcp://localhost:3306", false, false, focus: true);
        var remove = Theme.SecondaryButton("刪除選取的列");
        remove.Click += (_, _) => RemoveSelectedRows();
        _test = Theme.SecondaryButton("測試本機服務");
        _test.Click += async (_, _) => await TestLocalAsync();
        AddRow(addWeb, addApi, addTcp, remove, _test);

        AddText("設定結果預覽：", Theme.Bold, top: 10, bottom: 2);
        _preview = AddText("", Theme.Mono, Theme.Text, bottom: 6);

        AddHelpBox("「本機服務位址」怎麼填？",
            "•  IIS 網站：通常是 http://localhost，若 IIS 繫結了其他連接埠就填 http://localhost:連接埠\n" +
            "•  ASP.NET Core（Kestrel）：程式啟動時顯示的「Now listening on」位址，例如 http://localhost:5000\n" +
            "•  Node.js、Python 等：程式監聽的連接埠，例如 http://localhost:3000\n" +
            "•  也可以只輸入連接埠數字，例如 5000，程式會自動補成 http://localhost:5000\n" +
            "•  服務只接受 https 且使用自簽憑證時，請勾選「自簽憑證」\n" +
            "•  「API 用途」：給程式（Python、curl、手機 App 等）呼叫的網址請勾選，會關閉 Cloudflare 的瀏覽器檢查；" +
            "沒勾的話，程式用預設設定呼叫可能被擋（403 Error 1010）。給人用瀏覽器開的網站不用勾，保留防護。" +
            "需要 Token 有「Zone → Config Rules → Edit」權限\n" +
            "•  MySQL、SQL Server 等 TCP 服務：填 tcp://主機:連接埠，例如 tcp://localhost:3306。" +
            "使用者端要安裝 cloudflared 並執行連線指令（完成畫面和管理畫面會顯示），不能用瀏覽器開\n" +
            "•  子網域留空，代表直接使用網域本身（例如 https://yourdomain.com）");
    }

    DataGridView BuildGrid()
    {
        var grid = new DataGridView
        {
            Height = 190,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = true,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            GridColor = Theme.Border,
            EditMode = DataGridViewEditMode.EditOnEnter,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            EnableHeadersVisualStyles = false,
            Margin = new Padding(0, 4, 0, 6),
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Surface;
        grid.ColumnHeadersDefaultCellStyle.Font = Theme.Bold;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 6, 4, 6);
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.DefaultCellStyle.Padding = new Padding(4, 5, 4, 5);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(255, 236, 214);
        grid.DefaultCellStyle.SelectionForeColor = Theme.Text;

        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "子網域", FillWeight = 14 });
        var domainCol = new DataGridViewTextBoxColumn { HeaderText = "網域", FillWeight = 22, ReadOnly = true };
        domainCol.DefaultCellStyle.ForeColor = Theme.Muted;
        domainCol.DefaultCellStyle.BackColor = Theme.Surface;
        grid.Columns.Add(domainCol);
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "本機服務位址", FillWeight = 28 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "自簽憑證", FillWeight = 11 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            HeaderText = "API 用途",
            FillWeight = 11,
            ToolTipText = "給程式呼叫的網址：關閉 Cloudflare 的瀏覽器檢查，避免 Python、curl 等被擋（403 Error 1010）",
        });
        var localCol = new DataGridViewTextBoxColumn { HeaderText = "本機狀態", FillWeight = 14, ReadOnly = true };
        localCol.DefaultCellStyle.BackColor = Theme.Surface;
        grid.Columns.Add(localCol);
        foreach (DataGridViewColumn c in grid.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;

        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex is ColSub or ColService)
                SetLocalCell(grid.Rows[e.RowIndex], "", Theme.Muted);
            if (e.RowIndex >= 0 && e.ColumnIndex == ColService)
                UpdateApiCell(grid.Rows[e.RowIndex]);
            UpdatePreview();
        };
        grid.RowsRemoved += (_, _) => { UpdatePreview(); NotifyStateChanged(); };
        grid.RowsAdded += (_, _) => NotifyStateChanged();
        return grid;
    }

    public override void OnEnter()
    {
        foreach (DataGridViewRow row in _grid.Rows)
            row.Cells[ColDomain].Value = DomainSuffix;

        if (!_loaded)
        {
            _loaded = true;
            if (_state.Routes.Count > 0)
            {
                foreach (var r in _state.Routes)
                {
                    var sub = r.Hostname == _state.Domain ? "" : r.Hostname[..^(_state.Domain.Length + 1)];
                    AddGridRow(sub, r.Service, r.NoTlsVerify, r.DisableBrowserCheck);
                }
            }
            else
            {
                AddGridRow("www", "http://localhost:80", false, false);
                AddGridRow("api", "http://localhost:5000", false, true);
            }
            _ = TestLocalAsync();
        }
        UpdatePreview();
    }

    string DomainSuffix => "." + _state.Domain;

    string NextFreeSub(string prefix)
    {
        var used = ReadRows().Select(x => x.Route.Hostname).ToHashSet();
        if (!used.Contains($"{prefix}.{_state.Domain}")) return prefix;
        for (int i = 2; ; i++)
            if (!used.Contains($"{prefix}{i}.{_state.Domain}")) return $"{prefix}{i}";
    }

    void AddGridRow(string sub, string service, bool noTls, bool api, bool focus = false)
    {
        var i = _grid.Rows.Add(sub, DomainSuffix, service, noTls, api, "");
        UpdateApiCell(_grid.Rows[i]);
        if (focus)
        {
            _grid.Focus();
            _grid.CurrentCell = _grid.Rows[i].Cells[ColService];
        }
        UpdatePreview();
    }

    /// <summary>TCP 不經過瀏覽器檢查，「API 用途」不適用：取消勾選並鎖住。</summary>
    static void UpdateApiCell(DataGridViewRow row)
    {
        var cell = row.Cells[ColApi];
        var tcp = (row.Cells[ColService].Value as string ?? "").Trim().StartsWith("tcp://", StringComparison.OrdinalIgnoreCase);
        if (tcp && cell.Value is true) cell.Value = false;
        cell.ReadOnly = tcp;
        cell.Style.BackColor = tcp ? Theme.Surface : Color.Empty;
        cell.ToolTipText = tcp ? "TCP 不經過瀏覽器檢查，不需要勾選" : "";
    }

    void RemoveSelectedRows()
    {
        var rows = _grid.SelectedCells.Cast<DataGridViewCell>().Select(c => c.OwningRow).OfType<DataGridViewRow>().Distinct().ToList();
        if (rows.Count == 0 && _grid.CurrentRow is not null) rows.Add(_grid.CurrentRow);
        _grid.EndEdit();
        foreach (var r in rows) _grid.Rows.Remove(r);
    }

    List<(DataGridViewRow Row, RouteConfig Route)> ReadRows()
    {
        var list = new List<(DataGridViewRow, RouteConfig)>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var sub = (row.Cells[ColSub].Value as string ?? "").Trim().Trim('.').ToLowerInvariant();
            var svc = ToolConfig.NormalizeService(row.Cells[ColService].Value as string ?? "");
            if (sub.Length == 0 && svc.Length == 0) continue;
            var host = sub is "" or "@" ? _state.Domain : $"{sub}.{_state.Domain}";
            list.Add((row, new RouteConfig
            {
                Hostname = host,
                Service = svc,
                NoTlsVerify = row.Cells[ColTls].Value is true,
                DisableBrowserCheck = row.Cells[ColApi].Value is true && !svc.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase),
            }));
        }
        return list;
    }

    void UpdatePreview()
    {
        var rows = ReadRows();
        _preview.Text = rows.Count == 0
            ? "（尚未設定任何網址）"
            : string.Join("\n", rows.Select(x => $"{x.Route.PublicUrl}  →  {x.Route.Service}" +
                                                  (x.Route.DisableBrowserCheck ? "　（API：關閉瀏覽器檢查）" : "")));
    }

    static void SetLocalCell(DataGridViewRow row, string text, Color color)
    {
        row.Cells[ColLocal].Value = text;
        row.Cells[ColLocal].Style.ForeColor = color;
    }

    async Task TestLocalAsync()
    {
        _grid.EndEdit();
        var rows = ReadRows().Where(x => Uri.TryCreate(x.Route.Service, UriKind.Absolute, out _)).ToList();
        if (rows.Count == 0) return;

        _test.Enabled = false;
        foreach (var (row, _) in rows) SetLocalCell(row, "檢查中…", Theme.Muted);
        try
        {
            var results = await Deployer.CheckLocalServicesAsync(rows.Select(x => x.Route));
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Row.DataGridView is null) continue; // 已被刪除
                if (results[i].Ok) SetLocalCell(rows[i].Row, rows[i].Route.IsTcp ? "✓ 可以連線" : "✓ 有回應", Theme.Success);
                else SetLocalCell(rows[i].Row, "✗ 沒有回應", Theme.Danger);
            }
        }
        finally
        {
            _test.Enabled = true;
        }
    }

    public override bool OnNext()
    {
        _grid.EndEdit();
        var rows = ReadRows();

        // 把正規化後的位址寫回表格，讓使用者看到實際會用的值
        foreach (var (row, route) in rows) row.Cells[ColService].Value = route.Service;

        var probe = _state.ToConfig();
        probe.Routes = rows.Select(x => x.Route).ToList();
        probe.Rdp = null; // 遠端桌面在下一步驗證
        try
        {
            // 沒有任何網址也可以（只用遠端桌面），在下一步再整體檢查
            if (probe.Routes.Count > 0) probe.Validate();
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(FindForm(), ex.Message, "請檢查網址設定", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (rows.Any(x => x.Row.Cells[ColLocal].Value as string == "✗ 沒有回應") &&
            MessageBox.Show(FindForm(),
                "有些本機服務目前沒有回應。\n\n仍然可以繼續部署，等服務啟動後網址就能連線。要繼續嗎？",
                "本機服務沒有回應", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
            return false;

        if (probe.Routes.Any(r => r.IsTcp) && !ConfirmTcpExposure(probe.Routes.Where(r => r.IsTcp)))
            return false;

        _state.Routes = probe.Routes;
        return true;
    }

    /// <summary>TCP 網址沒有 Cloudflare 登入保護，提醒管理者這個服務等於對外公開。</summary>
    bool ConfirmTcpExposure(IEnumerable<RouteConfig> tcp) =>
        MessageBox.Show(FindForm(),
            "TCP 網址沒有 Cloudflare 登入保護：\n" +
            string.Join("\n", tcp.Select(r => $"    {r.PublicUrl}  →  {r.Service}")) + "\n\n" +
            "任何知道網址並安裝 cloudflared 的人，都能直接連到這個服務，只剩服務本身的帳號密碼保護。\n\n" +
            "請確認：\n" +
            "•  使用強密碼，MySQL 不要允許 root 從遠端登入\n" +
            "•  遠端帳號只給需要的資料庫與權限\n" +
            "•  服務已更新到最新版本\n\n" +
            "確定要開放嗎？",
            "TCP 服務會對外公開", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
}
