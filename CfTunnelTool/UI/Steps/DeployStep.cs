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
    public override string Heading => "步驟 4／4　開始部署";
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
    string Signature() => JsonSerializer.Serialize(new { _state.Domain, _state.TunnelName, _state.Routes });

    public override void OnEnter()
    {
        var routes = string.Join("\n", _state.Routes.Select(r => $"      https://{r.Hostname}  →  {r.Service}"));
        _summary.Text =
            $"網域：{_state.Domain}\n" +
            $"Tunnel 名稱：{_state.TunnelName}\n" +
            $"對外網址：\n{routes}";

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
        try
        {
            var config = _state.ToConfig();
            using var cf = new CloudflareClient(_state.ApiToken);
            var result = await new Deployer(cf, _log, Confirm).DeployAsync(config);

            _log.Step("儲存設定");
            config.TunnelId = result.TunnelId;
            _state.TunnelId = result.TunnelId;
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
            _deployedSignature = Signature();
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
            _running = false;
            _progress.Visible = false;
            _start.Enabled = true;
            _start.Text = ok ? "重新部署" : "重新部署";
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
        _log.Step("安裝連線服務 cloudflared");
        _log.Info("下載中… 50%（18 MB）");
    }
#endif
}
