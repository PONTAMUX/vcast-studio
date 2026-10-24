using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using VCast.Obs;

namespace VCast.Avatar;

// 顔トラッキングを動かす WebView2。普段は画面外に置き、「カメラ確認」のときだけ見える位置に出す。
// 画面外でも処理が止まらないよう、Chromium の背景スロットリングと隠れウィンドウ判定を無効化する。
// カメラの許可とページ移動は自分のローカルサーバー（origin 完全一致）だけに限定する
public sealed class TrackerWindow : Window
{
    readonly WebView2 _web = new();
    readonly string _origin;
    string? _pendingUrl;
    bool _ready, _shutdown;

    public TrackerWindow(string origin)
    {
        _origin = origin;
        Title = "カメラ確認 — VCast Studio";
        Width = 360; Height = 320;
        WindowStyle = WindowStyle.ToolWindow; ShowInTaskbar = false; ShowActivated = false;
        Left = -20000; Top = -20000;
        Content = _web;
        Loaded += async (_, _) => await Init();
        Closing += (_, e) =>
        {
            // ×で閉じられたら隠すだけ（カメラ自体のオンオフはアバター設定で行う）
            if (!_shutdown) { e.Cancel = true; SetVisibleOnScreen(false); }
        };
    }

    bool SameOrigin(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var u) && $"{u.Scheme}://{u.Authority}" == _origin;

    async Task Init()
    {
        try
        {
            var args = "--disable-features=CalculateNativeWinOcclusion --disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows";
            // 自己テスト用: 本物のカメラを使わず、ブラウザ内蔵の偽カメラ（テスト映像）で顔追跡の仕組みだけ確認する
            if (ObsEngine.TestMode && Environment.GetEnvironmentVariable("VCAST_REAL_CAMERA") != "1") args += " --use-fake-device-for-media-stream --use-fake-ui-for-media-stream";
            var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = args };
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(ObsEngine.ConfigDir, "webview2"), options);
            if (_shutdown) return;
            await _web.EnsureCoreWebView2Async(env);
            if (_shutdown) return;
            var core = _web.CoreWebView2;
            core.PermissionRequested += (_, e) =>
                e.State = e.PermissionKind == CoreWebView2PermissionKind.Camera && SameOrigin(e.Uri) ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            core.NavigationStarting += (_, e) => { if (!SameOrigin(e.Uri)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            _ready = true;
            if (_pendingUrl != null) core.Navigate(_pendingUrl);
        }
        catch (Exception ex)
        {
            ObsEngine.Log("[tracker] WebView2 init failed: " + ex.Message);
            if (!_shutdown) MessageBox.Show("顔トラッキングを起動できませんでした（WebView2）。\n" + ex.Message, "VCast Studio");
        }
    }

    public void NavigateTo(string url)
    {
        _pendingUrl = url;
        if (_ready && !_shutdown) _web.CoreWebView2.Navigate(url);
    }

    public void SetVisibleOnScreen(bool show)
    {
        if (show)
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - Width - 20; Top = wa.Bottom - Height - 20;
            Activate();
        }
        else { Left = -20000; Top = -20000; }
    }

    // 本当に終了する: WebView2 を解放してカメラを確実に止める
    public void Shutdown()
    {
        if (_shutdown) return;
        _shutdown = true;
        try { _web.Dispose(); } catch (Exception ex) { ObsEngine.Log("[tracker] dispose: " + ex.Message); }
        Close();
    }
}
