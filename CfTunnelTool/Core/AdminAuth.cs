using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CfTunnelTool.Core.Logs;

namespace CfTunnelTool.Core;

/// <summary>登入結果。Remaining：還剩幾次機會；LockedFor：還要等多久。</summary>
record AdminLoginResult(bool Ok, int Remaining, TimeSpan LockedFor)
{
    public bool Locked => LockedFor > TimeSpan.Zero;
}

/// <summary>
/// 管理畫面的帳號密碼：C:\ProgramData\CfTunnelTool\admin.dat（只有 Administrators 與 SYSTEM 能存取）。
/// 密碼以 PBKDF2-SHA256（600,000 次）加鹽雜湊，不保存明文；連續失敗 5 次鎖定 5 分鐘（記在檔案中，重開程式也不會解除）。
/// 注意：能執行本程式的人本來就是 Windows 系統管理員，這組密碼是「多一道門」，真正的界線仍是 Windows 帳號；
/// 刪除檔案也無法繞過，因為已部署的電腦重設密碼需要 Cloudflare API Token 驗證（見 AdminGate）。
/// </summary>
sealed class AdminAuth(string path)
{
    public const int MinPasswordLength = 10;
    public const int MaxUserLength = 32;
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);
    const int Iterations = 600_000;
    const int SaltBytes = 16, HashBytes = 32;

    public static AdminAuth Default { get; } = new(Path.Combine(AppStorage.Dir, "admin.dat"));

    public string FilePath => path;

    /// <summary>已設定且檔案可讀（檔案損毀視為未設定，由 AdminGate 依是否已部署決定要不要 Cloudflare 驗證）。</summary>
    public bool Exists => Load() is not null;

    /// <summary>已設定的管理帳號名稱（沒有設定時為 null）。</summary>
    public string? UserName => Load()?.User;

    sealed class Data
    {
        public string User { get; set; } = "";
        public string Salt { get; set; } = "";
        public string Hash { get; set; } = "";
        public int Iterations { get; set; }
        public int FailedCount { get; set; }
        public DateTime? LockUntil { get; set; }
    }

    /// <summary>檢查新帳號密碼，回傳問題說明；沒問題回傳 null。</summary>
    public static string? ValidateNew(string user, string pwd, string confirm) =>
        user.Trim().Length == 0 ? "請輸入帳號。" :
        user.Trim().Length > MaxUserLength ? $"帳號最多 {MaxUserLength} 個字元。" :
        pwd.Length == 0 ? "請輸入密碼。" :
        pwd != confirm ? "兩次輸入的密碼不一樣。" :
        pwd.Length < MinPasswordLength ? $"密碼至少要 {MinPasswordLength} 個字元。" :
        pwd.Equals(user.Trim(), StringComparison.OrdinalIgnoreCase) ? "密碼不能和帳號相同。" :
        null;

    /// <summary>建立或覆寫管理帳號（重設密碼也用這個）。</summary>
    public void Create(string user, string pwd)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        Save(new Data
        {
            User = user.Trim(),
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(Derive(pwd, salt, Iterations)),
            Iterations = Iterations,
        });
    }

    /// <summary>驗證帳號密碼；帳號不分大小寫。失敗會累計次數，達上限即鎖定。</summary>
    public AdminLoginResult Verify(string user, string pwd, DateTime? now = null)
    {
        var at = now ?? DateTime.Now;
        var d = Load() ?? throw new InvalidOperationException("尚未設定管理帳號。");
        if (d.LockUntil is { } until && until > at)
            return new AdminLoginResult(false, 0, until - at);

        var expected = Convert.FromBase64String(d.Hash);
        var actual = Derive(pwd, Convert.FromBase64String(d.Salt), d.Iterations);
        var ok = CryptographicOperations.FixedTimeEquals(expected, actual) &
                 string.Equals(user.Trim(), d.User, StringComparison.OrdinalIgnoreCase);
        if (ok)
        {
            if (d.FailedCount != 0 || d.LockUntil is not null)
            {
                d.FailedCount = 0;
                d.LockUntil = null;
                Save(d);
            }
            return new AdminLoginResult(true, MaxFailures, TimeSpan.Zero);
        }

        d.FailedCount = d.LockUntil is not null ? 1 : d.FailedCount + 1; // 鎖定期滿後重新計算
        d.LockUntil = null;
        if (d.FailedCount >= MaxFailures)
        {
            d.FailedCount = 0;
            d.LockUntil = at + LockDuration;
            Save(d);
            return new AdminLoginResult(false, 0, LockDuration);
        }
        Save(d);
        return new AdminLoginResult(false, MaxFailures - d.FailedCount, TimeSpan.Zero);
    }

    /// <summary>雜湊值（Base64）：給自我測試確認沒有保存明文。</summary>
    internal string? StoredHash => Load()?.Hash;

    static byte[] Derive(string pwd, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pwd), salt, iterations, HashAlgorithmName.SHA256, HashBytes);

    Data? Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    void Save(Data d)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(d));
        if (Cloudflared.IsAdministrator())
        {
            try { SecureFile(path); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException) { }
        }
    }

    /// <summary>檔案只開放 Administrators 與 SYSTEM（停用繼承）。</summary>
    static void SecureFile(string file)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(security);
    }

    /// <summary>寫入程式操作紀錄（登入成功、失敗、鎖定、重設、變更密碼）。</summary>
    public static void Audit(string level, string message) => OperationLog.Write(level, "管理畫面：" + message);
}
