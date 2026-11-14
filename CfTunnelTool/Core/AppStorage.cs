using System.Security.Cryptography;
using System.Text;

namespace CfTunnelTool.Core;

/// <summary>
/// 設定與 Token 的儲存位置：C:\ProgramData\CfTunnelTool\
///   config.json — 網域、Tunnel、路由（不含任何密鑰）
///   token.dat   — API Token，以 Windows DPAPI 加密，只有同一個 Windows 使用者能解開
/// </summary>
static class AppStorage
{
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CfTunnelTool");

    static string ConfigPath => Path.Combine(Dir, "config.json");
    static string TokenPath => Path.Combine(Dir, "token.dat");
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CfTunnelTool.ApiToken.v1");

    public static bool HasConfig => File.Exists(ConfigPath);

    public static ToolConfig? LoadConfig() => HasConfig ? ToolConfig.Load(ConfigPath) : null;

    public static void SaveConfig(ToolConfig config) => config.Save(ConfigPath);

    public static void SaveToken(string token)
    {
        Directory.CreateDirectory(Dir);
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(TokenPath, data);
    }

    public static string? LoadToken()
    {
        try
        {
            if (!File.Exists(TokenPath)) return null;
            var data = ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (CryptographicException)
        {
            return null; // 其他使用者存的，或檔案損毀
        }
    }

    public static void DeleteToken()
    {
        if (File.Exists(TokenPath)) File.Delete(TokenPath);
    }

    public static void DeleteAll()
    {
        DeleteToken();
        if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
    }
}
