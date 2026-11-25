// Diagnostic: installs WH_KEYBOARD_LL, injects one combined VK+scan event pair
// and reports exactly what the OS delivered: vk, scancode, transition, and the
// LLKHF_INJECTED flag that Raw Input games (e.g. Star Citizen) act on.
using System;
using System.Runtime.InteropServices;
using System.Threading;

internal static class HookProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage([In] ref MSG m);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage([In] ref MSG m);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint id, uint msg, IntPtr wp, IntPtr lp);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string name);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] i, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint map);

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct U { [FieldOffset(0)] public K ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct K
    {
        public ushort vk;
        public ushort scan;
        public uint flags;
        public uint time;
        public IntPtr extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public U u; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr h; public uint msg; public IntPtr wp; public IntPtr lp; public uint time; public int x; public int y; }

    private const int WM_QUIT = 0x0012;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    private static IntPtr hook;
    private static int downs, ups;
    private static uint lastScan;
    private static bool lastInjected, lastE0;

    private static IntPtr Proc(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(KBDLLHOOKSTRUCT));
            int msg = w.ToInt32();
            if (msg == WM_KEYDOWN) downs++;
            else if (msg == WM_KEYUP) ups++;
            lastScan = k.scanCode;
            lastInjected = (k.flags & 0x10) != 0;   // LLKHF_INJECTED
            lastE0 = (k.flags & 0x20) != 0;         // extended
        }
        return CallNextHookEx(hook, code, w, l);
    }

    private static void Send(ushort vk, bool up)
    {
        uint scan = MapVirtualKey(vk, 0);
        INPUT[] a = new INPUT[1];
        a[0].type = 1;
        a[0].u.ki.vk = vk;
        a[0].u.ki.scan = (ushort)scan;
        a[0].u.ki.flags = up ? 2u : 0u;
        uint r = SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
        Console.WriteLine("SendInput returned " + r + " (scan=" + scan + ")");
    }

    private static void Main()
    {
        uint tid = GetCurrentThreadId();
        HookProc cb = Proc;
        hook = SetWindowsHookEx(13, cb, GetModuleHandle(null), tid); // WH_KEYBOARD_LL
        if (hook == IntPtr.Zero)
        {
            Console.WriteLine("hook failed err=" + Marshal.GetLastWin32Error());
            return;
        }
        Console.WriteLine("hook installed on tid=" + tid);

        Thread t = new Thread(() =>
        {
            Thread.Sleep(150);
            Console.WriteLine("-- injecting one tap of 'L' --");
            Send(0x4C, false);
            Thread.Sleep(60);
            Send(0x4C, true);
            Thread.Sleep(250);
            PostThreadMessage(tid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        });
        t.IsBackground = true;
        t.Start();

        MSG m;
        while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessage(ref m); }

        UnhookWindowsHookEx(hook);
        Console.WriteLine("RESULT keydowns=" + downs + " keyups=" + ups +
                          " lastScan=" + lastScan + " injected=" + lastInjected + " extended=" + lastE0);
        Console.WriteLine(downs == 1 && ups == 1 ? "OK: no duplicate registration" : "PROBLEM: duplicate/missing events");
    }
}
