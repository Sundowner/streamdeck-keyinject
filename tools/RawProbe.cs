// What a Raw Input game (Star Citizen) actually receives from our injection.
// Creates a message-only window, registers for raw keyboard, injects one tap
// of 'L' and prints the RAWKEYBOARD record (make code + flags).
using System;
using System.Runtime.InteropServices;
using System.Threading;

internal static class RawProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint type; public uint size; public IntPtr device; public IntPtr wparam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        public RAWKEYBOARD keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RID { public ushort usagePage; public ushort usage; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct U { [FieldOffset(0)] public K ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct K
    {
        public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public U U; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public uint cbSize; public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProc lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra;
        public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private static WndProc keeper;

    [DllImport("user32.dll")] private static extern ushort RegisterClassEx([In] ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool RegisterRawInputDevices(RID[] rid, uint n, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint hsize);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] i, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint map);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage([In] ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage([In] ref MSG m);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetClassLongPtr(IntPtr h, int idx);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string n);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr h; public uint msg; public IntPtr wp; public IntPtr lp; public uint time; public int x; public int y; }

    private const int WM_INPUT = 0x00FF, WM_DESTROY = 0x0002, WM_QUIT = 0x0012, WM_CLOSE = 0x0010;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int RIMD_INPUT = 0x10000003;
    private static int makes, breaks, cookedDown, cookedUp;
    private static ushort lastMake, lastVKey;
    private static ushort lastFlags;

    private static IntPtr Proc(IntPtr h, uint msg, IntPtr wp, IntPtr lp)
    {
        if (msg == WM_INPUT)
        {
            uint size = 0;
            GetRawInputData(lp, RIMD_INPUT, IntPtr.Zero, ref size, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)));
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(lp, RIMD_INPUT, buf, ref size, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER))) == size)
                {
                    RAWINPUT ri = (RAWINPUT)Marshal.PtrToStructure(buf, typeof(RAWINPUT));
                    if (ri.header.type == 1) // RIM_TYPEKEYBOARD
                    {
                        if ((ri.keyboard.Flags & 1) == 0) { makes++; } else { breaks++; }
                        lastMake = ri.keyboard.MakeCode;
                        lastVKey = ri.keyboard.VKey;
                        lastFlags = ri.keyboard.Flags;
                        Console.WriteLine("  RAW keyboard: MakeCode=" + ri.keyboard.MakeCode +
                                          " VKey=0x" + ri.keyboard.VKey.ToString("X2") +
                                          " Flags=0x" + ri.keyboard.Flags.ToString("X") +
                                          " (make=" + ((ri.keyboard.Flags & 1) == 0) + ")");
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return IntPtr.Zero;
        }
        if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) { cookedDown++; return IntPtr.Zero; }
        if (msg == WM_KEYUP || msg == WM_SYSKEYUP) { cookedUp++; return IntPtr.Zero; }
        if (msg == WM_DESTROY) { PostQuitMessageLocal(); return IntPtr.Zero; }
        return DefWindowProcLocal(h, msg, wp, lp);
    }

    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr wp, IntPtr lp);
    private static void PostQuitMessageLocal() { PostQuitMessage(0); }
    private static IntPtr DefWindowProcLocal(IntPtr h, uint m, IntPtr wp, IntPtr lp) { return DefWindowProc(h, m, wp, lp); }

    private static void Send(ushort vk, bool up, int mode)
    {
        INPUT[] a = new INPUT[1];
        a[0].type = 1;
        a[0].U.ki.wVk = mode == 2 ? (ushort)0 : vk;
        a[0].U.ki.wScan = mode == 1 ? (ushort)0 : (ushort)MapVirtualKey(vk, 0);
        a[0].U.ki.dwFlags = (up ? 2u : 0u) | (mode == 2 ? 8u : 0u);
        uint r = SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
        Console.WriteLine("  SendInput(mode " + mode + ") returned " + r);
    }

    private static void Main(string[] args)
    {
        int mode = args.Length > 0 ? int.Parse(args[0]) : 0;
        Console.WriteLine("injection mode: " + (mode == 0 ? "vk+scan (new bridge shape)"
            : mode == 1 ? "vk only (old bridge shape)" : "scan only + SCANCODE flag"));

        keeper = Proc;
        WNDCLASSEX wc = new WNDCLASSEX();
        wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
        wc.lpfnWndProc = keeper;
        wc.hInstance = GetModuleHandle(null);
        wc.lpszClassName = "RawProbeWnd";
        if (RegisterClassEx(ref wc) == 0) { Console.WriteLine("register class failed err=" + Marshal.GetLastWin32Error()); return; }

        // Raw keyboard input is only delivered to the window that has focus, so
        // this must be a real, visible, foreground window - not a message-only one.
        IntPtr hwnd = CreateWindowEx(0, "RawProbeWnd", "KeyInject RawInput probe", 0x00CF0000 /*WS_OVERLAPPEDWINDOW*/,
            60, 60, 420, 220, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { Console.WriteLine("create window failed err=" + Marshal.GetLastWin32Error()); return; }
        ShowWindow(hwnd, 5 /*SW_SHOW*/);
        SetForegroundWindow(hwnd);
        SetFocus(hwnd);
        Console.WriteLine("probe window shown and focused: hwnd=" + hwnd + " foreground=" + (GetForegroundWindow() == hwnd));

        RID[] rid = new RID[1];
        rid[0].usagePage = 1; rid[0].usage = 6; rid[0].hwndTarget = hwnd; // generic keyboard
        if (!RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(RID))))
        { Console.WriteLine("RegisterRawInputDevices failed err=" + Marshal.GetLastWin32Error()); return; }
        Console.WriteLine("raw keyboard registered, MapVirtualKey(L)=" + MapVirtualKey(0x4C, 0));

        Thread t = new Thread(() =>
        {
            Thread.Sleep(200);
            Console.WriteLine("-- injecting one tap of 'L' --");
            Send(0x4C, false, mode);
            Thread.Sleep(70);
            Send(0x4C, true, mode);
            Thread.Sleep(300);
            PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); // must be posted, window owned by main thread
        });
        t.IsBackground = true;
        t.Start();

        MSG m;
        while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessage(ref m); }

        Console.WriteLine("RESULT raw makes=" + makes + " raw breaks=" + breaks +
                          " cooked downs=" + cookedDown + " cooked ups=" + cookedUp +
                          " lastMakeCode=" + lastMake + " lastVKey=0x" + lastVKey.ToString("X2") +
                          " lastFlags=0x" + lastFlags.ToString("X"));
        Console.WriteLine(makes == 1 && breaks == 1
            ? "OK: Raw Input saw the tap (this is what a RawInput game reads)"
            : "Raw Input did NOT see a complete tap");
    }
}
