using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OtterDesktop;

/// <summary>
/// Records only the time of the latest key press. It never stores key codes or typed text.
/// </summary>
internal sealed class GlobalKeyboardActivity : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;

    private readonly HookProcedure _callback;
    private IntPtr _hook;
    private long _lastKeyPressAt;

    public GlobalKeyboardActivity()
    {
        _callback = HookCallback;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = module is null ? IntPtr.Zero : GetModuleHandle(module.ModuleName);
        _hook = SetWindowsHookEx(WhKeyboardLl, _callback, moduleHandle, 0);
    }

    public bool IsTyping
    {
        get
        {
            var lastKeyPressAt = Interlocked.Read(ref _lastKeyPressAt);
            return _hook != IntPtr.Zero && lastKeyPressAt > 0 &&
                   Environment.TickCount64 - lastKeyPressAt < 1450;
        }
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        GC.SuppressFinalize(this);
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (message == (IntPtr)WmKeyDown || message == (IntPtr)WmSysKeyDown))
        {
            Interlocked.Exchange(ref _lastKeyPressAt, Environment.TickCount64);
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, HookProcedure callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
