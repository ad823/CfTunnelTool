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

        // 桌面的遠端桌面捷徑：一般權限就能執行（不跳 UAC），先檢查驗證、需要時自動開驗證頁，再開遠端桌面
        if (ConnectArgs.Parse(args) is { } connect)
            return QuickConnectForm.Run(connect);

        // 程式已開啟時再開一次：在提權之前就處理，不跳 UAC、不跳彈窗，直接把原本的視窗帶到前面
        if (!IsBackgroundTask(args) && SingleInstance.IsRunning())
            return ActivateExisting();

        // 其他功能（安裝服務、部署、管理、啟用遠端桌面）都需要系統管理員：
        // manifest 是 asInvoker（讓上面的捷徑免 UAC），所以在這裡提權重新執行
        if (!Cloudflared.IsAdministrator())
            return RelaunchAsAdministrator(args);

        // 「結束遠端並交還畫面」捷徑：不開任何視窗，直接交還
        if (args.Contains("--return-console"))
        {
            if (ConsoleSession.ReturnToConsole() is { } error)
                MessageBox.Show(error, "結束遠端並交還畫面", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 0;
        }

        // 排程每小時執行：收集紀錄後結束，不開任何視窗
        if (args.Contains("--collect-logs"))
        {
            try
            {
                Core.Logs.LogCollector.RunAsync(Core.Logs.CollectorSchedule.LoadToken()).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return 1;
            }
        }

        // 「設定 → 應用程式」的解除安裝
        if (args.Contains("--uninstall"))
            return UninstallFromAppsList();

        // 提權後再確認一次（兩次同時開啟時的競爭）
        using var mutex = new Mutex(true, SingleInstance.MutexName, out var isFirst);
        if (!isFirst)
            return ActivateExisting();
        using var activation = SingleInstance.StartListener();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show($"發生未預期的錯誤：\n\n{e.Exception.Message}", "CfTunnelTool",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

        var config = TryLoadConfig();

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
            switch (chooser.Mode)
            {
                case AppMode.User:
                    Application.Run(new ClientForm(null));
                    return 0;
                case AppMode.Host:
                    Application.Run(new HostSetupForm());
                    return 0;
                case AppMode.Admin:
                    if (!AdminGate.Ensure(null)) return 0;
                    break;
                default:
                    return 0;
            }

            var state = WizardState.From(null, AppStorage.LoadToken());
            Application.Run(new WizardForm(state));
            if (state.Result is null) return 0;
            config = AppStorage.LoadConfig();
            if (config is null) return 0;
        }
        else if (!AdminGate.Ensure(config))
        {
            return 0; // 管理畫面需要登入
        }

        Application.Run(new DashboardForm(config));
        return 0;
    }

    /// <summary>排程、捷徑等不開主視窗的啟動方式，不受單一執行個體限制。</summary>
    static bool IsBackgroundTask(string[] args) =>
        args.Contains("--return-console") || args.Contains("--collect-logs") || args.Contains("--uninstall");

    /// <summary>通知原本的程式把視窗帶到前面；原本的程式在其他使用者的工作階段時才顯示提示。</summary>
    static int ActivateExisting()
    {
        if (SingleInstance.SignalExisting() == ActivateResult.Activated) return 0;
        // 找不到喚醒事件：原本的程式在其他使用者的工作階段，或是 1.11.1 以前的舊版
        MessageBox.Show("CfTunnelTool 已經在執行中，但無法自動切換過去。\n\n" +
                        "可能是在這台電腦的其他使用者工作階段，或是舊版本仍開著；請切換到原本的視窗，或先關閉它再開啟。",
            "CfTunnelTool", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    static ToolConfig? TryLoadConfig()
    {
        try
        {
            return AppStorage.LoadConfig();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or IOException)
        {
            MessageBox.Show($"既有的設定檔無法讀取，將重新執行設定精靈。\n\n{ex.Message}", "CfTunnelTool",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
    }

    /// <summary>從「設定 → 應用程式」解除安裝：提醒先移除 Cloudflare 上的設定，再移除管理程式。</summary>
    static int UninstallFromAppsList()
    {
        if (AppStorage.HasConfig)
        {
            // 已部署的電腦：解除安裝也要管理帳號密碼，避免任何人都能移除
            if (!AdminGate.Ensure(TryLoadConfig())) return 0;
            var answer = MessageBox.Show(
                "這台電腦還有 CfTunnelTool 的 Cloudflare 設定（Tunnel、DNS、遠端桌面）與連線服務。\n\n" +
                "建議先開啟管理畫面按「移除全部設定」，再解除安裝。\n\n" +
                "是：開啟管理畫面\n否：只移除管理程式（連線服務與 Cloudflare 設定保留）\n取消：不做任何事",
                "解除安裝 CfTunnelTool", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (answer == DialogResult.Cancel) return 0;
            if (answer == DialogResult.Yes)
            {
                if (TryLoadConfig() is { } config) Application.Run(new DashboardForm(config));
                return 0;
            }
        }
        else if (MessageBox.Show("確定要解除安裝 CfTunnelTool 嗎？", "解除安裝 CfTunnelTool",
                     MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return 0;
        }

        var log = new MessageLog();
        SelfInstaller.Uninstall(log);
        MessageBox.Show("CfTunnelTool 已解除安裝。", "解除安裝 CfTunnelTool", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    /// <summary>沒有記錄視窗時使用的 ILog（不顯示訊息）。</summary>
    sealed class MessageLog : ILog
    {
        public void Info(string message) { }
        public void Step(string message) { }
        public void Ok(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }

    static int RelaunchAsAdministrator(string[] args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(" ", args.Select(a => $"\"{a}\"")),
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
