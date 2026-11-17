using System.Media;
using System.Runtime.InteropServices;
using CfTunnelTool.Core;

namespace CfTunnelTool.UI;

/// <summary>
/// 連不到電腦時（通常是 2FA／Access 工作階段到期）：自動開啟驗證頁，並跳出這個視窗提醒使用者。
/// 每 2 秒測試一次連線，驗證完成、連得到就自動關閉（DialogResult.OK），由呼叫端開啟遠端桌面。
/// </summary>
sealed class AuthWaitForm : Form
{
    public static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(3);
    const int PollEverySeconds = 2;

    readonly ClientMachine _machine;
    readonly string _team;
    readonly ILog? _log;
    readonly bool _demo;
    readonly Label _heading, _message, _elapsed;
    readonly Button _reopen, _retry, _cancel;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    DateTime _start;
    bool _probing;

    public AuthWaitForm(ClientMachine machine, string team, ILog? log, bool demo = false)
    {
        _machine = machine;
        _team = team;
        _log = log;
        _demo = demo;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"CfTunnelTool － 連線驗證　v{Theme.Version}";
        Icon = Theme.AppIcon;
        Font = Theme.Base;
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(24, 18, 24, 16) };
        _heading = new Label { AutoSize = true, Font = Theme.Sub, Margin = new Padding(0, 0, 0, 8) };
        _message = new Label { AutoSize = true, MaximumSize = new Size(470, 0), Margin = new Padding(0, 0, 0, 8) };
        _elapsed = new Label { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 0, 0, 12) };
        layout.Controls.Add(_heading);
        layout.Controls.Add(_message);
        layout.Controls.Add(_elapsed);

        _reopen = Theme.SecondaryButton("重新開啟驗證頁");
        _reopen.Click += async (_, _) => await OpenAuthAsync();
        _retry = Theme.PrimaryButton("再等一次");
        _retry.Click += (_, _) => StartWaiting();
        _cancel = Theme.SecondaryButton("取消");
        _cancel.DialogResult = DialogResult.Cancel;
        CancelButton = _cancel;
        foreach (var b in new[] { _reopen, _retry, _cancel }) b.Margin = new Padding(0, 0, 8, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        buttons.Controls.AddRange([_reopen, _retry, _cancel]);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        ShowWaiting();
        ResumeLayout(false);
        PerformLayout();

        _timer.Tick += async (_, _) => await TickAsync();
        Shown += async (_, _) =>
        {
            Alert();
            if (_demo)
            {
                _elapsed.Text = "已等候 12 秒（最多 3 分鐘）";
                return;
            }
            StartWaiting();
            await OpenAuthAsync();
        };
        FormClosed += (_, _) => _timer.Dispose();
    }

    /// <summary>顯示等候視窗；驗證完成、連得到時回傳 true。</summary>
    public static bool Run(IWin32Window? owner, ClientMachine machine, string team, ILog? log)
    {
        using var f = new AuthWaitForm(machine, team, log);
        return f.ShowDialog(owner) == DialogResult.OK;
    }

    void ShowWaiting()
    {
        _heading.Text = "需要重新驗證 2FA";
        _heading.ForeColor = Theme.Warning;
        _message.Text =
            $"要連到 {_machine.Name}（{_machine.Ip}）前，需要先通過 Cloudflare 驗證。\n\n" +
            "已自動開啟驗證網頁：請在瀏覽器輸入 Email 驗證碼與 2FA。\n" +
            "驗證完成後，這個視窗會自動關閉並開啟遠端桌面，不需要再按任何按鈕。";
        _retry.Visible = false;
        _reopen.Visible = true;
        _cancel.Text = "取消";
    }

    void StartWaiting()
    {
        ShowWaiting();
        _start = DateTime.Now;
        _elapsed.Text = "等候驗證中…";
        _timer.Start();
    }

    void ShowTimedOut()
    {
        _timer.Stop();
        _heading.Text = $"仍然連不到 {_machine.Name}";
        _heading.ForeColor = Theme.Danger;
        _message.Text =
            "可能的原因：\n" +
            "  •  瀏覽器的驗證還沒完成（請確認已輸入 Email 驗證碼與 2FA）\n" +
            "  •  那台電腦沒有開機，或 IP 已改變\n" +
            "  •  WARP 沒有連線（請看右下角的 WARP 圖示）\n\n" +
            "完成驗證後可以按「再等一次」，或關閉後把記錄截圖給管理者。";
        _elapsed.Text = $"已等候 {(int)WaitLimit.TotalMinutes} 分鐘";
        _retry.Visible = true;
        _cancel.Text = "關閉";
        Alert();
    }

    async Task OpenAuthAsync()
    {
        _reopen.Enabled = false;
        try
        {
            var viaWarp = await Task.Run(() => WarpClient.OpenReauthenticate(_team));
            if (IsDisposed) return;
            if (viaWarp)
            {
                _log?.Info("已請 WARP 開啟驗證頁：請在瀏覽器完成 Email 驗證碼與 2FA。");
            }
            else
            {
                _log?.Info("已開啟 WARP 視窗與驗證網址；如果沒有出現驗證頁，請在 WARP 視窗點「個人資料 → 帳戶資訊 → 重新驗證」。");
                _message.Text += "\n\n如果沒有出現驗證頁：請在 WARP 視窗點「個人資料 → 帳戶資訊 → 重新驗證」。";
            }
            TopMost = false; // 讓瀏覽器的驗證頁可以蓋在上面
        }
        finally
        {
            if (!IsDisposed) _reopen.Enabled = true;
        }
    }

    async Task TickAsync()
    {
        var elapsed = DateTime.Now - _start;
        if (elapsed >= WaitLimit)
        {
            ShowTimedOut();
            return;
        }
        _elapsed.Text = $"已等候 {(int)elapsed.TotalSeconds} 秒（最多 {(int)WaitLimit.TotalMinutes} 分鐘）";
        if (_probing || (int)elapsed.TotalSeconds % PollEverySeconds != 0) return;

        _probing = true;
        try
        {
            if (await RdpHost.TestTcpAsync(_machine.Ip, _machine.Port, 1500) && !IsDisposed)
            {
                _timer.Stop();
                DialogResult = DialogResult.OK;
                Close();
            }
        }
        finally { _probing = false; }
    }

    /// <summary>主動提醒：視窗移到最上層、閃工作列並發出提示音。</summary>
    void Alert()
    {
        TopMost = true;
        Activate();
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = Handle,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount = 5,
        };
        FlashWindowEx(ref info);
        if (!_demo) SystemSounds.Exclamation.Play();
    }

    const uint FLASHW_ALL = 3, FLASHW_TIMERNOFG = 12;

    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
}
