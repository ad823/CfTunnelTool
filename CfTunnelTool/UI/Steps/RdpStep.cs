using System.Text.RegularExpressions;
using CfTunnelTool.Core;

namespace CfTunnelTool.UI.Steps;

/// <summary>精靈步驟：瀏覽器遠端桌面（RDP）＋ 2FA。</summary>
sealed partial class RdpStep : StepPage
{
    const int ColName = 0, ColIp = 1, ColPort = 2, ColTest = 3;

    readonly WizardState _state;
    readonly CheckBox _enable;
    readonly List<Control> _settings = [];
    readonly TextBox _sub;
    readonly Label _domainSuffix;
    readonly DataGridView _grid;
    readonly Button _test;
    readonly TextBox _emails;
    readonly CheckBox _totp, _securityKey, _biometrics;
    readonly ComboBox _session;
    readonly CheckBox _clipText, _fileUpload, _fileDownload;
    readonly Label _hostStatus;
    readonly Button _enableHost;
    readonly ComboBox _account;
    readonly TextBox _pwd1, _pwd2;
    readonly CheckBox _keepAutoLogon;
    readonly Button _setPassword;
    readonly Label _accountHint;
    bool _loaded;

    public override string SidebarTitle => "遠端桌面";
    public override string Heading => "步驟 4／5　遠端桌面（RDP）＋ 2FA";

    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex InvalidNameChars();

    public RdpStep(WizardState state)
    {
        _state = state;

        AddText("讓使用者用瀏覽器就能連到這台電腦或區網內其他電腦的遠端桌面。登入時要先通過 Email 驗證碼和 2FA，" +
                "再輸入 Windows 帳號密碼。不需要的話，不勾選直接按「下一步」即可。");

        _enable = new CheckBox
        {
            Text = "啟用瀏覽器遠端桌面（RDP）＋ 2FA",
            AutoSize = true,
            Font = Theme.Sub,
            Margin = new Padding(0, 4, 0, 8),
        };
        _enable.CheckedChanged += (_, _) => UpdateEnabled();
        Add(_enable, stretch: false);

        // ---- 網址 ----
        S(AddText("遠端桌面網址：", Theme.Bold, top: 6, bottom: 2));
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

        // ---- 剪貼簿與檔案 ----
        S(AddText("剪貼簿與檔案傳輸：", Theme.Bold, top: 12, bottom: 2));
        _clipText = Check("文字剪貼簿（雙向複製貼上）", true);
        _fileUpload = Check("允許上傳檔案到遠端", true);
        _fileDownload = Check("允許從遠端下載檔案", true);
        S(Add(_clipText, stretch: false));
        S(Add(_fileUpload, stretch: false));
        S(Add(_fileDownload, stretch: false));

        // ---- 這台電腦 ----
        S(AddText("這台電腦的遠端桌面：", Theme.Bold, top: 12, bottom: 2));
        _hostStatus = InlineLabel("檢查中…");
        _enableHost = Theme.SecondaryButton("啟用這台電腦的遠端桌面");
        _enableHost.Click += async (_, _) => await EnableHostAsync();
        var openSettings = Theme.SecondaryButton("Windows 遠端桌面設定 ↗");
        openSettings.Click += (_, _) => RdpHost.OpenSettings();
        S(AddRow(_hostStatus, _enableHost, openSettings));

        // ---- 這台電腦的遠端登入密碼 ----
        S(AddText("這台電腦的遠端登入密碼：", Theme.Bold, top: 12, bottom: 2));
        S(AddText("遠端桌面必須用有密碼的 Windows 帳號登入。帳號原本是空白密碼的話，可以在這裡設定密碼，" +
                  "並保留開機自動登入，在電腦前面使用時和原本一樣不用輸入密碼。", Theme.Small, Theme.Muted, bottom: 4));
        _account = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Margin = new Padding(0, 4, 8, 0) };
        _account.SelectedIndexChanged += (_, _) => UpdateAccountHint();
        S(AddRow(InlineLabel("Windows 帳號："), _account));
        _pwd1 = new TextBox { Width = 200, UseSystemPasswordChar = true, Margin = new Padding(0, 4, 12, 0) };
        _pwd2 = new TextBox { Width = 200, UseSystemPasswordChar = true, Margin = new Padding(0, 4, 0, 0) };
        S(AddRow(InlineLabel("新密碼："), _pwd1, InlineLabel("再輸入一次："), _pwd2));
        _keepAutoLogon = Check("保留開機自動登入（在電腦前面使用時不用輸入密碼）", true);
        _setPassword = Theme.SecondaryButton("設定密碼");
        _setPassword.Click += async (_, _) => await SetPasswordAsync();
        S(AddRow(_keepAutoLogon, _setPassword));
        _accountHint = S(AddText("讀取帳號中…", Theme.Small, Theme.Muted, top: 2, bottom: 4));

        AddHelpBox("使用者怎麼連線？",
            "1.  開啟管理者提供的網址（每台電腦一個連結，完成頁和管理畫面都會列出）\n" +
            "2.  輸入自己的 Email，收信取得驗證碼並輸入\n" +
            "3.  第一次登入時依畫面註冊 2FA（例如用 Authenticator App 掃描 QR Code），之後每次輸入 App 上的 6 位數驗證碼\n" +
            "4.  在瀏覽器裡輸入那台電腦的 Windows 帳號密碼，就會看到遠端桌面\n\n" +
            "限制：剪貼簿單次上限 500 KB、不能複製圖片、沒有聲音、檔案傳輸為 Beta 功能。需要聲音或多螢幕時，請改用 WARP ＋ 遠端桌面連線。");

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

    void RefreshHostStatus()
    {
        var s = RdpHost.GetStatus();
        if (s.IsHomeEdition)
        {
            _hostStatus.Text = "✗ Windows 家用版無法當遠端桌面主機（可以只當中繼，開放區網其他電腦）";
            _hostStatus.ForeColor = Theme.Danger;
            _enableHost.Visible = false;
        }
        else if (!s.Enabled)
        {
            _hostStatus.Text = "! 尚未啟用";
            _hostStatus.ForeColor = Theme.Warning;
            _enableHost.Visible = true;
        }
        else
        {
            var notes = new List<string> { $"✓ 已啟用（連接埠 {s.Port}" + (s.NlaRequired ? "，NLA 開啟）" : "）") };
            if (!s.SecurityLayerOk) notes.Add("! 安全性層是舊的「RDP」模式，瀏覽器無法連線，請改為「協商」或「SSL」");
            _hostStatus.Text = string.Join("　", notes);
            _hostStatus.ForeColor = s.SecurityLayerOk ? Theme.Success : Theme.Warning;
            _enableHost.Visible = false;
        }
    }

    async Task EnableHostAsync()
    {
        _enableHost.Enabled = false;
        try
        {
            await Task.Run(RdpHost.EnableRemoteDesktop);
            MessageBox.Show(FindForm(), "已啟用這台電腦的遠端桌面，並開放防火牆規則。", "遠端桌面",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show(FindForm(), "啟用失敗：\n\n" + ex.Message, "遠端桌面", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _enableHost.Enabled = true;
            RefreshHostStatus();
        }
    }

    void UpdateEnabled()
    {
        foreach (var c in _settings) c.Enabled = _enable.Checked;
        if (_account.Items.Count > 0) UpdateAccountHint();
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
                _sub.Text = rdp.Hostname == _state.Domain ? "" :
                    rdp.Hostname.EndsWith("." + _state.Domain) ? rdp.Hostname[..^(_state.Domain.Length + 1)] : "rdp";
                foreach (var m in rdp.Machines) AddMachineRow(m.Name, m.Ip, m.Port);
                _emails.Text = string.Join(Environment.NewLine, rdp.AllowedEmails);
                _totp.Checked = rdp.MfaMethods.Contains("totp");
                _securityKey.Checked = rdp.MfaMethods.Contains("security_key");
                _biometrics.Checked = rdp.MfaMethods.Contains("biometrics");
                _session.SelectedIndex = Math.Max(0, Array.IndexOf(RdpConfig.SessionDurations, rdp.MfaSessionDuration));
                _clipText.Checked = rdp.ClipboardText;
                _fileUpload.Checked = rdp.FileUpload;
                _fileDownload.Checked = rdp.FileDownload;
            }
            else
            {
                _sub.Text = "rdp";
                _session.SelectedIndex = Array.IndexOf(RdpConfig.SessionDurations, "8h");
                AddThisComputer(focus: false);
            }
            UpdateEnabled();
            _ = LoadAccountsAsync();
        }
        RefreshHostStatus();
    }

    // ---------- 遠端登入密碼 ----------

    async Task LoadAccountsAsync()
    {
        var users = await Task.Run(LocalAccount.ListUsers);
        if (IsDisposed) return;
        _account.Items.Clear();
        foreach (var u in users) _account.Items.Add(u);

        // 預設選開機自動登入的帳號，其次是目前執行本程式的帳號
        var auto = LocalAccount.GetAutoLogon();
        var current = LocalAccount.CurrentUserName();
        var pick = users.FirstOrDefault(u => auto.Enabled && u.Name.Equals(auto.UserName, StringComparison.OrdinalIgnoreCase))
                   ?? users.FirstOrDefault(u => u.Name.Equals(current, StringComparison.OrdinalIgnoreCase))
                   ?? users.FirstOrDefault();
        if (pick is not null) _account.SelectedItem = pick;
        UpdateAccountHint();
    }

    void UpdateAccountHint()
    {
        if (_account.SelectedItem is not LocalUser u)
        {
            _accountHint.Text = _account.Items.Count == 0 ? "讀不到這台電腦的帳號清單。" : "";
            return;
        }

        var local = u.IsLocal;
        _pwd1.Enabled = _pwd2.Enabled = _keepAutoLogon.Enabled = _setPassword.Enabled = local && _enable.Checked;
        var auto = LocalAccount.GetAutoLogon();
        var autoText = auto.Enabled ? $"目前開機自動登入：{auto.UserName}" : "目前沒有設定開機自動登入";

        if (local)
        {
            _accountHint.Text = $"遠端登入時，帳號請輸入 {LocalAccount.RemoteLoginName(u.Name)}，密碼輸入在這裡設定的密碼。　（{autoText}）";
            _accountHint.ForeColor = Theme.Muted;
        }
        else if (u.Source == "MicrosoftAccount")
        {
            _accountHint.Text = "這是 Microsoft 帳號，密碼要到 account.microsoft.com 修改，這裡無法設定。" +
                                "遠端登入時，帳號輸入 MicrosoftAccount\\你的信箱，密碼輸入 Microsoft 帳號密碼（不是 PIN）。";
            _accountHint.ForeColor = Theme.Warning;
        }
        else
        {
            _accountHint.Text = "這是網域或 Entra ID 帳號，密碼由公司管理，這裡無法設定。";
            _accountHint.ForeColor = Theme.Warning;
        }
    }

    async Task SetPasswordAsync()
    {
        if (_account.SelectedItem is not LocalUser { IsLocal: true } u) return;
        var pwd = _pwd1.Text;
        string? problem =
            pwd.Length == 0 ? "請輸入新密碼。" :
            pwd != _pwd2.Text ? "兩次輸入的密碼不一樣。" :
            pwd.Length < 8 ? "密碼至少要 8 個字元（建議 12 個以上）。" :
            new[] { pwd.Any(char.IsUpper), pwd.Any(char.IsLower), pwd.Any(char.IsDigit), pwd.Any(c => !char.IsLetterOrDigit(c)) }
                .Count(x => x) < 3 ? "密碼請至少混合大寫、小寫、數字、符號其中三種。" :
            pwd.Contains(u.Name, StringComparison.OrdinalIgnoreCase) ? "密碼不能包含帳號名稱。" :
            null;
        if (problem is not null)
        {
            MessageBox.Show(FindForm(), problem, "設定密碼", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var keep = _keepAutoLogon.Checked;
        var confirmText =
            $"確定要把「{u.Name}」的 Windows 密碼設定為剛才輸入的密碼嗎？\n\n" +
            (keep ? "開機會繼續自動登入這個帳號，在電腦前面使用時不用輸入密碼。\n" +
                    "注意：任何人坐到這台電腦前都能直接使用，電腦放在公共區域時請設定螢幕保護程式鎖定。\n\n"
                  : "之後在這台電腦前面登入也需要輸入這組密碼。\n\n") +
            "請記住這組密碼，遠端桌面登入時會用到。";
        if (MessageBox.Show(FindForm(), confirmText, "設定密碼", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _setPassword.Enabled = false;
        try
        {
            await Task.Run(() => LocalAccount.SetRemotePassword(u.Name, pwd, keep));
            _pwd1.Clear();
            _pwd2.Clear();
            MessageBox.Show(FindForm(),
                "密碼已設定" + (keep ? "，開機仍會自動登入。" : "。") + "\n\n" +
                $"遠端桌面登入時：\n帳號：{LocalAccount.RemoteLoginName(u.Name)}\n密碼：剛才設定的密碼",
                "設定完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException
                                       or System.Security.SecurityException
                                       or System.Security.Principal.IdentityNotMappedException)
        {
            MessageBox.Show(FindForm(), ex.Message, "設定密碼失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _setPassword.Enabled = true;
            UpdateAccountHint();
        }
    }

    RdpConfig BuildConfig()
    {
        var sub = _sub.Text.Trim().Trim('.').ToLowerInvariant();
        var rdp = new RdpConfig
        {
            Enabled = _enable.Checked,
            Hostname = sub is "" or "@" ? _state.Domain : $"{sub}.{_state.Domain}",
            Machines = ReadMachines(),
            AllowedEmails = _emails.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList(),
            MfaMethods = new[] { (_totp, "totp"), (_securityKey, "security_key"), (_biometrics, "biometrics") }
                .Where(x => x.Item1.Checked).Select(x => x.Item2).ToList(),
            MfaSessionDuration = RdpConfig.SessionDurations[Math.Max(0, _session.SelectedIndex)],
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
                    RefreshHostStatus();
                }
            }
        }

        _state.Rdp = rdp.Enabled || _state.Rdp is not null ? rdp : null;
        return true;
    }
}
