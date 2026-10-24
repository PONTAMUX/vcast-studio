using VCast.Obs;

namespace VCast.Core;

// プロファイル（番組ごとの「設定＋シーン」のセット）。企業やチームで、番組・出演者ごとに切り替える用途
//   %APPDATA%\VCastStudio\profiles\<名前>\settings.json / scenes.json
//   初回起動時に、これまでの設定とシーンを「標準」プロファイルとして取り込む（元のファイルは残す）
public static class Profiles
{
    public const string DefaultName = "標準";
    static bool _init;
    static string Root => Path.Combine(ObsEngine.ConfigDir, "profiles");
    public static string Current { get; private set; } = DefaultName;
    public static string ScenesPath => Path.Combine(Root, Current, "scenes.json");
    public static string SettingsPath => Path.Combine(Root, Current, "settings.json");
    static string PointerFile => Path.Combine(ObsEngine.ConfigDir, "profile.txt");

    public static void EnsureInit()
    {
        if (_init) return;
        _init = true;
        try
        {
            Directory.CreateDirectory(ConfigRoot());
            if (!Directory.Exists(Root))
            {
                var d = Path.Combine(Root, DefaultName);
                Directory.CreateDirectory(d);
                var oldSettings = Path.Combine(ObsEngine.ConfigDir, "settings.json");
                var oldScenes = Path.Combine(ObsEngine.ConfigDir, "scenes", "default.json");
                if (File.Exists(oldSettings)) File.Copy(oldSettings, Path.Combine(d, "settings.json"), true);
                if (File.Exists(oldScenes)) File.Copy(oldScenes, Path.Combine(d, "scenes.json"), true);
            }
            var name = File.Exists(PointerFile) ? File.ReadAllText(PointerFile).Trim() : DefaultName;
            Current = Exists(name) ? name : DefaultName;
            Directory.CreateDirectory(Path.Combine(Root, Current));
        }
        catch (Exception ex) { ObsEngine.Log("[profiles] init: " + ex.Message); }
    }

    static string ConfigRoot() => ObsEngine.ConfigDir;

    public static List<string> List()
    {
        EnsureInit();
        try { return Directory.GetDirectories(Root).Select(Path.GetFileName).OfType<string>().OrderBy(n => n == DefaultName ? "" : n).ToList(); }
        catch { return new List<string> { DefaultName }; }
    }

    public static bool Exists(string name) => Directory.Exists(Path.Combine(Root, name));

    // 使える名前か（フォルダ名になるので、使えない文字と長さを確認）
    public static string? ValidateName(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "名前を入力してください";
        if (name.Length > 40) return "名前は 40 文字までです";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.')) return "使えない文字が含まれています（\\ / : * ? \" < > | など）";
        if (Exists(name)) return "同じ名前がすでにあります";
        return null;
    }

    public static string ScenesPathOf(string name) => Path.Combine(Root, name, "scenes.json");

    // 使うプロファイルを覚える。書けなかったら false（呼び出し側は切り替えを取りやめる）
    public static bool SetCurrent(string name)
    {
        try { File.WriteAllText(PointerFile, name); }
        catch (Exception ex) { ObsEngine.Log("[profiles] pointer: " + ex.Message); return false; }
        Current = name;
        return true;
    }

    // 新規作成。copyFrom があればその設定とシーンを複製、無ければ初期設定＋空のシーン
    public static void Create(string name, string? copyFrom)
    {
        var d = Path.Combine(Root, name);
        Directory.CreateDirectory(d);
        if (copyFrom == null) return;
        foreach (var f in new[] { "settings.json", "scenes.json" })
        {
            var src = Path.Combine(Root, copyFrom, f);
            if (File.Exists(src)) File.Copy(src, Path.Combine(d, f), true);
        }
    }

    public static void Rename(string from, string to)
    {
        Directory.Move(Path.Combine(Root, from), Path.Combine(Root, to));
        if (Current == from && !SetCurrent(to)) { Directory.Move(Path.Combine(Root, to), Path.Combine(Root, from)); throw new IOException("プロファイルの記録を更新できませんでした"); }
    }

    public static void Delete(string name)
    {
        if (name == Current || List().Count <= 1) throw new InvalidOperationException("使用中のプロファイル、または最後の 1 つは削除できません");
        Directory.Delete(Path.Combine(Root, name), true);
    }
}
