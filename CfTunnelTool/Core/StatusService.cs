using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text;

namespace CfTunnelTool.Core;

/// <summary>
/// 狀態回報的 TCP 伺服器：每個連線送出一次目前的使用狀態（JSON）就關閉，不讀取任何輸入。
/// </summary>
sealed class StatusServer : IDisposable
{
    readonly TcpListener _listener;
    readonly CancellationTokenSource _cts = new();
    readonly Func<MachineUsage> _snapshot;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public StatusServer(int port, IPAddress? bind = null, Func<MachineUsage>? snapshot = null)
    {
        _snapshot = snapshot ?? SessionStatus.QueryLocal;
        _listener = new TcpListener(bind ?? IPAddress.IPv6Any, port);
        if (bind is null) _listener.Server.DualMode = true; // IPv4 與 IPv6 都接受
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = RespondAsync(client);
        }
    }

    async Task RespondAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(SessionStatus.ToJson(_snapshot()));
                using var timeout = new CancellationTokenSource(3000);
                await client.GetStream().WriteAsync(bytes, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}

/// <summary>
/// 「使用狀態回報」Windows 服務：在被連線的電腦上執行（CfTunnelTool.exe --status-service），
/// 讓使用者連線前知道這台電腦有沒有人正在使用。防火牆只允許內部網路連入。
/// </summary>
sealed class StatusService : ServiceBase
{
    public const string Name = "CfTunnelToolStatus";
    public const string DisplayNameText = "CfTunnelTool 使用狀態回報";
    public const string Argument = "--status-service";
    const string FirewallRuleName = "CfTunnelTool-Status";

    StatusServer? _server;

    public StatusService() => ServiceName = Name;

    protected override void OnStart(string[] args) => _server = StartOnFirstFreePort();

    /// <summary>依序嘗試候選連接埠，用第一個綁得到的（被系統保留的會被拒絕）。</summary>
    internal static StatusServer StartOnFirstFreePort(IReadOnlyList<int>? ports = null)
    {
        SocketException? last = null;
        foreach (var port in ports ?? SessionStatus.Ports)
        {
            try { return new StatusServer(port); }
            catch (SocketException ex) { last = ex; }
        }
        throw new InvalidOperationException(
            $"連接埠 {string.Join("、", ports ?? SessionStatus.Ports)} 都無法使用（可能被系統保留或其他程式占用）。", last);
    }

    /// <summary>這個連接埠現在能不能綁定（沒有被系統保留、也沒有其他程式在用）。</summary>
    static bool CanBind(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.IPv6Any, port);
            l.Server.DualMode = true;
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// 用 netsh 手動保留第一個綁得到的候選連接埠（IPv4 與 IPv6），之後 Hyper-V 等元件就不會把它占走。
    /// 手動保留的連接埠，程式仍然可以明確綁定。回傳保留的連接埠；都綁不到時回傳 null。
    /// </summary>
    static int? ReservePort(ILog log)
    {
        foreach (var port in SessionStatus.Ports)
        {
            if (!CanBind(port)) continue;
            foreach (var ip in new[] { "ipv4", "ipv6" })
                Cloudflared.Run("netsh.exe", "int", ip, "add", "excludedportrange", "protocol=tcp",
                    $"startport={port}", "numberofports=1", "store=persistent"); // 已經保留過會回錯誤，忽略
            log.Ok($"使用狀態回報使用 TCP {port}（已保留，不會被其他元件占走）");
            return port;
        }
        return null;
    }

    protected override void OnStop()
    {
        _server?.Dispose();
        _server = null;
    }

    // ================= 安裝與狀態 =================

    /// <summary>服務使用的執行檔：正式版是安裝位置，開發模式是目前的執行檔。</summary>
    static string ServiceExe => SelfInstaller.IsSingleFile ? SelfInstaller.InstalledExe : Environment.ProcessPath!;

    public static ServiceControllerStatus? GetStatus()
    {
        try
        {
            using var sc = new ServiceController(Name);
            return sc.Status;
        }
        catch (InvalidOperationException)
        {
            return null; // 沒有安裝
        }
    }

    public static bool IsInstalled => GetStatus() is not null;

    /// <summary>已安裝的服務程式版本；和目前執行的版本不同時，要按「更新」。</summary>
    public static string? InstalledVersion =>
        File.Exists(ServiceExe) ? FileVersionInfo.GetVersionInfo(ServiceExe).ProductVersion?.Split('+')[0] : null;

    /// <summary>停止執行中的服務（更新執行檔前），回傳原本是否在執行。</summary>
    public static bool StopIfRunning()
    {
        try
        {
            using var sc = new ServiceController(Name);
            if (sc.Status == ServiceControllerStatus.Stopped) return false;
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            return false;
        }
    }

    public static void Start()
    {
        using var sc = new ServiceController(Name);
        if (sc.Status != ServiceControllerStatus.Running)
        {
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
        }
    }

    /// <summary>
    /// 安裝或更新服務：把程式安裝到 Program Files、建立開機自動啟動的服務（當掉自動重啟），
    /// 開放防火牆（只允許內部網路），並啟動。需要系統管理員權限，會阻塞數秒。
    /// </summary>
    public static void Install(ILog log)
    {
        StopIfRunning();
        SelfInstaller.Install(log); // 複製執行檔（服務已停止，不會被鎖住）

        var binPath = $"\"{ServiceExe}\" {Argument}";
        var exists = IsInstalled;
        var r = exists
            ? Cloudflared.Run("sc.exe", "config", Name, "binPath=", binPath, "start=", "auto", "DisplayName=", DisplayNameText)
            : Cloudflared.Run("sc.exe", "create", Name, "binPath=", binPath, "start=", "auto", "DisplayName=", DisplayNameText);
        if (r.ExitCode != 0)
            throw new InvalidOperationException("建立使用狀態回報服務失敗：\n" + r.Output.Trim());
        Cloudflared.Run("sc.exe", "description", Name,
            "回報這台電腦目前有沒有人在使用（遠端桌面或電腦前），讓 CfTunnelTool 使用者連線前先確認，避免把正在使用的人擠掉。");
        Cloudflared.Run("sc.exe", "failure", Name, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/60000");
        log.Ok(exists ? "已更新使用狀態回報服務" : "已建立使用狀態回報服務（開機自動啟動）");

        var fw = Cloudflared.Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", FirewallScript());
        if (fw.ExitCode != 0)
            throw new InvalidOperationException("開放使用狀態回報的防火牆失敗：\n" + fw.Output.Trim());
        log.Ok($"已開放防火牆 TCP {string.Join("、", SessionStatus.Ports)}（只允許內部網路）");

        if (ReservePort(log) is null)
            throw new InvalidOperationException(
                $"TCP {string.Join("、", SessionStatus.Ports)} 都無法使用（可能被 Hyper-V、WSL、Docker 保留或其他程式占用）。\n" +
                "可以用「netsh interface ipv4 show excludedportrange protocol=tcp」查看被保留的範圍。");
        Start();
        log.Ok("使用狀態回報服務已啟動");
    }

    /// <summary>PowerShell：重建防火牆規則，只允許內部網路連到狀態回報連接埠。</summary>
    internal static string FirewallScript() =>
        "$ErrorActionPreference='Stop'; " +
        $"Remove-NetFirewallRule -Name '{FirewallRuleName}' -ErrorAction SilentlyContinue; " +
        $"New-NetFirewallRule -Name '{FirewallRuleName}' -DisplayName '{DisplayNameText}' -Direction Inbound " +
        $"-Protocol TCP -LocalPort {string.Join(',', SessionStatus.Ports)} -RemoteAddress {string.Join(',', RdpHost.InternalAddresses)} " +
        "-Action Allow -Profile Any | Out-Null";

    /// <summary>停止並刪除服務與防火牆規則（解除安裝時呼叫；沒有安裝時不做任何事）。</summary>
    public static void Uninstall(ILog log)
    {
        if (!IsInstalled) return;
        StopIfRunning();
        Cloudflared.Run("sc.exe", "delete", Name);
        Cloudflared.Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
            $"Remove-NetFirewallRule -Name '{FirewallRuleName}' -ErrorAction SilentlyContinue");
        // 取消安裝時手動保留的連接埠（沒有保留過的會回錯誤，忽略）
        foreach (var port in SessionStatus.Ports)
            foreach (var ip in new[] { "ipv4", "ipv6" })
                Cloudflared.Run("netsh.exe", "int", ip, "delete", "excludedportrange", "protocol=tcp",
                    $"startport={port}", "numberofports=1", "store=persistent");
        log.Ok("已移除使用狀態回報服務");
    }
}
