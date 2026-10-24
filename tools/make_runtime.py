#!/usr/bin/env python3
"""VCast が実際に使う OBS ランタイムだけを取り出して最小構成を作る。

使い方: python tools/make_runtime.py [元の OBS フォルダ] [出力先]
  既定: 元 = C:\\Program Files\\obs-studio、出力 = <repo>\\runtime\\obs-min

- 読み込むプラグイン（ObsEngine.AllowedModules と同じ）の DLL から、PE のインポート表を再帰的にたどって
  必要な DLL を集める（Qt は obs-browser が参照するぶんだけ残る）。pdb・画面アプリ本体・不要な言語は入れない
- LoadLibrary で実行時に探されるもの（libobs-d3d11 / winrt / エンコーダ検出 exe / ffmpeg-mux / CEF 一式）は名前で追加
- ライセンス表記（GPL）を同梱する
"""
import os, re, shutil, struct, sys

ALLOWED = ["image-source", "obs-ffmpeg", "obs-filters", "obs-nvenc", "obs-qsv11", "obs-outputs", "obs-text", "obs-transitions",
           "obs-webrtc", "obs-x264", "rtmp-services", "text-freetype2", "win-capture", "win-dshow", "win-wasapi", "nv-filters", "obs-browser"]
RUNTIME_LOADED_BIN = ["libobs-d3d11.dll", "libobs-winrt.dll", "obs-ffmpeg-mux.exe", "obs-nvenc-test.exe", "obs-qsv-test.exe", "obs-amf-test.exe", "obs-frontend-api.dll"]
KEEP_LOCALES = {"ja-JP", "en-US", "ja", "en", "en-US.ini", "ja-JP.ini"}


def imports(path):
    """PE のインポート DLL 名（小文字）を返す。"""
    try:
        d = open(path, "rb").read()
        pe = struct.unpack_from("<I", d, 0x3C)[0]
        nsec = struct.unpack_from("<H", d, pe + 6)[0]
        optsz = struct.unpack_from("<H", d, pe + 20)[0]
        opt = pe + 24
        magic = struct.unpack_from("<H", d, opt)[0]
        dd = opt + (112 if magic == 0x20B else 96)
        imp_rva = struct.unpack_from("<I", d, dd + 8)[0]
        if not imp_rva:
            return []
        secs = []
        for i in range(nsec):
            o = pe + 24 + optsz + i * 40
            vs, va, rs, raw = struct.unpack_from("<IIII", d, o + 8)
            secs.append((va, max(vs, rs), raw))

        def off(rva):
            for va, sz, raw in secs:
                if va <= rva < va + sz:
                    return rva - va + raw
            return None
        out = []
        o = off(imp_rva)
        while o is not None:
            name_rva = struct.unpack_from("<I", d, o + 12)[0]
            if name_rva == 0:
                break
            no = off(name_rva)
            out.append(d[no:d.index(b"\0", no)].decode("ascii", "ignore").lower())
            o += 20
        return out
    except Exception:
        return []


def copy(src, dst):
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copy2(src, dst)


def copy_tree_filtered(src, dst):
    """言語ファイルは日本語・英語だけ残してコピー。pdb は入れない。"""
    for root, dirs, files in os.walk(src):
        rel = os.path.relpath(root, src)
        parts = rel.split(os.sep)
        if "locale" in parts or "locales" in parts:
            # locale/ja-JP.ini 形式と、CEF の locales/ja.pak 形式の両方
            files = [f for f in files if os.path.splitext(f)[0] in {"ja", "en-US", "ja-JP", "en"} or f in KEEP_LOCALES]
        for f in files:
            if f.lower().endswith(".pdb"):
                continue
            copy(os.path.join(root, f), os.path.join(dst, rel, f))


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else r"C:\Program Files\obs-studio"
    dst = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "runtime", "obs-min")
    dst = os.path.abspath(dst)
    bin_src = os.path.join(src, "bin", "64bit")
    plug_src = os.path.join(src, "obs-plugins", "64bit")
    if os.path.exists(dst):
        shutil.rmtree(dst)

    # 依存 DLL を集める（bin/64bit にあるもののみ）
    bin_names = {f.lower(): f for f in os.listdir(bin_src)}
    plug_names = {f.lower(): f for f in os.listdir(plug_src)}
    need_bin = {"obs.dll"} | {n.lower() for n in RUNTIME_LOADED_BIN}
    plugin_dlls = [f"{m}.dll" for m in ALLOWED]
    queue = [os.path.join(bin_src, bin_names[n]) for n in need_bin if n in bin_names]
    queue += [os.path.join(plug_src, plug_names[p.lower()]) for p in plugin_dlls if p.lower() in plug_names]
    seen = set()
    while queue:
        p = queue.pop()
        if p in seen:
            continue
        seen.add(p)
        for imp in imports(p):
            if imp in bin_names and imp not in need_bin:
                need_bin.add(imp)
                queue.append(os.path.join(bin_src, bin_names[imp]))
    for n in sorted(need_bin):
        if n in bin_names:
            copy(os.path.join(bin_src, bin_names[n]), os.path.join(dst, "bin", "64bit", bin_names[n]))

    # プラグイン本体
    for p in plugin_dlls:
        if p.lower() in plug_names:
            copy(os.path.join(plug_src, plug_names[p.lower()]), os.path.join(dst, "obs-plugins", "64bit", plug_names[p.lower()]))
    # ブラウザ部品（CEF）一式
    for n in ["obs-browser-page.exe", "libcef.dll", "chrome_elf.dll", "libEGL.dll", "libGLESv2.dll", "icudtl.dat", "resources.pak",
              "chrome_100_percent.pak", "chrome_200_percent.pak", "v8_context_snapshot.bin"]:
        if n.lower() in plug_names:
            copy(os.path.join(plug_src, plug_names[n.lower()]), os.path.join(dst, "obs-plugins", "64bit", plug_names[n.lower()]))
    for extra in ("vk_swiftshader.dll", "vk_swiftshader_icd.json", "vulkan-1.dll", "d3dcompiler_47.dll", "dxcompiler.dll", "dxil.dll"):
        if extra.lower() in plug_names:
            copy(os.path.join(plug_src, plug_names[extra.lower()]), os.path.join(dst, "obs-plugins", "64bit", plug_names[extra.lower()]))
    if "locales" in plug_names:
        copy_tree_filtered(os.path.join(plug_src, "locales"), os.path.join(dst, "obs-plugins", "64bit", "locales"))

    # データ（エフェクト・各モジュールの data。言語は日本語・英語のみ）
    copy_tree_filtered(os.path.join(src, "data", "libobs"), os.path.join(dst, "data", "libobs"))
    for m in ALLOWED:
        d = os.path.join(src, "data", "obs-plugins", m)
        if os.path.isdir(d):
            copy_tree_filtered(d, os.path.join(dst, "data", "obs-plugins", m))
    # 仮想カメラのドライバ（登録済みの PC でだけ使う。win-dshow の data にある）

    # ライセンス（GPL 本文と作者一覧）。見つからなければ失敗させる（ライセンス無しで配布しないため）
    for name, sub in (("gplv2.txt", "license"), ("AUTHORS", "authors")):
        p = os.path.join(src, "data", "obs-studio", sub, name)
        if not os.path.exists(p):
            raise SystemExit(f"ライセンスファイルが見つかりません: {p}")
        copy(p, os.path.join(dst, "licenses", "OBS-" + name))

    total = sum(os.path.getsize(os.path.join(r, f)) for r, _, fs in os.walk(dst) for f in fs)
    n = sum(len(fs) for _, _, fs in os.walk(dst))
    print(f"出力: {dst}\nファイル数: {n}  合計: {total / 1024 / 1024:.1f} MB")
    print("bin/64bit に入れた DLL: " + ", ".join(sorted(bin_names[n] for n in need_bin if n in bin_names)))


if __name__ == "__main__":
    main()
