using System.Text.Json;
using CfTunnelTool.Core;

namespace CfTunnelTool.UI.Steps;

sealed class DeployStep : StepPage
{
    readonly WizardState _state;
    readonly Label _summary;
    readonly Button _start;
    readonly ProgressBar _progress;
    readonly LogBox _log;
    bool _running;
    string? _deployedSignature;

    public override string SidebarTitle => "部署";
    public override string Heading => "步驟 5／5　開始部署";
    public override bool CanGoNext => _state.Result is not null && !_running && IsCurrentDeployed;
    public override bool CanGoBack => !_running;
    public override bool IsBusy => _running;

    bool IsCurrentDeployed => _deployedSignature == Signature();

    public DeployStep(WizardState state)
    {
        _state = state;

        AddText("請確認以下設定，然後按「開始部署」。整個過程大約 1 分鐘，所有步驟都會自動完成。");
        _summary = AddHelpBox("即將套用的設定", "", Theme.Surface, Theme.Text);

        _start = Theme.PrimaryButton("開始部署");
        _start.MinimumSize = new Size(140, 40);
        _start.Click += async (_, _) => await DeployAsync();
        _progress = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 25,
            Width = 260,
            Height = 10,
            Visible = false,
            Margin = new Padding(8, 16, 0, 0),
        };
        AddRow(_start, _progress);

        _log = new LogBox { MinimumSize = new Size(0, 140), Margin = new Padding(0, 8, 0, 0) };
        Add(_log, fill: true);
    }

    /// <summary>目前設定的指紋；回上一步改了設定後就需要重新部署。</summary>
    string Signature()
    {
        var rdp = _state.Rdp;
        return JsonSerializer.Serialize(new
        {
            _state.Domain,
            _state.TunnelName,
            _state.Routes,
            Rdp = rdp is null ? null : new
            {
                rdp.Enabled, rdp.BrowserEnabled, rdp.WarpEnabled, rdp.UseOwnVnet, rdp.Hostname, rdp.AllowedEmails, rdp.MfaMethods, rdp.MfaSessionDuration,
                rdp.LoginSessionDuration, rdp.ClipboardText, rdp.FileUpload, rdp.FileDownload,
                Machines = rdp.Machines.Select(m => new { m.Name, m.Ip, m.Port }),
            },
        });
    }

    public override void OnEnter()
    {
        var lines = new List<string>
        {
            $"網域：{_state.Domain}",
            $"Tunnel 名稱：{_state.TunnelName}",
        };
        if (_state.Routes.Count > 0)
        {
            lines.Add("網站／API／TCP：");
            lines.AddRange(_state.Routes.Select(r => $"      {r.PublicUrl}  →  {r.Service}" + (r.IsTcp ? "（沒有登入保護）" : r.DisableBrowserCheck ? "（API：關閉瀏覽器檢查）" : "")));
        }
        if (_state.Rdp is { Enabled: true } rdp)
        {
            var modes = string.Join("＋", new[] { rdp.WarpEnabled ? "WARP" : null, rdp.BrowserEnabled ? $"瀏覽器 https://{rdp.Hostname}" : null }.Where(x => x is not null));
            lines.Add($"遠端桌面：{modes}" + (rdp.UseOwnVnet ? $"（獨立虛擬網路 cft-{_state.TunnelName}）" : ""));
            lines.AddRange(rdp.Machines.Select(m => $"      {m.Name}  →  {m.Ip}:{m.Port}"));
            lines.Add($"允許登入：{string.Join("、", rdp.AllowedEmails)}");
            lines.Add($"2FA：{string.Join("、", rdp.MfaMethods.Select(RdpDeployer.MfaMethodName))}（{RdpDeployer.SessionName(rdp.MfaSessionDuration)}）");
            lines.Add($"Email 驗證碼：{RdpDeployer.LoginName(rdp.LoginSessionDuration)}輸入一次（整個 Cloudflare 帳號共用）");
        }
        else if (_state.Rdp is { Enabled: false, AppId: not null })
        {
            lines.Add("遠端桌面：停用（部署時會移除先前的遠端桌面設定）");
        }
        _summary.Text = string.Join("\n", lines);

        if (_state.Result is not null && !IsCurrentDeployed)
            _start.Text = "重新部署（設定已變更）";
        NotifyStateChanged();
    }

    async Task DeployAsync()
    {
        _running = true;
        _start.Enabled = false;
        _progress.Visible = true;
        _log.Clear();
        NotifyStateChanged();

        var owner = FindForm();
        bool Confirm(string q) =>
            MessageBox.Show(owner, q, "請確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

        var ok = false;
        var config = _state.ToConfig();
        try
        {
            using var cf = new CloudflareClient(_state.ApiToken);
            var result = await new Deployer(cf, _log, Confirm).DeployAsync(config);

            _log.Step("儲存設定");
            config.TunnelId = result.TunnelId;
            AppStorage.SaveConfig(config);
            if (_state.RememberToken) AppStorage.SaveToken(_state.ApiToken);
            else AppStorage.DeleteToken();
            _log.Ok($"設定已儲存到 {AppStorage.Dir}");

            _log.Step("安裝管理程式");
            try { SelfInstaller.Install(_log); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                           or System.Runtime.InteropServices.COMException)
            {
                _log.Warn($"建立捷徑失敗（不影響連線）：{ex.Message}");
            }

            _state.Result = result;
            ok = true;
            _log.Info("");
            _log.Ok("全部完成！請按右下角「下一步」查看結果。");
        }
        catch (OperationCanceledException ex)
        {
            _log.Warn(ex.Message);
        }
        catch (Exception ex) when (ex is CloudflareApiException or InvalidOperationException or HttpRequestException
                                       or IOException or UnauthorizedAccessException or TaskCanceledException
                                       or System.ComponentModel.Win32Exception)
        {
            _log.Error(ex.Message);
            _log.Info("");
            _log.Info("修正問題後按「重新部署」即可，已完成的部分不會重複建立。");
        }
        finally
        {
            // 就算中途失敗，也把已建立資源的 ID 帶回來，重新部署時才會更新而不是重複建立
            _state.TunnelId = config.TunnelId ?? _state.TunnelId;
            _state.Rdp = config.Rdp?.Clone();
            if (ok) _deployedSignature = Signature();

            _running = false;
            _progress.Visible = false;
            _start.Enabled = true;
            _start.Text = "重新部署";
            NotifyStateChanged();
        }
    }

#if DEBUG
    internal void DemoLog()
    {
        _log.Step("查詢網域 yourdomain.com");
        _log.Ok("Zone ID 0123456789abcdef");
        _log.Step("檢查本機服務");
        _log.Ok("http://localhost:8080 有回應（HTTP 200）");
        _log.Warn("http://localhost:5000 沒有回應（Tunnel 仍會建立，服務啟動後即可連線）");
        _log.Step("準備 Tunnel「office-pc」");
        _log.Ok("已建立 Tunnel 6f1c2a…");
        _log.Step("設定 DNS 紀錄");
        _log.Ok("www.yourdomain.com：已新增 DNS 紀錄");
        _log.Error("api.yourdomain.com：已有其他 DNS 紀錄（A 1.2.3.4）。為了安全不會自動覆寫。");
        _log.Step("檢查 Zero Trust 設定（登入方式與 2FA）");
        _log.Ok("Zero Trust 組織：yourteam.cloudflareaccess.com（Email 驗證碼登入、2FA 已啟用）");
        _log.Step("設定私有網路路由");
        _log.Ok("office-pc：已新增路由 192.168.10.20/32");
        _log.Ok("accounting-pc：已新增路由 192.168.10.31/32");
        _log.Step("設定登入政策與 2FA");
        _log.Ok("2FA：Authenticator App、安全金鑰、生物辨識，每 8 小時重新驗證");
        _log.Step("安裝連線服務 cloudflared");
        _log.Info("下載中… 50%（18 MB）");
    }
#endif
}
