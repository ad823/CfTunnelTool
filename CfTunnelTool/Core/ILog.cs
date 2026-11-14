namespace CfTunnelTool.Core;

/// <summary>部署過程的訊息輸出（介面上的記錄視窗會實作它）。</summary>
interface ILog
{
    void Info(string message);
    void Step(string message);
    void Ok(string message);
    void Warn(string message);
    void Error(string message);
}
