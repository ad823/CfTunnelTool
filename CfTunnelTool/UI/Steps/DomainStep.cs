using CfTunnelTool.Core;

namespace CfTunnelTool.UI.Steps;

sealed class DomainStep : StepPage
{
    readonly WizardState _state;
    readonly ComboBox _zones;
    readonly TextBox _name;
    readonly Label _nameHint;
    readonly Label _zoneHint;

    public override string SidebarTitle => "網域";
    public override string Heading => "步驟 2／5　選擇網域與名稱";

    public override bool CanGoNext =>
        _zones.SelectedItem is ZoneInfo && ToolConfig.TunnelNamePattern().IsMatch(_name.Text.Trim());

    public DomainStep(WizardState state)
    {
        _state = state;

        AddText("要使用哪一個網域？", Theme.Bold, bottom: 2);
        _zones = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 380,
            Margin = new Padding(0, 4, 0, 2),
        };
        _zones.SelectedIndexChanged += (_, _) => NotifyStateChanged();
        Add(_zones, stretch: false);
        _zoneHint = AddText("下拉選單列出的是 API Token 能管理的網域。", Theme.Small, Theme.Muted, bottom: 16);

        AddText("幫這台電腦取一個 Tunnel 名稱：", Theme.Bold, bottom: 2);
        _name = new TextBox { Width = 380, Margin = new Padding(0, 4, 0, 2) };
        _name.TextChanged += (_, _) => { UpdateNameHint(); NotifyStateChanged(); };
        Add(_name, stretch: false);
        _nameHint = AddText("", Theme.Small, Theme.Muted, bottom: 12);

        AddHelpBox("什麼是 Tunnel？",
            "Tunnel 是這台電腦和 Cloudflare 之間的加密通道。這台電腦會主動連線出去，所以不需要固定 IP、" +
            "也不用在路由器或防火牆開任何連接埠。\n\n" +
            "設定好之後，可以在 Cloudflare 後台 → Zero Trust → Networks → Tunnels 看到它。" +
            "如果有多台電腦要對外，請每台各取一個不同的名稱，例如 office-pc、factory-pc。");
    }

    void UpdateNameHint()
    {
        if (ToolConfig.TunnelNamePattern().IsMatch(_name.Text.Trim()))
        {
            _nameHint.Text = "只能用英文、數字、- 和 _。預設為這台電腦的名稱。";
            _nameHint.ForeColor = Theme.Muted;
        }
        else
        {
            _nameHint.Text = "✗ 名稱只能用英文、數字、- 和 _，且需以英文或數字開頭。";
            _nameHint.ForeColor = Theme.Danger;
        }
    }

    public override void OnEnter()
    {
        _zones.BeginUpdate();
        _zones.Items.Clear();
        foreach (var z in _state.Zones) _zones.Items.Add(z);
        _zones.EndUpdate();

        var selected = _state.Zones.FirstOrDefault(z => z.Name == _state.Domain) ?? _state.Zones.FirstOrDefault();
        if (selected is not null) _zones.SelectedItem = selected;

        _name.Text = _state.TunnelName;
        UpdateNameHint();

        // 修改既有設定時不允許改網域和名稱，避免留下孤兒 Tunnel / DNS 紀錄
        _zones.Enabled = _name.Enabled = !_state.IsEditing;
        if (_state.IsEditing)
        {
            _zoneHint.Text = "已部署的設定無法更換網域或 Tunnel 名稱。若要更換，請先在管理畫面「移除全部設定」再重新設定。";
            _zoneHint.ForeColor = Theme.Warning;
            if (_state.Zones.All(z => z.Name != _state.Domain))
            {
                _zoneHint.Text = $"✗ 這組 Token 無法管理目前使用的網域 {_state.Domain}，請回上一步換一組 Token。";
                _zoneHint.ForeColor = Theme.Danger;
                _zones.SelectedItem = null;
            }
        }
        NotifyStateChanged();
    }

    public override bool OnNext()
    {
        var zone = (ZoneInfo)_zones.SelectedItem!;
        if (zone.Status != "active" &&
            MessageBox.Show(FindForm(),
                $"網域 {zone.Name} 在 Cloudflare 上的狀態是「{zone.Status}」，尚未完全啟用，網址可能暫時無法使用。\n\n仍要繼續嗎？",
                "網域尚未啟用", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return false;

        if (_state.Domain != zone.Name)
        {
            // 換了網域：把舊的網址前綴帶到新網域
            foreach (var r in _state.Routes)
                if (_state.Domain.Length > 0 && r.Hostname.EndsWith(_state.Domain))
                    r.Hostname = r.Hostname[..^_state.Domain.Length] + zone.Name;
        }
        _state.Domain = zone.Name;
        _state.TunnelName = _name.Text.Trim();
        return true;
    }
}
