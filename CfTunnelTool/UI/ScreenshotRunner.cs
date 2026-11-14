#if DEBUG
using System.Drawing.Imaging;
using CfTunnelTool.Core;

namespace CfTunnelTool.UI;

/// <summary>開發用：用假資料依序顯示每個畫面並截圖（CfTunnelTool.exe --screenshots 資料夾）。</summary>
static class ScreenshotRunner
{
    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);

        var state = new WizardState
        {
            ApiToken = "demo-token",
            TokenVerified = true,
            Zones =
            [
                new ZoneInfo("yourdomain.com", "zone", "account", "active"),
                new ZoneInfo("another-site.com", "zone2", "account", "active"),
            ],
            Domain = "yourdomain.com",
            TunnelName = "office-pc",
        };

        var wizard = new WizardForm(state)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(30, 30),
            TopMost = true,
            ConfirmOnClose = false,
        };
        wizard.Show();
        for (int i = 0; i < 6; i++)
        {
            if (i == 4)
                state.Routes =
                [
                    new RouteConfig { Hostname = "www.yourdomain.com", Service = "http://localhost:8080" },
                    new RouteConfig { Hostname = "api.yourdomain.com", Service = "http://localhost:5000" },
                ];
            if (i == 5) state.Result = new DeployResult("demo", true, "healthy", 0);

            wizard.ShowStep(i);
            if (i == 4) wizard.DemoDeployLog();
            Pump(i == 3 ? 4000 : 700);
            Capture(wizard, Path.Combine(dir, $"wizard-{i}.png"));
        }
        wizard.Close();

        var config = new ToolConfig
        {
            Domain = "yourdomain.com",
            TunnelName = "office-pc",
            TunnelId = "demo",
            Routes = state.Routes,
        };
        var dashboard = new DashboardForm(config, demo: true)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(30, 30),
            TopMost = true,
        };
        dashboard.Show();
        Pump(4500);
        Capture(dashboard, Path.Combine(dir, "dashboard.png"));
        dashboard.Close();

        var prompt = new TokenPromptForm { StartPosition = FormStartPosition.Manual, Location = new Point(30, 30), TopMost = true };
        prompt.Show();
        Pump(500);
        Capture(prompt, Path.Combine(dir, "token-prompt.png"));
        prompt.Close();
    }

    static void Pump(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    const uint PW_RENDERFULLCONTENT = 2;

    /// <summary>
    /// 只擷取這個視窗本身的內容（PrintWindow），不會拍到蓋在上面的其他視窗。
    /// </summary>
    static void Capture(Form form, string path)
    {
        using var bmp = new Bitmap(form.Width, form.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            try { PrintWindow(form.Handle, hdc, PW_RENDERFULLCONTENT); }
            finally { g.ReleaseHdc(hdc); }
        }
        bmp.Save(path, ImageFormat.Png);
    }
}
#endif
