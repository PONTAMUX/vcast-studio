using VCast.Obs;

namespace VCast.Core;

// ソース種別の説明・分類と、ファイル/ウィンドウからのソース作成
public static class SourceCatalog
{
    public record Category(string Name, string[] Ids);
    // 内蔵アバター（実体は localhost の描画ページを開くブラウザソース）
    public const string AvatarId = "vcast_avatar";
    public const string PlaylistId = "vcast_playlist"; // 動画・音声を順番に流す（実体は ffmpeg_source）

    public static readonly Category[] Categories =
    {
        new("映像", new[] { AvatarId, "game_capture", "window_capture", "monitor_capture", "dshow_input" }),
        new("音声", new[] { "wasapi_input_capture", "wasapi_output_capture", "wasapi_process_output_capture" }),
        new("素材・文字", new[] { PlaylistId, "ffmpeg_source", "image_source", "slideshow_v2", "browser_source", "text_gdiplus_v3", "color_source_v3" }),
    };

    static readonly Dictionary<string, string> Descriptions = new()
    {
        [PlaylistId] = "動画・音声を順番に流す（ループ・シャッフル・次へ）。BGM や待機画面に",
        [AvatarId] = "VRM の 3D アバターを表示する（カメラなしでも動く・別ソフト不要）",
        ["game_capture"] = "ゲーム画面を軽く・きれいに取り込む。ゲームを起動すると映ります（窓表示のゲームは下の「開いているアプリ」から）",
        ["window_capture"] = "選んだアプリのウィンドウだけを映す（ブラウザ・Discord など）",
        ["monitor_capture"] = "モニターの画面全体をそのまま映す（VCast の画面自身や、開いているほかのウィンドウも映るので注意）",
        ["dshow_input"] = "Webカメラやキャプチャーボードの映像",
        ["wasapi_input_capture"] = "マイクの音",
        ["wasapi_output_capture"] = "PC から出ている音（ゲーム音・BGM など）をまとめて取り込む",
        ["wasapi_process_output_capture"] = "指定したアプリの音だけを取り込む",
        ["ffmpeg_source"] = "動画・音声ファイルを再生する（ループ再生もできる）",
        ["image_source"] = "画像ファイルを表示する（PNG の透過もそのまま）",
        ["slideshow_v2"] = "複数の画像を順番に切り替えて表示する",
        ["browser_source"] = "Web ページを表示する（コメント欄・アラート・ウィジェットなど）",
        ["text_gdiplus_v3"] = "文字を表示する（フォント・色・縁取りを変えられる）",
        ["color_source_v3"] = "単色の板（背景や帯に）",
    };

    public static string Describe(string id) => Descriptions.TryGetValue(id, out var d) ? d : "";

    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tga", ".jxr", ".psd" };
    static readonly HashSet<string> MediaExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".webm", ".flv", ".avi", ".ts", ".m4v", ".wmv",
        ".mp3", ".wav", ".ogg", ".opus", ".flac", ".m4a", ".aac", ".wma",
    };
    static readonly HashSet<string> HtmlExt = new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm" };

    public static bool CanOpen(string path)
    {
        var ext = Path.GetExtension(path);
        return ImageExt.Contains(ext) || MediaExt.Contains(ext) || HtmlExt.Contains(ext);
    }

    // ファイル → 対応するソースを作って現在のシーンに追加
    public static SceneItemVm? AddFile(SceneManager scenes, string path)
    {
        var ext = Path.GetExtension(path);
        var name = SceneManager.UniqueName(Path.GetFileNameWithoutExtension(path));
        if (ImageExt.Contains(ext))
            return scenes.AddSource("image_source", name, s => Native.obs_data_set_string(s, "file", path));
        if (MediaExt.Contains(ext))
            return scenes.AddSource("ffmpeg_source", name, s =>
            {
                Native.obs_data_set_bool(s, "is_local_file", true);
                Native.obs_data_set_string(s, "local_file", path);
            });
        if (HtmlExt.Contains(ext))
            return scenes.AddSource("browser_source", name, s =>
            {
                Native.obs_data_set_bool(s, "is_local_file", true);
                Native.obs_data_set_string(s, "local_file", path);
                Native.obs_data_set_int(s, "width", ObsEngine.BaseWidth);
                Native.obs_data_set_int(s, "height", ObsEngine.BaseHeight);
            });
        return null;
    }

    // 開いているアプリ → そのアプリの音だけを取り込むソースとして追加
    public static SceneItemVm? AddAppAudio(SceneManager scenes, WindowInfo w) =>
        scenes.AddSource("wasapi_process_output_capture", SceneManager.UniqueName(w.Exe + " の音"),
            s => Native.obs_data_set_string(s, "window", w.ObsWindowId));

    // 開いているウィンドウ → ウィンドウキャプチャとして追加
    public static SceneItemVm? AddWindow(SceneManager scenes, WindowInfo w)
    {
        var baseName = string.IsNullOrWhiteSpace(w.Title) ? w.Exe : w.Title;
        if (baseName.Length > 40) baseName = baseName[..40] + "…";
        return scenes.AddSource("window_capture", SceneManager.UniqueName(baseName),
            s =>
            {
                Native.obs_data_set_string(s, "window", w.ObsWindowId);
                Native.obs_data_set_int(s, "method", 2); // Windows Graphics Capture。自動だとGPU描画のアプリが黒くなることがある
            });
    }
}
