// Key Inject bridge for Stream Deck (v2 SDK).
// Runs as the plugin's "Streams" bridge app. Hosts a WebSocket server on
// 127.0.0.1:12345 that the Property Inspectors connect to, and injects real
// keyboard input into the foreground window via the Win32 SendInput API.
// No external dependencies: raw TcpListener + hand-rolled WebSocket framing,
// compiled with the .NET Framework 4 csc.exe that ships with Windows.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace KeyInject
{
    internal static class Program
    {
        private const int Port = 12345;   // must match property-inspector/js/pi.js

        private static void Main(string[] args)
        {
            // Stream Deck launches the plugin via CodePath with either
            // --plugin "ws://...?key=..." (older) or "-port <n> -pluginUUID ...
            // -registerEvent registerPlugin" (7.x). We connect back, register
            // (and execute physical keyDown/keyUp there), and separately serve
            // the Property Inspectors ourselves on Port.
            Logger.Write("=== start, args: " + string.Join(" | ", args));
            Logger.Write("bridge elevation: " + (TargetProbe.SelfElevated() ? "ELEVATED" : "normal") +
                " | current target: " + TargetProbe.Describe());
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                Logger.Write("UNHANDLED: " + e.ExceptionObject);
            };
            KeyExecutor executor = new KeyExecutor();
            WebSocketServer server = new WebSocketServer(Port, executor);
            try
            {
                server.Start();
                Logger.Write("PI server listening on 127.0.0.1:" + Port);
            }
            catch (SocketException ex)
            {
                Logger.Write("Could not listen on 127.0.0.1:" + Port + " (" + ex.Message +
                    "). Is another copy of the bridge running?");
                Environment.Exit(1);
            }
            AppDomain.CurrentDomain.ProcessExit += delegate { executor.ReleaseAll(); server.Stop(); };

            string sdUrl = GetArg(args, "--plugin");
            string sdUuid = GetArg(args, "--pluginUUID") ?? GetArg(args, "-pluginUUID");
            string regEvent = GetArg(args, "-registerEvent") ?? "register";
            string portArg = GetArg(args, "--port") ?? GetArg(args, "-port");
            if (string.IsNullOrEmpty(sdUrl) && !string.IsNullOrEmpty(portArg) && !string.IsNullOrEmpty(sdUuid))
            {
                // Stream Deck 7.x launch form: -port <n> -pluginUUID <id>
                // -registerEvent registerPlugin -info {json}; the plugin dials
                // ws://127.0.0.1:<port> and registers itself immediately.
                sdUrl = "ws://127.0.0.1:" + portArg;
            }
            if (!string.IsNullOrEmpty(sdUrl))
            {
                Logger.Write("Connecting to Stream Deck at " + sdUrl + " (registerEvent=" + regEvent + ")");
                Thread t = new Thread(delegate () { StreamDeckClient.Run(sdUrl, sdUuid, regEvent, executor); });
                t.IsBackground = true;
                t.Start();
            }
            else
            {
                Logger.Write("No connection arguments found: running standalone (PI control only).");
            }

            Thread.Sleep(Timeout.Infinite);
        }

        private static string GetArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // Simple file logger: %LOCALAPPDATA%\KeyInject\bridge.log
    // ---------------------------------------------------------------------
    internal static class Logger
    {
        private static readonly object gate = new object();
        private static readonly string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KeyInject", "bridge.log");

        public static void Write(string message)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + "\n");
                }
            }
            catch { }
            try { Console.WriteLine(message); } catch { }
        }
    }

    // ---------------------------------------------------------------------
    // Per-button settings mirror
    // ---------------------------------------------------------------------
    // Stream Deck sometimes hands the plugin an EMPTY settings object for a
    // button that is configured, and a Property Inspector can then save that
    // emptiness back, killing the button. The bridge keeps its own copy so a
    // bad round-trip cannot silence a working key.
    internal static class SettingStore
    {
        private static readonly object gate = new object();
        private static readonly string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KeyInject", "settings.store");
        private static readonly Dictionary<string, string[]> rows =
            new Dictionary<string, string[]>();
        private static bool loaded;

        // [0]=mode [1]=keys [2]=sequence [3]=releaseMs [4]=cooldownMs
        public static string[] Get(string context)
        {
            if (context.Length == 0) return null;
            EnsureLoaded();
            lock (gate)
            {
                string[] v;
                return rows.TryGetValue(context, out v) ? v : null;
            }
        }

        public static void Put(string context, string mode, string keys, string seq,
                               string releaseMs, string cooldownMs, bool fromInspector)
        {
            if (context.Length == 0) return;
            EnsureLoaded();
            lock (gate)
            {
                if (keys.Length == 0 && seq.Length == 0)
                {
                    // Only an explicit clear from the inspector may erase a row;
                    // an empty payload from the app never does.
                    if (fromInspector && rows.Remove(context)) SaveLocked();
                    return;
                }
                string[] old;
                if (rows.TryGetValue(context, out old) &&
                    old[0] == mode && old[1] == keys && old[2] == seq &&
                    old[3] == releaseMs && old[4] == cooldownMs) return;
                rows[context] = new string[] { mode, keys, seq, releaseMs, cooldownMs };
                SaveLocked();
            }
        }

        private static void EnsureLoaded()
        {
            lock (gate)
            {
                if (loaded) return;
                loaded = true;
                try
                {
                    if (!File.Exists(path)) return;
                    foreach (string line in File.ReadAllLines(path))
                    {
                        if (line.Length == 0) continue;
                        string[] p = line.Split('\t');
                        if (p.Length < 6) continue;
                        rows[Unesc(p[0])] = new string[] { Unesc(p[1]), Unesc(p[2]), Unesc(p[3]), Unesc(p[4]), Unesc(p[5]) };
                    }
                    Logger.Write("Settings mirror loaded: " + rows.Count + " button(s).");
                }
                catch (Exception ex) { Logger.Write("Settings mirror load failed: " + ex.Message); }
            }
        }

        private static void SaveLocked()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string[]> kv in rows)
                {
                    sb.Append(Esc(kv.Key)).Append('\t')
                      .Append(Esc(kv.Value[0])).Append('\t')
                      .Append(Esc(kv.Value[1])).Append('\t')
                      .Append(Esc(kv.Value[2])).Append('\t')
                      .Append(Esc(kv.Value[3])).Append('\t')
                      .Append(Esc(kv.Value[4])).Append('\n');
                }
                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception ex) { Logger.Write("Settings mirror save failed: " + ex.Message); }
        }

        private static string Esc(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n");
        }

        private static string Unesc(string s)
        {
            return s.Replace("\\t", "\t").Replace("\\n", "\n").Replace("\\\\", "\\");
        }
    }

    // ---------------------------------------------------------------------
    // Key resolution: token text -> Win32 virtual-key code + extended flag
    // ---------------------------------------------------------------------
    internal static class KeyMap
    {
        private static readonly Dictionary<string, int> Named = BuildNamed();
        private static readonly HashSet<int> Extended = BuildExtended();

        private static Dictionary<string, int> BuildNamed()
        {
            Dictionary<string, int> d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            d["esc"] = 0x1B; d["escape"] = 0x1B;
            d["tab"] = 0x09;
            d["enter"] = 0x0D; d["return"] = 0x0D;
            d["space"] = 0x20; d["spacebar"] = 0x20;
            d["backspace"] = 0x08;
            d["insert"] = 0x2D; d["delete"] = 0x2E; d["del"] = 0x2E;
            d["home"] = 0x24; d["end"] = 0x23;
            d["pageup"] = 0x21; d["prior"] = 0x21;
            d["pagedown"] = 0x22; d["next"] = 0x22;
            d["up"] = 0x26; d["down"] = 0x28; d["left"] = 0x25; d["right"] = 0x27;
            d["printscreen"] = 0x2C; d["prtsc"] = 0x2C;
            d["pause"] = 0x13; d["capslock"] = 0x14;
            d["ctrl"] = 0x11; d["control"] = 0x11;
            d["rctrl"] = 0xA3; d["alt"] = 0x12; d["menu"] = 0x12;
            d["ralt"] = 0xB5; d["shift"] = 0x10;
            d["rshift"] = 0xA1; d["win"] = 0x5B; d["lwin"] = 0x5B;
            d["rwin"] = 0x5C;
            d["numlock"] = 0x90; d["scrolllock"] = 0x91;
            for (int n = 1; n <= 12; n++) d["f" + n] = 0x6F + n;
            // Numpad (explicit names; a bare digit is always the main-row key)
            d["num0"] = 0x60; d["num1"] = 0x61; d["num2"] = 0x62; d["num3"] = 0x63;
            d["num4"] = 0x64; d["num5"] = 0x65; d["num6"] = 0x66; d["num7"] = 0x67;
            d["num8"] = 0x68; d["num9"] = 0x69;
            d["nummultiply"] = 0x6A; d["numadd"] = 0x6B; d["numsub"] = 0x6D;
            d["numdec"] = 0x6E;
            d["luent"] = 0x6C; // numpad Enter (extended)
            return d;
        }

        private static HashSet<int> BuildExtended()
        {
            // Virtual-key codes that require KEYEVENTF_EXTENDEDKEY.
            return new HashSet<int>(new int[]
            {
                0x2D, 0x2E, 0x24, 0x23, 0x21, 0x22,   // ins del home end pgup pgdn
                0x26, 0x28, 0x25, 0x27,               // arrows
                0x2C, 0x13,                           // prtsc, pause
                0xA3, 0xB5,                           // rctrl, ralt
                0x5C,                                 // rwin
                0x6C                                  // numpad enter
            });
        }

        // Validate a whole combo without executing it, so a bad token can be
        // reported on the key instead of surfacing only in the log.
        public static bool TryValidate(string keys, out string badToken)
        {
            badToken = "";
            string[] parts = keys.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                ushort vk; bool ext;
                if (!TryResolve(parts[i], out vk, out ext))
                {
                    badToken = parts[i].Trim();
                    return false;
                }
            }
            return true;
        }

        public static bool TryResolve(string token, out ushort vk, out bool extended)
        {
            vk = 0; extended = false;
            token = token.Trim();
            if (token.Length == 0) return false;

            int code;
            if (Named.TryGetValue(token, out code))
            {
                vk = (ushort)code;
                extended = Extended.Contains(code);
                return true;
            }
            if (token.Length == 1)
            {
                char c = char.ToUpperInvariant(token[0]);
                if (c >= 'A' && c <= 'Z') { vk = (ushort)c; return true; }
                if (c >= '0' && c <= '9') { vk = (ushort)c; return true; }
                switch (c)
                {
                    case '-': vk = 0xBD; return true;
                    case '=': vk = 0xBB; return true;
                    case '[': vk = 0xDB; return true;
                    case ']': vk = 0xDD; return true;
                    case '\\': vk = 0xDC; return true;
                    case ';': vk = 0xBA; return true;
                    case '\'': vk = 0xDE; return true;
                    case ',': vk = 0xBC; return true;
                    case '.': vk = 0xBE; return true;
                    case '/': vk = 0xBF; return true;
                    case '`': vk = 0xC0; return true;
                }
            }
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // SendInput plumbing
    // ---------------------------------------------------------------------
    // Union with the INPUT struct: sized/padded so sizeof matches the real
    // INPUT union (32 bytes on x64) even though we only fill the ki member.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    internal struct INPUT_UNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public INPUT_UNION U;
    }

    internal static class Native
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(IntPtr token, int cls, ref uint info, int len, out uint returned);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }

    // ---------------------------------------------------------------------
    // Target probe: what would actually receive our injected keys right now?
    // ---------------------------------------------------------------------
    internal static class TargetProbe
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenElevation = 20;

        public static bool SelfElevated()
        {
            try
            {
                IntPtr tok;
                if (!Native.OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_QUERY, out tok))
                    return false;
                uint info = 0; uint ret;
                bool ok = Native.GetTokenInformation(tok, TokenElevation, ref info, 4, out ret);
                Native.CloseHandle(tok);
                return ok && info != 0;
            }
            catch { return false; }
        }

        // Returns: win='title' proc=name pid=N target=ELEVATED|normal|unknown
        public static string Describe()
        {
            try
            {
                IntPtr h = Native.GetForegroundWindow();
                if (h == IntPtr.Zero) return "no foreground window";

                int len = Native.GetWindowTextLength(h);
                var sb = new System.Text.StringBuilder(Math.Max(1, len + 1));
                Native.GetWindowText(h, sb, sb.Capacity);
                string title = sb.ToString();
                if (title.Length > 48) title = title.Substring(0, 48);

                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                string proc = "unknown";
                try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { }

                string elev = "unknown";
                IntPtr p = Native.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (p != IntPtr.Zero)
                {
                    IntPtr tok;
                    if (Native.OpenProcessToken(p, TOKEN_QUERY, out tok))
                    {
                        uint info = 0; uint ret;
                        if (Native.GetTokenInformation(tok, TokenElevation, ref info, 4, out ret))
                            elev = info != 0 ? "ELEVATED" : "normal";
                        Native.CloseHandle(tok);
                    }
                    Native.CloseHandle(p);
                }
                return "win='" + title + "' proc=" + proc + " pid=" + pid + " target=" + elev;
            }
            catch (Exception ex) { return "probe failed: " + ex.Message; }
        }
    }

    // ---------------------------------------------------------------------
    // Executor: serial command queue so sequences and holds never overlap
    // ---------------------------------------------------------------------
    internal sealed class KeyExecutor
    {
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_SCANCODE = 0x0008;
        private const uint MAPVK_VK_TO_VSC = 0;

        private readonly object gate = new object();
        private readonly Queue<string> queue = new Queue<string>();
        private readonly HashSet<ushort> heldMods = new HashSet<ushort>(); // mods we pressed
        private readonly Dictionary<ushort, List<ushort>> modStack =
            new Dictionary<ushort, List<ushort>>();                        // base -> mods we pressed
        private Thread worker;

        public void Enqueue(string commandJson)
        {
            lock (gate)
            {
                queue.Enqueue(commandJson);
                Monitor.Pulse(gate);
                if (worker == null)
                {
                    worker = new Thread(Run);
                    worker.IsBackground = true;
                    worker.Start();
                }
            }
        }

        public void ReleaseAll()
        {
            lock (gate)
            {
                queue.Enqueue("{\"event\":\"releaseAll\"}");
                Monitor.Pulse(gate);
            }
        }

        // Parse and check tokens without executing, so the Property Inspector
        // gets an immediate, specific error for typos.
        public bool TryValidate(string json, out string error)
        {
            error = null;
            try
            {
                JsonReader r = new JsonReader(json);
                r.Expect('{');
                string ev = "", keys = "", seq = "";
                while (true)
                {
                    string name = r.ReadString();
                    r.Expect(':');
                    string value = r.ReadString();
                    if (name == "event") ev = value;
                    else if (name == "keys") keys = value;
                    else if (name == "sequence") seq = value;
                    if (!r.TryEat(',')) break;
                }

                ushort vk; bool ext;
                if (ev == "down" || ev == "up" || ev == "tap")
                {
                    ParseMods(keys, out vk, out ext);
                }
                else if (ev == "sequence")
                {
                    foreach (string raw in seq.Split(','))
                    {
                        string token = raw.Trim();
                        if (token.Length == 0) continue;
                        string t = token.EndsWith("ms", StringComparison.OrdinalIgnoreCase)
                            ? token.Substring(0, token.Length - 2) : token;
                        int ms;
                        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out ms)) continue;
                        string key = (token[0] == '+' || token[0] == '-') ? token.Substring(1) : token;
                        if (key.Length > 0 && !KeyMap.TryResolve(key, out vk, out ext))
                            throw new Exception("Unknown key token: '" + key + "'");
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private void Run()
        {
            while (true)
            {
                string cmd;
                lock (gate)
                {
                    while (queue.Count == 0) Monitor.Wait(gate);
                    cmd = queue.Dequeue();
                }
                try { Execute(cmd); }
                catch (Exception ex) { Logger.Write("Command failed: " + ex.Message); }
            }
        }

        private void Execute(string json)
        {
            JsonReader r = new JsonReader(json);
            r.Expect('{');
            string ev = "";
            string keys = "";
            string seq = "";
            while (true)
            {
                string name = r.ReadString();
                r.Expect(':');
                string value = r.ReadString();
                if (name == "event") ev = value;
                else if (name == "keys") keys = value;
                else if (name == "sequence") seq = value;
                if (!r.TryEat(',')) break;
            }

            if (ev == "down") DoDown(keys);
            else if (ev == "up") DoUp(keys);
            else if (ev == "tap") DoTap(keys);
            else if (ev == "sequence") RunSequence(seq);
            else if (ev == "releaseAll") DoReleaseAll();
        }

        private List<ushort> ParseMods(string keysToken, out ushort baseVk, out bool baseExtended)
        {
            baseVk = 0; baseExtended = false;
            List<ushort> mods = new List<ushort>();
            string[] parts = keysToken.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                ushort vk; bool ext;
                if (!KeyMap.TryResolve(parts[i], out vk, out ext))
                    throw new Exception("Unknown key token: '" + parts[i] + "'");
                if (i == parts.Length - 1) { baseVk = vk; baseExtended = ext; }
                else mods.Add(vk);
            }
            return mods;
        }

        private void SendKey(ushort vk, bool extended, bool up)
        {
            // Map the virtual key to its hardware scan code for the current
            // keyboard layout, and put BOTH vk and wScan in a single event.
            // This is the shape DirectInput/Raw-Input readers key off (they use
            // wScan and ignored our earlier vk-only events) while still being
            // translated by the normal Win32 path for Notepad/browsers.
            // One event only - two events would double-register in ordinary apps.
            uint scan = Native.MapVirtualKey(vk, MAPVK_VK_TO_VSC);
            uint flags = (up ? KEYEVENTF_KEYUP : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0);

            INPUT[] a = new INPUT[1];
            a[0].type = 1; // INPUT_KEYBOARD
            a[0].U.ki.wVk = vk;
            a[0].U.ki.wScan = (ushort)scan;
            a[0].U.ki.dwFlags = flags;
            uint r1 = Native.SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));

            // Fallback: if the combined event was rejected outright (0 records
            // processed), try the scan-code-only form some readers prefer.
            if (r1 == 0 && scan != 0)
            {
                INPUT[] b = new INPUT[1];
                b[0].type = 1;
                b[0].U.ki.wVk = 0;
                b[0].U.ki.wScan = (ushort)scan;
                b[0].U.ki.dwFlags = flags | KEYEVENTF_SCANCODE;
                uint r2 = Native.SendInput(1, b, Marshal.SizeOf(typeof(INPUT)));
                if (r2 == 0) LogSendFailure(vk, up);
            }
            else if (r1 == 0)
            {
                LogSendFailure(vk, up);
            }
        }

        private static int failCount;
        private static void LogSendFailure(ushort vk, bool up)
        {
            failCount++;
            if (failCount <= 5 || failCount % 50 == 0)
            {
                Logger.Write(string.Format(
                    "SendInput BLOCKED (vk=0x{0:X2} {1}, err={2}, {3}) - " +
                    "target window is elevated or ignores injected input (failures: {4})",
                    vk, up ? "up" : "down",
                    Marshal.GetLastWin32Error(),
                    TargetProbe.Describe(), failCount));
            }
        }

        private static string ForegroundProcessName()
        {
            try
            {
                IntPtr h = Native.GetForegroundWindow();
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (pid == 0) return "none";
                return Process.GetProcessById((int)pid).ProcessName;
            }
            catch { return "unknown"; }
        }

        private bool IsDown(ushort vk)
        {
            return (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        private void DoDown(string keysToken)
        {
            ushort vk; bool ext;
            List<ushort> mods = ParseMods(keysToken, out vk, out ext);
            List<ushort> pressed = new List<ushort>();
            foreach (ushort m in mods)
                if (!heldMods.Contains(m) && !IsDown(m)) { SendKey(m, false, false); heldMods.Add(m); pressed.Add(m); }
            modStack[vk] = pressed;
            SendKey(vk, ext, false);
        }

        private void DoUp(string keysToken)
        {
            ushort vk; bool ext;
            ParseMods(keysToken, out vk, out ext);
            SendKey(vk, ext, true);
            List<ushort> pressed;
            if (modStack.TryGetValue(vk, out pressed))
            {
                for (int i = pressed.Count - 1; i >= 0; i--)
                {
                    heldMods.Remove(pressed[i]);
                    SendKey(pressed[i], false, true);
                }
                modStack.Remove(vk);
            }
        }

        private void DoTap(string keysToken)
        {
            DoDown(keysToken);
            Thread.Sleep(25);
            DoUp(keysToken);
        }

        // Sequence grammar (comma-separated tokens):
        //   key / mod+key        -> tap
        //   +key                 -> hold down
        //   -key                 -> release
        //   200 / 200ms          -> wait
        private void RunSequence(string seq)
        {
            List<int> vks = new List<int>(); // tokens that are key presses (for safety release)
            foreach (string raw in seq.Split(','))
            {
                string token = raw.Trim();
                if (token.Length == 0) continue;
                int ms;
                string t = token.EndsWith("ms", StringComparison.OrdinalIgnoreCase)
                    ? token.Substring(0, token.Length - 2) : token;
                if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out ms))
                {
                    if (ms > 0) Thread.Sleep(Math.Min(ms, 60000));
                    continue;
                }
                if (token[0] == '+')
                {
                    DoDown(token.Substring(1));
                }
                else if (token[0] == '-')
                {
                    DoUp(token.Substring(1));
                }
                else
                {
                    DoTap(token);
                    Thread.Sleep(25);
                }
            }
            DoReleaseAll();
        }

        private void DoReleaseAll()
        {
            List<ushort> all = new List<ushort>(heldMods);
            heldMods.Clear();
            foreach (KeyValuePair<ushort, List<ushort>> kv in modStack)
                SendKey(kv.Key, KeyIsExtended(kv.Key), true);
            modStack.Clear();
            foreach (ushort m in all) SendKey(m, false, true);
        }

        private static bool KeyIsExtended(ushort vk)
        {
            return vk == 0x2D || vk == 0x2E || vk == 0x24 || vk == 0x23 ||
                   vk == 0x21 || vk == 0x22 || vk == 0x26 || vk == 0x28 ||
                   vk == 0x25 || vk == 0x27 || vk == 0x2C || vk == 0x13 ||
                   vk == 0xA3 || vk == 0xB5 || vk == 0x5C || vk == 0x6C;
        }
    }

    // ---------------------------------------------------------------------
    // Minimal strict JSON reader for our flat {"k":"v",...} protocol
    // ---------------------------------------------------------------------
    internal sealed class JsonReader
    {
        private readonly string s;
        private int i;

        public JsonReader(string s) { this.s = s; i = 0; }

        public void Expect(char c)
        {
            SkipWs();
            if (i >= s.Length || s[i] != c) throw new Exception("Expected '" + c + "' at offset " + i);
            i++;
        }

        public bool TryEat(char c)
        {
            SkipWs();
            if (i < s.Length && s[i] == c) { i++; return true; }
            return false;
        }

        public string ReadString()
        {
            SkipWs();
            if (i < s.Length && s[i] == '"')
            {
                i++;
                StringBuilder sb = new StringBuilder();
                while (i < s.Length && s[i] != '"')
                {
                    char c = s[i];
                    if (c == '\\')
                    {
                        i++;
                        if (i >= s.Length) break;
                        char e = s[i];
                        switch (e)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 't': sb.Append('\t'); break;
                            case 'r': sb.Append('\r'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'u':
                                if (i + 4 < s.Length)
                                {
                                    sb.Append((char)int.Parse(s.Substring(i + 1, 4), NumberStyles.HexNumber));
                                    i += 4;
                                }
                                break;
                            default: sb.Append(e); break;
                        }
                    }
                    else sb.Append(c);
                    i++;
                }
                if (i >= s.Length) throw new Exception("Unterminated string");
                i++; // closing quote
                return sb.ToString();
            }
            throw new Exception("Expected string at offset " + i);
        }

        private void SkipWs()
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }
    }

    // ---------------------------------------------------------------------
    // Stream Deck registration client: connects to the WebSocket URL Stream
    // Deck passed on the command line, registers, and answers its pings.
    // ---------------------------------------------------------------------
    internal static class StreamDeckClient
    {
        public static void Run(string url, string pluginUuid, string registerEvent, KeyExecutor executor)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                Logger.Write("Unparseable --plugin URL: " + url);
                return;
            }
            for (int attempt = 0; attempt < 10; attempt++)
            {
                bool connected = false;
                try
                {
                    connected = Session(uri, pluginUuid, registerEvent, executor);
                }
                catch (Exception ex)
                {
                    Logger.Write("Stream Deck link failed: " + ex.Message);
                }
                if (connected)
                {
                    // The link dropped deliberately (upgrade, reload, Stream Deck
                    // quitting): exit so Stream Deck manages our lifecycle.
                    Environment.Exit(0);
                }
                Thread.Sleep(3000);
            }
            Logger.Write("Could not reach Stream Deck; giving up registration.");
        }

        // Returns true if the session actually connected before it ended.
        private static bool Session(Uri uri, string pluginUuid, string registerEvent, KeyExecutor executor)
        {
            int port = uri.Port > 0 ? uri.Port : 80;
            using (TcpClient socket = new TcpClient())
            {
                socket.Connect(uri.Host, port);
                socket.NoDelay = true;
                NetworkStream stream = socket.GetStream();

                byte[] nonce = new byte[16];
                using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(nonce);
                string key = Convert.ToBase64String(nonce);
                string path = string.IsNullOrEmpty(uri.Query) ? "/" : uri.PathAndQuery;
                string request = "GET " + path + " HTTP/1.1\r\n" +
                    "Host: " + uri.Host + ":" + port + "\r\n" +
                    "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                    "Sec-WebSocket-Key: " + key + "\r\n" +
                    "Sec-WebSocket-Version: 13\r\n\r\n";
                byte[] rb = Encoding.ASCII.GetBytes(request);
                stream.Write(rb, 0, rb.Length);
                stream.Flush();

                StreamReader reader = new StreamReader(stream, Encoding.ASCII);
                string statusLine = reader.ReadLine();
                if (statusLine == null || !statusLine.Contains("101"))
                    throw new Exception("handshake refused: " + statusLine);
                string line;
                while ((line = reader.ReadLine()) != null && line.Length > 0) { }

                // v2 native protocol: { "event": <registerEvent>, "uuid": <pluginUUID> }
                // (the older v1 form used "eventType"; Stream Deck 7.6 ignores that
                // and terminates a plugin that never registers).
                Send(stream, "{\"event\":\"" + registerEvent + "\",\"uuid\":\"" + pluginUuid + "\"}", true);
                Logger.Write("Registration sent; waiting for Stream Deck ack.");

                // Publish the live stream so other parts (PI title relays) can
                // send commands to Stream Deck while the session is up.
                lock (appStreamGate) appStream = stream;
                try
                {
                    // Read loop: log early frames, answer pings, and execute physical
                    // button presses (keyDown/keyUp) straight from the app. This way
                    // the buttons work even when no Property Inspector is open.
                    int logged = 0;
                    while (true)
                    {
                        string message = ReadFrame(stream);
                        if (message == null) return true;
                        if (logged < 5)
                        {
                            Logger.Write("Stream Deck <- " + (message.Length > 300 ? message.Substring(0, 300) : message));
                            logged++;
                        }
                        if (message.Contains("ping"))
                            Send(stream, message, true);
                        else if (message.Contains("\"keyDown\"") || message.Contains("\"keyUp\""))
                            HandleDeckEvent(message, executor);
                    }
                }
                finally
                {
                    lock (appStreamGate) appStream = null;
                }
            }
        }

        // ----- plugin -> Stream Deck outbound channel -----

        private static NetworkStream appStream;
        private static readonly object appStreamGate = new object();

        public static bool SendToApp(string json)
        {
            lock (appStreamGate)
            {
                if (appStream == null) return false;
                try { Send(appStream, json, true); return true; }
                catch (Exception ex)
                {
                    Logger.Write("SendToApp failed: " + ex.Message);
                    appStream = null;
                    return false;
                }
            }
        }

        // ----- physical button execution (keyDown/keyUp from the app) -----

        private sealed class HoldState
        {
            public string Keys;
            public int Generation;
        }

        private static readonly Dictionary<string, HoldState> holds =
            new Dictionary<string, HoldState>();
        private static readonly object holdGate = new object();
        private static int holdCounter;

        public static bool HasActiveHold
        {
            get { lock (holdGate) return holds.Count > 0; }
        }

        private static void HandleDeckEvent(string message, KeyExecutor executor)
        {
            bool down = message.Contains("\"keyDown\"");
            bool up = message.Contains("\"keyUp\"");
            if (!down && !up) return;
            if (!message.Contains("\"action\":\"com.sundowner.keyinject.action\"")) return;

            string context = ExtractString(message, "\"context\":\"");
            string device = ExtractString(message, "\"device\":\"");
            string settings = ExtractObject(message, "\"settings\":");
            string mode = ExtractString(settings, "\"mode\":\"");
            string keys = ExtractString(settings, "\"keys\":\"");
            string seq = ExtractString(settings, "\"sequence\":\"");
            int releaseMs = ExtractInt(settings, "\"releaseMs\":", 10000);
            if (releaseMs < 500) releaseMs = 500;
            int cooldownMs = ExtractInt(settings, "\"cooldownMs\":", 0);
            if (cooldownMs < 0) cooldownMs = 0;

            // The app occasionally delivers an empty settings object for a
            // configured button. Fall back to the mirror instead of going dead.
            if (keys.Length == 0 && seq.Length == 0)
            {
                string[] mirror = SettingStore.Get(context);
                if (mirror != null)
                {
                    if (mode.Length == 0) mode = mirror[0];
                    keys = mirror[1];
                    seq = mirror[2];
                    int pm;
                    if (int.TryParse(mirror[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out pm) && pm >= 500)
                        releaseMs = pm;
                    if (int.TryParse(mirror[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out pm))
                        cooldownMs = pm;
                    Logger.Write("Empty settings from Stream Deck; using bridge mirror: keys=\"" + keys + "\"");
                }
            }
            else
            {
                SettingStore.Put(context, mode, keys, seq,
                    releaseMs.ToString(CultureInfo.InvariantCulture),
                    cooldownMs.ToString(CultureInfo.InvariantCulture), false);
            }

            if (down)
            {
                if (cooldownMs > 0 && IsCooling(context))
                {
                    Logger.Write("Deck press ignored (still cooling down): keys=\"" + keys + "\"");
                    return;
                }
                Logger.Write("Deck press [" + ShortId(context) + " slot " +
                    ExtractInt(message, "\"column\":", -1) + "," + ExtractInt(message, "\"row\":", -1) +
                    "]: mode=" + (mode.Length == 0 ? "tap" : mode) +
                    " keys=\"" + keys + "\" seq=\"" + (seq.Length > 60 ? seq.Substring(0, 60) : seq) + "\"" +
                    " cooldown=" + cooldownMs + "ms | target: " + TargetProbe.Describe());
                if (mode != "sequence" && keys.Length == 0)
                {
                    // Empty settings used to be silent, which made a wiped button
                    // look like a broken injector. Flash the key instead.
                    Logger.Write("No key configured for this button; nothing sent.");
                    ShowAlert(context);
                    return;
                }
                if (mode != "sequence" && keys.Length > 0)
                {
                    string bad;
                    if (!KeyMap.TryValidate(keys, out bad))
                    {
                        Logger.Write("Invalid key token '" + bad + "' - not sent.");
                        ShowAlert(context);
                        return;
                    }
                }
                if (mode == "hold")
                {
                    if (keys.Length == 0) return;
                    executor.Enqueue("{\"event\":\"down\",\"keys\":\"" + Escape(keys) + "\"}");
                    int gen;
                    lock (holdGate)
                    {
                        gen = ++holdCounter;
                        holds[context] = new HoldState { Keys = keys, Generation = gen };
                    }
                    Thread safety = new Thread(delegate ()
                    {
                        Thread.Sleep(releaseMs);
                        lock (holdGate)
                        {
                            HoldState h;
                            if (!holds.TryGetValue(context, out h) || h.Generation != gen) return;
                            holds.Remove(context);
                        }
                        Logger.Write("Hold exceeded safety window; force-releasing \"" + keys + "\"");
                        executor.Enqueue("{\"event\":\"up\",\"keys\":\"" + Escape(keys) + "\"}");
                    });
                    safety.IsBackground = true;
                    safety.Start();
                }
                else if (mode == "sequence")
                {
                    if (seq.Length == 0) return;
                    executor.Enqueue("{\"event\":\"sequence\",\"sequence\":\"" + Escape(seq) + "\"}");
                }
                else
                {
                    if (keys.Length == 0) return;
                    executor.Enqueue("{\"event\":\"tap\",\"keys\":\"" + Escape(keys) + "\"}");
                }

                if (cooldownMs > 0) BeginCooldown(context, device, cooldownMs);
            }
            else
            {
                HoldState h;
                lock (holdGate)
                {
                    if (!holds.TryGetValue(context, out h)) h = null;
                    else holds.Remove(context);
                }
                if (h != null)
                {
                    Logger.Write("Deck release: keys=\"" + h.Keys + "\"");
                    executor.Enqueue("{\"event\":\"up\",\"keys\":\"" + Escape(h.Keys) + "\"}");
                }
            }
        }

        // Minimal flat extractors for nested JSON (values are simple
        // string/number fields; good enough for our own settings shape).
        private static string ExtractString(string s, string marker)
        {
            int i = s.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return "";
            i += marker.Length;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i += 2; continue; }
                if (c == '"') break;
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static string ExtractObject(string s, string marker)
        {
            int i = s.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return "";
            i += marker.Length;
            if (i >= s.Length || s[i] != '{') return "";
            int depth = 0;
            int start = i;
            for (; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return s.Substring(start, i - start + 1);
                }
            }
            return "";
        }

        // ----- cooldown animation -------------------------------------------------
        //
        // Stream Deck has no animation support in setImage (GIF and friends are
        // refused), so a cooldown is a short film: the bridge pushes one SVG
        // frame per tick while the key recharges, then resets the key by sending
        // the command with the field omitted -- per the protocol, an omitted
        // "image"/"title" restores whatever the user had configured.

        private const string ActionUuid = "com.sundowner.keyinject.action";
        private const int CooldownFrameMs = 100;   // 10 fps: smooth enough, cheap

        private sealed class CooldownRun
        {
            public long EndTicks;
            public int Generation;
        }

        private static readonly Dictionary<string, CooldownRun> cooldowns =
            new Dictionary<string, CooldownRun>();
        private static readonly object cooldownGate = new object();
        private static int cooldownCounter;

        public static bool IsCooling(string context)
        {
            lock (cooldownGate)
            {
                CooldownRun r;
                if (!cooldowns.TryGetValue(context, out r)) return false;
                if (DateTime.UtcNow.Ticks >= r.EndTicks) { cooldowns.Remove(context); return false; }
                return true;
            }
        }

        public static void BeginCooldown(string context, string device, int durationMs)
        {
            if (context.Length == 0 || durationMs <= 0) return;
            int gen;
            long end;
            lock (cooldownGate)
            {
                gen = ++cooldownCounter;
                end = DateTime.UtcNow.Ticks + (long)durationMs * TimeSpan.TicksPerMillisecond;
                cooldowns[context] = new CooldownRun { EndTicks = end, Generation = gen };
            }
            Thread t = new Thread(delegate () { AnimateCooldown(context, device, durationMs, end, gen); });
            t.IsBackground = true;
            t.Start();
        }

        private static void AnimateCooldown(string context, string device, int durationMs, long endTicks, int gen)
        {
            try
            {
                AnimateLoop(context, device, durationMs, endTicks, gen);
            }
            catch (Exception ex)
            {
                Logger.Write("Animate crashed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void AnimateLoop(string context, string device, int durationMs, long endTicks, int gen)
        {
            double totalTicks = (double)durationMs * TimeSpan.TicksPerMillisecond;
            int lastArc = -1;
            int frameSent = 0;
            while (true)
            {
                long remaining = endTicks - DateTime.UtcNow.Ticks;
                if (remaining <= 0) break;
                lock (cooldownGate)
                {
                    CooldownRun r;
                    if (!cooldowns.TryGetValue(context, out r) || r.Generation != gen) return;
                }

                double frac = remaining / totalTicks;
                int arc = (int)Math.Round(frac * 100.0);

                if (arc != lastArc)
                {
                    lastArc = arc;
                    frameSent++;
                    string frame = RingImage(frac);
                    if (!SendToApp("{\"event\":\"setImage\",\"action\":\"" + ActionUuid +
                        "\",\"context\":\"" + Escape(context) + "\",\"device\":\"" + Escape(device) +
                        "\",\"payload\":{\"image\":\"" + frame + "\",\"state\":0}}"))
                    {
                        Logger.Write("Cooldown frames stopped: no app link.");
                        break;
                    }
                }
                Thread.Sleep(CooldownFrameMs);
            }

            lock (cooldownGate)
            {
                CooldownRun r;
                if (cooldowns.TryGetValue(context, out r) && r.Generation == gen) cooldowns.Remove(context);
            }

            // Undo the film. Omitting "image" resets the key to the artwork of
            // its current state; setState re-applies that artwork as well, so
            // the key cannot be left showing the empty ring.
            SendToApp("{\"event\":\"setState\",\"action\":\"" + ActionUuid +
                "\",\"context\":\"" + Escape(context) + "\",\"device\":\"" + Escape(device) +
                "\",\"payload\":{\"state\":0}}");
            SendToApp("{\"event\":\"setImage\",\"action\":\"" + ActionUuid +
                "\",\"context\":\"" + Escape(context) + "\",\"device\":\"" + Escape(device) +
                "\",\"payload\":{\"state\":0}}");
            // One line per cooldown cycle; the per-frame detail was pure
            // instrumentation and is not worth 10 lines a second in the log.
            Logger.Write("Cooldown finished: " + durationMs + " ms, " + frameSent +
                " frames, key reset.");
        }

        // Flash the red "not available" indicator on a key. Used when a press
        // cannot be honoured, so a mis-configured button is not silent.
        public static void ShowAlert(string context)
        {
            if (context.Length == 0) return;
            SendToApp("{\"event\":\"showAlert\",\"context\":\"" + Escape(context) + "\"}");
        }

        // First 8 chars of a context, enough to tell two instances of the same
        // action apart in the log.
        public static string ShortId(string context)
        {
            if (context.Length <= 8) return context.Length == 0 ? "none" : context;
            return context.Substring(0, 8);
        }

        // Stream Deck only accepts raster images here - an SVG data URI is
        // accepted by the socket and then silently ignored, which is why the
        // ring never appeared. So the frames are real PNGs, drawn with GDI+
        // and cached per remaining percent (101 possible frames).
        private static readonly Dictionary<int, string> ringFrames = new Dictionary<int, string>();
        private static readonly object ringGate = new object();

        private static string RingImage(double fractionRemaining)
        {
            double f = fractionRemaining < 0 ? 0 : (fractionRemaining > 1 ? 1 : fractionRemaining);
            int pct = (int)Math.Round(f * 100.0);
            lock (ringGate)
            {
                string cached;
                if (ringFrames.TryGetValue(pct, out cached)) return cached;
                string png = DrawRingPng(pct);
                ringFrames[pct] = png;
                return png;
            }
        }

        // A recharge arc that unwinds clockwise. Drawn at 144x144 so it stays
        // crisp on the larger devices too.
        private static string DrawRingPng(int pct)
        {
            const int size = 144;
            const float stroke = 14f;
            float pad = stroke / 2f + 3f;
            RectangleF ring = new RectangleF(pad, pad, size - 2f * pad, size - 2f * pad);

            using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    using (Pen track = new Pen(Color.FromArgb(255, 43, 43, 43), stroke))
                        g.DrawArc(track, ring, 0f, 360f);

                    Color live = pct > 25
                        ? Color.FromArgb(255, 0x4e, 0xc9, 0xff)
                        : Color.FromArgb(255, 0xff, 0xb0, 0x20);
                    using (Pen arc = new Pen(live, stroke))
                    {
                        arc.StartCap = LineCap.Round;
                        arc.EndCap = LineCap.Round;
                        float sweep = 360f * pct / 100f;
                        if (sweep >= 360f) g.DrawArc(arc, ring, 0f, 360f);
                        else if (sweep > 0.5f) g.DrawArc(arc, ring, -90f, sweep);
                    }

                    // Two bars in the middle: the key is resting, not ready.
                    using (SolidBrush glyph = new SolidBrush(Color.FromArgb(255, 236, 236, 236)))
                    {
                        g.FillRectangle(glyph, 51f, 52f, 15f, 40f);
                        g.FillRectangle(glyph, 78f, 52f, 15f, 40f);
                    }

                    using (MemoryStream ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
        }

        private static int ExtractInt(string s, string marker, int fallback)
        {
            int i = s.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return fallback;
            i += marker.Length;
            int value = 0, digits = 0;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9')
            {
                value = value * 10 + (s[i] - '0');
                i++;
                digits++;
            }
            return digits == 0 ? fallback : value;
        }

        internal static string Escape(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') { sb.Append('\\'); sb.Append(c); }
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') { /* drop control characters */ }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static string ReadFrame(NetworkStream stream)
        {
            int b0 = stream.ReadByte();
            if (b0 < 0) return null;
            int b1 = stream.ReadByte();
            if (b1 < 0) return null;
            long len = b1 & 0x7F;
            if (len == 126)
            {
                int hi = stream.ReadByte(), lo = stream.ReadByte();
                if (hi < 0 || lo < 0) return null;
                len = (hi << 8) | lo;
            }
            else if (len == 127)
            {
                len = 0;
                for (int n = 0; n < 8; n++)
                {
                    int q = stream.ReadByte();
                    if (q < 0) return null;
                    len = (len << 8) | (long)(uint)(q & 0xFF);
                }
            }
            if (len > 1048576) return null;
            bool masked = (b1 & 0x80) != 0;
            byte[] mask = null;
            if (masked)
            {
                mask = new byte[4];
                if (!ReadBytes(stream, mask, 4)) return null;
            }
            byte[] payload = new byte[len];
            if (len > 0 && !ReadBytes(stream, payload, (int)len)) return null;
            if (masked)
                for (int n = 0; n < payload.Length; n++) payload[n] ^= mask[n % 4];
            if ((b0 & 0x0F) == 0x8) return null; // close
            return Encoding.UTF8.GetString(payload);
        }

        private static bool ReadBytes(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        private static void Send(NetworkStream stream, string text, bool masked)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text);
            List<byte> frame = new List<byte>();
            frame.Add(0x81);
            byte[] mask = new byte[4];
            if (masked)
            {
                using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(mask);
                if (payload.Length < 126) frame.Add((byte)(0x80 | payload.Length));
                else
                {
                    frame.Add((byte)(0x80 | 126));
                    frame.Add((byte)(payload.Length >> 8));
                    frame.Add((byte)(payload.Length & 0xFF));
                }
                frame.AddRange(mask);
                for (int i = 0; i < payload.Length; i++) frame.Add((byte)(payload[i] ^ mask[i % 4]));
            }
            else
            {
                if (payload.Length < 126) frame.Add((byte)payload.Length);
                else
                {
                    frame.Add(126);
                    frame.Add((byte)(payload.Length >> 8));
                    frame.Add((byte)(payload.Length & 0xFF));
                }
                frame.AddRange(payload);
            }
            byte[] bytes = frame.ToArray();
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
    }

    // ---------------------------------------------------------------------
    // WebSocket server (RFC 6455 subset: text, close, ping/pong)
    // ---------------------------------------------------------------------
    internal sealed class WebSocketServer
    {
        private const string MagicGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly TcpListener listener;
        private readonly KeyExecutor executor;
        private readonly List<ClientLink> clients = new List<ClientLink>();
        private readonly object clientsGate = new object();
        private volatile bool running;

        public WebSocketServer(int port, KeyExecutor executor)
        {
            this.listener = new TcpListener(IPAddress.Loopback, port);
            this.executor = executor;
        }

        public void Start()
        {
            listener.Start();
            running = true;
            Thread t = new Thread(AcceptLoop);
            t.IsBackground = true;
            t.Start();
        }

        public void Stop()
        {
            running = false;
            try { listener.Stop(); } catch { }
        }

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient socket;
                try { socket = listener.AcceptTcpClient(); }
                catch { break; }
                Thread t = new Thread(delegate () { Handle(socket); });
                t.IsBackground = true;
                t.Start();
            }
        }

        private void Handle(TcpClient socket)
        {
            ClientLink link = null;
            try
            {
                socket.NoDelay = true;
                NetworkStream stream = socket.GetStream();

                // --- HTTP upgrade handshake ---
                StreamReader reader = new StreamReader(stream, Encoding.ASCII);
                string requestLine = reader.ReadLine();
                if (requestLine == null || !requestLine.StartsWith("GET")) { socket.Close(); return; }
                string key = null;
                string line;
                while ((line = reader.ReadLine()) != null && line.Length > 0)
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0 &&
                        string.Equals(line.Substring(0, colon).Trim(), "Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                        key = line.Substring(colon + 1).Trim();
                }
                if (key == null) { socket.Close(); return; }

                using (SHA1 sha1 = SHA1.Create())
                {
                    byte[] hash = sha1.ComputeHash(Encoding.ASCII.GetBytes(key + MagicGuid));
                    string accept = Convert.ToBase64String(hash);
                    string response = "HTTP/1.1 101 Switching Protocols\r\n" +
                                      "Upgrade: websocket\r\n" +
                                      "Connection: Upgrade\r\n" +
                                      "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
                    byte[] bytes = Encoding.ASCII.GetBytes(response);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }

                link = new ClientLink(this, socket, stream);
                lock (clientsGate) clients.Add(link);

                // --- frame loop ---
                bool closed = false;
                StringBuilder text = new StringBuilder();
                while (!closed)
                {
                    int b0 = stream.ReadByte();
                    if (b0 < 0) break;
                    int b1 = stream.ReadByte();
                    if (b1 < 0) break;

                    bool fin = (b0 & 0x80) != 0;
                    int opcode = b0 & 0x0F;
                    bool masked = (b1 & 0x80) != 0;
                    long len = b1 & 0x7F;
                    if (len == 126)
                    {
                        int hi = stream.ReadByte(), lo = stream.ReadByte();
                        if (hi < 0 || lo < 0) break;
                        len = (hi << 8) | lo;
                    }
                    else if (len == 127)
                    {
                        len = 0;
                        for (int n = 0; n < 8; n++)
                        {
                            int q = stream.ReadByte();
                            if (q < 0) return;
                            len = (len << 8) | (long)(uint)(q & 0xFF);
                        }
                    }
                    if (len > 131072) break; // oversized: drop the link

                    byte[] mask = null;
                    if (masked)
                    {
                        mask = new byte[4];
                        if (!ReadExact(stream, mask, 4)) break;
                    }
                    byte[] payload = new byte[len];
                    if (len > 0 && !ReadExact(stream, payload, (int)len)) break;
                    if (masked)
                        for (int n = 0; n < payload.Length; n++) payload[n] ^= mask[n % 4];

                    if (opcode == 0x8) // close
                    {
                        link.SendRaw(0x88, new byte[0]);
                        closed = true;
                    }
                    else if (opcode == 0x9) // ping -> pong
                    {
                        link.SendRaw(0x8A, payload);
                    }
                    else if (opcode == 0x1 || opcode == 0x2 || opcode == 0x0) // text/binary/continuation
                    {
                        text.Append(Encoding.UTF8.GetString(payload));
                        if (fin)
                        {
                            string message = text.ToString();
                            text.Length = 0;
                            Dispatch(link, message);
                        }
                    }
                    // ignore other opcodes
                }
            }
            catch (Exception ex)
            {
                Logger.Write("Connection error: " + ex.Message);
            }
            finally
            {
                if (link != null)
                {
                    lock (clientsGate) clients.Remove(link);
                    try { link.CloseSocket(); } catch { }
                }
                // A dropped Property Inspector may have left keys held down —
                // unless the hold belongs to a physical button press (those end
                // with their own keyUp event from the app).
                if (!StreamDeckClient.HasActiveHold)
                    executor.ReleaseAll();
            }
        }

        private void Dispatch(ClientLink link, string message)
        {
            try
            {
                JsonReader r = new JsonReader(message);
                r.Expect('{');
                string type = "";
                string note = "";
                string ctx = "";
                string dev = "";
                string ttl = "";
                string secs = "";
                string keysVal = "";
                string modeVal = "";
                string seqVal = "";
                string relVal = "";
                string cdVal = "";
                while (true)
                {
                    string name = r.ReadString();
                    r.Expect(':');
                    string value = r.ReadString();
                    if (name == "type") type = value;
                    if (name == "note") note = value;
                    if (name == "context") ctx = value;
                    if (name == "device") dev = value;
                    if (name == "title") ttl = value;
                    if (name == "seconds") secs = value;
                    if (name == "keys") keysVal = value;
                    if (name == "mode") modeVal = value;
                    if (name == "sequence") seqVal = value;
                    if (name == "releaseMs") relVal = value;
                    if (name == "cooldownMs") cdVal = value;
                    if (!r.TryEat(',')) break;
                }

                if (type == "pi")
                {
                    // Breadcrumb from a Property Inspector, for diagnostics.
                    Logger.Write("PI: " + note);
                }
                else if (type == "title")
                {
                    // Stream Deck refuses setTitle from a Property Inspector,
                    // so relay it as a proper plugin command over our session.
                    if (ctx.Length == 0)
                    {
                        Logger.Write("Title push ignored: no context given.");
                    }
                    else
                    {
                        bool pushed = StreamDeckClient.SendToApp(
                            "{\"event\":\"setTitle\",\"action\":\"com.sundowner.keyinject.action\"," +
                            "\"context\":\"" + StreamDeckClient.Escape(ctx) + "\"," +
                            "\"device\":\"" + StreamDeckClient.Escape(dev) + "\"," +
                            "\"payload\":{\"title\":\"" + StreamDeckClient.Escape(ttl) + "\",\"target\":\"action\"}}");
                        Logger.Write("Title push: \"" + (ttl.Length > 40 ? ttl.Substring(0, 40) : ttl) +
                            "\" " + (pushed ? "-> app" : "(no app link)"));
                    }
                }
                else if (type == "settings")
                {
                    // The inspector tells us what the button is supposed to do,
                    // so the plugin side has its own copy of the truth.
                    SettingStore.Put(ctx, modeVal, keysVal, seqVal, relVal, cdVal, true);
                    Logger.Write("Mirror [" + StreamDeckClient.ShortId(ctx) + "]: mode=" + modeVal +
                        " keys=\"" + keysVal + "\" cooldown=" + cdVal + "ms");
                    link.SendJson("{\"type\":\"ack\",\"ok\":true}");
                }
                else if (type == "validate")
                {
                    // The Property Inspector asks the bridge to check a token
                    // string, so there is one key map rather than two.
                    string bad = "";
                    bool ok = keysVal.Length == 0 || KeyMap.TryValidate(keysVal, out bad);
                    link.SendJson("{\"type\":\"validated\",\"ok\":" + (ok ? "true" : "false") +
                        ",\"bad\":\"" + Escape(bad) + "\"}");
                }
                else if (type == "cooldown")
                {
                    // Preview from the Property Inspector: animate the real
                    // button without sending any keystroke.
                    int seconds;
                    if (!int.TryParse(secs, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) ||
                        seconds < 1 || seconds > 600)
                    {
                        link.SendJson("{\"type\":\"ack\",\"ok\":false,\"error\":\"cooldown must be 1-600 seconds\"}");
                    }
                    else if (ctx.Length == 0)
                    {
                        link.SendJson("{\"type\":\"ack\",\"ok\":false,\"error\":\"no action context\"}");
                    }
                    else
                    {
                        StreamDeckClient.BeginCooldown(ctx, dev, seconds * 1000);
                        link.SendJson("{\"type\":\"ack\",\"ok\":true}");
                    }
                }
                else if (type == "key" || type == "sequence")
                {
                    string error;
                    if (!executor.TryValidate(message, out error))
                    {
                        link.SendJson("{\"type\":\"ack\",\"ok\":false,\"error\":\"" + Escape(error) + "\"}");
                    }
                    else
                    {
                        executor.Enqueue(message);
                        link.SendJson("{\"type\":\"ack\",\"ok\":true}");
                    }
                }
                else if (type == "releaseAll")
                {
                    executor.ReleaseAll();
                    link.SendJson("{\"type\":\"ack\",\"ok\":true}");
                }
                else if (type == "hello" || type == "ping")
                {
                    link.SendJson("{\"type\":\"hello\",\"name\":\"key-inject\",\"version\":\"1.0\",\"port\":" +
                        ((IPEndPoint)listener.LocalEndpoint).Port + "}");
                }
                // registration / unknown types: no reply needed
            }
            catch (Exception ex)
            {
                link.SendJson("{\"type\":\"ack\",\"ok\":false,\"error\":\"" + Escape(ex.Message) + "\"}");
            }
        }

        public void BroadcastJson(string json)
        {
            lock (clientsGate)
                foreach (ClientLink c in clients)
                    try { c.SendJson(json); } catch { }
        }

        private static bool ReadExact(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        private static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }

    internal sealed class ClientLink
    {
        private readonly WebSocketServer server;
        private readonly TcpClient socket;
        private readonly NetworkStream stream;
        private readonly object sendGate = new object();

        public ClientLink(WebSocketServer server, TcpClient socket, NetworkStream stream)
        {
            this.server = server;
            this.socket = socket;
            this.stream = stream;
        }

        public void SendJson(string json)
        {
            SendRaw(0x81, Encoding.UTF8.GetBytes(json));
        }

        public void SendRaw(byte opcode, byte[] payload)
        {
            List<byte> frame = new List<byte>();
            frame.Add(opcode); // FIN set, server frames are unmasked
            if (payload.Length < 126)
            {
                frame.Add((byte)payload.Length);
            }
            else if (payload.Length <= 0xFFFF)
            {
                frame.Add(126);
                frame.Add((byte)(payload.Length >> 8));
                frame.Add((byte)(payload.Length & 0xFF));
            }
            else
            {
                frame.Add(127);
                ulong len2 = (ulong)payload.Length;
                for (int n = 7; n >= 0; n--) frame.Add((byte)((len2 >> (n * 8)) & 0xFF));
            }
            frame.AddRange(payload);
            byte[] bytes = frame.ToArray();
            lock (sendGate)
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        }

        public void CloseSocket()
        {
            try { socket.Close(); } catch { }
        }
    }
}
