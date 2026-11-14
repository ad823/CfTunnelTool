using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace CfTunnelTool.Core.Logs;

/// <summary>
/// 本機紀錄儲存：C:\ProgramData\CfTunnelTool\logs\&lt;種類&gt;\yyyy-MM.jsonl，一行一筆 JSON。
/// 資料夾權限只開放 Administrators 與 SYSTEM（紀錄含 Email 與 IP）。
/// </summary>
static class LogStore
{
    /// <summary>紀錄根目錄；自我測試時可改到暫存資料夾。</summary>
    public static string Root
    {
        get => _root;
        set { _root = value; _secured = false; }
    }
    static string _root = Path.Combine(AppStorage.Dir, "logs");

    public const int DefaultRetentionDays = 365;

    static readonly object Gate = new();
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    static bool _secured;

    static string FileFor(string kind, DateTime time) =>
        Path.Combine(Root, kind, $"{time:yyyy-MM}.jsonl");

    public static void Append<T>(string kind, IEnumerable<T> items, Func<T, DateTime> timeOf)
    {
        lock (Gate)
        {
            EnsureRoot();
            foreach (var group in items.GroupBy(i => FileFor(kind, timeOf(i))))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(group.Key)!);
                var sb = new StringBuilder();
                foreach (var item in group) sb.AppendLine(JsonSerializer.Serialize(item, Json));
                File.AppendAllText(group.Key, sb.ToString(), new UTF8Encoding(false));
            }
        }
    }

    /// <summary>讀出時間介於 from～to 的紀錄（依時間排序）。</summary>
    public static List<T> Query<T>(string kind, DateTime from, DateTime to, Func<T, DateTime> timeOf)
    {
        var result = new List<T>();
        var dir = Path.Combine(Root, kind);
        if (!Directory.Exists(dir)) return result;

        var firstMonth = new DateTime(from.Year, from.Month, 1);
        lock (Gate)
        {
            foreach (var file in Directory.GetFiles(dir, "*.jsonl").Order())
            {
                if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM", null,
                        System.Globalization.DateTimeStyles.None, out var month)) continue;
                if (month < firstMonth || month > to) continue;
                foreach (var line in File.ReadLines(file))
                {
                    if (line.Length == 0) continue;
                    try
                    {
                        if (JsonSerializer.Deserialize<T>(line, Json) is { } item)
                        {
                            var t = timeOf(item);
                            if (t >= from && t <= to) result.Add(item);
                        }
                    }
                    catch (JsonException) { /* 損毀的行略過 */ }
                }
            }
        }
        return result.OrderBy(timeOf).ToList();
    }

    /// <summary>刪除超過保留天數的月份檔案（整個月份都超過才刪）。</summary>
    public static int Cleanup(int keepDays, DateTime now)
    {
        var removed = 0;
        if (!Directory.Exists(Root)) return 0;
        var cutoff = now.AddDays(-keepDays);
        lock (Gate)
        {
            foreach (var file in Directory.GetFiles(Root, "*.jsonl", SearchOption.AllDirectories))
            {
                if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM", null,
                        System.Globalization.DateTimeStyles.None, out var month)) continue;
                if (month.AddMonths(1) <= cutoff)
                {
                    File.Delete(file);
                    removed++;
                }
            }
        }
        return removed;
    }

    static void EnsureRoot()
    {
        Directory.CreateDirectory(Root);
        if (_secured) return;
        _secured = true;
        // 只有系統管理員身分才鎖定權限；否則鎖定後自己也寫不進去（正式版一律以系統管理員執行）
        if (!Cloudflared.IsAdministrator()) return;
        try { SecureDirectory(Root); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException) { }
    }

    /// <summary>資料夾只開放 Administrators 與 SYSTEM（停用繼承）。</summary>
    public static void SecureDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        info.SetAccessControl(security);
    }

    // ---------- CSV 匯出 ----------

    /// <summary>產生 CSV（逗號、引號、換行會正確跳脫）。寫檔時請用含 BOM 的 UTF-8，Excel 開啟才不會亂碼。</summary>
    public static string ToCsv(IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
            sb.Append(string.Join(',', row.Select(Escape))).Append("\r\n");
        return sb.ToString();

        static string Escape(string? v)
        {
            v ??= "";
            return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }
    }

    public static void WriteCsv(string path, IEnumerable<string[]> rows) =>
        File.WriteAllText(path, ToCsv(rows), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
}
