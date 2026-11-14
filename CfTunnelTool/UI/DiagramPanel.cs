using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace CfTunnelTool.UI;

/// <summary>歡迎頁的架構示意圖：瀏覽器 → Cloudflare ← 這台電腦 → 網站/API。</summary>
sealed class DiagramPanel : Control
{
    static readonly (string Text, bool Highlight)[] Boxes =
    [
        ("任何地方的\n瀏覽器 / App", false),
        ("Cloudflare\n自動 HTTPS", true),
        ("這台電腦\ncloudflared 服務", false),
        ("你的網站\n與 API", false),
    ];

    static readonly (string Label, bool Reverse)[] Arrows =
    [
        ("HTTPS", false),
        ("加密通道", true),   // 由這台電腦主動往外連
        ("localhost", false),
    ];

    public DiagramPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Height = 120;
        Margin = new Padding(0, 6, 0, 12);
        BackColor = Color.White;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        float s = DeviceDpi / 96f;
        float gap = 86 * s;
        int n = Boxes.Length;
        float boxW = (Width - 2 - gap * (n - 1)) / n;
        float boxH = 66 * s;
        float top = (Height - boxH) / 2 + 8 * s;

        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var borderPen = new Pen(Theme.Border, 1.2f * s);

        for (int i = 0; i < n; i++)
        {
            var rect = new RectangleF(1 + i * (boxW + gap), top, boxW, boxH);
            using var path = RoundedRect(rect, 10 * s);
            using var fill = new SolidBrush(Boxes[i].Highlight ? Theme.Accent : Theme.Surface);
            g.FillPath(fill, path);
            if (!Boxes[i].Highlight) g.DrawPath(borderPen, path);
            using var textBrush = new SolidBrush(Boxes[i].Highlight ? Color.White : Theme.Text);
            g.DrawString(Boxes[i].Text, Boxes[i].Highlight ? Theme.Bold : Theme.Base, textBrush, rect, center);

            if (i < n - 1)
            {
                float y = top + boxH / 2;
                float x1 = rect.Right + 8 * s, x2 = rect.Right + gap - 8 * s;
                var (label, reverse) = Arrows[i];
                using var pen = new Pen(reverse ? Theme.Accent : Theme.Muted, 2.2f * s)
                {
                    CustomEndCap = new AdjustableArrowCap(4, 4),
                    DashStyle = reverse ? DashStyle.Dash : DashStyle.Solid,
                };
                if (reverse) g.DrawLine(pen, x2, y, x1, y);
                else g.DrawLine(pen, x1, y, x2, y);

                var labelRect = new RectangleF(rect.Right, y - 30 * s, gap, 22 * s);
                using var lb = new SolidBrush(reverse ? Theme.AccentDark : Theme.Muted);
                g.DrawString(label, Theme.Small, lb, labelRect, center);
            }
        }

        // 說明文字
        using var note = new SolidBrush(Theme.Muted);
        g.DrawString("虛線：這台電腦主動往外連線，所以不需要固定 IP，也不用設定路由器或防火牆",
            Theme.Small, note, new PointF(1, 0));
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
