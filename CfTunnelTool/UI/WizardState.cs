using System.Text.RegularExpressions;
using CfTunnelTool.Core;

namespace CfTunnelTool.UI;

/// <summary>精靈各步驟之間共用的資料。</summary>
sealed class WizardState
{
    public string ApiToken { get; set; } = "";
    public bool RememberToken { get; set; } = true;
    public bool TokenVerified { get; set; }
    public List<ZoneInfo> Zones { get; set; } = [];

    public string Domain { get; set; } = "";
    public string TunnelName { get; set; } = DefaultTunnelName();

    /// <summary>修改既有設定時才有值。</summary>
    public string? TunnelId { get; set; }
    public List<RouteConfig> Routes { get; set; } = [];
    public string? CloudflaredPath { get; set; }

    /// <summary>部署成功後才有值。</summary>
    public DeployResult? Result { get; set; }

    public bool IsEditing => TunnelId is not null;

    public static WizardState From(ToolConfig? config, string? storedToken)
    {
        var s = new WizardState { ApiToken = storedToken ?? "" };
        if (config is not null)
        {
            s.Domain = config.Domain;
            s.TunnelName = config.TunnelName;
            s.TunnelId = config.TunnelId;
            s.Routes = config.Routes.Select(r => r.Clone()).ToList();
            s.CloudflaredPath = config.CloudflaredPath;
        }
        return s;
    }

    public ToolConfig ToConfig() => new()
    {
        Domain = Domain,
        TunnelName = TunnelName,
        TunnelId = TunnelId,
        Routes = Routes.Select(r => r.Clone()).ToList(),
        CloudflaredPath = CloudflaredPath,
    };

    static string DefaultTunnelName()
    {
        var name = Regex.Replace(Environment.MachineName.ToLowerInvariant(), "[^a-z0-9_-]", "-").Trim('-');
        return name.Length == 0 ? "my-pc" : name.Length > 60 ? name[..60] : name;
    }
}
