using System.Net.WebSockets;
using System.Text;
using VCast.Obs;

namespace VCast.Core;

public sealed record ChatMessage(DateTime At, string Name, string Text, string Color, bool IsSystem = false);

// Twitch のチャットを読むだけの接続（匿名ログイン justinfan。ログイン・キー・トークンは不要で、書き込みはできない）
public sealed class TwitchChat : IDisposable
{
    public event Action<ChatMessage>? Message;
    public event Action<string>? State; // 「接続中」「接続しました」など
    CancellationTokenSource? _cts;
    public string Channel { get; private set; } = "";
    public bool Running => _cts != null;

    // チャンネル名は英数字とアンダースコアだけ（URL 形式で貼られても受け付ける）
    public static string NormalizeChannel(string? s)
    {
        s = (s ?? "").Trim();
        var i = s.IndexOf("twitch.tv/", StringComparison.OrdinalIgnoreCase);
        if (i >= 0) s = s[(i + 10)..];
        s = s.TrimStart('#', '@').Split('/', '?', '#')[0].ToLowerInvariant();
        return s.Length is >= 1 and <= 25 && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? s : "";
    }

    public bool Start(string channel)
    {
        Stop();
        var ch = NormalizeChannel(channel);
        if (ch.Length == 0) { State?.Invoke("チャンネル名が正しくありません（例: samplech）"); return false; }
        Channel = ch;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => Loop(ch, token));
        return true;
    }

    public void Stop()
    {
        var c = _cts; _cts = null;
        try { c?.Cancel(); } catch { }
        if (c != null) State?.Invoke("切断しました");
    }

    async Task Loop(string channel, CancellationToken ct)
    {
        var wait = 2;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                State?.Invoke("接続中…");
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri("wss://irc-ws.chat.twitch.tv:443"), ct);
                await Send(ws, "CAP REQ :twitch.tv/tags twitch.tv/commands", ct);
                await Send(ws, "NICK justinfan" + Random.Shared.Next(10000, 99999), ct);
                await Send(ws, "JOIN #" + channel, ct);
                var buf = new byte[16384]; var sb = new StringBuilder();
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(buf, ct);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                    if (!r.EndOfMessage) continue;
                    foreach (var line in sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.StartsWith("PING")) { await Send(ws, "PONG" + line[4..], ct); continue; }
                        if (line.Contains(" 366 ")) { wait = 2; State?.Invoke($"接続しました（#{channel}）"); continue; }
                        var m = Parse(line);
                        if (m != null) Message?.Invoke(m);
                    }
                    sb.Clear();
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { ObsEngine.Log("[chat] " + ex.Message); }
            if (ct.IsCancellationRequested) return;
            State?.Invoke($"切れました。{wait} 秒後に再接続します…");
            try { await Task.Delay(wait * 1000, ct); } catch { return; }
            wait = Math.Min(wait * 2, 30);
        }
    }

    static Task Send(ClientWebSocket ws, string s, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(s + "\r\n"), WebSocketMessageType.Text, true, ct);

    // @badges=...;color=#FF0000;display-name=Taro;emotes=25:0-4;... :taro!taro@taro.tmi.twitch.tv PRIVMSG #ch :Kappa こんにちは
    public static ChatMessage? Parse(string line)
    {
        if (!line.StartsWith('@')) return null;
        var sp = line.IndexOf(' ');
        if (sp < 0) return null;
        var tags = line[1..sp].Split(';').Select(t => t.Split('=', 2)).Where(a => a.Length == 2).ToDictionary(a => a[0], a => a[1]);
        var rest = line[(sp + 1)..];
        var p = rest.IndexOf(" PRIVMSG ", StringComparison.Ordinal);
        if (p < 0) return null;
        var c = rest.IndexOf(" :", p + 9, StringComparison.Ordinal);
        if (c < 0) return null;
        var text = rest[(c + 2)..];
        if (text.StartsWith("\u0001ACTION ") && text.EndsWith('\u0001')) text = text[8..^1];
        var name = tags.TryGetValue("display-name", out var dn) && dn.Length > 0 ? dn : rest[1..rest.IndexOf('!')];
        // エモートは文字位置で取り除く（読み上げ・表示を読みやすくするため。位置は文字（コードポイント）単位）
        if (tags.TryGetValue("emotes", out var em) && em.Length > 0)
        {
            var cps = text.EnumerateRunes().Select(r => r.ToString()).ToList();
            var ranges = em.Split('/').SelectMany(e => e.Split(':', 2).Skip(1).FirstOrDefault()?.Split(',') ?? Array.Empty<string>())
                           .Select(r => r.Split('-')).Where(a => a.Length == 2 && int.TryParse(a[0], out _) && int.TryParse(a[1], out _))
                           .Select(a => (int.Parse(a[0]), int.Parse(a[1]))).OrderByDescending(a => a.Item1).ToList();
            foreach (var (s, e) in ranges) if (s >= 0 && e < cps.Count && s <= e) cps.RemoveRange(s, e - s + 1);
            text = string.Concat(cps).Trim();
        }
        if (text.Length == 0) return null;
        return new ChatMessage(DateTime.Now, name, text, tags.TryGetValue("color", out var col) ? col : "");
    }

    public void Dispose() => Stop();
}
