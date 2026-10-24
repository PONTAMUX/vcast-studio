using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VCast.Obs;

namespace VCast.Core;

// obs-websocket v5 互換の外部操作サーバー（Stream Deck / Touch Portal / OBS 用リモコンアプリなどから操作できる）
// 管理者権限なしで LAN にも出せるよう、TcpListener で HTTP の Upgrade を自前で処理して WebSocket にする
public sealed class RemoteServer : IDisposable
{
    public delegate (bool ok, int code, string? comment, JsonObject? data) Handler(string requestType, JsonObject? data);

    const int MaxConnections = 16, MaxPerIp = 6, MaxBatch = 50, MaxRequestsPerSecond = 100;
    static readonly TimeSpan HeaderDeadline = TimeSpan.FromSeconds(5), IdentifyDeadline = TimeSpan.FromSeconds(10), SendDeadline = TimeSpan.FromSeconds(5);

    readonly TcpListener _listener;
    readonly string _password;
    readonly Handler _handle;
    readonly CancellationTokenSource _cts = new();
    readonly ConcurrentDictionary<Client, byte> _clients = new();
    readonly ConcurrentDictionary<TcpClient, IPAddress> _tcps = new(); // 受け付けた全 TCP 接続（認証前のものも）
    public int Port { get; }
    public bool Lan { get; }
    public int ClientCount => _clients.Count(c => c.Key.Identified);

    // パスワードは必須（空なら起動しない）。LAN に出すときも、この PC の中だけのときも同じ
    public RemoteServer(int port, bool lan, string password, Handler handle)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("パスワードが必要です", nameof(password));
        Port = port; Lan = lan; _password = password; _handle = handle;
        _listener = new TcpListener(lan ? IPAddress.Any : IPAddress.Loopback, port);
        _listener.Start(); // 使用中なら SocketException（呼び出し側で案内）
        _ = Task.Run(AcceptLoop);
        ObsEngine.Log($"[remote] listening on {(lan ? "0.0.0.0" : "127.0.0.1")}:{port}");
    }

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            var ip = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
            // 全体と IP ごとの上限（認証前の接続も数える）。超えたら即切断
            if (_tcps.Count >= MaxConnections || _tcps.Values.Count(x => x.Equals(ip)) >= MaxPerIp) { try { tcp.Close(); } catch { } continue; }
            _tcps[tcp] = ip;
            _ = Task.Run(() => Serve(tcp));
        }
    }

    async Task Serve(TcpClient tcp)
    {
        Client? client = null;
        try
        {
            tcp.NoDelay = true;
            var stream = tcp.GetStream();
            // --- HTTP Upgrade（期限つき。ヘッダの検証も行う） ---
            string? header;
            using (var hcts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token)) { hcts.CancelAfter(HeaderDeadline); header = await ReadHeader(stream, hcts.Token); }
            if (header == null) return;
            var lines = header.Split("\r\n");
            string? H(string name) => lines.Skip(1).Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)).Select(p => p[1].Trim()).FirstOrDefault();
            var key = H("Sec-WebSocket-Key");
            var ok = lines[0].StartsWith("GET ") && lines[0].EndsWith(" HTTP/1.1") && (H("Upgrade")?.Equals("websocket", StringComparison.OrdinalIgnoreCase) ?? false)
                && (H("Connection")?.Contains("Upgrade", StringComparison.OrdinalIgnoreCase) ?? false) && H("Sec-WebSocket-Version") == "13" && IsValidKey(key);
            // ブラウザ（Origin あり）は、この PC 内のページ以外を拒否。Stream Deck など専用アプリは Origin を送らないので通る。Origin は認証の代わりにはならない
            var origin = H("Origin");
            if (origin != null && !IsLocalOrigin(origin)) ok = false;
            if (!ok) { await WriteWithDeadline(stream, Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")); return; }
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var protocols = H("Sec-WebSocket-Protocol") ?? "";
            var proto = protocols.Split(',').Select(p => p.Trim()).Contains("obswebsocket.json") ? "obswebsocket.json" : null;
            var resp = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" + $"Sec-WebSocket-Accept: {accept}\r\n" + (proto != null ? $"Sec-WebSocket-Protocol: {proto}\r\n" : "") + "\r\n";
            await WriteWithDeadline(stream, Encoding.ASCII.GetBytes(resp));

            using var ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: proto, keepAliveInterval: TimeSpan.FromSeconds(30));
            client = new Client(ws, tcp);
            _clients[client] = 0;
            // --- Hello ---
            var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await client.Send(0, new JsonObject { ["obsWebSocketVersion"] = "5.5.0", ["rpcVersion"] = 1, ["authentication"] = new JsonObject { ["challenge"] = challenge, ["salt"] = salt } });

            var buf = new byte[1 << 16];
            using var msg = new MemoryStream();
            var identifyBy = DateTime.UtcNow + IdentifyDeadline;
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                // 認証が済むまでは期限つきで待つ（黙って居座る接続を切る）
                using var rcts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                if (!client.Identified) { var left = identifyBy - DateTime.UtcNow; rcts.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.Zero); }
                var r = await ws.ReceiveAsync(buf, rcts.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (r.MessageType == WebSocketMessageType.Binary) { await client.Close(4002, "This server only accepts JSON text messages."); break; }
                if (msg.Length + r.Count > 1 << 20) break; // 1MB を超えるメッセージは受けない
                msg.Write(buf, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length);
                msg.SetLength(0);
                if (!await OnMessage(client, text, challenge, salt)) break;
            }
        }
        catch { }
        finally
        {
            if (client != null) _clients.TryRemove(client, out _);
            _tcps.TryRemove(tcp, out _);
            try { tcp.Close(); } catch { }
        }
    }

    static bool IsValidKey(string? key)
    {
        if (key == null) return false;
        try { return Convert.FromBase64String(key).Length == 16; } catch { return false; }
    }

    static bool IsLocalOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u) && (u.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1");

    static async Task WriteWithDeadline(Stream s, byte[] data)
    {
        using var c = new CancellationTokenSource(SendDeadline);
        await s.WriteAsync(data, c.Token);
    }

    // false を返すと切断
    async Task<bool> OnMessage(Client c, string text, string? challenge, string? salt)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(text); } catch { await c.Close(4002, "DecodeError"); return false; }
        var op = root?["op"]?.GetValue<int>() ?? -1;
        var d = root?["d"] as JsonObject;
        switch (op)
        {
            case 1: // Identify
            {
                if (c.Identified) { await c.Close(4008, "You are already Identified with the obs-websocket server."); return false; }
                var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(_password + salt)));
                var expected = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
                var given = d?["authentication"]?.GetValue<string>() ?? "";
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(expected)))
                { await c.Close(4009, "Authentication failed."); return false; }
                c.Identified = true;
                c.Subscriptions = d?["eventSubscriptions"]?.GetValue<int>() ?? 0x7FF; // 既定は全部（高頻度のものは送らない）
                await c.Send(2, new JsonObject { ["negotiatedRpcVersion"] = 1 });
                return true;
            }
            case 3: // Reidentify
                if (!c.Identified) { await c.Close(4007, "You have not been Identified."); return false; }
                c.Subscriptions = d?["eventSubscriptions"]?.GetValue<int>() ?? c.Subscriptions;
                await c.Send(2, new JsonObject { ["negotiatedRpcVersion"] = 1 });
                return true;
            case 6: // Request
            {
                if (!c.Identified) { await c.Close(4007, "Not identified."); return false; }
                if (!c.AllowRequest(MaxRequestsPerSecond)) { await c.Close(4011, "Too many requests."); return false; }
                var type = d?["requestType"]?.GetValue<string>() ?? "";
                var id = d?["requestId"]?.DeepClone();
                var (ok, code, comment, data) = Invoke(type, d?["requestData"] as JsonObject);
                var status = new JsonObject { ["result"] = ok, ["code"] = code };
                if (comment != null) status["comment"] = comment;
                var resp = new JsonObject { ["requestType"] = type, ["requestId"] = id, ["requestStatus"] = status };
                if (data != null) resp["responseData"] = data;
                await c.Send(7, resp);
                return true;
            }
            case 8: // RequestBatch（順番に実行するだけ）
            {
                if (!c.Identified) { await c.Close(4007, "Not identified."); return false; }
                if (!c.AllowRequest(MaxRequestsPerSecond)) { await c.Close(4011, "Too many requests."); return false; }
                var results = new JsonArray();
                var reqs = d?["requests"] as JsonArray ?? new JsonArray();
                if (reqs.Count > MaxBatch) { await c.Close(4002, $"Batch too large (max {MaxBatch})."); return false; }
                foreach (var req in reqs)
                {
                    var type = req?["requestType"]?.GetValue<string>() ?? "";
                    var (ok, code, comment, data) = Invoke(type, req?["requestData"] as JsonObject);
                    var st = new JsonObject { ["result"] = ok, ["code"] = code };
                    if (comment != null) st["comment"] = comment;
                    var item = new JsonObject { ["requestType"] = type, ["requestId"] = req?["requestId"]?.DeepClone(), ["requestStatus"] = st };
                    if (data != null) item["responseData"] = data;
                    results.Add(item);
                }
                await c.Send(9, new JsonObject { ["requestId"] = d?["requestId"]?.DeepClone(), ["results"] = results });
                return true;
            }
            default:
                await c.Close(4004, "UnknownOpCode");
                return false;
        }
    }

    // 操作は UI スレッドで実行（libobs とアプリの状態を触るため）
    (bool, int, string?, JsonObject?) Invoke(string type, JsonObject? data)
    {
        try
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp == null) return (false, 207, "shutting down", null);
            return ((bool, int, string?, JsonObject?))disp.Invoke(() => _handle(type, data), TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) { ObsEngine.Log("[remote] " + type + ": " + ex.Message); return (false, 702, ex.Message, null); }
    }

    // イベント（購読カテゴリのビットと一致するクライアントへ）
    public void Emit(string eventType, int intent, JsonObject? data = null)
    {
        foreach (var c in _clients.Keys)
        {
            if (!c.Identified || (c.Subscriptions & intent) == 0) continue;
            var ev = new JsonObject { ["eventType"] = eventType, ["eventIntent"] = intent };
            if (data != null) ev["eventData"] = data.DeepClone();
            _ = c.Send(5, ev);
        }
    }

    public const int IntentGeneral = 1, IntentConfig = 2, IntentScenes = 4, IntentInputs = 8, IntentTransitions = 16, IntentFilters = 32, IntentOutputs = 64, IntentSceneItems = 128, IntentMediaInputs = 256, IntentVendors = 512, IntentUi = 1024;

    static async Task<string?> ReadHeader(NetworkStream s, CancellationToken ct)
    {
        var buf = new List<byte>(1024);
        var one = new byte[1];
        while (buf.Count < 8192)
        {
            if (await s.ReadAsync(one, ct) <= 0) return null;
            buf.Add(one[0]);
            if (buf.Count >= 4 && buf[^4] == '\r' && buf[^3] == '\n' && buf[^2] == '\r' && buf[^1] == '\n') return Encoding.ASCII.GetString(buf.ToArray());
        }
        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        foreach (var t in _tcps.Keys) { try { t.Close(); } catch { } } // 認証前の接続も含めて全部閉じる
        foreach (var c in _clients.Keys) { try { c.Socket.Abort(); } catch { } }
    }

    sealed class Client
    {
        public WebSocket Socket { get; }
        readonly TcpClient _tcp;
        public bool Identified;
        public int Subscriptions;
        readonly SemaphoreSlim _gate = new(1, 1);
        int _pending; // 送信待ちの件数（遅い受信者に溜め込まない）
        long _windowStart; int _windowCount;
        public Client(WebSocket ws, TcpClient tcp) { Socket = ws; _tcp = tcp; }

        public bool AllowRequest(int perSecond)
        {
            var now = Environment.TickCount64;
            if (now - _windowStart > 1000) { _windowStart = now; _windowCount = 0; }
            return ++_windowCount <= perSecond;
        }

        public async Task Send(int op, JsonObject d)
        {
            // 送信が詰まっている（相手が受け取らない）クライアントは切る
            if (Interlocked.Increment(ref _pending) > 200) { Interlocked.Decrement(ref _pending); try { _tcp.Close(); } catch { } return; }
            var bytes = Encoding.UTF8.GetBytes(new JsonObject { ["op"] = op, ["d"] = d }.ToJsonString());
            try
            {
                await _gate.WaitAsync();
                try
                {
                    if (Socket.State != WebSocketState.Open) return;
                    using var c = new CancellationTokenSource(SendDeadline);
                    await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, c.Token);
                }
                catch { try { _tcp.Close(); } catch { } } // 送信期限切れ・切断
                finally { _gate.Release(); }
            }
            finally { Interlocked.Decrement(ref _pending); }
        }

        public async Task Close(int code, string reason)
        {
            try
            {
                using var c = new CancellationTokenSource(SendDeadline);
                await Socket.CloseAsync((WebSocketCloseStatus)code, reason, c.Token);
            }
            catch { try { _tcp.Close(); } catch { } }
        }
    }
}
