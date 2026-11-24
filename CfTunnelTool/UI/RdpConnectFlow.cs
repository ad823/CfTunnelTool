using CfTunnelTool.Core;

namespace CfTunnelTool.UI;

/// <summary>
/// 連到一台公司電腦的完整流程，使用者畫面的「連線」與桌面捷徑（--connect）共用：
/// 確認 WARP 已連線 → 切換到該電腦的地點 → 測試連線 →
/// 連不到就自動開啟驗證頁並顯示等候視窗（<see cref="AuthWaitForm"/>）→ 連得到就開啟遠端桌面。
/// 不需要系統管理員權限。
/// </summary>
sealed class RdpConnectFlow(ClientConfig config, ILog log, IWin32Window? owner, Action save)
{
    /// <summary>更新畫面上某台電腦的狀態文字（使用者畫面的清單用）。</summary>
    public Action<ClientMachine, string, Color>? Status { get; init; }

    /// <summary>還沒顯示給使用者看的錯誤訊息（桌面捷徑模式用來跳訊息）；使用者自己取消時為 null。</summary>
    public string? LastError { get; private set; }

    void Fail(string message)
    {
        LastError = message;
        log.Error(message);
    }

    /// <summary>連線；成功開啟遠端桌面時回傳 true。</summary>
    public async Task<bool> ConnectAsync(ClientMachine m)
    {
        LastError = null;
        if (!await EnsureWarpConnectedAsync())
        {
            Fail("WARP 無法連線，請按「開啟 WARP」確認是否已登入組織。");
            return false;
        }

        log.Step($"連線到 {m.Name}（{m.Ip}:{m.Port}）");
        if (!await EnsureSiteAsync(m)) return false;

        Status?.Invoke(m, "測試中…", Theme.Muted);
        if (await RdpHost.TestTcpAsync(m.Ip, m.Port))
        {
            if (!await ConfirmNotInUseAsync(m)) return false;
            Launch(m);
            return true;
        }

        Status?.Invoke(m, "等候驗證…", Theme.Warning);
        log.Info("連不到：可能需要重新驗證 2FA，自動開啟驗證頁…");
        if (AuthWaitForm.Run(owner, m, config.Team, log))
        {
            log.Ok("驗證完成");
            if (!await ConfirmNotInUseAsync(m)) return false;
            Launch(m);
            return true;
        }

        // 使用者已經在等候視窗看到原因（取消或逾時），這裡只寫記錄，不設定 LastError
        Status?.Invoke(m, "✗ 連不到", Theme.Danger);
        log.Error($"仍然連不到 {m.Name}。請確認那台電腦已開機、驗證已完成，或把記錄截圖給管理者。");
        return false;
    }

    /// <summary>
    /// 連線前確認那台電腦有沒有人正在使用；有人在用時詢問，預設「否」。
    /// 那台電腦沒有啟用使用狀態回報（或是舊版）時查不到，照舊直接連線。
    /// </summary>
    const string Nl = "\n";

    async Task<bool> ConfirmNotInUseAsync(ClientMachine m)
    {
        var usage = await SessionStatus.QueryAsync(m.Ip);
        if (usage is null) return true;
        if (SessionStatus.FindOccupant(usage, Environment.MachineName) is not { } who)
        {
            log.Ok($"{m.Name} 目前沒人使用");
            return true;
        }

        Status?.Invoke(m, who.Short, Theme.Warning);
        log.Warn($"{m.Name}：{who.Description}");
        var answer = MessageBox.Show(owner,
            $"{m.Name} 目前有人在使用：{Nl}{Nl}{who.Description}{Nl}{Nl}" +
            $"現在連進去，對方的畫面會被你接手（他的遠端桌面會中斷）。建議先和對方確認。{Nl}{Nl}仍要連線嗎？",
            "這台電腦有人在使用", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Yes) return true;
        log.Info("已取消連線。");
        return false;
    }

    void Launch(ClientMachine m)
    {
        var now = DateTime.Now;
        config.LastVerifiedAt = now; // 連得上代表 2FA 目前有效
        m.LastConnectedAt = now;
        TrySave();
        Status?.Invoke(m, "✓ 已開啟遠端桌面", Theme.Success);
        log.Ok("可以連線，開啟遠端桌面");
        WarpClient.OpenRdp(m);
    }

    /// <summary>一般權限可能無法寫入 C:\ProgramData 的連線清單：寫不進去只略過，不影響連線。</summary>
    void TrySave()
    {
        try { save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>確保 WARP 已連線（最多等 15 秒）。</summary>
    public async Task<bool> EnsureWarpConnectedAsync()
    {
        if (WarpClient.IsConnected(await Task.Run(WarpClient.GetStatus))) return true;
        log.Step("連上 WARP");
        await Task.Run(WarpClient.Connect);
        for (int i = 0; i < 5; i++)
        {
            await Task.Delay(3000);
            if (WarpClient.IsConnected(await Task.Run(WarpClient.GetStatus))) return true;
        }
        return false;
    }

    /// <summary>
    /// 確保 WARP 在這台電腦所在的虛擬網路（地點）。無法自動切換時，引導使用者到 WARP 設定手動選擇。
    /// </summary>
    async Task<bool> EnsureSiteAsync(ClientMachine m)
    {
        if (m.VnetId is null || config.CurrentVnetId == m.VnetId) return true;

        var siteName = m.VnetName is null ? "預設" : $"cft-{m.VnetName}";
        log.Step($"切換到地點「{m.VnetName ?? "預設"}」");
        if (await Task.Run(() => WarpClient.SwitchVirtualNetwork(m.VnetId, log)))
        {
            config.CurrentVnetId = m.VnetId;
            TrySave();
            await Task.Delay(3000); // 切換後 WARP 會重新連線
            await EnsureWarpConnectedAsync();
            return true;
        }

        var answer = MessageBox.Show(owner,
            $"無法自動切換到「{m.Name}」所在的地點。\n\n" +
            $"請開啟 WARP 視窗 → 設定 → 虛擬網路（Virtual networks），選擇「{siteName}」，再按「確定」繼續連線。",
            "切換地點", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        WarpClient.OpenGui();
        if (answer != DialogResult.OK)
        {
            log.Info("已取消切換地點。");
            return false;
        }
        config.CurrentVnetId = m.VnetId;
        TrySave();
        return true;
    }
}
