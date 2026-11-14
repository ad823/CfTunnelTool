using CfTunnelTool.Core;

namespace CfTunnelTool.UI.Steps;

sealed class WelcomeStep : StepPage
{
    public override string SidebarTitle => "歡迎";
    public override string Heading => "歡迎使用 CfTunnelTool";
    public override string NextText => "開始設定  >";

    public WelcomeStep(WizardState state)
    {
        if (state.IsEditing)
            AddHelpBox("修改既有設定",
                $"這台電腦已經設定過（Tunnel：{state.TunnelName}，網域：{state.Domain}）。\n" +
                "接下來的步驟會帶出目前的設定，修改後重新部署即可，不會影響其他電腦。");

        AddText("這個精靈會一步一步帶你完成設定，讓任何地方的人都能用你的網址" +
                "（例如 https://api.yourdomain.com）連到這台電腦上的網站和 API。");

        Add(new DiagramPanel());

        AddText("✓  不需要固定 IP，也不用設定路由器或開防火牆\n" +
                "✓  自動提供 HTTPS 憑證，不用自己申請\n" +
                "✓  安裝成 Windows 服務，開機自動連線，關閉本程式也不影響",
                color: Theme.Success, top: 0, bottom: 10);

        AddHelpBox("開始前請準備",
            "1.  一個已經加入 Cloudflare 的網域（DNS 由 Cloudflare 管理）\n" +
            "2.  能登入 Cloudflare 後台的帳號\n" +
            "3.  這台電腦上已經在執行的網站或 API，例如 http://localhost:5000（還沒好也可以先設定）\n" +
            "4.  大約 5 分鐘");

        if (Cloudflared.IsAdministrator())
            AddText("✓ 已以系統管理員身分執行", Theme.Small, Theme.Success, top: 6);
        else
            AddText("! 目前不是系統管理員身分，部署時可能無法安裝服務。請關閉後按右鍵「以系統管理員身分執行」。",
                Theme.Small, Theme.Warning, top: 6);
    }
}
