namespace CfTunnelTool.Core;

enum TwoFactorState { Unknown, EveryTime, Valid, Expired }

/// <summary>
/// 估算使用者端 2FA 是否還有效。Cloudflare 沒有提供可查詢的 session 狀態，
/// 所以用「上次確認有效的時間 ＋ 管理者設定的驗證間隔」估算；實際連線前仍會用 TCP 測試確認。
/// </summary>
readonly record struct TwoFactorStatus(TwoFactorState State, DateTime? ExpiresAt, TimeSpan? Interval)
{
    public static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.Length < 2 || !double.TryParse(t[..^1], out var n)) return null;
        return t[^1] switch
        {
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            _ => null,
        };
    }

    public static TwoFactorStatus Compute(string? duration, DateTime? lastVerified, DateTime now)
    {
        var interval = ParseDuration(duration);
        if (interval is { TotalMinutes: 0 }) return new(TwoFactorState.EveryTime, null, interval);
        if (interval is null || lastVerified is null) return new(TwoFactorState.Unknown, null, interval);
        var expires = lastVerified.Value + interval.Value;
        return new(expires > now ? TwoFactorState.Valid : TwoFactorState.Expired, expires, interval);
    }

    public string IntervalText => Interval switch
    {
        null => "依管理者設定",
        { TotalMinutes: 0 } => "每次連線都要驗證",
        { TotalHours: >= 1 } i when i.TotalHours % 1 == 0 => $"每 {i.TotalHours:0} 小時驗證一次",
        var i => $"每 {i.Value.TotalMinutes:0} 分鐘驗證一次",
    };

    public string Describe(DateTime now) => State switch
    {
        TwoFactorState.EveryTime => "每次連線都要通過 2FA",
        TwoFactorState.Valid => $"有效，約 {ExpiresAt:HH:mm} 到期" +
                                (ExpiresAt!.Value.Date != now.Date ? $"（{ExpiresAt:M/d}）" : "") + $"（{IntervalText}）",
        TwoFactorState.Expired => $"可能已過期，下次連線需要重新驗證（{IntervalText}）",
        _ => $"尚未驗證，第一次連線時會要求 2FA（{IntervalText}）",
    };
}
