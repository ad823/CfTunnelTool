using System.Security.Cryptography;
using System.Text;

namespace CfTunnelTool.Core.Logs;

/// <summary>
/// 每小時自動收集紀錄的 Windows 排程（以 SYSTEM 身分執行，不需要使用者登入）。
/// SYSTEM 讀不到用使用者身分加密的 Token，所以排程用的 Token 另外以 DPAPI LocalMachine 加密，
/// 存在只有 Administrators 與 SYSTEM 能存取的 logs 資料夾。
/// </summary>
static class CollectorSchedule
{
    public const string TaskName = @"\CfTunnelTool\CollectLogs";
    static string TokenPath => Path.Combine(LogStore.Root, "collector.dat");
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CfTunnelTool.Collector.v1");

    static string ExePath => File.Exists(SelfInstaller.InstalledExe) ? SelfInstaller.InstalledExe : Environment.ProcessPath!;

    public static bool IsEnabled => Cloudflared.Run("schtasks.exe", "/Query", "/TN", TaskName).ExitCode == 0;

    /// <summary>建立（或覆寫）排程。token 為 null 時只收集本機遠端桌面使用紀錄。</summary>
    public static void Enable(string? token)
    {
        if (token is not null) SaveToken(token);
        else DeleteToken();

        var r = Cloudflared.Run("schtasks.exe", "/Create", "/TN", TaskName,
            "/TR", $"\"{ExePath}\" --collect-logs",
            "/SC", "HOURLY", "/RU", "SYSTEM", "/RL", "HIGHEST", "/F");
        if (r.ExitCode != 0)
            throw new InvalidOperationException("建立排程失敗：\n" + r.Output.Trim());
    }

    public static void Disable()
    {
        Cloudflared.Run("schtasks.exe", "/Delete", "/TN", TaskName, "/F");
        DeleteToken();
    }

    static void SaveToken(string token)
    {
        Directory.CreateDirectory(LogStore.Root);
        try { LogStore.SecureDirectory(LogStore.Root); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException) { }
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(TokenPath, data);
    }

    public static string? LoadToken()
    {
        try
        {
            if (!File.Exists(TokenPath)) return null;
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), Entropy, DataProtectionScope.LocalMachine));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool HasToken => File.Exists(TokenPath);

    static void DeleteToken()
    {
        if (File.Exists(TokenPath)) File.Delete(TokenPath);
    }
}
