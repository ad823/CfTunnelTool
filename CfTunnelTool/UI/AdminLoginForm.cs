using CfTunnelTool.Core;

namespace CfTunnelTool.UI;

enum AdminLoginMode
{
    /// <summary>第一次使用：設定管理帳號密碼。</summary>
    Setup,
    /// <summary>輸入帳號密碼登入。</summary>
    Login,
    /// <summary>忘記密碼或密碼檔不見：先用 Cloudflare API Token 驗證，再設定新帳號密碼。</summary>
    Reset,
    /// <summary>管理畫面的「變更管理密碼」。</summary>
    ChangePassword,
}

/// <summary>管理畫面的帳號密碼視窗：設定、登入、以 Cloudflare 驗證重設、變更密碼。</summary>
sealed class AdminLoginForm : Form
{
    readonly AdminAuth _auth;
    readonly ToolConfig? _config;
    readonly string _purpose;
    readonly TableLayoutPanel _layout;
    AdminLoginMode _mode;

    TextBox? _user, _pwd, _pwd2, _oldPwd, _token;
    Label? _message;
    Button? _ok;

    /// <param name="purpose">顯示在登入畫面上方的說明，例如「閒置超過 15 分鐘，請重新登入」。</param>
    public AdminLoginForm(AdminLoginMode mode, ToolConfig? config, AdminAuth? auth = null, string? purpose = null)
    {
        _auth = auth ?? AdminAuth.Default;
        _config = config;
        _mode = mode;
        _purpose = purpose ?? "";

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = Theme.AppIcon;
        Font = Theme.Base;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;
        TopMost = mode == AdminLoginMode.Login && purpose is not null;

        _layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(22, 18, 22, 16) };
        Controls.Add(_layout);
        Build();
        ResumeLayout(false);
        PerformLayout();
    }

    // ================= 版面 =================

    void Build()
    {
        _layout.SuspendLayout();
        _layout.Controls.Clear();
        _user = _pwd = _pwd2 = _oldPwd = _token = null;
        Text = $"CfTunnelTool 管理畫面　v{Theme.Version}";

        switch (_mode)
        {
            case AdminLoginMode.Setup:
                Heading("設定管理帳號密碼");
                Note("開啟管理畫面（Cloudflare 設定、部署、移除）需要這組帳號密碼。\n" +
                     $"密碼至少 {AdminAuth.MinPasswordLength} 個字元；只存加鹽雜湊，不會保存明文。\n" +
                     "忘記密碼時，可以用 Cloudflare API Token 驗證後重設。");
                _user = Field("帳號：", false, "admin");
                _pwd = Field("密碼：", true);
                _pwd2 = Field("再輸入一次：", true);
                Buttons("設定並進入", SetupAsync);
                break;

            case AdminLoginMode.Login:
                Heading("登入管理畫面");
                if (_purpose.Length > 0) Note(_purpose);
                _user = Field("帳號：", false, _auth.UserName ?? "");
                _pwd = Field("密碼：", true);
                var forgot = new LinkLabel { Text = "忘記密碼…", AutoSize = true, Margin = new Padding(0, 2, 0, 6) };
                forgot.LinkClicked += (_, _) => Forgot();
                _layout.Controls.Add(forgot);
                Buttons("登入", LoginAsync);
                break;

            case AdminLoginMode.Reset:
                Heading("以 Cloudflare 驗證重設管理密碼");
                Note((_auth.Exists ? "" : "這台電腦還沒有設定管理帳號密碼（從舊版升級，或密碼檔被刪除）。\n") +
                     "請輸入這個地點使用的 Cloudflare API Token，確認是此 Cloudflare 帳號的管理者後，再設定新的帳號密碼。\n" +
                     "Token 只用來驗證，不會儲存；已儲存的 Token 不能用來重設。");
                _token = Field("API Token：", true, width: 420, mono: true);
                Buttons("驗證", VerifyTokenAsync);
                break;

            case AdminLoginMode.ChangePassword:
                Heading("變更管理密碼");
                Note($"帳號：{_auth.UserName}");
                _oldPwd = Field("目前密碼：", true);
                _pwd = Field("新密碼：", true);
                _pwd2 = Field("再輸入一次：", true);
                Buttons("變更", ChangePasswordAsync);
                break;
        }

        _message = new Label { AutoSize = true, ForeColor = Theme.Danger, MaximumSize = new Size(460, 0), Margin = new Padding(0, 4, 0, 0) };
        _layout.Controls.Add(_message);
        _layout.ResumeLayout(true);
        ActiveControl = _mode == AdminLoginMode.Login && _user!.Text.Length > 0 ? _pwd : (Control?)_user ?? _oldPwd ?? _token;
    }

    void Heading(string text) => _layout.Controls.Add(new Label
    {
        Text = text, AutoSize = true, Font = Theme.Sub, Margin = new Padding(0, 0, 0, 8),
    });

    void Note(string text) => _layout.Controls.Add(new Label
    {
        Text = text, AutoSize = true, ForeColor = Theme.Muted, MaximumSize = new Size(460, 0), Margin = new Padding(0, 0, 0, 10),
    });

    TextBox Field(string label, bool secret, string value = "", int width = 260, bool mono = false)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        row.Controls.Add(new Label { Text = label, AutoSize = false, Width = 110, Height = 28, TextAlign = ContentAlignment.MiddleLeft });
        var box = new TextBox { Width = width, UseSystemPasswordChar = secret, Text = value };
        if (mono) box.Font = Theme.Mono;
        row.Controls.Add(box);
        _layout.Controls.Add(row);
        return box;
    }

    void Buttons(string okText, Func<Task> onOk)
    {
        _ok = Theme.PrimaryButton(okText);
        _ok.Click += async (_, _) =>
        {
            _ok.Enabled = false;
            try { await onOk(); }
            finally { if (!IsDisposed) _ok.Enabled = true; }
        };
        var cancel = Theme.SecondaryButton("取消");
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Right, Margin = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.Add(_ok);
        buttons.Controls.Add(cancel);
        _layout.Controls.Add(buttons);
        AcceptButton = _ok;
        CancelButton = cancel;
    }

    void ShowMessage(string message) => _message!.Text = message;

    // ================= 動作 =================

    async Task SetupAsync()
    {
        if (AdminAuth.ValidateNew(_user!.Text, _pwd!.Text, _pwd2!.Text) is { } problem) { ShowMessage(problem); return; }
        var (user, pwd) = (_user.Text.Trim(), _pwd.Text);
        await Task.Run(() => _auth.Create(user, pwd));
        AdminAuth.Audit("成功", $"已設定管理帳號「{user}」");
        DialogResult = DialogResult.OK;
    }

    async Task LoginAsync()
    {
        var (user, pwd) = (_user!.Text, _pwd!.Text);
        var r = await Task.Run(() => _auth.Verify(user, pwd));
        if (r.Ok)
        {
            AdminAuth.Audit("成功", $"「{user.Trim()}」登入");
            DialogResult = DialogResult.OK;
            return;
        }
        _pwd.Clear();
        _pwd.Select();
        if (r.Locked)
        {
            AdminAuth.Audit("警告", $"登入失敗次數過多，鎖定 {Math.Ceiling(r.LockedFor.TotalMinutes)} 分鐘（輸入的帳號：{user.Trim()}）");
            ShowMessage($"失敗次數過多，請 {Math.Ceiling(r.LockedFor.TotalMinutes)} 分鐘後再試。");
        }
        else
        {
            AdminAuth.Audit("警告", $"登入失敗（輸入的帳號：{user.Trim()}）");
            ShowMessage($"帳號或密碼錯誤，還有 {r.Remaining} 次機會。");
        }
    }

    void Forgot()
    {
        if (_config is null)
        {
            MessageBox.Show(this,
                "這台電腦還沒有部署（沒有 Cloudflare 設定），無法用 Cloudflare 驗證。\n\n" +
                $"請以系統管理員身分刪除\n{_auth.FilePath}\n然後重新開啟 CfTunnelTool 設定新的帳號密碼。",
                "忘記密碼", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _mode = AdminLoginMode.Reset;
        Build();
    }

    async Task VerifyTokenAsync()
    {
        var token = _token!.Text.Trim();
        if (token.Length == 0) { ShowMessage("請輸入 API Token。"); return; }
        if (_config is null) { ShowMessage("沒有 Cloudflare 設定，無法驗證。"); return; }

        ShowMessage("驗證中…");
        _message!.ForeColor = Theme.Muted;
        var error = await VerifyCloudflareAsync(token, _config);
        _message.ForeColor = Theme.Danger;
        if (error is not null)
        {
            AdminAuth.Audit("警告", "以 Cloudflare 驗證重設管理密碼失敗：" + error);
            ShowMessage("驗證失敗：" + error);
            return;
        }
        AdminAuth.Audit("成功", "已通過 Cloudflare API Token 驗證，準備設定新的管理帳號密碼");
        _mode = AdminLoginMode.Setup;
        Build();
    }

    /// <summary>確認 Token 能讀取這個地點的網域與 Tunnel，代表是此 Cloudflare 帳號的管理者。成功回傳 null。</summary>
    static async Task<string?> VerifyCloudflareAsync(string token, ToolConfig config)
    {
        try
        {
            using var cf = new CloudflareClient(token);
            var zone = await cf.GetZoneAsync(config.Domain);
            if (config.TunnelId is { Length: > 0 } tunnelId)
                await cf.GetTunnelAsync(zone.AccountId, tunnelId);
            else
                await cf.TestTunnelAccessAsync(zone.AccountId);
            return null;
        }
        catch (CloudflareApiException ex) { return ex.Message; }
        catch (HttpRequestException ex) { return "無法連到 Cloudflare：" + ex.Message; }
        catch (TaskCanceledException) { return "連線逾時，請稍後再試。"; }
    }

    async Task ChangePasswordAsync()
    {
        var user = _auth.UserName ?? "";
        var (oldPwd, pwd) = (_oldPwd!.Text, _pwd!.Text);
        if (AdminAuth.ValidateNew(user, pwd, _pwd2!.Text) is { } problem) { ShowMessage(problem); return; }
        var r = await Task.Run(() => _auth.Verify(user, oldPwd));
        if (!r.Ok)
        {
            _oldPwd.Clear();
            AdminAuth.Audit("警告", "變更管理密碼失敗：目前密碼錯誤");
            ShowMessage(r.Locked ? $"失敗次數過多，請 {Math.Ceiling(r.LockedFor.TotalMinutes)} 分鐘後再試。"
                          : $"目前密碼錯誤，還有 {r.Remaining} 次機會。");
            return;
        }
        await Task.Run(() => _auth.Create(user, pwd));
        AdminAuth.Audit("成功", "已變更管理密碼");
        MessageBox.Show(this, "管理密碼已變更。", "變更管理密碼", MessageBoxButtons.OK, MessageBoxIcon.Information);
        DialogResult = DialogResult.OK;
    }
}

/// <summary>進入管理畫面前的關卡：決定要設定、登入或以 Cloudflare 驗證重設，通過後啟動閒置鎖定。</summary>
static class AdminGate
{
    public static bool Ensure(ToolConfig? config)
    {
        var auth = AdminAuth.Default;
        // 已部署卻沒有密碼檔（舊版升級或被刪除）：必須先通過 Cloudflare 驗證，刪檔案也繞不過
        var mode = auth.Exists ? AdminLoginMode.Login : config is not null ? AdminLoginMode.Reset : AdminLoginMode.Setup;
        using var form = new AdminLoginForm(mode, config, auth);
        if (form.ShowDialog() != DialogResult.OK) return false;
        // 全新設定（沒有部署）時，不沿用先前留下的 API Token，避免刪除密碼檔後直接拿舊 Token 部署
        if (mode == AdminLoginMode.Setup) AppStorage.DeleteToken();
        AdminSession.Start();
        return true;
    }
}

/// <summary>
/// 管理畫面閒置鎖定：本程式超過 15 分鐘沒有滑鼠或鍵盤操作，就把所有視窗變透明並要求重新登入。
/// 用透明而不是隱藏，進行中的對話框與背景作業（例如部署）不會被中斷。
/// </summary>
static class AdminSession
{
    static readonly TimeSpan IdleLimit = GetIdleLimit();
    static DateTime _lastActivity = DateTime.Now;
    static System.Windows.Forms.Timer? _timer;
    static bool _locking;

    static TimeSpan GetIdleLimit()
    {
#if DEBUG
        // 開發測試用：CFTT_IDLE_SECONDS=20 可以縮短閒置時間
        if (int.TryParse(Environment.GetEnvironmentVariable("CFTT_IDLE_SECONDS"), out var s) && s > 0)
            return TimeSpan.FromSeconds(s);
#endif
        return TimeSpan.FromMinutes(15);
    }

    public static void Start()
    {
        _lastActivity = DateTime.Now;
        if (_timer is not null) return;
        Application.AddMessageFilter(new ActivityFilter());
        _timer = new System.Windows.Forms.Timer { Interval = (int)Math.Min(30_000, IdleLimit.TotalMilliseconds / 2) };
        _timer.Tick += (_, _) =>
        {
            if (!_locking && DateTime.Now - _lastActivity >= IdleLimit) Lock();
        };
        _timer.Start();
    }

    static void Lock()
    {
        _locking = true;
        var forms = Application.OpenForms.Cast<Form>().Where(f => f.Visible && f.Opacity > 0).ToList();
        foreach (var f in forms) f.Opacity = 0;
        AdminAuth.Audit("資訊", $"閒置超過 {IdleLimit.TotalMinutes:0} 分鐘，已鎖定");
        try
        {
            while (true)
            {
                ToolConfig? config = null;
                try { config = AppStorage.LoadConfig(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or IOException) { }

                using var login = new AdminLoginForm(AdminLoginMode.Login, config,
                    purpose: $"閒置超過 {IdleLimit.TotalMinutes:0} 分鐘，管理畫面已鎖定，請重新登入。");
                if (login.ShowDialog() == DialogResult.OK) break;
                if (MessageBox.Show("要結束 CfTunnelTool 嗎？\n進行中的作業（例如部署）會被中斷。", "管理畫面已鎖定",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                {
                    Environment.Exit(0);
                }
            }
        }
        finally
        {
            foreach (var f in forms.Where(f => !f.IsDisposed)) f.Opacity = 1;
            _lastActivity = DateTime.Now;
            _locking = false;
        }
    }

    sealed class ActivityFilter : IMessageFilter
    {
        const int WmKeyDown = 0x100, WmSysKeyDown = 0x104, WmMouseMove = 0x200, WmLButtonDown = 0x201,
                  WmRButtonDown = 0x204, WmMouseWheel = 0x20A, WmNcMouseMove = 0xA0, WmNcLButtonDown = 0xA1;

        public bool PreFilterMessage(ref Message m)
        {
            if (!_locking && m.Msg is WmKeyDown or WmSysKeyDown or WmMouseMove or WmLButtonDown or WmRButtonDown
                    or WmMouseWheel or WmNcMouseMove or WmNcLButtonDown)
                _lastActivity = DateTime.Now;
            return false;
        }
    }
}
