using CfTunnelTool.UI.Steps;

namespace CfTunnelTool.UI;

/// <summary>設定精靈：左側步驟列、中間內容、下方按鈕。</summary>
sealed class WizardForm : Form
{
    readonly WizardState _state;
    readonly StepPage[] _steps;
    readonly Panel _host;
    readonly Label _heading;
    readonly Button _back, _next, _cancel;
    readonly List<Label> _sideItems = [];
    int _index = -1;

    /// <summary>截圖測試時關閉「確定要離開」的詢問。</summary>
    internal bool ConfirmOnClose = true;

    StepPage Current => _steps[_index];

    public WizardForm(WizardState state)
    {
        _state = state;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = (state.IsEditing ? "CfTunnelTool － 修改設定" : "CfTunnelTool 設定精靈") + $"　v{Theme.Version}";
        Icon = Theme.AppIcon;
        ClientSize = new Size(1040, 720);
        MinimumSize = new Size(920, 660);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        Font = Theme.Base;

        _steps =
        [
            new WelcomeStep(state),
            new TokenStep(state),
            new DomainStep(state),
            new RoutesStep(state),
            new RdpStep(state),
            new DeployStep(state),
            new DoneStep(state),
        ];
        foreach (var s in _steps) s.StateChanged += (_, _) => UpdateButtons();

        // ---- 內容區 ----
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(36, 24, 28, 12) };
        _host = new Panel { Dock = DockStyle.Fill };
        _heading = new Label
        {
            Dock = DockStyle.Top,
            Height = 56,
            Font = Theme.Heading,
            ForeColor = Theme.Text,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        content.Controls.Add(_host);
        content.Controls.Add(_heading);

        // ---- 下方按鈕列 ----
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 68, BackColor = Theme.Surface };

        _next = Theme.PrimaryButton("下一步  >");
        _next.MinimumSize = new Size(130, 40);
        _next.Margin = new Padding(0);
        _next.Click += (_, _) => GoNext();
        _back = Theme.SecondaryButton("<  上一步");
        _back.MinimumSize = new Size(110, 40);
        _back.Margin = new Padding(0, 0, 10, 0);
        _back.Click += (_, _) => { if (_index > 0) ShowStep(_index - 1); };
        _cancel = Theme.SecondaryButton("取消");
        _cancel.MinimumSize = new Size(90, 40);
        _cancel.Click += (_, _) => Close();

        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(0, 14, 28, 0),
        };
        right.Controls.Add(_next);
        right.Controls.Add(_back);
        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(28, 14, 0, 0),
        };
        left.Controls.Add(_cancel);
        footer.Controls.Add(right);
        footer.Controls.Add(left);
        footer.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Border }); // 分隔線

        // 加入順序決定 Dock 的配置：側欄佔滿高度，按鈕列只在內容區下方
        Controls.Add(content);
        Controls.Add(footer);
        Controls.Add(BuildSidebar());

        ResumeLayout(false);
        PerformLayout();

        ShowStep(0);
    }

    Panel BuildSidebar()
    {
        var side = new Panel { Dock = DockStyle.Left, Width = 230, BackColor = Theme.Sidebar };
        var list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(24, 30, 12, 12),
        };
        list.Controls.Add(new Label
        {
            Text = "CfTunnelTool",
            AutoSize = true,
            Font = new Font(Theme.FontName, 14F, FontStyle.Bold),
            ForeColor = Color.White,
            Margin = new Padding(0, 0, 0, 2),
        });
        list.Controls.Add(new Label
        {
            Text = "讓外網用網址連到這台電腦",
            AutoSize = true,
            Font = Theme.Small,
            ForeColor = Theme.SidebarText,
            Margin = new Padding(0, 0, 0, 30),
        });
        foreach (var step in _steps)
        {
            var item = new Label
            {
                Text = step.SidebarTitle,
                AutoSize = true,
                Font = Theme.Base,
                ForeColor = Theme.SidebarText,
                Margin = new Padding(0, 8, 0, 8),
                Tag = step.SidebarTitle,
            };
            _sideItems.Add(item);
            list.Controls.Add(item);
        }
        side.Controls.Add(list);
        side.Controls.Add(new Label
        {
            Text = $"v{Theme.Version}　使用 Cloudflare Tunnel",
            Dock = DockStyle.Bottom,
            Height = 36,
            Font = Theme.Small,
            ForeColor = Color.FromArgb(100, 108, 120),
            Padding = new Padding(24, 0, 0, 0),
        });
        return side;
    }

    internal void ShowStep(int index)
    {
        _host.SuspendLayout();
        _host.Controls.Clear();
        _index = index;
        _host.Controls.Add(Current);
        _heading.Text = Current.Heading;
        Current.OnEnter();
        _host.ResumeLayout();

        string[] marks = ["①", "②", "③", "④", "⑤", "⑥", "⑦", "⑧"];
        for (int i = 0; i < _sideItems.Count; i++)
        {
            var item = _sideItems[i];
            var title = (string)item.Tag!;
            if (i < index)
            {
                item.Text = $"✓   {title}";
                item.ForeColor = Color.FromArgb(120, 200, 150);
                item.Font = Theme.Base;
            }
            else if (i == index)
            {
                item.Text = $"{marks[i]}   {title}";
                item.ForeColor = Theme.Accent;
                item.Font = Theme.Bold;
            }
            else
            {
                item.Text = $"{marks[i]}   {title}";
                item.ForeColor = Theme.SidebarText;
                item.Font = Theme.Base;
            }
        }
        UpdateButtons();
    }

    void UpdateButtons()
    {
        if (_index < 0) return;
        var s = Current;
        _next.Text = s.NextText;
        _next.Enabled = s.CanGoNext && !s.IsBusy;
        _back.Visible = _index > 0 && s.CanGoBack;
        _back.Enabled = !s.IsBusy;
        _cancel.Visible = _index < _steps.Length - 1;
        _cancel.Enabled = !s.IsBusy;
    }

    void GoNext()
    {
        if (!Current.CanGoNext || !Current.OnNext()) return;
        if (_index == _steps.Length - 1) Close();
        else ShowStep(_index + 1);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (Current.IsBusy)
        {
            e.Cancel = true;
            MessageBox.Show(this, "正在處理中，請等待完成後再關閉。", "請稍候", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (ConfirmOnClose && _state.Result is null && _index > 0 &&
            MessageBox.Show(this, "設定還沒完成，確定要離開嗎？", "離開設定精靈",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }

#if DEBUG
    internal void DemoDeployLog() => _steps.OfType<DeployStep>().First().DemoLog();
    internal void DemoScrollToEnd() => Current.ScrollToEnd();
#endif
}
