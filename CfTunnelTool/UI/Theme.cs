using System.Diagnostics;

namespace CfTunnelTool.UI;

static class Theme
{
    public const string FontName = "Microsoft JhengHei UI";

    /// <summary>程式版本（由 csproj 的 Version 或 CI 依 tag 設定），例如 1.0.0。</summary>
    public static readonly string Version =
        typeof(Theme).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    public static readonly Font Base = new(FontName, 10F);
    public static readonly Font Bold = new(FontName, 10F, FontStyle.Bold);
    public static readonly Font Small = new(FontName, 9F);
    public static readonly Font Sub = new(FontName, 12F, FontStyle.Bold);
    public static readonly Font Heading = new(FontName, 17F, FontStyle.Bold);
    public static readonly Font CardValue = new(FontName, 13F, FontStyle.Bold);
    public static readonly Font Mono = new("Consolas", 9.75F);

    public static readonly Color Accent = Color.FromArgb(243, 128, 32);       // Cloudflare 橘
    public static readonly Color AccentDark = Color.FromArgb(196, 95, 10);
    public static readonly Color Sidebar = Color.FromArgb(30, 35, 45);
    public static readonly Color SidebarText = Color.FromArgb(150, 158, 172);
    public static readonly Color Text = Color.FromArgb(33, 37, 41);
    public static readonly Color Muted = Color.FromArgb(108, 117, 125);
    public static readonly Color Border = Color.FromArgb(222, 226, 230);
    public static readonly Color Surface = Color.FromArgb(246, 247, 249);
    public static readonly Color HelpBack = Color.FromArgb(255, 246, 233);
    public static readonly Color WarnBack = Color.FromArgb(253, 238, 238);
    public static readonly Color Success = Color.FromArgb(25, 135, 84);
    public static readonly Color Warning = Color.FromArgb(200, 115, 0);
    public static readonly Color Danger = Color.FromArgb(200, 35, 51);

    static Icon? _icon;
    public static Icon AppIcon
    {
        get
        {
            if (_icon is null)
            {
                using var s = typeof(Theme).Assembly.GetManifestResourceStream("app.ico");
                _icon = s is null ? SystemIcons.Application : new Icon(s);
            }
            return _icon;
        }
    }

    public static Button PrimaryButton(string text)
    {
        var b = BaseButton(text);
        b.BackColor = Accent;
        b.ForeColor = Color.White;
        b.Font = Bold;
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = AccentDark;
        b.EnabledChanged += (_, _) => b.BackColor = b.Enabled ? Accent : Color.FromArgb(225, 228, 232);
        return b;
    }

    public static Button SecondaryButton(string text)
    {
        var b = BaseButton(text);
        b.BackColor = Color.White;
        b.ForeColor = Text;
        b.FlatAppearance.BorderColor = Color.FromArgb(200, 204, 210);
        b.FlatAppearance.MouseOverBackColor = Surface;
        return b;
    }

    public static Button DangerButton(string text)
    {
        var b = SecondaryButton(text);
        b.ForeColor = Danger;
        b.FlatAppearance.BorderColor = Color.FromArgb(230, 160, 165);
        b.FlatAppearance.MouseOverBackColor = WarnBack;
        return b;
    }

    static Button BaseButton(string text) => new()
    {
        Text = text,
        FlatStyle = FlatStyle.Flat,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowOnly,
        MinimumSize = new Size(96, 36),
        Padding = new Padding(10, 2, 10, 2),
        Margin = new Padding(0, 0, 8, 0),
        Cursor = Cursors.Hand,
        UseVisualStyleBackColor = false,
    };

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { MessageBox.Show($"無法開啟瀏覽器，請手動前往：\n{url}"); }
    }
}
