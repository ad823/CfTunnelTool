using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace CfTunnelTool.Core;

/// <summary>本機 Windows 帳號。Source：Local、MicrosoftAccount、ActiveDirectory、AzureAD。</summary>
record LocalUser(string Name, string Source)
{
    public bool IsLocal => Source == "Local";

    public override string ToString() => Source switch
    {
        "Local" => Name,
        "MicrosoftAccount" => $"{Name}（Microsoft 帳號）",
        _ => $"{Name}（{Source}）",
    };
}

/// <summary>目前的開機自動登入設定。</summary>
record AutoLogonStatus(bool Enabled, string? UserName, string? Domain);

/// <summary>
/// 設定本機帳號的密碼、開機自動登入與遠端桌面權限。
/// 自動登入的密碼存在 LSA 機密（與微軟 Sysinternals Autologon 相同做法），不會以明文寫進登錄檔。
/// 所有寫入動作都需要系統管理員權限，且會阻塞，請在背景執行緒呼叫。
/// </summary>
static class LocalAccount
{
    const string WinlogonKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    const string RemoteDesktopUsersSid = "S-1-5-32-555";

    // ---------- 讀取 ----------

    /// <summary>列出這台電腦已啟用的本機帳號（透過 Get-LocalUser，可分辨是否為 Microsoft 帳號）。</summary>
    public static List<LocalUser> ListUsers()
    {
        var r = Cloudflared.Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
            "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
            "@(Get-LocalUser | Where-Object Enabled | Select-Object Name,@{n='Source';e={\"$($_.PrincipalSource)\"}}) | ConvertTo-Json -Compress");
        if (r.ExitCode != 0 || string.IsNullOrWhiteSpace(r.Output)) return [];

        var json = r.Output.Trim();
        if (json.StartsWith('{')) json = $"[{json}]"; // 只有一個帳號時不是陣列
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray()
                .Select(e => new LocalUser(
                    e.GetProperty("Name").GetString() ?? "",
                    e.TryGetProperty("Source", out var s) ? s.GetString() ?? "Local" : "Local"))
                .Where(u => u.Name.Length > 0)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>啟動本程式的 Windows 帳號名稱（不含電腦名稱）。</summary>
    public static string CurrentUserName()
    {
        var name = WindowsIdentity.GetCurrent().Name;
        var i = name.IndexOf('\\');
        return i >= 0 ? name[(i + 1)..] : name;
    }

    public static AutoLogonStatus GetAutoLogon()
    {
        using var key = Registry.LocalMachine.OpenSubKey(WinlogonKey);
        var enabled = key?.GetValue("AutoAdminLogon") as string == "1";
        return new AutoLogonStatus(enabled, key?.GetValue("DefaultUserName") as string, key?.GetValue("DefaultDomainName") as string);
    }

    /// <summary>遠端桌面登入時要輸入的帳號格式，例如 OFFICE-PC\evan。</summary>
    public static string RemoteLoginName(string userName) => $"{Environment.MachineName}\\{userName}";

    // ---------- 寫入 ----------

    /// <summary>
    /// 幫本機帳號設定密碼、加入「Remote Desktop Users」群組，並視需要設定開機自動登入。
    /// </summary>
    public static void SetRemotePassword(string userName, string password, bool keepAutoLogon)
    {
        SetPassword(userName, password);
        AddToRemoteDesktopUsers(userName);
        if (keepAutoLogon) EnableAutoLogon(userName, password);
    }

    static void SetPassword(string userName, string password)
    {
        var info = new USER_INFO_1003 { usri1003_password = password };
        var rc = NetUserSetInfo(null, userName, 1003, ref info, out _);
        if (rc != 0)
            throw new InvalidOperationException(rc switch
            {
                2221 => $"找不到帳號「{userName}」。",
                2245 => "密碼不符合這台電腦的密碼原則（太短、太簡單或與最近使用過的密碼相同），請換一組更長、更複雜的密碼。",
                5 => "沒有權限修改密碼，請以系統管理員身分執行。",
                _ => $"設定密碼失敗（錯誤碼 {rc}）。",
            });
    }

    static void AddToRemoteDesktopUsers(string userName)
    {
        // 群組名稱會依系統語言不同（例如「Remote Desktop Users」），用固定的 SID 查出實際名稱
        var group = new SecurityIdentifier(RemoteDesktopUsersSid).Translate(typeof(NTAccount)).Value;
        var groupName = group[(group.IndexOf('\\') + 1)..];
        var member = new LOCALGROUP_MEMBERS_INFO_3 { lgrmi3_domainandname = $"{Environment.MachineName}\\{userName}" };
        var rc = NetLocalGroupAddMembers(null, groupName, 3, ref member, 1);
        const int ErrorMemberInAlias = 1378; // 已經是成員
        if (rc != 0 && rc != ErrorMemberInAlias)
            throw new InvalidOperationException($"密碼已設定，但加入「{groupName}」群組失敗（錯誤碼 {rc}）。");
    }

    static void EnableAutoLogon(string userName, string password)
    {
        using (var key = Registry.LocalMachine.OpenSubKey(WinlogonKey, writable: true)
                         ?? throw new InvalidOperationException("找不到 Winlogon 設定。"))
        {
            key.SetValue("AutoAdminLogon", "1", RegistryValueKind.String);
            key.SetValue("DefaultUserName", userName, RegistryValueKind.String);
            key.SetValue("DefaultDomainName", Environment.MachineName, RegistryValueKind.String);
            key.DeleteValue("DefaultPassword", throwOnMissingValue: false); // 不留明文密碼
            key.DeleteValue("AutoLogonCount", throwOnMissingValue: false);  // 避免只自動登入幾次就失效
        }
        StoreLsaSecret("DefaultPassword", password);
    }

    static void StoreLsaSecret(string keyName, string value)
    {
        var attrs = new LSA_OBJECT_ATTRIBUTES { Length = Marshal.SizeOf<LSA_OBJECT_ATTRIBUTES>() };
        const uint PolicyCreateSecret = 0x00000020;
        var status = LsaOpenPolicy(IntPtr.Zero, ref attrs, PolicyCreateSecret, out var policy);
        if (status != 0)
            throw new InvalidOperationException($"密碼已設定，但開啟自動登入失敗（LsaOpenPolicy {LsaNtStatusToWinError(status)}）。");
        try
        {
            var key = ToLsaString(keyName);
            var data = ToLsaString(value);
            try
            {
                status = LsaStorePrivateData(policy, ref key, ref data);
                if (status != 0)
                    throw new InvalidOperationException($"密碼已設定，但開啟自動登入失敗（LsaStorePrivateData {LsaNtStatusToWinError(status)}）。");
            }
            finally
            {
                Marshal.FreeHGlobal(key.Buffer);
                Marshal.ZeroFreeGlobalAllocUnicode(data.Buffer);
            }
        }
        finally
        {
            LsaClose(policy);
        }
    }

    static LSA_UNICODE_STRING ToLsaString(string s) => new()
    {
        Buffer = Marshal.StringToHGlobalUni(s),
        Length = (ushort)(s.Length * 2),
        MaximumLength = (ushort)(s.Length * 2 + 2),
    };

    // ---------- Win32 ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct USER_INFO_1003
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string usri1003_password;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct LOCALGROUP_MEMBERS_INFO_3
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string lgrmi3_domainandname;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LSA_OBJECT_ATTRIBUTES
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LSA_UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetUserSetInfo(string? servername, string username, int level, ref USER_INFO_1003 buf, out int parmErr);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetLocalGroupAddMembers(string? servername, string groupname, int level, ref LOCALGROUP_MEMBERS_INFO_3 buf, int totalentries);

    [DllImport("advapi32.dll")]
    static extern uint LsaOpenPolicy(IntPtr systemName, ref LSA_OBJECT_ATTRIBUTES objectAttributes, uint desiredAccess, out IntPtr policyHandle);

    [DllImport("advapi32.dll")]
    static extern uint LsaStorePrivateData(IntPtr policyHandle, ref LSA_UNICODE_STRING keyName, ref LSA_UNICODE_STRING privateData);

    [DllImport("advapi32.dll")]
    static extern uint LsaClose(IntPtr policyHandle);

    [DllImport("advapi32.dll")]
    static extern int LsaNtStatusToWinError(uint status);
}
