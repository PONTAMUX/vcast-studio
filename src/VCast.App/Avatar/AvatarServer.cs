using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VCast.Avatar;

// localhost 専用の小さなサーバー。アバター描画ページ・トラッカーページを配信し、WebSocket で中継する。
//   /                         Avatar/www 以下の静的ファイル（ライブラリなど。秘密は含まない）
//   /model.vrm?t=TOKEN        いま選ばれている VRM ファイル
//   /ws?role=render&t=TOKEN   描画ページ（OBS のブラウザソース）
//   /ws?role=tracker&t=TOKEN  顔トラッキングページ（隠し WebView2）
// TOKEN は起動ごとのランダム値。同じ PC の他プログラムがマイク音量や入力状態を盗み見たり、偽データを送り込んだりできないようにする
public sealed class AvatarServer : IDisposable
{
    const int MaxMessageBytes = 256 * 1024, MaxClients = 16;
    static readonly Dictionary<string, HashSet<string>> AllowedFromRole = new()
    {
        ["render"] = new() { "hello", "model-status", "log" },
        ["tracker"] = new() { "hello", "face", "cameras", "tracker-status" },
    };

    HttpListener _http = new();
    readonly string _root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Avatar", "www")) + Path.DirectorySeparatorChar;
    readonly ConcurrentDictionary<Client, byte> _clients = new();
    readonly CancellationTokenSource _cts = new();
    public int Port { get; }
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    public string? ModelPath { get; set; }
    public event Action<string, string, JsonElement>? Message; // (role, type, root) 許可済みメッセージのみ
    public event Action<Client>? ClientConnected;
    // 描画ページへ送るメッセージの写し（ネイティブ描画が同じ入力を受け取る）。(type, json)
    public event Action<string, string>? Tap;
    public bool HasTap => Tap != null;

    public AvatarServer(int preferredPort = 18480)
    {
        // 使えるポートを探す（localhost は管理者権限なしで待ち受けできる）
        // 開けなかった HttpListener は自動で破棄されるので、試すたびに作り直す（VCast をもう 1 つ起動しているときなど）
        for (var port = preferredPort; port < preferredPort + 50; port++)
        {
            var listener = new HttpListener();
            try
            {
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();
                _http = listener;
                Port = port;
                break;
            }
            catch (HttpListenerException) { try { listener.Close(); } catch { } }
            catch (ObjectDisposedException) { }
        }
        if (!_http.IsListening) throw new InvalidOperationException("アバター用のローカルポートを開けませんでした");
        _ = Task.Run(AcceptLoop);
    }

    public string Origin => $"http://localhost:{Port}";
    public string BaseUrl => Origin + "/";
    public string PageUrl(string page, string extraQuery = "") => $"{BaseUrl}{page}?t={Token}{extraQuery}";
    public int Count(string role) => _clients.Keys.Count(c => c.Role == role);

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    bool TokenOk(HttpListenerRequest r) => CryptographicOperations.FixedTimeEquals(
        Encoding.ASCII.GetBytes(r.QueryString["t"] ?? ""), Encoding.ASCII.GetBytes(Token));

    async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            if (!IPAddress.IsLoopback(req.RemoteEndPoint.Address)) { Deny(ctx, 403); return; }
            var path = req.Url!.AbsolutePath;
            if (path == "/ws")
            {
                // ブラウザから来る場合は自分のページ以外（他サイト）からの接続を拒否
                var origin = req.Headers["Origin"];
                if (!req.IsWebSocketRequest || !TokenOk(req) || (origin != null && origin != Origin) || _clients.Count >= MaxClients) { Deny(ctx, 403); return; }
                await HandleSocket(ctx);
                return;
            }
            if (path == "/model.vrm") { if (!TokenOk(req)) { Deny(ctx, 403); return; } await ServeFile(ctx, ModelPath, "application/octet-stream"); return; }
            if (path == "/") path = "/avatar.html";
            var full = Path.GetFullPath(Path.Combine(_root, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) { Deny(ctx, 403); return; }
            await ServeFile(ctx, full, ContentType(full));
        }
        catch (Exception ex)
        {
            Obs.ObsEngine.Log("[avatar-http] " + ex.Message);
            try { ctx.Response.Abort(); } catch { }
        }
    }

    static void Deny(HttpListenerContext ctx, int code) { ctx.Response.StatusCode = code; ctx.Response.Close(); }

    static async Task ServeFile(HttpListenerContext ctx, string? path, string type)
    {
        if (path == null || !File.Exists(path)) { Deny(ctx, 404); return; }
        ctx.Response.ContentType = type;
        ctx.Response.AddHeader("Cache-Control", "no-cache");
        await using var fs = File.OpenRead(path);
        ctx.Response.ContentLength64 = fs.Length;
        await fs.CopyToAsync(ctx.Response.OutputStream);
        ctx.Response.Close();
    }

    static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".wasm" => "application/wasm",
        ".json" => "application/json",
        ".css" => "text/css",
        _ => "application/octet-stream",
    };

    async Task HandleSocket(HttpListenerContext ctx)
    {
        var role = ctx.Request.QueryString["role"] == "tracker" ? "tracker" : "render";
        var wsCtx = await ctx.AcceptWebSocketAsync(null);
        var client = new Client(wsCtx.WebSocket, role, _cts.Token);
        _clients[client] = 0;
        try
        {
            try { ClientConnected?.Invoke(client); } catch (Exception ex) { Obs.ObsEngine.Log("[avatar-ws] connected handler: " + ex.Message); }
            var buf = new byte[1 << 16];
            using var msg = new MemoryStream();
            while (client.Socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var r = await client.Socket.ReceiveAsync(buf, _cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (msg.Length + r.Count > MaxMessageBytes) { Obs.ObsEngine.Log("[avatar-ws] message too large, closing"); break; }
                msg.Write(buf, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length); // 全体を組み立ててからデコード
                msg.SetLength(0);
                Dispatch(role, text);
            }
        }
        catch { }
        finally
        {
            _clients.TryRemove(client, out _);
            client.Dispose();
        }
    }

    // ロールごとに許可された種類だけ受け付ける
    void Dispatch(string role, string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("type", out var t)) return;
            var type = t.GetString() ?? "";
            if (!AllowedFromRole[role].Contains(type)) return;
            if (type == "face") { Broadcast("render", "face", text); return; } // 顔データは描画ページへそのまま中継（最新だけ）
            Message?.Invoke(role, type, doc.RootElement.Clone());
        }
        catch (JsonException) { }
    }

    // 指定ロールの全クライアントへ。reliable=false は高頻度データ（種類ごとに最新値だけ残す）
    public void Broadcast(string role, string type, string json, bool reliable = false)
    {
        if (role == "render") { try { Tap?.Invoke(type, json); } catch { } }
        foreach (var c in _clients.Keys) if (c.Role == role) c.Enqueue(type, json, reliable);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _http.Stop(); _http.Close(); } catch { }
        foreach (var c in _clients.Keys) c.Dispose();
        _clients.Clear();
    }

    // 接続ごとに送信ループを1本。制御メッセージは順番どおり(FIFO)、高頻度データは種類ごとに最新だけ
    public sealed class Client : IDisposable
    {
        public WebSocket Socket { get; }
        public string Role { get; }
        readonly ConcurrentQueue<string> _reliable = new();
        readonly ConcurrentDictionary<string, string> _latest = new();
        readonly SemaphoreSlim _signal = new(0);
        readonly CancellationTokenSource _cts;

        public Client(WebSocket socket, string role, CancellationToken parent)
        {
            Socket = socket; Role = role;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
            _ = Task.Run(SendLoop);
        }

        public void Enqueue(string type, string json, bool reliable)
        {
            if (reliable) { if (_reliable.Count < 256) _reliable.Enqueue(json); }
            else _latest[type] = json;
            _signal.Release();
        }

        async Task SendLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await _signal.WaitAsync(_cts.Token);
                    while (_reliable.TryDequeue(out var m)) await Send(m);
                    foreach (var key in _latest.Keys)
                        if (_latest.TryRemove(key, out var m)) await Send(m);
                }
            }
            catch { }
        }

        Task Send(string json) => Socket.State == WebSocketState.Open
            ? Socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, _cts.Token)
            : Task.CompletedTask;

        public void Dispose()
        {
            _cts.Cancel();
            try { Socket.Abort(); Socket.Dispose(); } catch { }
        }
    }
}
