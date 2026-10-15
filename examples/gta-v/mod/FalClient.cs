using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NpcVoice
{
    /// <summary>
    /// One long-lived WebSocket session to the FishTTS /realtime endpoint (server/fal/fal_app.py), speaking
    /// the same protocol as fal_client.realtime: msgpack TTSInput out, msgpack AudioChunk back,
    /// JSON text frames for fal's own errors and meta messages.
    /// </summary>
    internal sealed class FalRealtimeClient : IDisposable
    {
        private const string Host = "fal.run";
        private readonly string _path;
        private readonly string _auth;
        private RawWebSocket _ws;

        public FalRealtimeClient(string appId, string apiKey, string path)
        {
            _path = $"/{appId.Trim('/')}/{path.Trim('/')}";
            _auth = "Key " + apiKey;
        }

        public bool IsConnected => _ws != null && _ws.IsOpen;

        public Task ConnectAsync(CancellationToken ct)
        {
            return Task.Run(() =>
            {
                Reset();
                _ws = RawWebSocket.Connect(Host, _path, _auth);
            }, ct);
        }

        /// <summary>Send one line; onAudio gets each PCM chunk (16-bit mono) until the server sends done.</summary>
        public async Task SpeakAsync(string text, Voice voice, Action<byte[], int> onAudio, CancellationToken ct)
        {
            if (!IsConnected) await ConnectAsync(ct).ConfigureAwait(false);
            RawWebSocket ws = _ws ?? throw new IOException("not connected to fal");

            byte[] payload = MsgPack.EncodeStringMap(new Dictionary<string, string>
            {
                ["text"] = text,
                ["reference_audio_url"] = voice.Url,
                ["reference_text"] = voice.RefText,
            });

            using (ct.Register(Reset)) // blocking reads end when the socket is closed
            {
                await Task.Run(() =>
                {
                    ws.Send(payload, RawWebSocket.Binary);
                    while (true)
                    {
                        var (op, data) = ws.Receive();
                        if (op == RawWebSocket.Close)
                            throw new IOException("fal closed the realtime session");

                        if (op == RawWebSocket.Text)
                        {
                            CheckTextFrame(Encoding.UTF8.GetString(data));
                            continue;
                        }

                        if (!(MsgPack.Decode(data) is Dictionary<string, object> chunk)) continue;

                        int rate = chunk.TryGetValue("sample_rate", out var r) && r is long l ? (int)l : 44100;
                        if (chunk.TryGetValue("audio", out var a) && a is byte[] pcm && pcm.Length > 0)
                            onAudio(pcm, rate);
                        if (chunk.TryGetValue("done", out var d) && d is bool done && done)
                            return;
                    }
                }).ConfigureAwait(false);
            }
        }

        public void Reset()
        {
            var ws = _ws;
            _ws = null;
            ws?.Dispose();
        }

        public void Dispose() => Reset();

        private static void CheckTextFrame(string json)
        {
            Dictionary<string, object> obj;
            try { obj = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>; }
            catch { return; }
            if (obj == null) return;

            obj.TryGetValue("type", out var type);
            if ("x-fal-error".Equals(type))
            {
                obj.TryGetValue("error", out var error);
                obj.TryGetValue("reason", out var reason);
                throw new IOException($"fal: {error} {reason}".Trim());
            }
            // x-fal-message and anything else are meta messages: ignore.
        }
    }

    /// <summary>
    /// Minimal RFC 6455 client over TLS. .NET Framework's ClientWebSocket rejects fal's stream
    /// ("Invalid data format for the specific protocol operation"), so frames are handled here:
    /// masked client frames, fragmentation, automatic pong for fal's keep-alive pings.
    /// Blocking calls; FalRealtimeClient runs them on a worker thread.
    /// </summary>
    internal sealed class RawWebSocket : IDisposable
    {
        public const int Text = 1, Binary = 2, Close = 8, Ping = 9, Pong = 10;

        private readonly object _sendLock = new object();
        private readonly Random _rng = new Random();
        private TcpClient _tcp;
        private SslStream _ssl;
        private volatile bool _open;

        public bool IsOpen => _open;

        public static RawWebSocket Connect(string host, string path, string authorization)
        {
            var ws = new RawWebSocket();
            try
            {
                // fal sends a ping every ~5 s while a cold runner boots, so a long read timeout is safe.
                ws._tcp = new TcpClient { ReceiveTimeout = 300000, SendTimeout = 30000, NoDelay = true };
                ws._tcp.Connect(host, 443);
                ws._ssl = new SslStream(ws._tcp.GetStream());
                ws._ssl.AuthenticateAsClient(host, null, SslProtocols.Tls12, false);

                var keyBytes = new byte[16];
                ws._rng.NextBytes(keyBytes);
                string request =
                    $"GET {path} HTTP/1.1\r\nHost: {host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                    $"Sec-WebSocket-Key: {Convert.ToBase64String(keyBytes)}\r\nSec-WebSocket-Version: 13\r\n" +
                    $"Authorization: {authorization}\r\nUser-Agent: NpcVoice/1.0\r\n\r\n";
                byte[] req = Encoding.ASCII.GetBytes(request);
                ws._ssl.Write(req, 0, req.Length);
                ws._ssl.Flush();

                var header = new StringBuilder();
                while (!header.ToString().EndsWith("\r\n\r\n"))
                {
                    int b = ws._ssl.ReadByte();
                    if (b < 0 || header.Length > 32768) throw new IOException("fal closed the connection during the handshake");
                    header.Append((char)b);
                }
                string status = header.ToString().Split('\r')[0];
                if (!status.Contains(" 101 ")) throw new IOException("fal realtime handshake failed: " + status);

                ws._open = true;
                return ws;
            }
            catch
            {
                ws.Dispose();
                throw;
            }
        }

        public void Send(byte[] payload, int opcode)
        {
            var frame = new MemoryStream(payload.Length + 14);
            frame.WriteByte((byte)(0x80 | opcode));
            if (payload.Length < 126) frame.WriteByte((byte)(0x80 | payload.Length));
            else if (payload.Length < 65536)
            {
                frame.WriteByte(0x80 | 126);
                frame.WriteByte((byte)(payload.Length >> 8));
                frame.WriteByte((byte)payload.Length);
            }
            else
            {
                frame.WriteByte(0x80 | 127);
                for (int i = 7; i >= 0; i--) frame.WriteByte((byte)((long)payload.Length >> (8 * i)));
            }

            var mask = new byte[4];
            lock (_rng) _rng.NextBytes(mask);
            frame.Write(mask, 0, 4);
            for (int i = 0; i < payload.Length; i++) frame.WriteByte((byte)(payload[i] ^ mask[i & 3]));

            lock (_sendLock)
            {
                _ssl.Write(frame.GetBuffer(), 0, (int)frame.Length);
                _ssl.Flush();
            }
        }

        /// <summary>Next complete data message (Text/Binary) or Close. Pings are answered inline.</summary>
        public (int, byte[]) Receive()
        {
            var message = new MemoryStream();
            int messageOp = Binary;
            while (true)
            {
                byte[] h = ReadExact(2);
                bool fin = (h[0] & 0x80) != 0;
                int op = h[0] & 0x0F;
                bool masked = (h[1] & 0x80) != 0;
                long len = h[1] & 0x7F;
                if (len == 126) { byte[] e = ReadExact(2); len = (e[0] << 8) | e[1]; }
                else if (len == 127) { byte[] e = ReadExact(8); len = 0; foreach (byte x in e) len = (len << 8) | x; }
                byte[] mask = masked ? ReadExact(4) : null;
                byte[] data = ReadExact((int)len);
                if (mask != null) for (int i = 0; i < data.Length; i++) data[i] ^= mask[i & 3];

                switch (op)
                {
                    case Close:
                        _open = false;
                        try { Send(data, Close); } catch { }
                        return (Close, data);
                    case Ping:
                        Send(data, Pong);
                        continue;
                    case Pong:
                        continue;
                }

                if (op != 0) messageOp = op; // 0 = continuation of a fragmented message
                message.Write(data, 0, data.Length);
                if (fin) return (messageOp, message.ToArray());
            }
        }

        public void Dispose()
        {
            _open = false;
            try { _ssl?.Dispose(); } catch { }
            try { _tcp?.Close(); } catch { }
        }

        private byte[] ReadExact(int n)
        {
            var buf = new byte[n];
            int off = 0;
            while (off < n)
            {
                int r = _ssl.Read(buf, off, n - off);
                if (r <= 0)
                {
                    _open = false;
                    throw new IOException("fal closed the realtime connection");
                }
                off += r;
            }
            return buf;
        }
    }
}
