namespace CfTunnelTool.UI;

/// <summary>精靈中的一個步驟。內容由上而下排列在 <see cref="Stack"/> 裡。</summary>
abstract class StepPage : UserControl
{
    protected readonly TableLayoutPanel Stack;
    readonly Panel _scroller;

    protected StepPage()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.White;
        Font = Theme.Base;
        ForeColor = Theme.Text;

        // 外層 Panel 負責捲動；內容只有超出高度時才出現捲軸
        _scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        Stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 0,
            Padding = new Padding(0, 0, 12, 0),
        };
        Stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _scroller.Controls.Add(Stack);
        Controls.Add(_scroller);
    }

    /// <summary>左側步驟列顯示的名稱。</summary>
    public abstract string SidebarTitle { get; }

    /// <summary>頁面上方的大標題。</summary>
    public abstract string Heading { get; }

    public virtual string NextText => "下一步  >";
    public virtual bool CanGoNext => true;
    public virtual bool CanGoBack => true;

    /// <summary>執行中（例如部署中）時不允許切換頁面或關閉視窗。</summary>
    public virtual bool IsBusy => false;

    public event EventHandler? StateChanged;
    protected void NotifyStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>每次切換到這一頁時呼叫。</summary>
    public virtual void OnEnter() { }

    /// <summary>按「下一步」時呼叫，回傳 false 可阻止離開（例如驗證失敗）。</summary>
    public virtual bool OnNext() => true;

    // ---------- 版面輔助 ----------

    protected T Add<T>(T control, bool stretch = true, bool fill = false) where T : Control
    {
        if (fill)
        {
            // 有「填滿剩餘高度」的列（例如記錄視窗）時，改成整頁不捲動
            _scroller.AutoScroll = false;
            Stack.AutoSize = false;
            Stack.Dock = DockStyle.Fill;
        }
        var row = Stack.RowCount++;
        Stack.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        if (fill) control.Dock = DockStyle.Fill;
        else control.Anchor = stretch ? AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
                                      : AnchorStyles.Left | AnchorStyles.Top;
        Stack.Controls.Add(control, 0, row);
        return control;
    }

    protected Label AddText(string text, Font? font = null, Color? color = null, int top = 2, int bottom = 8)
        => Add(new Label
        {
            Text = text,
            AutoSize = true,
            Font = font ?? Theme.Base,
            ForeColor = color ?? Theme.Text,
            Margin = new Padding(0, top, 0, bottom),
        });

    /// <summary>加入一個有底色的說明框，回傳內文 Label 以便之後更新。</summary>
    protected Label AddHelpBox(string title, string body, Color? back = null, Color? titleColor = null)
    {
        var box = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = back ?? Theme.HelpBack,
            Padding = new Padding(16, 12, 16, 14),
            Margin = new Padding(0, 8, 0, 10),
        };
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        box.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Font = Theme.Bold,
            ForeColor = titleColor ?? Theme.AccentDark,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0),
        }, 0, 0);
        var bodyLabel = new Label
        {
            Text = body,
            AutoSize = true,
            ForeColor = Theme.Text,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 6, 0, 0),
        };
        box.Controls.Add(bodyLabel, 0, 1);
        Add(box);
        return bodyLabel;
    }

    protected FlowLayoutPanel AddRow(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 6),
        };
        row.Controls.AddRange(controls);
        return Add(row, stretch: false);
    }

    protected static Label InlineLabel(string text, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = color ?? Theme.Text,
        Margin = new Padding(0, 8, 8, 0),
    };
}
