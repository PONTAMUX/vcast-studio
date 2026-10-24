using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VCast.Obs;

namespace VCast.Core;

public sealed record VoiceStyle(int Id, string Label); // 「ずんだもん（ノーマル）」など

// VOICEVOX（無料の音声合成ソフト。同じ PC で起動しておく）に読み上げさせる。127.0.0.1:50021 だけに接続し、コメントの文字はこの PC の外へ出さない
public sealed class VoiceVoxReader : IDisposable
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:50021";
    public int Speaker { get; set; } = 3;
    public double Speed { get; set; } = 1.1;
    public double Volume { get; set; } = 1.0;
    public bool ReadName { get; set; }
    public int MaxChars { get; set; } = 60;
    public int MaxQueue { get; set; } = 4;     // 読み上げが追いつかないときは古いものを飛ばす
    public string Dictionary { get; set; } = ""; // 1 行に「元=読み」
    public string Status { get; private set; } = "";
    public event Action<string>? StatusChanged;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly BlockingCollection<(string text, int speaker)> _queue = new(new ConcurrentQueue<(string, int)>());
    readonly CancellationTokenSource _cts = new();
    readonly Thread _worker;
    volatile bool _flush;
    public Func<byte[], bool>? PlayOverride; // 自己テスト用（実際には鳴らさない）

    static readonly string[] Bots = { "nightbot", "streamelements", "streamlabs", "moobot", "fossabot", "wizebot", "sery_bot" };
    static readonly Regex UrlRe = new(@"https?://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex LaughRe = new(@"[wWｗＷ]{3,}", RegexOptions.Compiled);
    static readonly Regex RepeatRe = new(@"(.)\1{4,}", RegexOptions.Compiled);

    public VoiceVoxReader()
    {
        _worker = new Thread(Run) { IsBackground = true, Name = "VoiceVoxReader" };
        _worker.Start();
    }

    // コメント → 読み上げる文字（読まないものは null）。命令（!で始まる）・bot・URL・連続文字・絵文字っぽい記号を整える
    public string? Clean(string name, string text)
    {
        if (Bots.Contains(name.ToLowerInvariant())) return null;
        text = text.Trim();
        if (text.Length == 0 || text.StartsWith('!') || text.StartsWith('/')) return null;
        text = UrlRe.Replace(text, "URL省略");
        text = LaughRe.Replace(text, "わら");
        text = RepeatRe.Replace(text, "$1$1$1");
        foreach (var line in Dictionary.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = line.Trim().Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Length > 0) text = text.Replace(kv[0].Trim(), kv[1].Trim(), StringComparison.OrdinalIgnoreCase);
        }
        // 読めない記号（絵文字・制御文字）を落とす
        var sb = new StringBuilder();
        foreach (var r in text.EnumerateRunes())
        {
            var cat = Rune.GetUnicodeCategory(r);
            if (cat is System.Globalization.UnicodeCategory.OtherSymbol or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.Control
                or System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.PrivateUse) continue;
            sb.Append(r.ToString());
        }
        text = sb.ToString().Trim();
        if (text.Length == 0) return null;
        if (text.Length > MaxChars) text = text[..MaxChars] + "、以下省略";
        return ReadName ? $"{name}さん、{text}" : text;
    }

    public bool Enqueue(string name, string text)
    {
        var t = Clean(name, text);
        if (t == null) return false;
        while (_queue.Count >= MaxQueue && _queue.TryTake(out _)) { } // 古いものから捨てる
        _queue.Add((t, Speaker));
        return true;
    }

    // いま読んでいるもの・待っているものをやめる
    public void StopAll() { _flush = true; while (_queue.TryTake(out _)) { } try { player?.Stop(); } catch { } }

    System.Media.SoundPlayer? player;

    void Run()
    {
        while (!_cts.IsCancellationRequested)
        {
            (string text, int speaker) item;
            try { item = _queue.Take(_cts.Token); } catch { return; }
            _flush = false;
            try
            {
                var wav = Synthesize(item.text, item.speaker).GetAwaiter().GetResult();
                if (_flush) continue;
                if (PlayOverride != null) { PlayOverride(wav); continue; }
                using var ms = new MemoryStream(wav);
                player = new System.Media.SoundPlayer(ms);
                player.PlaySync(); // 1 つずつ順番に読む
                SetStatus("");
            }
            catch (Exception ex) { SetStatus("読み上げできません: " + Short(ex)); }
        }
    }

    static string Short(Exception ex) => ex is HttpRequestException or TaskCanceledException ? "VOICEVOX に繋がりません（起動していますか？）" : ex.Message;
    void SetStatus(string s) { if (Status == s) return; Status = s; StatusChanged?.Invoke(s); }

    public async Task<byte[]> Synthesize(string text, int speaker)
    {
        var q = await Http.PostAsync($"{BaseUrl}/audio_query?speaker={speaker}&text={Uri.EscapeDataString(text)}", null);
        q.EnsureSuccessStatusCode();
        var query = JsonNode.Parse(await q.Content.ReadAsStringAsync())!.AsObject();
        query["speedScale"] = Speed; query["volumeScale"] = Volume;
        var s = await Http.PostAsync($"{BaseUrl}/synthesis?speaker={speaker}", new StringContent(query.ToJsonString(), Encoding.UTF8, "application/json"));
        s.EnsureSuccessStatusCode();
        return await s.Content.ReadAsByteArrayAsync();
    }

    // 声の一覧（キャラクター × スタイル）。VOICEVOX が起動していなければ null
    public async Task<List<VoiceStyle>?> GetStyles()
    {
        try
        {
            var json = await Http.GetStringAsync($"{BaseUrl}/speakers");
            var list = new List<VoiceStyle>();
            foreach (var sp in JsonNode.Parse(json)!.AsArray())
                foreach (var st in sp!["styles"]!.AsArray())
                    list.Add(new VoiceStyle(st!["id"]!.GetValue<int>(), $"{sp["name"]}（{st["name"]}）"));
            return list;
        }
        catch (Exception ex) { ObsEngine.Log("[voicevox] " + ex.Message); return null; }
    }

    public async Task<string?> GetVersion()
    {
        try { return (await Http.GetStringAsync($"{BaseUrl}/version")).Trim('"'); } catch { return null; }
    }

    public void Dispose() { _cts.Cancel(); StopAll(); }
}
