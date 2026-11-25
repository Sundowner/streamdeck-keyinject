// PiProbe: talk to the Key Inject bridge's own WebSocket server (127.0.0.1:12345)
// exactly like a Property Inspector does, so a cooldown cycle can be started on a
// real key without touching the game.
//
//   PiProbe.exe <context> <device> [seconds]
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

internal static class PiProbe
{
    static TcpClient client;
    static NetworkStream stream;

    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: PiProbe <context> <device> [seconds]");
            return 2;
        }
        string ctx = args[0], dev = args[1];
        string secs = args.Length > 2 ? args[2] : "4";

        client = new TcpClient();
        client.Connect("127.0.0.1", 12345);
        stream = client.GetStream();

        byte[] nonce = new byte[16];
        using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(nonce);
        string key = Convert.ToBase64String(nonce);
        string req = "GET / HTTP/1.1\r\nHost: 127.0.0.1:12345\r\nUpgrade: websocket\r\n" +
            "Connection: Upgrade\r\nSec-WebSocket-Key: " + key + "\r\nSec-WebSocket-Version: 13\r\n\r\n";
        byte[] rb = Encoding.ASCII.GetBytes(req);
        stream.Write(rb, 0, rb.Length);
        stream.Flush();

        StreamReader reader = new StreamReader(stream, Encoding.ASCII);
        string status = reader.ReadLine();
        if (status == null || !status.Contains("101"))
        {
            Console.WriteLine("handshake refused: " + status);
            return 1;
        }
        string line;
        while ((line = reader.ReadLine()) != null && line.Length > 0) { }
        Console.WriteLine("connected to bridge");

        // raw mode: send a verbatim PI command ("raw" then the JSON, or
        // "@file.json" to read it from a file, with {CTX}/{DEV} substituted).
        if (args.Length > 2 && args[2] == "raw")
        {
            string body = args.Length > 3 ? args[3] : "{}";
            if (body.StartsWith("@")) body = File.ReadAllText(body.Substring(1));
            body = body.Replace("{CTX}", ctx).Replace("{DEV}", dev).Replace("\n", " ").Trim();
            Send(body);
            Console.WriteLine("sent: " + body);
            for (int i = 0; i < 3; i++)
            {
                string r = Read();
                if (r == null) break;
                Console.WriteLine("reply: " + r);
            }
            client.Close();
            return 0;
        }

        string msg = "{\"type\":\"cooldown\",\"seconds\":\"" + secs +
            "\",\"context\":\"" + ctx + "\",\"device\":\"" + dev + "\",\"action\":\"com.sundowner.keyinject.action\"}";
        Send(msg);
        Console.WriteLine("sent: " + msg);

        for (int i = 0; i < 4; i++)
        {
            string reply = Read();
            if (reply == null) break;
            Console.WriteLine("reply: " + reply);
        }
        // Stay alive until the animation would have finished, so the bridge is
        // not left with a dead client mid-flight.
        int hold = (int.Parse(secs) + 1) * 1000;
        Thread.Sleep(hold);
        client.Close();
        return 0;
    }

    static void Send(string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        List<byte> frame = new List<byte>();
        frame.Add(0x81);
        byte[] mask = new byte[4];
        using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(mask);
        if (payload.Length < 126) frame.Add((byte)(0x80 | payload.Length));
        else if (payload.Length <= 0xFFFF)
        {
            frame.Add((byte)(0x80 | 126));
            frame.Add((byte)(payload.Length >> 8));
            frame.Add((byte)(payload.Length & 0xFF));
        }
        else
        {
            frame.Add((byte)(0x80 | 127));
            for (int shift = 56; shift >= 0; shift -= 8) frame.Add((byte)(payload.Length >> shift));
        }
        frame.AddRange(mask);
        for (int i = 0; i < payload.Length; i++) frame.Add((byte)(payload[i] ^ mask[i % 4]));
        byte[] bytes = frame.ToArray();
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    static string Read()
    {
        int b0 = stream.ReadByte();
        if (b0 < 0) return null;
        int b1 = stream.ReadByte();
        if (b1 < 0) return null;
        long len = b1 & 0x7F;
        if (len == 126)
        {
            int hi = stream.ReadByte(), lo = stream.ReadByte();
            len = (hi << 8) | lo;
        }
        else if (len == 127)
        {
            len = 0;
            for (int i = 0; i < 8; i++) { int q = stream.ReadByte(); if (q < 0) return null; len = (len << 8) | q; }
        }
        StringBuilder sb = new StringBuilder();
        for (long i = 0; i < len; i++) { int c = stream.ReadByte(); if (c < 0) return null; sb.Append((char)c); }
        return sb.ToString();
    }
}
