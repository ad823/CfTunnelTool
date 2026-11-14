using CfTunnelTool.Core;

namespace CfTunnelTool.UI;

/// <summary>
/// 「被遠端連線的電腦」需要的本機設定：遠端桌面狀態與啟用、遠端登入密碼（保留開機自動登入）、
/// 「結束遠端並交還畫面」捷徑。精靈的遠端桌面步驟與「被連線的電腦」畫面共用。
/// </summary>
sealed class HostAccountPanel : TableLayoutPanel
{
    readonly Label _hostStatus;
    readonly Button _enableHost;
    readonly Label _firewallStatus;
    readonly Button _restrictFirewall;
    int _firewallCheck;
    readonly ComboBox _account;
    readonly TextBox _pwd1, _pwd2;
    readonly CheckBox _keepAutoLogon;
    readonly Button _setPassword;
    readonly Label _accountHint;
    readonly Button _returnShortcut;
    readonly Label _returnHint;
    bool _accountsLoaded;

    public HostAccountPanel()
    {
        ColumnCount = 1;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = new Padding(0);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // ---- 遠端桌面狀態 ----
        AddText("這台電腦的遠端桌面：", Theme.Bold, top: 12);
        _hostStatus = Inline("檢查中…");
        _enableHost = Theme.SecondaryButton("啟用這台電腦的遠端桌面");
        _enableHost.Click += async (_, _) => await EnableHostAsync();
        var openSettings = Theme.SecondaryButton("Windows 遠端桌面設定 ↗");
        openSettings.Click += (_, _) => RdpHost.OpenSettings();
        AddFlow(_hostStatus, _enableHost, openSettings);
        _firewallStatus = Inline("");
        _restrictFirewall = Theme.SecondaryButton("只允許內部網路連線");
        _restrictFirewall.Visible = false;
        _restrictFirewall.Click += async (_, _) => await RestrictFirewallAsync();
        AddFlow(Inline("防火牆來源："), _firewallStatus, _restrictFirewall);

        // ---- 遠端登入密碼 ----
        AddText("這台電腦的遠端登入密碼：", Theme.Bold, top: 12);
        AddText("遠端桌面必須用有密碼的 Windows 帳號登入。帳號原本是空白密碼的話，可以在這裡設定密碼（至少 8 個字元），" +
                "並保留開機自動登入，在電腦前面使用時和原本一樣不用輸入密碼。", Theme.Small, Theme.Muted);
        _account = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Margin = new Padding(0, 4, 8, 0) };
        _account.SelectedIndexChanged += (_, _) => UpdateAccountHint();
        AddFlow(Inline("Windows 帳號："), _account);
        _pwd1 = new TextBox { Width = 200, UseSystemPasswordChar = true, Margin = new Padding(0, 4, 12, 0) };
        _pwd2 = new TextBox { Width = 200, UseSystemPasswordChar = true, Margin = new Padding(0, 4, 0, 0) };
        AddFlow(Inline("新密碼："), _pwd1, Inline("再輸入一次："), _pwd2);
        _keepAutoLogon = new CheckBox
        {
            Text = "保留開機自動登入（在電腦前面使用時不用輸入密碼）",
            AutoSize = true,
            Checked = true,
            Margin = new Padding(0, 8, 18, 0),
        };
        _setPassword = Theme.SecondaryButton("設定密碼");
        _setPassword.Click += async (_, _) => await SetPasswordAsync();
        AddFlow(_keepAutoLogon, _setPassword);
        _accountHint = AddText("讀取帳號中…", Theme.Small, Theme.Muted, top: 2);

        // ---- 結束遠端並交還畫面 ----
        AddText("結束遠端並交還畫面：", Theme.Bold, top: 12);
        AddText("遠端使用者離開前雙擊桌面上的這個捷徑（不要直接按 ✕），電腦前面的螢幕會直接回到桌面，不需要再輸入 PIN 或密碼。" +
                "交還後螢幕是未鎖定的，電腦放在公共區域時請斟酌使用。", Theme.Small, Theme.Muted);
        _returnShortcut = Theme.SecondaryButton("在桌面建立「結束遠端並交還畫面」捷徑");
        _returnShortcut.Click += (_, _) => CreateReturnShortcut();
        _returnHint = Inline("");
        AddFlow(_returnShortcut, _returnHint);
    }

    Label AddText(string text, Font font, Color? color = null, int top = 0)
    {
        var l = new Label
        {
            Text = text,
            AutoSize = true,
            Font = font,
            ForeColor = color ?? Theme.Text,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, top, 0, 4),
        };
        Controls.Add(l);
        return l;
    }

    void AddFlow(params Control[] controls)
    {
        var f = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 2, 0, 4),
        };
        f.Controls.AddRange(controls);
        Controls.Add(f);
    }

    static Label Inline(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(0, 8, 8, 0),
    };

    /// <summary>第一次顯示時讀取帳號清單，並更新各項狀態。</summary>
    public async Task LoadAsync()
    {
        RefreshStatus();
        if (_accountsLoaded) return;
        _accountsLoaded = true;

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

    public void RefreshStatus()
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

        _ = RefreshFirewallAsync(s);

        _returnHint.Text = SelfInstaller.HasReturnConsoleShortcut ? "✓ 已建立" : "";
        _returnHint.ForeColor = Theme.Success;
        if (_account.Items.Count > 0) UpdateAccountHint();
    }

    /// <summary>在背景讀取防火牆規則（PowerShell 需要數秒），只採用最後一次檢查的結果。</summary>
    async Task RefreshFirewallAsync(RdpHostStatus s)
    {
        var check = ++_firewallCheck;
        _restrictFirewall.Visible = false;
        if (s.IsHomeEdition || !s.Enabled)
        {
            _firewallStatus.Text = "—";
            _firewallStatus.ForeColor = Theme.Muted;
            return;
        }

        _firewallStatus.Text = "檢查中…";
        _firewallStatus.ForeColor = Theme.Muted;
        var open = await Task.Run(() => RdpHost.GetOpenFirewallRules(s.Port));
        if (IsDisposed || check != _firewallCheck) return;

        if (open is null)
        {
            _firewallStatus.Text = "! 讀不到防火牆規則，請手動確認遠端桌面只允許區網連線";
            _firewallStatus.ForeColor = Theme.Warning;
        }
        else if (open.Count == 0)
        {
            _firewallStatus.Text = "✓ 只允許內部網路（區網與私有 IP），網際網路無法直接連進來";
            _firewallStatus.ForeColor = Theme.Success;
        }
        else
        {
            _firewallStatus.Text = "✗ 允許任何來源：路由器有轉送或 UPnP 時，網際網路可以直接連進來嘗試登入";
            _firewallStatus.ForeColor = Theme.Danger;
            _restrictFirewall.Visible = true;
        }
    }

    async Task RestrictFirewallAsync()
    {
        _restrictFirewall.Enabled = false;
        try
        {
            var port = RdpHost.GetStatus().Port;
            var n = await Task.Run(() => RdpHost.RestrictFirewallToInternal(port));
            MessageBox.Show(FindForm(),
                $"已把 {n} 條遠端桌面防火牆規則改為只允許內部網路連線（同網段與私有 IP）。\n\n" +
                "經 Cloudflare 連進來的使用者不受影響；網際網路直接連到這台電腦的嘗試會被擋掉。\n" +
                "也建議到路由器關閉 3389 的連接埠轉送、DMZ 與 UPnP。",
                "遠端桌面防火牆", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(FindForm(), ex.Message, "遠端桌面防火牆", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _restrictFirewall.Enabled = true;
            RefreshStatus();
        }
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (_account.Items.Count > 0) UpdateAccountHint();
    }

    async Task EnableHostAsync()
    {
        _enableHost.Enabled = false;
        try
        {
            await Task.Run(RdpHost.EnableRemoteDesktop);
            MessageBox.Show(FindForm(), "已啟用這台電腦的遠端桌面，並開放防火牆規則（只允許內部網路連線）。", "遠端桌面",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show(FindForm(), "啟用失敗：\n\n" + ex.Message, "遠端桌面", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _enableHost.Enabled = true;
            RefreshStatus();
        }
    }

    void UpdateAccountHint()
    {
        if (_account.SelectedItem is not LocalUser u)
        {
            _accountHint.Text = _account.Items.Count == 0 ? "讀不到這台電腦的帳號清單。" : "";
            return;
        }

        var local = u.IsLocal;
        _pwd1.Enabled = _pwd2.Enabled = _keepAutoLogon.Enabled = _setPassword.Enabled = local && Enabled;
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

    /// <summary>密碼規則：只要求至少 8 個字元，且兩次輸入相同。</summary>
    public static string? ValidatePassword(string pwd, string confirm) =>
        pwd.Length == 0 ? "請輸入新密碼。" :
        pwd != confirm ? "兩次輸入的密碼不一樣。" :
        pwd.Length < 8 ? "密碼至少要 8 個字元。" :
        null;

    async Task SetPasswordAsync()
    {
        if (_account.SelectedItem is not LocalUser { IsLocal: true } u) return;
        var pwd = _pwd1.Text;
        if (ValidatePassword(pwd, _pwd2.Text) is { } problem)
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
                $"遠端桌面登入時：\n帳號：{LocalAccount.RemoteLoginName(u.Name)}\n密碼：剛才設定的密碼\n\n" +
                "建議也在「設定 → 帳戶 → 登入選項」設定 PIN，登出或鎖定後在電腦前面只要輸入 PIN（PIN 不能用來遠端登入）。",
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

    void CreateReturnShortcut()
    {
        try
        {
            SelfInstaller.CreateReturnConsoleShortcut(new NullLog());
            MessageBox.Show(FindForm(),
                "已在桌面建立「結束遠端並交還畫面」捷徑，所有使用者登入都看得到。\n\n" +
                "遠端使用者要離開時，雙擊這個捷徑（會跳出系統管理員確認，按「是」）。\n" +
                "電腦前面的螢幕就會直接回到桌面，不需要再輸入 PIN 或密碼。\n\n" +
                "注意：遠端登入的帳號需要是系統管理員才能使用。",
                "結束遠端並交還畫面", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                       or System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show(FindForm(), "建立捷徑失敗：\n\n" + ex.Message, "結束遠端並交還畫面",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        RefreshStatus();
    }

    sealed class NullLog : ILog
    {
        public void Info(string message) { }
        public void Step(string message) { }
        public void Ok(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }
}
