using System.Runtime.InteropServices;
using Microsoft.Win32;

// F15Key: stops Teams going "Away" from inactivity.
// Double-click = start + enable auto-start. Double-click again = stop + disable auto-start.

internal static class Program
{
    private const string StopEventName = @"Local\F15Key.Stop";
    private const string MutexName = @"Local\F15Key.Instance";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "F15Key";

    // Teams switches to Away after about 5 minutes without input; act well before that.
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    private static int Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);

        if (!isFirstInstance)
        {
            // Already running: disable auto-start, ask that instance to stop, then exit.
            SetAutoStart(false);
            stopEvent.Set();
            return 0;
        }

        SetAutoStart(true);

        try
        {
            while (!stopEvent.WaitOne(CheckInterval))
            {
                // Paused while locked (Win+L); resumes on its own after login.
                if (!Native.IsSessionLocked() && Native.GetIdleTime() >= IdleThreshold)
                {
                    Native.PressF15();
                }
            }
        }
        finally
        {
            mutex.ReleaseMutex();
        }

        return 0;
    }

    // Per-user Run key: no admin rights, visible in Task Manager > Startup apps.
    private static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }
}

internal static class Native
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_F15 = 0x7E; // No app uses F15 and no physical keyboard has it: harmless.

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SwitchDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    // When locked, the input desktop is the secure logon desktop, which the user session cannot switch to.
    public static bool IsSessionLocked()
    {
        IntPtr desktop = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
        if (desktop == IntPtr.Zero)
        {
            return true;
        }

        try
        {
            return !SwitchDesktop(desktop);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    public static TimeSpan GetIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        // Unsigned subtraction handles the 49-day tick counter wrap-around.
        uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMs);
    }

    public static void PressF15()
    {
        INPUT[] inputs =
        [
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_F15 } } },
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_F15, dwFlags = KEYEVENTF_KEYUP } } },
        ];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    // Union sized for the largest member (MOUSEINPUT) so SendInput gets the correct struct size.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
