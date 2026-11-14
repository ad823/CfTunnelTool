using System.Net;
using System.Net.Sockets;
using CfTunnelTool.Core;

namespace CfTunnelTool.UI;

/// <summary>新增或編輯使用者連線清單中的一台電腦。</summary>
sealed class MachineEditForm : Form
{
    readonly TextBox _name, _ip, _port, _user;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ClientMachine Result { get; private set; }

    public MachineEditForm(ClientMachine? machine)
    {
        Result = machine?.Clone() ?? new ClientMachine { Name = "", Ip = "192.168.", Port = 3389 };

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = machine is null ? "新增電腦" : "編輯電腦";
        Icon = Theme.AppIcon;
        Font = Theme.Base;
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(20, 18, 20, 14) };
        TextBox Field(string label, string value, int width, string? hint = null)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 12, 0) });
            var box = new TextBox { Text = value, Width = width, Margin = new Padding(0, 4, 0, hint is null ? 4 : 0) };
            grid.Controls.Add(box);
            if (hint is not null)
            {
                grid.Controls.Add(new Label());
                grid.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, Margin = new Padding(0, 2, 0, 6) });
            }
            return box;
        }

        _name = Field("名稱：", Result.Name, 260);
        _ip = Field("內網 IP：", Result.Ip, 200, "公司那台電腦的區網 IP，例如 192.168.5.200");
        _port = Field("連接埠：", Result.Port.ToString(), 80);
        _user = Field("預設帳號：", Result.UserName ?? "", 260, "選填，例如 電腦名稱\\帳號；密碼仍然每次輸入");

        grid.Controls.Add(new Label());
        grid.Controls.Add(new Label
        {
            Text = "新增的電腦需要管理者也已經在 CfTunnelTool 開放，否則會連不到。",
            AutoSize = true,
            ForeColor = Theme.Warning,
            Font = Theme.Small,
            Margin = new Padding(0, 6, 0, 10),
        });

        var ok = Theme.PrimaryButton("確定");
        var cancel = Theme.SecondaryButton("取消");
        cancel.DialogResult = DialogResult.Cancel;
        ok.Click += (_, _) => Save();
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Right };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        grid.Controls.Add(new Label());
        grid.Controls.Add(buttons);

        Controls.Add(grid);
        AcceptButton = ok;
        CancelButton = cancel;
        ResumeLayout(false);
        PerformLayout();
    }

    void Save()
    {
        var name = _name.Text.Trim();
        var ip = _ip.Text.Trim();
        string? problem =
            name.Length == 0 ? "請輸入名稱。" :
            !IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork || ip.Count(c => c == '.') != 3
                ? "IP 格式不正確，請輸入像 192.168.5.200 這樣的 IPv4 位址。" :
            !int.TryParse(_port.Text.Trim(), out var port) || port is < 1 or > 65535 ? "連接埠必須是 1～65535 的數字。" :
            null;
        if (problem is not null)
        {
            MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Result.Name = name;
        Result.Ip = ip;
        Result.Port = int.Parse(_port.Text.Trim());
        Result.UserName = string.IsNullOrWhiteSpace(_user.Text) ? null : _user.Text.Trim();
        DialogResult = DialogResult.OK;
        Close();
    }
}
