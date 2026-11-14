using System.ComponentModel;
using System.Diagnostics;
using CfTunnelTool.Core;
using CfTunnelTool.UI;

namespace CfTunnelTool;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

#if DEBUG
        if (args.Length >= 2 && args[0] == "--screenshots")
        {
            ScreenshotRunner.Run(args[1]);
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--selftest")
        {
            SelfTest.Run(args[1]);
            return 0;
        }
#endif

        // 正式版的 manifest 已要求系統管理員權限；這裡是保險（例如開發模式執行）
        if (!Cloudflared.IsAdministrator())
            return RelaunchAsAdministrator();

        using var mutex = new Mutex(true, @"Global\CfTunnelTool.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("CfTunnelTool 已經在執行中。", "CfTunnelTool", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show($"發生未預期的錯誤：\n\n{e.Exception.Message}", "CfTunnelTool",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

        ToolConfig? config = null;
        try
        {
            config = AppStorage.LoadConfig();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or IOException)
        {
            MessageBox.Show($"既有的設定檔無法讀取，將重新執行設定精靈。\n\n{ex.Message}", "CfTunnelTool",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // 這台電腦是「使用者端」（之前選過「我要連到公司的電腦」）
        if (config is null && AppStorage.LoadClient() is { } client)
        {
            Application.Run(new ClientForm(client));
            return 0;
        }

        // 第一次使用：先選擇模式；管理者跑設定精靈，完成後進入管理畫面
        if (config is null)
        {
            var chooser = new ModeChooserForm();
            Application.Run(chooser);
            if (chooser.Mode == AppMode.User)
            {
                Application.Run(new ClientForm(null));
                return 0;
            }
            if (chooser.Mode != AppMode.Admin) return 0;

            var state = WizardState.From(null, AppStorage.LoadToken());
            Application.Run(new WizardForm(state));
            if (state.Result is null) return 0;
            config = AppStorage.LoadConfig();
            if (config is null) return 0;
        }

        Application.Run(new DashboardForm(config));
        return 0;
    }

    static int RelaunchAsAdministrator()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Win32Exception)
        {
            MessageBox.Show("CfTunnelTool 需要系統管理員權限才能安裝連線服務。\n請重新開啟，並在 Windows 詢問時按「是」。",
                "CfTunnelTool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        return 0;
    }
}
