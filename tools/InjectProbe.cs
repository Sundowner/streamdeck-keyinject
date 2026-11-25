// Diagnostic without a global hook: inject one tap and poll GetAsyncKeyState
// to count press/release transitions. Two downs => double registration.
using System;
using System.Runtime.InteropServices;
using System.Threading;

internal static class InjectProbe
{
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct U { [FieldOffset(0)] public K ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct K
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public U U; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] i, int size);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint map);

    // mode: 0 = combined vk+scan (current bridge shape), 1 = vk only (old shape)
    private static void Send(ushort vk, bool up, int mode)
    {
        INPUT[] a = new INPUT[1];
        a[0].type = 1;
        a[0].U.ki.wVk = vk;
        a[0].U.ki.wScan = mode == 0 ? (ushort)MapVirtualKey(vk, 0) : (ushort)0;
        a[0].U.ki.dwFlags = up ? 2u : 0u;
        uint r = SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
        if (r != 1) Console.WriteLine("  SendInput returned " + r + " err=" + Marshal.GetLastWin32Error());
    }

    private static void Main(string[] args)
    {
        int mode = args.Length > 0 ? int.Parse(args[0]) : 0;
        ushort vk = 0x4C; // 'L'
        Console.WriteLine("mode=" + (mode == 0 ? "vk+scan" : "vk-only") +
                          " scan(L)=" + MapVirtualKey(vk, 0));

        bool[] seen = new bool[256];
        int transitions = 0, downs = 0;

        Thread sender = new Thread(() =>
        {
            Thread.Sleep(120);
            Send(vk, false, mode);
            Thread.Sleep(60);
            Send(vk, true, mode);
        });
        sender.IsBackground = true;
        sender.Start();

        DateTime end = DateTime.Now.AddMilliseconds(900);
        bool last = false;
        while (DateTime.Now < end)
        {
            bool now = (GetAsyncKeyState(vk) & 0x8000) != 0;
            if (now != last)
            {
                transitions++;
                if (now) downs++;
                Console.WriteLine("  transition " + transitions + ": " + (now ? "DOWN" : "UP"));
                last = now;
            }
            Thread.Sleep(3);
        }
        Console.WriteLine("RESULT downs=" + downs + " transitions=" + transitions +
                          (downs == 1 ? "  -> single registration (good)" : "  -> DUPLICATED or missed"));
    }
}
