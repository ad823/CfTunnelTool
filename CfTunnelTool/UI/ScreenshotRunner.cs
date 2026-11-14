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
        // 0 歡迎、1 Token、2 網域、3 網址、4 遠端桌面、5 部署、6 完成
        for (int i = 0; i < 7; i++)
        {
            if (i == 4) state.Rdp = DemoRdp(); // 避免截到真實的電腦名稱與 IP
            if (i == 5)
                state.Routes =
                [
                    new RouteConfig { Hostname = "www.yourdomain.com", Service = "http://localhost:8080" },
                    new RouteConfig { Hostname = "api.yourdomain.com", Service = "http://localhost:5000" },
                ];
            if (i == 6) state.Result = new DeployResult("demo", true, "healthy", 0);

            wizard.ShowStep(i);
            if (i == 5) wizard.DemoDeployLog();
            Pump(i is 3 or 4 ? 4000 : 700);
            Capture(wizard, Path.Combine(dir, $"wizard-{i}.png"));
            if (i == 4)
            {
                wizard.DemoScrollToEnd();
                Pump(500);
                Capture(wizard, Path.Combine(dir, "wizard-4b.png"));
            }
        }
        wizard.Close();

        var config = new ToolConfig
        {
            Domain = "yourdomain.com",
            TunnelName = "office-pc",
            TunnelId = "demo",
            Routes = state.Routes,
            Rdp = DemoRdp(),
        };
        var dashboard = new DashboardForm(config, demo: true)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(30, 30),
            TopMost = true,
        };
        dashboard.Show();
        Pump(8000); // 等本機服務與 RDP 連接埠檢查（各最多 3 秒）完成
        Capture(dashboard, Path.Combine(dir, "dashboard.png"));
        dashboard.Close();

        var prompt = new TokenPromptForm { StartPosition = FormStartPosition.Manual, Location = new Point(30, 30), TopMost = true };
        prompt.Show();
        Pump(500);
        Capture(prompt, Path.Combine(dir, "token-prompt.png"));
        prompt.Close();

        var chooser = new ModeChooserForm { StartPosition = FormStartPosition.Manual, Location = new Point(30, 30), TopMost = true };
        chooser.Show();
        Pump(500);
        Capture(chooser, Path.Combine(dir, "mode-chooser.png"));
        chooser.Close();

        var demoClient = ClientConfig.FromRdp(DemoRdp());
        demoClient.Team = "yourteam";
        demoClient.LastVerifiedAt = DateTime.Now.AddHours(-2);
        demoClient.Machines[0].UserName = @"OFFICE-PC\alice";
        demoClient.Machines[0].LastConnectedAt = DateTime.Now.AddHours(-2);
        demoClient.Machines.Add(new ClientMachine { Name = "meeting-room", Ip = "192.168.10.45", Port = 3389 });
        var client = new ClientForm(demoClient, demo: true)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(30, 30),
            TopMost = true,
        };
        client.Show();
        Pump(1500);
        Capture(client, Path.Combine(dir, "client.png"));
        client.Close();

        var edit = new MachineEditForm(demoClient.Machines[0]) { StartPosition = FormStartPosition.Manual, Location = new Point(30, 30), TopMost = true };
        edit.Show();
        Pump(500);
        Capture(edit, Path.Combine(dir, "machine-edit.png"));
        edit.Close();
    }

    static RdpConfig DemoRdp() => new()
    {
        Enabled = true,
        WarpEnabled = true,
        Hostname = "rdp.yourdomain.com",
        AllowedEmails = ["alice@yourdomain.com", "@yourdomain.com"],
        VnetId = "a1b2c3d4-0000-4000-8000-000000000000",
        AuthDomain = "yourteam.cloudflareaccess.com",
        Machines =
        [
            new RdpMachine { Name = "office-pc", Ip = "192.168.10.20", Port = 3389 },
            new RdpMachine { Name = "accounting-pc", Ip = "192.168.10.31", Port = 3389 },
        ],
    };

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
