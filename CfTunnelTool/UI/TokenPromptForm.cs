namespace CfTunnelTool.UI;

/// <summary>管理畫面需要 API Token 時跳出的小視窗。</summary>
sealed class TokenPromptForm : Form
{
    readonly TextBox _token;
    readonly CheckBox _remember;

    public string Token => _token.Text.Trim();
    public bool Remember => _remember.Checked;

    public TokenPromptForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "輸入 Cloudflare API Token";
        Icon = Theme.AppIcon;
        Font = Theme.Base;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(20, 18, 20, 16),
        };
        layout.Controls.Add(new Label
        {
            Text = "查看線上狀態或變更設定需要 API Token（就是設定時使用的那一組）。\n" +
                   "如果找不到了，可以到 Cloudflare → My Profile → API Tokens 重新建立。",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10),
        });
        _token = new TextBox { Width = 500, UseSystemPasswordChar = true, Font = Theme.Mono };
        layout.Controls.Add(_token);
        _remember = new CheckBox
        {
            Text = "記住這組 Token（以 Windows 加密儲存）",
            AutoSize = true,
            Checked = true,
            Margin = new Padding(0, 10, 0, 12),
        };
        layout.Controls.Add(_remember);

        var ok = Theme.PrimaryButton("確定");
        ok.DialogResult = DialogResult.OK;
        var cancel = Theme.SecondaryButton("取消");
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0),
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        ResumeLayout(false);
        PerformLayout();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK && Token.Length == 0)
        {
            e.Cancel = true;
            _token.Focus();
        }
        base.OnFormClosing(e);
    }
}
