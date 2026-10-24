using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VCast.Obs;

namespace VCast.Core;

// 配信先。ストリームキーは DPAPI（この PC のこのユーザーだけが復号できる）で暗号化して保存
public sealed class StreamDestination
{
    public string Name { get; set; } = "YouTube";
    public string Service { get; set; } = "YouTube"; // YouTube / Twitch / Custom
    public string Server { get; set; } = "";
    public string KeyProtected { get; set; } = "";
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string Key
    {
        get
        {
            if (string.IsNullOrEmpty(KeyProtected)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(KeyProtected), Entropy, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }
        set => KeyProtected = string.IsNullOrEmpty(value) ? "" :
            Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
    }

    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VCastStudio.StreamKey");

    // 公式に案内されている取り込み先（YouTube は RTMPS、Twitch は自動で最寄りへ振り分けられる URL）
    public string ResolvedServer => Service switch
    {
        "YouTube" => "rtmps://a.rtmps.youtube.com:443/live2",
        "Twitch" => "rtmp://live.twitch.tv/app",
        _ => Server,
    };
}

public sealed class MacroDef
{
    public string Name { get; set; } = "";
    public string Scene { get; set; } = "";       // 移動するシーン名（空 = そのまま）
    public string Expression { get; set; } = "";  // アバターの表情 id
    public string Mic { get; set; } = "";         // "" / mute / unmute
    public int Pad { get; set; }                  // サウンドボードのパッド番号（0 = なし）
    public string Record { get; set; } = "";      // "" / start / stop
}

public sealed class AppSettings
{
    public List<StreamDestination> Destinations { get; set; } = new() { new StreamDestination() };
    public string Quality { get; set; } = "standard";     // high / standard / light / custom
    public string VideoEncoder { get; set; } = "auto";    // auto / obs_nvenc_h264_tex / obs_x264 ...
    public string Codec { get; set; } = "h264";           // h264 / hevc（対応する GPU のとき。同じ回線でより綺麗）
    public string RecordCodec { get; set; } = "hevc";     // 録画の圧縮方式: hevc（既定・ファイルが小さい）/ h264（どこでも再生）/ same（配信と同じ）。GPU が HEVC に未対応なら自動で H.264
    public bool RecordSeparate { get; set; }              // 録画だけ別の高画質設定（配信とは別のエンコーダ）
    public int RecordQuality { get; set; } = 18;          // 数字が小さいほど高画質（CQP/CRF）。18 = ほぼ劣化なし
    public string RateControl { get; set; } = "CBR";
    public int VideoBitrate { get; set; } = 6000;
    public int KeyintSec { get; set; } = 2;
    public int AudioBitrate { get; set; } = 160;
    public string RecordFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VCast");
    public string RecordFormat { get; set; } = "mp4";      // mp4（ハイブリッド）/ mkv
    public int ReplaySeconds { get; set; } = 30;
    public bool ReplayEnabled { get; set; } = false;
    public bool PreflightCheck { get; set; } = true;
    public bool SimpleMode { get; set; } = true; // 初めての人はかんたんモードから（オフ = 詳細モード）
    public bool ProMode { get; set; }            // プロモード: 詳細モードに vMix のような機能（スタジオモード・プロファイル・マクロ・レイアウト集）を足す
    public List<MacroDef> Macros { get; set; } = new();
    public bool OnboardingDone { get; set; }     // 「はじめの3ステップ」を全部終えたら、以後は出さない
    public int Fps { get; set; } = 60; // 60 / 30。変更は再起動で反映
    public string TransitionId { get; set; } = "fade_transition";
    public int TransitionMs { get; set; } = 300;
    public bool StudioMode { get; set; }
    public int StreamDelaySec { get; set; } // 放送事故対策の遅延配信（0 = なし）
    public bool RecordWithStream { get; set; } // 配信を始めたら録画も自動で始める（終えたら止める）
    // コメントビュー（Twitch のチャットを読む）と VOICEVOX 読み上げ
    public string ChatChannel { get; set; } = "";
    public bool ChatAutoConnect { get; set; }
    public bool ChatTopmost { get; set; }
    public bool ChatRead { get; set; }
    public int ChatSpeaker { get; set; } = 3;
    public double ChatSpeed { get; set; } = 1.1;
    public double ChatVolume { get; set; } = 1.0;
    public bool ChatReadName { get; set; }
    public string ChatDict { get; set; } = "草=くさ\nｗｗｗ=わら";
    // 縦型（1080x1920）の同時出力
    public bool VerticalEnabled { get; set; }
    public string VerticalLayout { get; set; } = "stack"; // stack / avatar / crop
    public List<StreamDestination> VerticalDestinations { get; set; } = new() { new StreamDestination { Name = "縦型", Service = "Custom", Enabled = false } };
    public bool VerticalRecord { get; set; }
    // 外部操作（obs-websocket v5 互換。Stream Deck など）。既定はオフ
    public bool RemoteEnabled { get; set; }
    public int RemotePort { get; set; } = 4455;
    public string RemotePassword { get; set; } = "";
    public bool RemoteLan { get; set; } // ほかの機器（スマホなど）からの接続を許可

    // 画質プリセット → ビットレート（映像は 1080p60 キャンバスのまま、帯域だけ変える）
    public static readonly (string Id, string Label, int Kbps, string Note)[] Presets =
    {
        ("high", "高画質", 8000, "回線に余裕がある人向け（上り 12Mbps 以上）"),
        ("standard", "標準", 6000, "おすすめ（上り 9Mbps 以上）"),
        ("light", "軽量", 3500, "回線や PC が不安なとき（上り 6Mbps 以上）"),
    };

    public int EffectiveVideoBitrate => Presets.FirstOrDefault(p => p.Id == Quality) is { Id: not null } p && Quality != "custom" ? p.Kbps : VideoBitrate;

    // 設定画面は複製を編集し、保存したときだけ書き戻す（キャンセルで元のまま）
    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;
    public void CopyFrom(AppSettings o)
    {
        foreach (var p in typeof(AppSettings).GetProperties().Where(p => p.CanWrite)) p.SetValue(this, p.GetValue(o));
    }

    static string PathOf { get { Profiles.EnsureInit(); return Profiles.SettingsPath; } }

    public static AppSettings Load()
    {
        try { if (File.Exists(PathOf)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathOf)) ?? new(); }
        catch (Exception ex) { ObsEngine.Log("[settings] load: " + ex.Message); }
        return new();
    }

    // 一時ファイルに書いてから置き換える（途中で止まっても元のファイルを壊さない）。成否を返す
    public bool Save()
    {
        try
        {
            var path = PathOf;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, true);
            return true;
        }
        catch (Exception ex) { ObsEngine.Log("[settings] save: " + ex.Message); return false; }
    }

    public static string NewPassword() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).Replace("+", "x").Replace("/", "y").TrimEnd('=');
}
