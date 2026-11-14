using System.Text.RegularExpressions;
using CfTunnelTool.Core;

namespace CfTunnelTool.UI.Steps;

/// <summary>精靈步驟：瀏覽器遠端桌面（RDP）＋ 2FA。</summary>
sealed partial class RdpStep : StepPage
{
    const int ColName = 0, ColIp = 1, ColPort = 2, ColTest = 3;

    readonly WizardState _state;
    readonly CheckBox _enable;
    readonly CheckBox _modeBrowser, _modeWarp, _ownVnet;
    readonly List<Control> _settings = [];
    readonly TextBox _sub;
    readonly Label _domainSuffix;
    readonly DataGridView _grid;
    readonly Button _test;
    readonly TextBox _emails;
    readonly CheckBox _totp, _securityKey, _biometrics;
    readonly ComboBox _session, _login;
    readonly CheckBox _clipText, _fileUpload, _fileDownload;
    readonly HostAccountPanel _host;
    bool _loaded;

    public override string SidebarTitle => "遠端桌面";
    public override string Heading => "步驟 4／5　遠端桌面（RDP）＋ 2FA";

    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex InvalidNameChars();

    public RdpStep(WizardState state)
    {
        _state = state;

        AddText("讓使用者從外面連到這台電腦或區網內其他電腦的遠端桌面。連線前要先通過 Email 驗證碼和 2FA，" +
                "再輸入 Windows 帳號密碼。不需要的話，不勾選直接按「下一步」即可。");

        _enable = new CheckBox
        {
            Text = "啟用遠端桌面（RDP）＋ 2FA",
            AutoSize = true,
            Font = Theme.Sub,
            Margin = new Padding(0, 4, 0, 8),
        };
        _enable.CheckedChanged += (_, _) => UpdateEnabled();
        Add(_enable, stretch: false);

        // ---- 連線方式 ----
        S(AddText("連線方式（可以兩種都開）：", Theme.Bold, top: 4, bottom: 2));
        _modeWarp = Check("WARP ＋ 遠端桌面連線（最流暢；使用者需安裝 WARP，程式可自動安裝）", false);
        _modeBrowser = Check("瀏覽器（免安裝，但畫面較慢；適合偶爾使用或外部人員）", true);
        S(Add(_modeWarp, stretch: false));
        S(Add(_modeBrowser, stretch: false));
        _ownVnet = Check("這個地點使用獨立的虛擬網路（有多個地點、而且內網 IP 可能重複時勾選）", false);
        _ownVnet.Margin = new Padding(0, 10, 18, 0);
        S(Add(_ownVnet, stretch: false));
        S(AddText("勾選後，不同地點的相同 IP（例如兩個廠區都有 192.168.1.10）不會衝突。WARP 使用者同一時間只能連一個地點，CfTunnelTool 使用者端會在連線時自動切換。",
            Theme.Small, Theme.Muted, top: 0, bottom: 4));

        // ---- 網址 ----
        S(AddText("瀏覽器連線網址（只有瀏覽器方式會用到）：", Theme.Bold, top: 10, bottom: 2));
        _sub = new TextBox { Width = 160, Margin = new Padding(0, 4, 4, 0) };
        _domainSuffix = InlineLabel("");
        _domainSuffix.ForeColor = Theme.Muted;
        S(AddRow(_sub, _domainSuffix));

        // ---- 電腦清單 ----
        S(AddText("要開放的電腦：", Theme.Bold, top: 10, bottom: 2));
        S(AddText("這台電腦會當作中繼，也可以加入同一個區網內的其他電腦（填該電腦的區網 IP）。", Theme.Small, Theme.Muted, bottom: 4));
        _grid = BuildGrid();
        S(Add(_grid));

        var addThis = Theme.SecondaryButton("+ 加入這台電腦");
        addThis.Click += (_, _) => AddThisComputer(focus: true);
        var addOther = Theme.SecondaryButton("+ 新增區網電腦");
        addOther.Click += (_, _) => AddMachineRow("pc" + (_grid.Rows.Count + 1), "192.168.1.", 3389, focusIp: true);
        var remove = Theme.SecondaryButton("刪除選取的列");
        remove.Click += (_, _) => RemoveSelectedRows();
        _test = Theme.SecondaryButton("測試連線");
        _test.Click += async (_, _) => await TestMachinesAsync();
        S(AddRow(addThis, addOther, remove, _test));

        // ---- 允許的 Email ----
        S(AddText("允許登入的人（一行一個 Email，或輸入 @公司網域.com 代表整個網域）：", Theme.Bold, top: 12, bottom: 2));
        _emails = new TextBox
        {
            Multiline = true,
            Height = 64,
            ScrollBars = ScrollBars.Vertical,
            Margin = new Padding(0, 4, 0, 4),
        };
        S(Add(_emails));

        // ---- 2FA ----
        S(AddText("2FA 驗證方式（使用者第一次登入時註冊）：", Theme.Bold, top: 10, bottom: 2));
        _totp = Check("Authenticator App（Google / Microsoft Authenticator 等）", true);
        _securityKey = Check("安全金鑰（YubiKey 等）", true);
        _biometrics = Check("生物辨識（Windows Hello、Touch ID）", true);
        S(Add(_totp, stretch: false));
        S(Add(_securityKey, stretch: false));
        S(Add(_biometrics, stretch: false));

        _session = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, Margin = new Padding(0, 4, 0, 0) };
        foreach (var d in RdpConfig.SessionDurations) _session.Items.Add(RdpDeployer.SessionName(d));
        S(AddRow(InlineLabel("重新驗證 2FA："), _session));

        _login = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, Margin = new Padding(0, 4, 0, 0) };
        foreach (var d in RdpConfig.LoginDurations) _login.Items.Add(RdpDeployer.LoginName(d) + "輸入一次");
        S(AddRow(InlineLabel("Email 驗證碼："), _login));
        S(AddText("Email 驗證碼的有效時間是 Cloudflare Zero Trust 的全域登入時間，同一個帳號的所有應用程式共用。" +
                  "在有效時間內重新連線，只需要再做 2FA。", Theme.Small, Theme.Muted, top: 2, bottom: 0));

        // ---- 剪貼簿與檔案 ----
        S(AddText("剪貼簿與檔案傳輸：", Theme.Bold, top: 12, bottom: 2));
        _clipText = Check("文字剪貼簿（雙向複製貼上）", true);
        _fileUpload = Check("允許上傳檔案到遠端", true);
        _fileDownload = Check("允許從遠端下載檔案", true);
        S(Add(_clipText, stretch: false));
        S(Add(_fileUpload, stretch: false));
        S(Add(_fileDownload, stretch: false));

        // ---- 這台電腦（遠端桌面狀態、遠端登入密碼、交還畫面捷徑） ----
        _host = S(Add(new HostAccountPanel()));
        S(AddText("區網的其他電腦：在那台電腦上執行 CfTunnelTool，選「這台電腦要被遠端連線」，也能做上面這些設定。",
            Theme.Small, Theme.Muted, top: 2, bottom: 4));

        AddHelpBox("使用者怎麼連線？",
            "【WARP ＋ 遠端桌面連線】\n" +
            "1.  部署完成後按「複製連線資訊」傳給使用者（裡面有連線代碼，不含任何密碼）\n" +
            "2.  使用者在自己的電腦執行 CfTunnelTool，選「我要連到公司的電腦」，貼上連線代碼\n" +
            "3.  程式自動安裝 WARP，使用者用 Email 驗證碼登入並註冊 2FA，桌面會出現遠端桌面捷徑\n" +
            "4.  點捷徑連線；第一次或驗證過期時，右下角會跳出通知要求通過 2FA\n\n" +
            "【瀏覽器】\n" +
            "1.  開啟管理者提供的網址，輸入 Email 驗證碼，第一次登入時註冊 2FA\n" +
            "2.  在瀏覽器裡輸入那台電腦的 Windows 帳號密碼\n" +
            "限制：剪貼簿單次上限 500 KB、不能複製圖片、沒有聲音、畫面較慢。");

        AddHelpBox("資安提醒",
            "•  這台電腦是中繼站：部署時建議不要勾選「記住 Token」，避免電腦被入侵時連帶影響你的 Cloudflare 網域\n" +
            "•  被開放的電腦請設定固定 IP（或在路由器做 DHCP 保留），IP 改變後需要回來修改設定\n" +
            "•  Windows 帳號請使用強密碼並設定帳號鎖定原則；只把需要的人加入「Remote Desktop Users」群組\n" +
            "•  加入 Microsoft Entra ID 的電腦，需要關閉遠端桌面的 NLA 強制要求才能用瀏覽器登入",
            Theme.WarnBack, Theme.Danger);
    }

    T S<T>(T control) where T : Control
    {
        _settings.Add(control);
        return control;
    }

    static CheckBox Check(string text, bool value) => new()
    {
        Text = text,
        AutoSize = true,
        Checked = value,
        Margin = new Padding(0, 4, 18, 0),
    };

    DataGridView BuildGrid()
    {
        var grid = new DataGridView
        {
            Height = 140,
            AllowUserToAddRows = false,
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

        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "電腦名稱", FillWeight = 26 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "區網 IP", FillWeight = 26 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "連接埠", FillWeight = 12 });
        var testCol = new DataGridViewTextBoxColumn { HeaderText = "連線測試", FillWeight = 20, ReadOnly = true };
        testCol.DefaultCellStyle.BackColor = Theme.Surface;
        grid.Columns.Add(testCol);
        foreach (DataGridViewColumn c in grid.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;

        grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex is ColIp or ColPort)
                SetTestCell(grid.Rows[e.RowIndex], "", Theme.Muted);
        };
        return grid;
    }

    static string MachineNameDefault()
    {
        var n = InvalidNameChars().Replace(Environment.MachineName, "-").Trim('-', '.');
        return n.Length == 0 ? "this-pc" : n;
    }

    void AddMachineRow(string name, string ip, int port, bool focusIp = false)
    {
        var i = _grid.Rows.Add(name, ip, port.ToString(), "");
        if (focusIp)
        {
            _grid.Focus();
            _grid.CurrentCell = _grid.Rows[i].Cells[ColIp];
        }
    }

    void AddThisComputer(bool focus)
    {
        var ip = RdpHost.GetLanIPv4();
        if (ip is null)
        {
            MessageBox.Show(FindForm(), "找不到這台電腦的區網 IP，請手動新增一列並輸入 IP。", "加入這台電腦",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (ReadMachines().Any(m => m.Ip == ip))
        {
            if (focus)
                MessageBox.Show(FindForm(), $"這台電腦（{ip}）已經在清單中。", "加入這台電腦",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        AddMachineRow(MachineNameDefault(), ip, RdpHost.GetStatus().Port);
    }

    void RemoveSelectedRows()
    {
        var rows = _grid.SelectedCells.Cast<DataGridViewCell>().Select(c => c.OwningRow).OfType<DataGridViewRow>().Distinct().ToList();
        if (rows.Count == 0 && _grid.CurrentRow is not null) rows.Add(_grid.CurrentRow);
        _grid.EndEdit();
        foreach (var r in rows) _grid.Rows.Remove(r);
    }

    List<RdpMachine> ReadMachines()
    {
        _grid.EndEdit();
        var list = new List<RdpMachine>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var name = (row.Cells[ColName].Value as string ?? "").Trim();
            var ip = (row.Cells[ColIp].Value as string ?? "").Trim();
            if (name.Length == 0 && ip.Length == 0) continue;
            _ = int.TryParse(row.Cells[ColPort].Value as string, out var port);
            list.Add(new RdpMachine { Name = name, Ip = ip, Port = port == 0 ? 3389 : port });
        }
        return list;
    }

    static void SetTestCell(DataGridViewRow row, string text, Color color)
    {
        row.Cells[ColTest].Value = text;
        row.Cells[ColTest].Style.ForeColor = color;
    }

    async Task TestMachinesAsync()
    {
        _grid.EndEdit();
        _test.Enabled = false;
        try
        {
            var tasks = new List<Task>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                var ip = (row.Cells[ColIp].Value as string ?? "").Trim();
                if (!int.TryParse(row.Cells[ColPort].Value as string, out var port)) port = 3389;
                if (!System.Net.IPAddress.TryParse(ip, out _)) { SetTestCell(row, "✗ IP 格式錯誤", Theme.Danger); continue; }
                SetTestCell(row, "測試中…", Theme.Muted);
                var r = row;
                tasks.Add(Task.Run(async () =>
                {
                    var ok = await RdpHost.TestTcpAsync(ip, port);
                    BeginInvoke(new Action(() =>
                    {
                        if (r.DataGridView is null) return;
                        if (ok) SetTestCell(r, "✓ 可以連線", Theme.Success);
                        else SetTestCell(r, "✗ 連不到", Theme.Danger);
                    }));
                }));
            }
            await Task.WhenAll(tasks);
        }
        finally
        {
            _test.Enabled = true;
        }
    }

    void UpdateEnabled()
    {
        foreach (var c in _settings) c.Enabled = _enable.Checked;
    }

    public override void OnEnter()
    {
        _domainSuffix.Text = "." + _state.Domain;

        if (!_loaded)
        {
            _loaded = true;
            var rdp = _state.Rdp;
            _enable.Checked = rdp?.Enabled ?? false;
            if (rdp is not null)
            {
                _modeBrowser.Checked = rdp.BrowserEnabled;
                _modeWarp.Checked = rdp.WarpEnabled;
                _ownVnet.Checked = rdp.UseOwnVnet;
                _sub.Text = rdp.Hostname == _state.Domain ? "" :
                    rdp.Hostname.EndsWith("." + _state.Domain) ? rdp.Hostname[..^(_state.Domain.Length + 1)] : "rdp";
                foreach (var m in rdp.Machines) AddMachineRow(m.Name, m.Ip, m.Port);
                _emails.Text = string.Join(Environment.NewLine, rdp.AllowedEmails);
                _totp.Checked = rdp.MfaMethods.Contains("totp");
                _securityKey.Checked = rdp.MfaMethods.Contains("security_key");
                _biometrics.Checked = rdp.MfaMethods.Contains("biometrics");
                _session.SelectedIndex = Math.Max(0, Array.IndexOf(RdpConfig.SessionDurations, rdp.MfaSessionDuration));
                _login.SelectedIndex = Math.Max(0, Array.IndexOf(RdpConfig.LoginDurations, rdp.LoginSessionDuration));
                _clipText.Checked = rdp.ClipboardText;
                _fileUpload.Checked = rdp.FileUpload;
                _fileDownload.Checked = rdp.FileDownload;
            }
            else
            {
                _sub.Text = "rdp";
                _session.SelectedIndex = Array.IndexOf(RdpConfig.SessionDurations, "0m"); // 預設每次連線都要 2FA
                _login.SelectedIndex = Array.IndexOf(RdpConfig.LoginDurations, "8h");
                AddThisComputer(focus: false);
            }
            UpdateEnabled();
        }
        _ = _host.LoadAsync();
    }

    RdpConfig BuildConfig()
    {
        var sub = _sub.Text.Trim().Trim('.').ToLowerInvariant();
        var rdp = new RdpConfig
        {
            Enabled = _enable.Checked,
            BrowserEnabled = _modeBrowser.Checked,
            WarpEnabled = _modeWarp.Checked,
            UseOwnVnet = _ownVnet.Checked,
            Hostname = sub is "" or "@" ? _state.Domain : $"{sub}.{_state.Domain}",
            Machines = ReadMachines(),
            AllowedEmails = _emails.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList(),
            MfaMethods = new[] { (_totp, "totp"), (_securityKey, "security_key"), (_biometrics, "biometrics") }
                .Where(x => x.Item1.Checked).Select(x => x.Item2).ToList(),
            MfaSessionDuration = RdpConfig.SessionDurations[Math.Max(0, _session.SelectedIndex)],
            LoginSessionDuration = RdpConfig.LoginDurations[Math.Max(0, _login.SelectedIndex)],
            ClipboardText = _clipText.Checked,
            FileUpload = _fileUpload.Checked,
            FileDownload = _fileDownload.Checked,
        };
        // 保留之前部署時記錄的資源 ID，部署時才能更新而不是重複建立
        RdpDeployer.CarryOverIds(rdp, _state.Rdp);
        return rdp;
    }

    public override bool OnNext()
    {
        var rdp = BuildConfig();
        var probe = _state.ToConfig();
        probe.Rdp = rdp;
        try
        {
            probe.Validate();
        }
        catch (InvalidOperationException ex)
        {
            var msg = ex.Message.StartsWith("請至少設定一個對外網址")
                ? "目前沒有設定任何網站／API 網址，也沒有啟用遠端桌面。\n請勾選「啟用瀏覽器遠端桌面」，或回上一步新增網址。"
                : ex.Message;
            MessageBox.Show(FindForm(), msg, "請檢查遠端桌面設定", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (rdp.Enabled)
        {
            // 清單中包含這台電腦、但這台電腦還沒啟用遠端桌面：提醒並提供一鍵啟用
            var thisIp = RdpHost.GetLanIPv4();
            var host = RdpHost.GetStatus();
            if (thisIp is not null && rdp.Machines.Any(m => m.Ip == thisIp) && !host.Enabled && !host.IsHomeEdition)
            {
                var answer = MessageBox.Show(FindForm(),
                    "清單中包含這台電腦，但這台電腦還沒有啟用遠端桌面。\n\n要現在啟用嗎？（會同時開放 Windows 防火牆的遠端桌面規則）",
                    "啟用遠端桌面", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return false;
                if (answer == DialogResult.Yes)
                {
                    try { RdpHost.EnableRemoteDesktop(); }
                    catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        MessageBox.Show(FindForm(), "啟用失敗：\n\n" + ex.Message, "遠端桌面", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    _host.RefreshStatus();
                }
            }
        }

        _state.Rdp = rdp.Enabled || _state.Rdp is not null ? rdp : null;
        return true;
    }
}
