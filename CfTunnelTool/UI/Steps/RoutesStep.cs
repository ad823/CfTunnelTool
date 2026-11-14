using CfTunnelTool.Core;

namespace CfTunnelTool.UI.Steps;

sealed class RoutesStep : StepPage
{
    const int ColSub = 0, ColDomain = 1, ColService = 2, ColTls = 3, ColLocal = 4;

    readonly WizardState _state;
    readonly DataGridView _grid;
    readonly Button _test;
    readonly Label _preview;
    bool _loaded;

    public override string SidebarTitle => "網址";
    public override string Heading => "步驟 3／4　設定對外網址";
    public override bool CanGoNext => _grid.Rows.Count > 0;

    public RoutesStep(WizardState state)
    {
        _state = state;

        AddText("每一列代表一個對外網址，以及它要轉到這台電腦上的哪個服務。網站和 API 各一列，可以自由新增或刪除。");

        _grid = BuildGrid();
        Add(_grid);

        var addWeb = Theme.SecondaryButton("+ 新增網站");
        addWeb.Click += (_, _) => AddGridRow(NextFreeSub("www"), "http://localhost:80", false, focus: true);
        var addApi = Theme.SecondaryButton("+ 新增 API");
        addApi.Click += (_, _) => AddGridRow(NextFreeSub("api"), "http://localhost:5000", false, focus: true);
        var remove = Theme.SecondaryButton("刪除選取的列");
        remove.Click += (_, _) => RemoveSelectedRows();
        _test = Theme.SecondaryButton("測試本機服務");
        _test.Click += async (_, _) => await TestLocalAsync();
        AddRow(addWeb, addApi, remove, _test);

        AddText("設定結果預覽：", Theme.Bold, top: 10, bottom: 2);
        _preview = AddText("", Theme.Mono, Theme.Text, bottom: 6);

        AddHelpBox("「本機服務位址」怎麼填？",
            "•  IIS 網站：通常是 http://localhost，若 IIS 繫結了其他連接埠就填 http://localhost:連接埠\n" +
            "•  ASP.NET Core（Kestrel）：程式啟動時顯示的「Now listening on」位址，例如 http://localhost:5000\n" +
            "•  Node.js、Python 等：程式監聽的連接埠，例如 http://localhost:3000\n" +
            "•  也可以只輸入連接埠數字，例如 5000，程式會自動補成 http://localhost:5000\n" +
            "•  服務只接受 https 且使用自簽憑證時，請勾選「自簽憑證」\n" +
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
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "本機服務位址", FillWeight = 30 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "自簽憑證", FillWeight = 12 });
        var localCol = new DataGridViewTextBoxColumn { HeaderText = "本機狀態", FillWeight = 16, ReadOnly = true };
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
                    AddGridRow(sub, r.Service, r.NoTlsVerify);
                }
            }
            else
            {
                AddGridRow("www", "http://localhost:80", false);
                AddGridRow("api", "http://localhost:5000", false);
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

    void AddGridRow(string sub, string service, bool noTls, bool focus = false)
    {
        var i = _grid.Rows.Add(sub, DomainSuffix, service, noTls, "");
        if (focus)
        {
            _grid.Focus();
            _grid.CurrentCell = _grid.Rows[i].Cells[ColService];
        }
        UpdatePreview();
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
            list.Add((row, new RouteConfig { Hostname = host, Service = svc, NoTlsVerify = row.Cells[ColTls].Value is true }));
        }
        return list;
    }

    void UpdatePreview()
    {
        var rows = ReadRows();
        _preview.Text = rows.Count == 0
            ? "（尚未設定任何網址）"
            : string.Join("\n", rows.Select(x => $"https://{x.Route.Hostname}  →  {x.Route.Service}"));
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
                if (results[i].Ok) SetLocalCell(rows[i].Row, "✓ 有回應", Theme.Success);
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
        try
        {
            probe.Validate();
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

        _state.Routes = probe.Routes;
        return true;
    }
}
