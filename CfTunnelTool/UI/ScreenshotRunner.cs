#if DEBUG
using System.Drawing.Imaging;
using CfTunnelTool.Core;

namespace CfTunnelTool.UI;

/// <summary>開發用：用假資料依序顯示每個畫面並截圖（CfTunnelTool.exe --screenshots 資料夾）。</summary>
static class ScreenshotRunner
{
    public static void Run(string dir)
    {
        LogBox.PersistToFile = false; // 截圖的假記錄不寫入真正的操作紀錄
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
                    new RouteConfig { Hostname = "mysql.yourdomain.com", Service = "tcp://localhost:3306" },
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
        // 另一個地點（獨立虛擬網路），IP 與第一個地點相同也不衝突
        demoClient.Machines.Add(new ClientMachine
        {
            Name = "factory-pc", Ip = "192.168.10.20", Port = 3389,
            VnetId = "b2c3d4e5-0000-4000-8000-000000000000", VnetName = "factory-b",
        });
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

        var wait = new AuthWaitForm(demoClient.Machines[0], "yourteam", null, demo: true)
            { StartPosition = FormStartPosition.Manual, Location = new Point(30, 30) };
        wait.Show();
        Pump(800);
        Capture(wait, Path.Combine(dir, "auth-wait.png"));
        wait.Close();

        var host = new HostSetupForm { StartPosition = FormStartPosition.Manual, Location = new Point(30, 30), TopMost = true };
        host.Show();
        Pump(3000);
        Capture(host, Path.Combine(dir, "host-setup.png"));
        host.Close();

        var edit = new MachineEditForm(demoClient.Machines[0]) { StartPosition = FormStartPosition.Manual, Location = new Point(30, 30), TopMost = true };
        edit.Show();
        Pump(500);
        Capture(edit, Path.Combine(dir, "machine-edit.png"));
        edit.Close();

        // 管理帳號密碼視窗：暫存的密碼檔，不影響本機設定
        var authPath = Path.Combine(Path.GetTempPath(), $"cftunnel-shots-admin-{Guid.NewGuid():N}.dat");
        try
        {
            var auth = new AdminAuth(authPath);
            var demoConfig = new ToolConfig { Domain = "yourdomain.com", TunnelName = "office-pc" };
            foreach (var (mode, name) in new[] { (AdminLoginMode.Setup, "admin-setup"), (AdminLoginMode.Reset, "admin-reset"),
                                                 (AdminLoginMode.Login, "admin-login"), (AdminLoginMode.ChangePassword, "admin-change") })
            {
                if (mode == AdminLoginMode.Login) auth.Create("admin", "DemoPassword1");
                var f = new AdminLoginForm(mode, demoConfig, auth) { StartPosition = FormStartPosition.Manual, Location = new Point(30, 30), TopMost = true };
                f.Show();
                Pump(500);
                Capture(f, Path.Combine(dir, name + ".png"));
                f.Close();
            }
        }
        finally
        {
            try { File.Delete(authPath); } catch (IOException) { }
        }

        // 紀錄視窗：用暫存資料夾的假資料
        var realRoot = Core.Logs.LogStore.Root;
        Core.Logs.LogStore.Root = Path.Combine(Path.GetTempPath(), "cftunnel-shots-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            SeedDemoLogs();
            var logs = new LogsForm(LogsMode.Admin) { StartPosition = FormStartPosition.Manual, Location = new Point(30, 30), TopMost = true };
            logs.Show();
            Pump(1500);
            Capture(logs, Path.Combine(dir, "logs-login.png"));
            var tabs = FindTabs(logs);
            if (tabs is not null)
            {
                tabs.SelectedIndex = 1;
                Pump(500);
                Capture(logs, Path.Combine(dir, "logs-rdp.png"));
                tabs.SelectedIndex = 2;
                Pump(500);
                Capture(logs, Path.Combine(dir, "logs-network.png"));
            }
            logs.Close();
        }
        finally
        {
            try { Directory.Delete(Core.Logs.LogStore.Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Core.Logs.LogStore.Root = realRoot;
        }
    }

    static TabControl? FindTabs(Control c) =>
        c as TabControl ?? c.Controls.Cast<Control>().Select(FindTabs).FirstOrDefault(t => t is not null);

    static void SeedDemoLogs()
    {
        var now = DateTime.Now;
        Core.Logs.LogStore.Append(Core.Logs.LogKinds.Login, new[]
        {
            new Core.Logs.LoginRecord(now.AddHours(-1), "alice@yourdomain.com", "203.0.113.10", "yourteam.cloudflareaccess.com", "login", true, "onetimepin", "a1"),
            new Core.Logs.LoginRecord(now.AddMinutes(-50), "alice@yourdomain.com", "203.0.113.10", "rdp.yourdomain.com", "login", true, "onetimepin", "a2"),
            new Core.Logs.LoginRecord(now.AddMinutes(-20), "unknown@example.com", "198.51.100.7", "rdp.yourdomain.com", "login", false, "onetimepin", "a3"),
            new Core.Logs.LoginRecord(now.AddMinutes(-5), "bob@yourdomain.com", "203.0.113.22", "warp", "login", true, "onetimepin", "a4"),
        }, r => r.Time);
        Core.Logs.LogStore.Append(Core.Logs.LogKinds.RdpEvents, new[]
        {
            new Core.Logs.RdpEventRecord(now.AddMinutes(-48), 21, "OFFICE-PC", @"OFFICE-PC\alice", 2, "192.168.10.5", 1),
            new Core.Logs.RdpEventRecord(now.AddMinutes(-12), 24, "OFFICE-PC", @"OFFICE-PC\alice", 2, "192.168.10.5", 2),
            new Core.Logs.RdpEventRecord(now.AddMinutes(-30), 4625, "OFFICE-PC", @"OFFICE-PC\admin", 0, "192.168.10.5", 3),
            new Core.Logs.RdpEventRecord(now.AddMinutes(-4), 21, "OFFICE-PC", @"OFFICE-PC\bob", 3, "192.168.10.5", 4),
        }, r => r.Time);
        Core.Logs.LogStore.Append(Core.Logs.LogKinds.Network, new[]
        {
            new Core.Logs.NetworkRecord(now.AddMinutes(-4), "bob@yourdomain.com", "192.168.10.20:3389", 2_450_000, 38_900_000, 6),
            new Core.Logs.NetworkRecord(now.AddMinutes(-3), "bob@yourdomain.com", "192.168.10.20:3389", 1_120_000, 21_300_000, 4),
        }, r => r.Time);
        Core.Logs.LogStore.Append(Core.Logs.LogKinds.Operation, new[]
        {
            new Core.Logs.OperationRecord(now.AddHours(-2), "步驟", "設定瀏覽器遠端桌面應用程式", "OFFICE-PC"),
            new Core.Logs.OperationRecord(now.AddHours(-2), "成功", "瀏覽器遠端桌面已設定", "OFFICE-PC"),
        }, r => r.Time);
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
