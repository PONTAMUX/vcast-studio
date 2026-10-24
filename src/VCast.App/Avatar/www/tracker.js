// 顔トラッキング（アプリ本体の隠し WebView2 で動く）。カメラ → MediaPipe Face Landmarker → 表情52種＋頭の向き
// 結果は WebSocket でアプリ本体へ送り、本体が描画ページへ中継する（描画とトラッカーを分離しておく設計）
import { FaceLandmarker, FilesetResolver } from './vendor/mediapipe/vision_bundle.mjs';

const params = new URLSearchParams(location.search);
const deviceId = params.get('cam') || '';
const video = document.getElementById('cam');
const status = (s) => { document.getElementById('status').textContent = s; send({ type: 'tracker-status', text: s }); };

let ws = null;
function send(m) { if (ws && ws.readyState === 1) ws.send(JSON.stringify(m)); }
function connect() {
  ws = new WebSocket(`ws://${location.host}/ws?role=tracker&t=${encodeURIComponent(params.get('t') || '')}`);
  ws.onopen = () => { send({ type: 'hello', role: 'tracker' }); reportCameras(); };
  ws.onclose = () => setTimeout(connect, 1000);
}
connect();

async function reportCameras() {
  try {
    const list = (await navigator.mediaDevices.enumerateDevices())
      .filter((d) => d.kind === 'videoinput')
      .map((d, i) => ({ id: d.deviceId, label: d.label || `カメラ ${i + 1}` }));
    send({ type: 'cameras', list });
  } catch (e) { /* 権限前は label が空 */ }
}

async function start() {
  status('カメラを起動中…');
  const stream = await navigator.mediaDevices.getUserMedia({
    video: { deviceId: deviceId ? { exact: deviceId } : undefined, width: 640, height: 480, frameRate: 30 },
    audio: false,
  });
  video.srcObject = stream;
  await video.play();
  reportCameras();

  status('顔認識モデルを読み込み中…');
  const fileset = await FilesetResolver.forVisionTasks('./vendor/mediapipe/wasm');
  const landmarker = await FaceLandmarker.createFromOptions(fileset, {
    baseOptions: { modelAssetPath: './vendor/mediapipe/face_landmarker.task', delegate: 'GPU' },
    runningMode: 'VIDEO',
    numFaces: 1,
    outputFaceBlendshapes: true,
    outputFacialTransformationMatrixes: true,
  });
  status('トラッキング中');

  // 30fps に制限（描画は 60fps のまま、補間は描画側）
  // 隠しウィンドウでも止まらないよう requestAnimationFrame ではなくタイマーで回す
  let lastVideoTime = -1, seen = 0;
  setInterval(() => {
    const now = performance.now();
    if (video.currentTime === lastVideoTime) return;
    lastVideoTime = video.currentTime;
    const r = landmarker.detectForVideo(video, now);
    if (!r.faceBlendshapes?.length) {
      if (seen !== 0) { seen = 0; status('顔が見つかりません'); }
      return;
    }
    if (seen === 0) { seen = 1; status('トラッキング中'); }
    const blend = {};
    for (const c of r.faceBlendshapes[0].categories) blend[c.categoryName] = c.score;
    // 変換行列（列優先）から頭の向きを求める。鏡のように動かすため左右（yaw・roll）は反転
    const m = r.facialTransformationMatrixes[0].data;
    const yaw = Math.atan2(m[8], m[10]);
    const pitch = Math.asin(Math.max(-1, Math.min(1, -m[9])));
    const roll = Math.atan2(m[1], m[5]);
    // 位置（cm 単位の平行移動）: 体の揺れ・前後の傾きに使う
    const tx = m[12] / 100, ty = m[13] / 100;
    send({ type: 'face', blend, head: { yaw: -yaw, pitch: pitch, roll: -roll, tx: -tx, ty } });
  }, 16);
}

start().catch((e) => status('カメラを使えません: ' + (e?.message || e)));
