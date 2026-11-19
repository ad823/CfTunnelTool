using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CfTunnelTool.Core;

enum ActivateResult { Activated, OtherSession }

/// <summary>
/// 單一執行個體：程式已開啟時再開一次，不跳 UAC、不跳彈窗，直接把原本的視窗帶到前面。
/// 第二個執行個體在提權之前就檢查 mutex，再用同一工作階段（Local\）的事件通知原本的程式。
/// 原本的程式是系統管理員權限，事件的 DACL 要允許一般權限的程式開啟並觸發。
/// </summary>
static class SingleInstance
{
    public const string MutexName = @"Global\CfTunnelTool.SingleInstance";
    public const string ActivateEventName = @"Local\CfTunnelTool.Activate";

    /// <summary>
    /// 是否已有執行個體（任何工作階段）。提權程式建立的 mutex，一般權限可能打不開（拒絕存取），也代表存在。
    /// </summary>
    public static bool IsRunning(string mutexName = MutexName)
    {
        try
        {
            if (!Mutex.TryOpenExisting(mutexName, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// 通知同一工作階段中原本的程式把視窗帶到前面。
    /// 找不到事件代表原本的程式在其他使用者的工作階段（或是舊版、正在啟動），回傳 OtherSession。
    /// </summary>
    public static ActivateResult SignalExisting(string eventName = ActivateEventName)
    {
        // 剛被使用者開啟的程式擁有前景權限，轉讓給原本的程式，它才能把自己帶到最前面
        AllowSetForegroundWindow(ASFW_ANY);
        try
        {
            if (!EventWaitHandle.TryOpenExisting(eventName, out var ev)) return ActivateResult.OtherSession;
            using (ev) ev.Set();
            return ActivateResult.Activated;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            return ActivateResult.OtherSession;
        }
    }

    /// <summary>
    /// 建立喚醒事件並在背景執行緒等待；收到通知時呼叫 <paramref name="onSignal"/>（在背景執行緒上）。
    /// 回傳值要保留到程式結束；Dispose 會停止背景執行緒並關閉事件。
    /// </summary>
    public static IDisposable Listen(Action onSignal, string eventName = ActivateEventName)
    {
        var security = new EventWaitHandleSecurity();
        // 一般權限的第二個執行個體：只能觸發，不能改權限
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            EventWaitHandleRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            EventWaitHandleRights.FullControl, AccessControlType.Allow));

        return new Listener(EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, eventName, out _, security), onSignal);
    }

    sealed class Listener : IDisposable
    {
        readonly EventWaitHandle _event;
        readonly Thread _thread;
        volatile bool _stopped;

        public Listener(EventWaitHandle ev, Action onSignal)
        {
            _event = ev;
            _thread = new Thread(() =>
            {
                while (_event.WaitOne() && !_stopped)
                    onSignal();
            })
            { IsBackground = true, Name = "CfTunnelTool.Activate" };
            _thread.Start();
        }

        public void Dispose()
        {
            if (_stopped) return;
            _stopped = true;
            _event.Set();          // 叫醒背景執行緒讓它結束
            _thread.Join(2000);
            _event.Dispose();      // 沒有其他人開著時，事件就會消失
        }
    }

    /// <summary>
    /// 在 UI 執行緒開始接收「再開一次」的通知：建立隱藏的 invoker，跨越 Main 裡多次 Application.Run 都有效。
    /// </summary>
    public static IDisposable? StartListener()
    {
        var invoker = new Control();
        _ = invoker.Handle; // 在 UI 執行緒建立視窗代碼，背景執行緒才能 BeginInvoke 回來
        try
        {
            return Listen(() =>
            {
                try { invoker.BeginInvoke(BringToFront); }
                catch (InvalidOperationException) { /* 程式正在結束 */ }
            });
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return null; // 收不到通知只會讓第二次開啟顯示提示，不影響主要功能
        }
    }

    /// <summary>把目前的主視窗還原並帶到最前面；有對話框（登入、訊息方塊）時帶出最上層的對話框。</summary>
    public static void BringToFront()
    {
        var forms = Application.OpenForms.Cast<Form>().Where(f => f.Visible).ToList();
        var main = forms.FirstOrDefault(f => f.Owner is null) ?? forms.FirstOrDefault();
        if (main is null) return; // 兩個畫面切換之間

        var hwnd = main.Handle;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        var target = GetLastActivePopup(hwnd);
        if (target == IntPtr.Zero) target = hwnd;

        // 先暫時設為最上層再取消：就算 Windows 不給焦點（前景鎖定），視窗也會顯示在其他視窗上面
        foreach (var w in target == hwnd ? [hwnd] : new[] { hwnd, target })
        {
            SetWindowPos(w, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetWindowPos(w, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        }

        if (!SetForegroundWindow(target))
        {
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = target,
                dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
            };
            FlashWindowEx(ref info);
        }
    }

    const int ASFW_ANY = -1, SW_RESTORE = 9;
    static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_SHOWWINDOW = 0x40;

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    const uint FLASHW_ALL = 3, FLASHW_TIMERNOFG = 12;

    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetLastActivePopup(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
}
