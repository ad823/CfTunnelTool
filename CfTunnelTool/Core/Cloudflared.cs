using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;

namespace CfTunnelTool.Core;

/// <summary>負責 cloudflared.exe 的下載，以及 Windows 服務的安裝、移除與重新啟動。</summary>
static class Cloudflared
{
    /// <summary>cloudflared 在 Windows 上安裝的服務名稱（由 cloudflared 本身決定）。</summary>
    public const string ServiceName = "Cloudflared";

    static string DownloadUrl =>
        "https://github.com/cloudflare/cloudflared/releases/latest/download/" +
        (RuntimeInformation.OSArchitecture == Architecture.X86
            ? "cloudflared-windows-386.exe"
            : "cloudflared-windows-amd64.exe"); // ARM64 Windows 可用 x64 模擬執行

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "cloudflared", "cloudflared.exe");

    public static bool IsAdministrator() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static string? Locate(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null;

        var candidates = new List<string>
        {
            DefaultPath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "cloudflared", "cloudflared.exe"),
        };
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        candidates.AddRange(pathDirs.Select(d => Path.Combine(d.Trim('"'), "cloudflared.exe")));

        return candidates.FirstOrDefault(File.Exists);
    }

    public static async Task<string> EnsureInstalledAsync(string? configuredPath, ILog log)
    {
        var existing = Locate(configuredPath);
        if (existing is not null)
        {
            log.Ok($"已找到 cloudflared：{existing}");
            return existing;
        }

        var target = string.IsNullOrWhiteSpace(configuredPath) ? DefaultPath : Path.GetFullPath(configuredPath);
        log.Info($"從 Cloudflare 官方 GitHub 下載 cloudflared → {target}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CfTunnelTool/1.0");
        var tmp = target + ".download";

        using (var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(tmp);

            var buffer = new byte[81920];
            long done = 0;
            var nextReport = 25;
            int read;
            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0 && done * 100 / total.Value >= nextReport)
                {
                    log.Info($"下載中… {nextReport}%（{done / 1024 / 1024} MB）");
                    nextReport += 25;
                }
            }
        }
        File.Move(tmp, target, overwrite: true);

        var version = (await Task.Run(() => Run(target, "--version"))).Output.Trim();
        log.Ok($"下載完成：{version}");
        return target;
    }

    public static ServiceControllerStatus? GetServiceStatus()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status;
        }
        catch (InvalidOperationException)
        {
            return null; // 服務不存在
        }
    }

    /// <summary>安裝服務（會阻塞數秒，請在背景執行緒呼叫）。</summary>
    public static void InstallService(string exe, string tunnelToken, ILog log)
    {
        if (GetServiceStatus() is not null)
        {
            log.Info("移除舊的 Cloudflared 服務");
            UninstallService(exe);
        }

        log.Info("安裝 Cloudflared Windows 服務（開機自動啟動）");
        var r = Run(exe, "service", "install", tunnelToken);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"cloudflared service install 失敗 (exit {r.ExitCode})：\n{r.Output}");

        using var sc = new ServiceController(ServiceName);
        try { sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20)); }
        catch (System.ServiceProcess.TimeoutException) { }
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Running) log.Ok("服務已啟動");
        else log.Warn($"服務狀態：{sc.Status}");
    }

    /// <summary>移除服務（會阻塞數秒，請在背景執行緒呼叫）。</summary>
    public static void UninstallService(string? exe)
    {
        if (GetServiceStatus() is null) return;

        if (exe is not null)
        {
            var r = Run(exe, "service", "uninstall");
            if (r.ExitCode == 0 && GetServiceStatus() is null) return;
        }

        // 後備方案：直接用 sc.exe 停止並刪除
        Run("sc.exe", "stop", ServiceName);
        Thread.Sleep(2000);
        var del = Run("sc.exe", "delete", ServiceName);
        if (del.ExitCode != 0)
            throw new InvalidOperationException($"無法移除 {ServiceName} 服務：\n{del.Output}");
    }

    /// <summary>重新啟動服務（會阻塞數秒，請在背景執行緒呼叫）。</summary>
    public static void RestartService()
    {
        using var sc = new ServiceController(ServiceName);
        if (sc.Status != ServiceControllerStatus.Stopped)
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    public record RunResult(int ExitCode, string Output);

    /// <summary>執行外部程式。注意：參數可能含 Tunnel Token，不要把參數寫進記錄。</summary>
    public static RunResult Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return new RunResult(p.ExitCode, stdout.Result + stderr.Result);
    }
}
