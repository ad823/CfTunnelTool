using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CfTunnelTool.Core;

/// <summary>
/// 把本程式複製到 C:\Program Files\CfTunnelTool\，並建立開始功能表與桌面捷徑，
/// 讓使用者之後可以隨時開啟管理畫面（就算原本的下載檔被刪掉也沒關係）。
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

    /// <summary>單一執行檔發佈時 Assembly.Location 會是空字串（這正是我們要判斷的）。</summary>
#pragma warning disable IL3000
    static bool IsSingleFile => string.IsNullOrEmpty(typeof(SelfInstaller).Assembly.Location);
#pragma warning restore IL3000

    static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

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

        CreateShortcut(StartMenuShortcut, InstalledExe);
        CreateShortcut(DesktopShortcut, InstalledExe);
        log.Ok("已建立開始功能表與桌面捷徑「CfTunnelTool」");
    }

    public static void Uninstall(ILog log)
    {
        foreach (var lnk in new[] { StartMenuShortcut, DesktopShortcut })
            if (File.Exists(lnk)) File.Delete(lnk);

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

    static void CreateShortcut(string lnkPath, string target)
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
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, [Path.GetDirectoryName(target)!]);
                t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, ["CfTunnelTool 管理畫面"]);
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            }
            finally { Marshal.FinalReleaseComObject(lnk); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }
}
