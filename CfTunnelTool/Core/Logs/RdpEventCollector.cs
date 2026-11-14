using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;

namespace CfTunnelTool.Core.Logs;

/// <summary>
/// 收集「這台電腦」的遠端桌面事件，並組成工作階段：
///   TerminalServices-LocalSessionManager/Operational：21 登入、23 登出、24 中斷、25 重新連線
///   Security：4625 登入失敗（登入類型 10 或來自網路）
/// 只讀取，不修改任何事件記錄。需要系統管理員權限（讀取 Security）。
/// </summary>
static class RdpEventCollector
{
    const string LsmLog = "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational";
    const string SecurityLog = "Security";
    const long FirstRunWindowMs = 30L * 24 * 60 * 60 * 1000; // 第一次收集最多往回 30 天

    public const int Logon = 21, Logoff = 23, Disconnect = 24, Reconnect = 25, Failed = 4625;

    static string? Field(XElement root, string name) =>
        root.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    static string? DataField(XElement root, string name) =>
        root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Data" && (string?)e.Attribute("Name") == name)?.Value;

    static List<RdpEventRecord> Read(string log, string eventFilter, long afterId, Func<EventRecord, XElement, RdpEventRecord?> map)
    {
        var condition = afterId > 0
            ? $"({eventFilter}) and EventRecordID > {afterId}"
            : $"({eventFilter}) and TimeCreated[timediff(@SystemTime) <= {FirstRunWindowMs}]";
        var query = new EventLogQuery(log, PathType.LogName, $"*[System[{condition}]]");
        var list = new List<RdpEventRecord>();
        try
        {
            using var reader = new EventLogReader(query);
            for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
            {
                using (e)
                {
                    var xml = XElement.Parse(e.ToXml());
                    if (map(e, xml) is { } r) list.Add(r);
                }
            }
        }
        catch (Exception ex) when (ex is EventLogNotFoundException or EventLogException or UnauthorizedAccessException) { }
        return list;
    }

    static RdpEventRecord? MapSession(EventRecord e, XElement xml)
    {
        var address = Field(xml, "Address") ?? "";
        if (address.Equals("LOCAL", StringComparison.OrdinalIgnoreCase)) return null; // 電腦前面的本機登入
        _ = int.TryParse(Field(xml, "SessionID"), out var sessionId);
        return new RdpEventRecord(
            e.TimeCreated ?? DateTime.Now,
            e.Id,
            Environment.MachineName,
            Field(xml, "User") ?? "",
            sessionId,
            address,
            e.RecordId ?? 0);
    }

    static RdpEventRecord? MapFailure(EventRecord e, XElement xml)
    {
        var logonType = DataField(xml, "LogonType");
        var ip = DataField(xml, "IpAddress") ?? "";
        if (logonType is not ("10" or "3") || ip is "" or "-") return null;
        var domain = DataField(xml, "TargetDomainName");
        var user = DataField(xml, "TargetUserName") ?? "";
        return new RdpEventRecord(
            e.TimeCreated ?? DateTime.Now,
            Failed,
            Environment.MachineName,
            string.IsNullOrEmpty(domain) || domain == "-" ? user : $"{domain}\\{user}",
            0,
            ip,
            e.RecordId ?? 0);
    }

    /// <summary>收集新的事件並存檔，回傳新增筆數。</summary>
    public static int Collect(LogState state)
    {
        var total = 0;
        foreach (var (key, log, filter, map) in new (string, string, string, Func<EventRecord, XElement, RdpEventRecord?>)[]
                 {
                     ("lsm", LsmLog, "EventID=21 or EventID=23 or EventID=24 or EventID=25", MapSession),
                     ("security", SecurityLog, "EventID=4625", MapFailure),
                 })
        {
            var after = state.LastRdpRecordIds.GetValueOrDefault(key);
            var records = Read(log, filter, after, map);
            if (records.Count > 0)
            {
                LogStore.Append(LogKinds.RdpEvents, records, r => r.Time);
                state.LastRdpRecordIds[key] = Math.Max(after, records.Max(r => r.RecordId));
                total += records.Count;
            }
        }
        return total;
    }

    /// <summary>
    /// 把事件組成工作階段（純邏輯，可單獨測試）：
    /// 登入或重新連線＝開始；中斷或登出＝結束；沒有結束的是「使用中」；4625 為「登入失敗」。
    /// </summary>
    public static List<RdpSessionView> BuildSessions(IEnumerable<RdpEventRecord> events)
    {
        var result = new List<RdpSessionView>();
        var ordered = events.OrderBy(e => e.Time).ThenBy(e => e.RecordId).ToList();

        foreach (var f in ordered.Where(e => e.EventId == Failed))
            result.Add(new RdpSessionView(f.Time, null, f.Machine, f.User, 0, "登入失敗", f.Source));

        foreach (var group in ordered.Where(e => e.EventId != Failed).GroupBy(e => (e.Machine, e.SessionId)))
        {
            RdpEventRecord? open = null;
            foreach (var e in group)
            {
                if (e.EventId is Logon or Reconnect)
                {
                    if (open is not null) // 沒有收到結束事件就又開始：前一段視為已中斷
                        result.Add(new RdpSessionView(open.Time, e.Time, open.Machine, open.User, open.SessionId, "已中斷", open.Source));
                    open = e;
                }
                else if (e.EventId is Disconnect or Logoff && open is not null)
                {
                    result.Add(new RdpSessionView(open.Time, e.Time, open.Machine, open.User, open.SessionId,
                        e.EventId == Logoff ? "已登出" : "已中斷", open.Source));
                    open = null;
                }
            }
            if (open is not null)
                result.Add(new RdpSessionView(open.Time, null, open.Machine, open.User, open.SessionId, "使用中", open.Source));
        }
        return result.OrderByDescending(s => s.Start).ToList();
    }
}
