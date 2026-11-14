using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CfTunnelTool.Core;

/// <summary>
/// 把本程式複製到 C:\Program Files\CfTunnelTool\，建立開始功能表與桌面捷徑，
/// 並登錄到「設定 → 應用程式」清單（顯示發行者），讓使用者之後可以隨時開啟或解除安裝。
/// </summary>
static class SelfInstaller
{
    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CfTunnelTool");

    public static string InstalledExe => Path.Combine(InstallDir, "CfTunnelTool.exe");

    static string StartMenuShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "CfTunnelTool.lnk");

    static string DesktopShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "CfTunnelTool.lnk");

    /// <summary>「結束遠端並交還畫面」捷徑：放在所有使用者共用的桌面，遠端連進去就看得到。</summary>
    public static string ReturnConsoleShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "結束遠端並交還畫面.lnk");

    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\CfTunnelTool";

    /// <summary>發行商名稱（來自 csproj 的 Company）。</summary>
    public static string Publisher =>
        typeof(SelfInstaller).Assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "";

    /// <summary>單一執行檔發佈時 Assembly.Location 會是空字串（這正是我們要判斷的）。</summary>
#pragma warning disable IL3000
    static bool IsSingleFile => string.IsNullOrEmpty(typeof(SelfInstaller).Assembly.Location);
#pragma warning restore IL3000

    static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>給捷徑使用的執行檔：已安裝就用安裝位置，否則用目前的執行檔。</summary>
    static string ShortcutTarget => File.Exists(InstalledExe) ? InstalledExe : Environment.ProcessPath!;

    public static void Install(ILog log)
    {
        if (!IsSingleFile)
        {
            log.Info("（開發模式執行，略過安裝程式與捷徑）");
            return;
        }

        var current = Environment.ProcessPath!;
        if (!SamePath(current, InstalledExe))
        {
            Directory.CreateDirectory(InstallDir);
            File.Copy(current, InstalledExe, overwrite: true);
            log.Ok($"管理程式已安裝到 {InstalledExe}");
        }

        CreateShortcut(StartMenuShortcut, InstalledExe, "", "CfTunnelTool");
        CreateShortcut(DesktopShortcut, InstalledExe, "", "CfTunnelTool");
        log.Ok("已建立開始功能表與桌面捷徑「CfTunnelTool」");

        RegisterUninstallEntry();
        log.Ok($"已登錄到「設定 → 應用程式」（發行者：{Publisher}）");
    }

    /// <summary>登錄到「設定 → 應用程式」，顯示發行者與版本，並提供解除安裝。</summary>
    static void RegisterUninstallEntry()
    {
        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "CfTunnelTool");
        key.SetValue("DisplayVersion", typeof(SelfInstaller).Assembly.GetName().Version?.ToString(3) ?? "");
        key.SetValue("Publisher", Publisher);
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try { key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord); }
        catch (IOException) { }
    }

    public static void Uninstall(ILog log)
    {
        foreach (var lnk in new[] { StartMenuShortcut, DesktopShortcut, ReturnConsoleShortcut })
            if (File.Exists(lnk)) File.Delete(lnk);
        Logs.CollectorSchedule.Disable(); // 移除每小時收集紀錄的排程
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        if (!Directory.Exists(InstallDir)) return;

        if (Environment.ProcessPath is { } current && SamePath(Path.GetDirectoryName(current)!, InstallDir))
        {
            // 正在執行的 exe 無法刪除自己：程式結束 3 秒後由 cmd 刪除
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c timeout /t 3 /nobreak > nul & rmdir /s /q \"{InstallDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        else
        {
            Directory.Delete(InstallDir, recursive: true);
        }
        log.Ok("已移除管理程式與捷徑");
    }

    /// <summary>在共用桌面建立「結束遠端並交還畫面」捷徑（執行 CfTunnelTool.exe --return-console）。</summary>
    public static void CreateReturnConsoleShortcut(ILog log)
    {
        CreateShortcut(ReturnConsoleShortcut, ShortcutTarget, "--return-console",
            "結束遠端桌面，並把畫面交還給電腦前面的螢幕（不需重新登入）");
        log.Ok("已在桌面建立「結束遠端並交還畫面」捷徑（所有使用者都看得到）");
    }

    public static bool HasReturnConsoleShortcut => File.Exists(ReturnConsoleShortcut);

    static void CreateShortcut(string lnkPath, string target, string arguments, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("無法建立捷徑（找不到 WScript.Shell）。");
        var shell = Activator.CreateInstance(shellType)!;
        try
        {
            var lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [lnkPath])!;
            try
            {
                var t = lnk.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, [target]);
                t.InvokeMember("Arguments", BindingFlags.SetProperty, null, lnk, [arguments]);
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, [Path.GetDirectoryName(target)!]);
                t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, [description]);
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            }
            finally { Marshal.FinalReleaseComObject(lnk); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }
}
