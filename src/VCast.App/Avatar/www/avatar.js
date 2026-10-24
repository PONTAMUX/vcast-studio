// 配信画面に映るアバター描画ページ（OBS のブラウザソースで開かれる。背景は透明）
// 入力はすべてアプリ本体から WebSocket で届く: モデル / 顔トラッキング / VMC / マイク音量 / 表情 / キーボード・マウス / 設定
import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { VRMLoaderPlugin, VRMUtils } from '@pixiv/three-vrm';

const renderer = new THREE.WebGLRenderer({ alpha: true, antialias: true, premultipliedAlpha: true });
renderer.setPixelRatio(1);
renderer.setClearColor(0x000000, 0);
renderer.outputColorSpace = THREE.SRGBColorSpace;
document.body.appendChild(renderer.domElement);

const scene = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(28, 1, 0.05, 20);
const ambient = new THREE.AmbientLight(0xffffff, 0.5);
const sun = new THREE.DirectionalLight(0xffffff, 1.0);
sun.position.set(0.3, 1, 1);
scene.add(ambient, sun);

function resize() {
  const w = window.innerWidth, h = window.innerHeight;
  renderer.setSize(w, h, false);
  camera.aspect = w / h;
  camera.updateProjectionMatrix();
  frame();
}
window.addEventListener('resize', resize);

// ---- 状態 ----
let vrm = null;
let sx = 1; // VRM0 は正規化ボーンのフレームが逆向きなので Euler の x と z の符号を反転する（three-vrm の Mixamo 変換と同じ扱い）
const cfg = {
  lipsyncMic: true, inputLink: true, framing: 'bust',
  headSensitivity: 1.2, mirror: true, zoom: 1.0, cameraHeight: 0, cameraYaw: 0, brightness: 1.0, expressionStrength: 1.0, walkMode: false, walkMouse: true,
};
const state = {
  face: null, faceAt: 0,
  vmc: null, vmcAt: 0,
  mic: -100,
  expression: 'neutral',
  move: { x: 0, z: 0 }, moveAt: 0,
  input: { typingL: 0, typingR: 0, mouseActive: false, mouseX: 0.5, mouseY: 0.5 },
  calib: { yaw: 0, pitch: 0, roll: 0, blinkL: 0.15, blinkR: 0.15 },
  needCalib: true, firstFaceAt: 0,
};
const customApplied = new Set(); // パーフェクトシンクで値を入れた表情名

// ---- 通信 ----
let sock = null;
const report = (text) => { if (sock && sock.readyState === 1) sock.send(JSON.stringify({ type: 'model-status', text })); };
// 描画ページ内のエラーはアプリのログへ送る（OBS のブラウザの中は直接見られないため）。同じものは一度だけ
const sentErrors = new Set();
const logError = (e) => {
  const msg = String(e?.stack || e?.message || e).slice(0, 600);
  if (sentErrors.has(msg) || !sock || sock.readyState !== 1) return;
  sentErrors.add(msg);
  sock.send(JSON.stringify({ type: 'log', text: msg }));
};
window.addEventListener('error', (ev) => logError(ev.error || ev.message));
window.addEventListener('unhandledrejection', (ev) => logError(ev.reason));
function connect() {
  const token = new URLSearchParams(location.search).get('t') || '';
  const ws = new WebSocket(`ws://${location.host}/ws?role=render&t=${encodeURIComponent(token)}`);
  sock = ws;
  ws.onmessage = (ev) => {
    const m = JSON.parse(ev.data);
    switch (m.type) {
      case 'model': loadModel(m.url); break;
      case 'face': state.face = m; state.faceAt = performance.now(); if (!state.firstFaceAt) state.firstFaceAt = state.faceAt; break;
      case 'vmc': state.vmc = m; state.vmcAt = performance.now(); break;
      case 'mic': state.mic = m.db; break;
      case 'expression': state.expression = m.name; break;
      case 'input': state.input = m; break;
      case 'move': state.move = m; state.moveAt = performance.now(); break;
      case 'calibrate': state.needCalib = true; state.firstFaceAt = 0; break;
      case 'config': Object.assign(cfg, m.config); applyLight(); frame(); break;
    }
  };
  ws.onclose = () => setTimeout(connect, 1000);
  ws.onopen = () => ws.send(JSON.stringify({ type: 'hello', role: 'render' }));
}
connect();

function applyLight() {
  ambient.intensity = 0.5 * cfg.brightness;
  sun.intensity = 1.0 * cfg.brightness;
}

// ---- モデル読み込み ----
const loader = new GLTFLoader();
loader.register((parser) => new VRMLoaderPlugin(parser));
let loadingUrl = null, loadGen = 0;

async function loadModel(url) {
  if (!url || url === loadingUrl) return;
  loadingUrl = url;
  const gen = ++loadGen; // 連続で選び直されたら最後のものだけ採用する
  try {
    const gltf = await loader.loadAsync(url);
    const next = gltf.userData.vrm;
    if (!next) throw new Error('VRM ではありません');
    if (gen !== loadGen) { VRMUtils.deepDispose(gltf.scene); return; }
    VRMUtils.removeUnnecessaryVertices(gltf.scene);
    VRMUtils.combineSkeletons?.(gltf.scene);
    VRMUtils.rotateVRM0(next); // VRM0 は後ろ向きなので正面へ（scene を回すだけ。ボーンのフレームは逆のまま）
    if (vrm) { scene.remove(vrm.scene); VRMUtils.deepDispose(vrm.scene); }
    vrm = next;
    walk.baseRotY = next.scene.rotation.y; walk.x = 0; walk.z = 0; walk.yaw = 0; walk.amp = 0;
    sx = vrm.meta?.metaVersion === '0' ? -1 : 1;
    vrm.scene.traverse((o) => { if (o.isMesh) o.frustumCulled = false; });
    for (const n of ['neck', 'head', 'spine', 'chest', 'hips']) { const b = bone(n); if (b) b.rotation.order = 'YXZ'; }
    if (vrm.lookAt) vrm.lookAt.autoUpdate = false;
    scene.add(vrm.scene);
    applyArms(1, 1, 0);
    frame();
    state.needCalib = true; state.firstFaceAt = 0;
    const license = vrm.meta?.commercialUssageName || vrm.meta?.commercialUsage || '';
    report(`表示中: ${vrm.meta?.name || vrm.meta?.title || 'VRM'}（${sx < 0 ? 'VRM0' : 'VRM1'}${license ? '・商用: ' + license : ''}）`);
  } catch (e) {
    console.error('model load failed', e);
    if (gen === loadGen) { report('読み込み失敗: ' + (e?.message || e)); loadingUrl = null; }
  }
}

const bone = (n) => vrm?.humanoid.getNormalizedBoneNode(n);
// Euler をモデルのフレームに合わせて設定（VRM0 は x,z 反転）
const setRot = (n, x, y, z) => { const b = bone(n); if (b) b.rotation.set(x * sx, y, z * sx); };

// 画角: 胸から上 / 全身 をモデルの外接箱から計算し、ズーム・高さ・回り込みを足す
function frame() {
  if (!vrm) return;
  const p0 = vrm.scene.position.clone(); vrm.scene.position.set(0, 0, 0); // 歩いて動いた分は画角に含めない
  vrm.scene.updateMatrixWorld(true);
  const box = new THREE.Box3().setFromObject(vrm.scene);
  vrm.scene.position.copy(p0); vrm.scene.updateMatrixWorld(true);
  walk.height = Math.max(0.1, box.max.y - box.min.y);
  walk.halfBody = Math.max(walk.height * 0.2, (box.max.x - box.min.x) / 2); // 画面端で体が切れないための余白
  const height = Math.max(0.1, box.max.y - box.min.y);
  const head = new THREE.Vector3();
  bone('head').getWorldPosition(head);
  let yMin, yMax;
  if (cfg.framing === 'full' || cfg.walkMode) { yMin = box.min.y - height * 0.03; yMax = box.max.y + height * 0.05; }
  else { yMin = head.y - Math.max(height * 0.3, (box.max.y - head.y) * 2.2); yMax = box.max.y + height * 0.06; }
  yMin = Math.max(yMin, box.min.y - height * 0.03); // 小さいモデルで下が空かないように
  // 歩かせるモード: 画面の 45% の高さに縮め、足が画面の下寄りに来るようにする（歩き回る余白を作る）
  const zoom = cfg.walkMode ? 0.45 : cfg.zoom;
  const span = (yMax - yMin) / Math.max(0.3, zoom);
  const centerY = cfg.walkMode ? box.min.y - span * 0.14 + span / 2 : (yMin + yMax) / 2 + cfg.cameraHeight * height;
  const dist = span / 2 / Math.tan(THREE.MathUtils.degToRad(camera.fov / 2));
  const yaw = THREE.MathUtils.degToRad(cfg.cameraYaw);
  const cz = box.getCenter(new THREE.Vector3()).z;
  camera.position.set(Math.sin(yaw) * (dist + 0.1), centerY, cz + Math.cos(yaw) * (dist + 0.1));
  camera.lookAt(0, centerY, cz);
}
resize();

// ---- フィルタ ----
// One Euro filter: 止まっているときはジッタを強く抑え、速く動くと遅れを減らす（顔トラッキングの定番）
class OneEuro {
  constructor(minCutoff = 1.0, beta = 0.3, dCutoff = 1.0) { this.minCutoff = minCutoff; this.beta = beta; this.dCutoff = dCutoff; this.x = null; this.dx = 0; }
  static alpha(cutoff, dt) { const tau = 1 / (2 * Math.PI * cutoff); return 1 / (1 + tau / dt); }
  filter(v, dt) {
    if (this.x === null) { this.x = v; return v; }
    const dv = (v - this.x) / dt;
    this.dx += OneEuro.alpha(this.dCutoff, dt) * (dv - this.dx);
    const cutoff = this.minCutoff + this.beta * Math.abs(this.dx);
    this.x += OneEuro.alpha(cutoff, dt) * (v - this.x);
    return this.x;
  }
}
const headF = { yaw: new OneEuro(), pitch: new OneEuro(), roll: new OneEuro(), tx: new OneEuro(0.8, 0.2), ty: new OneEuro(0.8, 0.2) };
// 時定数 tau 秒で目標へ近づく（上げ下げで速さを変えられる）
const approach = (cur, target, dt, tauUp, tauDown = tauUp) => cur + (target - cur) * (1 - Math.exp(-dt / (target > cur ? tauUp : tauDown)));
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
const clamp01 = (v) => clamp(v, 0, 1);

const sm = { aa: 0, ih: 0, ou: 0, ee: 0, oh: 0, blinkL: 0, blinkR: 0, happy: 0, angry: 0, sad: 0, relaxed: 0, surprised: 0, armL: 0, armR: 0, eyeYaw: 0, eyePitch: 0, lean: 0, sway: 0 };
let nextBlink = performance.now() + 3000, blinkPhase = -1, doubleBlink = false;
let micFloor = -60, micFloorAt = 0;
let lastTypeL = -1e9, lastTypeR = -1e9;
let wander = { yaw: 0, pitch: 0, next: 0 };

// ---- 歩かせるモード（WASD） ----
const walk = { x: 0, z: 0, yaw: 0, amp: 0, phase: 0, baseRotY: 0, height: 1.5, halfBody: 0.3, was: false };
const rotX = (a) => _qd.setFromAxisAngle(_ax, a * sx);
const _ax = new THREE.Vector3(1, 0, 0);
function applyWalk(now, dt) {
  const on = cfg.walkMode;
  if (!on) {
    if (walk.was) { vrm.scene.position.set(0, 0, 0); vrm.scene.rotation.y = walk.baseRotY; walk.x = walk.z = walk.yaw = walk.amp = 0; walk.was = false; frame(); }
    return;
  }
  if (!walk.was) { walk.was = true; frame(); }
  const fresh = now - state.moveAt < 500;
  let mx = fresh ? clamp(state.move.x || 0, -1, 1) : 0, mz = fresh ? clamp(state.move.z || 0, -1, 1) : 0; // x: 右が+、z: 奥(W)が+
  // マウス: 左ボタンを押している間、カーソルの位置へ歩く（画面の左右=左右、上=奥、下=手前）。キー入力があればキーを優先
  if (fresh && cfg.walkMouse && state.move.mouse && mx === 0 && mz === 0) {
    const d0 = Math.max(0.5, camera.position.z - walk.z);
    const half = d0 * Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)) * camera.aspect;
    const tx = (state.move.mx * 2 - 1) * Math.max(0.05, half - walk.halfBody);
    const farZ = -walk.height * 2.5, nearZ = camera.position.z - walk.height * 1.2;
    const tz = farZ + state.move.my * (nearZ - farZ);
    const dx = tx - walk.x, dz = walk.z - tz; // dz: 奥へ行くほど正（キーの z と同じ向き）
    const dist = Math.hypot(dx, dz), stop = walk.height * 0.08;
    if (dist > stop) { const k = clamp((dist - stop) / (walk.height * 0.4), 0.25, 1); mx = dx / dist * k; mz = dz / dist * k; }
  }
  const len = Math.hypot(mx, mz);
  if (len > 1) { mx /= len; mz /= len; }
  const run = fresh && !!state.move.run && len > 0.1;
  const speed = walk.height * (run ? 1.9 : 0.9);
  walk.x += mx * speed * dt;
  walk.z -= mz * speed * dt; // 奥へ行くほど z は小さい
  // 画面の外へ出ない範囲に収める（奥行きごとの見える幅）
  const d = Math.max(0.5, camera.position.z - walk.z);
  const halfW = d * Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)) * camera.aspect;
  const lim = Math.max(0.05, halfW - walk.halfBody);
  walk.x = clamp(walk.x, -lim, lim);
  walk.z = clamp(walk.z, -walk.height * 2.5, camera.position.z - walk.height * 1.2);
  vrm.scene.position.set(walk.x, 0, walk.z);
  // 進む向きへ体を向ける（止まっているときは今の向きのまま）
  const turn = fresh ? clamp(state.move.turn || 0, -1, 1) : 0; // Q左 / E右
  if (turn !== 0) walk.yaw += turn * 3.2 * dt;
  else if (len > 0.1) {
    const target = Math.atan2(mx, -mz); // 世界のz軸は手前が+。Wで奥へ行くときは背中を向ける
    let diff = target - walk.yaw; diff = Math.atan2(Math.sin(diff), Math.cos(diff));
    walk.yaw += diff * (1 - Math.exp(-dt / 0.08));
  }
  vrm.scene.rotation.y = walk.baseRotY + walk.yaw;
  // 歩きモーション: 足の振り・膝・腕の振り・体の上下
  walk.amp = approach(walk.amp, len > 0.1 ? 1 : 0, dt, 0.08, 0.15);
  walk.phase += dt * (run ? 12.5 : 7.5) * (0.4 + 0.6 * len);
  const a = walk.amp;
  if (a > 0.002) {
    const s = Math.sin(walk.phase), c = Math.cos(walk.phase);
    const sw = (run ? 0.8 : 0.55) * a;
    // 正の回転 = 後ろへ振る。前に出す側は負
    setRot('leftUpperLeg', s * sw, 0, 0);
    setRot('rightUpperLeg', -s * sw, 0, 0);
    setRot('leftLowerLeg', Math.max(0, -c) * 0.9 * a, 0, 0);
    setRot('rightLowerLeg', Math.max(0, c) * 0.9 * a, 0, 0);
    for (const [n, ph] of [['leftUpperArm', -s], ['rightUpperArm', s]]) {
      const b = bone(n); if (b) b.quaternion.premultiply(rotX(ph * 0.45 * a));
    }
    const hips = bone('hips'); if (hips) hips.position.y = (walk.hipsY ??= hips.position.y) + Math.abs(Math.cos(walk.phase)) * 0.025 * walk.height * a;
    setRot('spine', 0.06 * a, s * 0.08 * a, 0);
  } else if (walk.hipsY !== undefined) {
    for (const n of ['leftUpperLeg', 'rightUpperLeg', 'leftLowerLeg', 'rightLowerLeg']) setRot(n, 0, 0, 0);
    const hips = bone('hips'); if (hips) hips.position.y = walk.hipsY;
  }
}

// ---- 毎フレーム ----
const clock = new THREE.Clock();
function tick() {
  requestAnimationFrame(tick);
  const dt = Math.min(clock.getDelta(), 0.1);
  const now = performance.now();
  // 動きの計算で例外が出ても描画は止めない
  if (vrm) {
    try { applyMotion(now, dt); } catch (e) { logError(e); }
    try { applyWalk(now, dt); } catch (e) { logError(e); }
    try { vrm.update(dt); } catch (e) { logError(e); }
  }
  renderer.render(scene, camera);
}
requestAnimationFrame(tick);

function applyMotion(now, dt) {
  const faceLive = state.face && now - state.faceAt < 800;
  const vmcLive = state.vmc && now - state.vmcAt < 800;
  const t = now / 1000;
  const mir = cfg.mirror ? 1 : -1;

  // --- 正面リセット（ボタン、または顔を捉えてから 2 秒後に自動） ---
  if (faceLive && state.needCalib) {
    if (!state.firstFaceAt) state.firstFaceAt = now;
    if (now - state.firstFaceAt > 2000) {
      const h = state.face.head, f = state.face.blend;
      state.calib = { yaw: h.yaw, pitch: h.pitch, roll: h.roll, blinkL: Math.min(0.5, f.eyeBlinkLeft), blinkR: Math.min(0.5, f.eyeBlinkRight) };
      state.needCalib = false;
    }
  }

  // --- 頭の向き（One Euro → クランプ → 感度） ---
  let yaw = 0, pitch = 0, roll = 0, tx = 0, ty = 0;
  if (faceLive) {
    const h = state.face.head, c = state.calib, k = cfg.headSensitivity;
    yaw = clamp((h.yaw - c.yaw) * k, -0.8, 0.8) * mir;
    pitch = clamp((h.pitch - c.pitch) * k, -0.5, 0.5);
    roll = clamp((h.roll - c.roll) * k, -0.45, 0.45) * mir;
    tx = (h.tx || 0) * mir; ty = h.ty || 0;
  } else if (!vmcLive) {
    // 待機: ゆっくりしたゆらぎ（周期をずらした波の合成）
    yaw = Math.sin(t * 0.31) * 0.04 + Math.sin(t * 0.77) * 0.015;
    pitch = Math.sin(t * 0.23) * 0.025 + Math.sin(t * 0.61) * 0.01;
    roll = Math.sin(t * 0.19) * 0.02;
  }
  yaw = headF.yaw.filter(yaw, dt); pitch = headF.pitch.filter(pitch, dt); roll = headF.roll.filter(roll, dt);
  tx = headF.tx.filter(tx, dt); ty = headF.ty.filter(ty, dt);

  if (!vmcLive || faceLive) {
    setRot('neck', pitch * 0.4, yaw * 0.4, roll * 0.4);
    setRot('head', pitch * 0.6, yaw * 0.6, roll * 0.6);
    // 呼吸と、頭の位置に合わせた体の傾き（生きている感じ）
    const breath = Math.sin(t * 1.6);
    sm.sway = approach(sm.sway, clamp(tx * 2.0, -0.12, 0.12), dt, 0.25);
    sm.lean = approach(sm.lean, clamp(-ty * 1.5, -0.1, 0.1), dt, 0.25);
    setRot('spine', breath * 0.012 + sm.lean * 0.5, yaw * 0.1, sm.sway * 0.6);
    setRot('chest', breath * 0.008, yaw * 0.05, sm.sway * 0.3);
    setRot('hips', 0, Math.sin(t * 0.13) * 0.02, 0);
  }
  if (vmcLive) applyVmcBones(faceLive);

  // --- 目線 ---
  let eyeYaw = 0, eyePitch = 0;
  if (faceLive) {
    const f = state.face.blend;
    const horiz = ((f.eyeLookInLeft - f.eyeLookOutLeft) + (f.eyeLookOutRight - f.eyeLookInRight)) / 2;
    eyeYaw = horiz * 22 * mir;
    eyePitch = (((f.eyeLookUpLeft + f.eyeLookUpRight) - (f.eyeLookDownLeft + f.eyeLookDownRight)) / 2) * 15;
  } else if (!vmcLive) {
    // たまに視線を移し（すばやく）、しばらく留まる
    if (now > wander.next) {
      wander = { yaw: (Math.random() - 0.5) * 16, pitch: (Math.random() - 0.5) * 8, next: now + 3000 + Math.random() * 5000 };
      if (Math.random() < 0.4) wander.yaw = wander.pitch = 0;
    }
    eyeYaw = wander.yaw; eyePitch = wander.pitch;
  }
  sm.eyeYaw = approach(sm.eyeYaw, eyeYaw, dt, 0.05); sm.eyePitch = approach(sm.eyePitch, eyePitch, dt, 0.05);
  if (vrm.lookAt) { vrm.lookAt.yaw = sm.eyeYaw; vrm.lookAt.pitch = sm.eyePitch; }

  // --- 口・目・表情の目標値 ---
  const tgt = { aa: 0, ih: 0, ou: 0, ee: 0, oh: 0, blinkL: 0, blinkR: 0, happy: 0, angry: 0, sad: 0, relaxed: 0, surprised: 0 };
  const em = vrm.expressionManager;
  const perfectSync = !!em?.getExpression('jawOpen');
  if (faceLive) {
    const f = state.face.blend, c = state.calib;
    if (!perfectSync) { // パーフェクトシンクのモデルは生の値を使うので、派生の母音は二重にかけない
      tgt.aa = clamp01(f.jawOpen * 1.6);
      tgt.oh = clamp01(f.mouthFunnel * 1.4);
      tgt.ou = clamp01(f.mouthPucker * 1.2);
      tgt.ee = clamp01((f.mouthStretchLeft + f.mouthStretchRight) * 0.8);
      tgt.ih = clamp01((f.mouthSmileLeft + f.mouthSmileRight) * 0.25);
    }
    // 開眼時の値（キャリブ）を 0 に、0.6 を超えたら閉じ切る
    const bl = clamp01((f.eyeBlinkLeft - c.blinkL) / Math.max(0.2, 0.75 - c.blinkL));
    const br = clamp01((f.eyeBlinkRight - c.blinkR) / Math.max(0.2, 0.75 - c.blinkR));
    // 鏡にするときは左右の目も入れ替える（頭の向きと一致させる。※片目ウインクで要確認）
    const [L, R] = cfg.mirror ? [br, bl] : [bl, br];
    tgt.blinkL = L > 0.6 ? 1 : L; tgt.blinkR = R > 0.6 ? 1 : R;
    tgt.happy = clamp01((f.mouthSmileLeft + f.mouthSmileRight) * 0.6 - 0.15);
    tgt.surprised = clamp01((f.browInnerUp - 0.4) * 1.5) * clamp01((f.eyeWideLeft + f.eyeWideRight) * 1.5);
  } else if (vmcLive) {
    for (const [k2, v] of Object.entries(state.vmc.blend || {})) {
      const key = k2.toLowerCase();
      const val = Number(v) || 0;
      if (key === 'a' || key === 'aa') tgt.aa = val; else if (key === 'i' || key === 'ih') tgt.ih = val;
      else if (key === 'u' || key === 'ou') tgt.ou = val; else if (key === 'e' || key === 'ee') tgt.ee = val;
      else if (key === 'o' || key === 'oh') tgt.oh = val; else if (key === 'blink_l' || key === 'blinkleft') tgt.blinkL = val;
      else if (key === 'blink_r' || key === 'blinkright') tgt.blinkR = val; else if (key === 'blink') { tgt.blinkL = val; tgt.blinkR = val; }
      else if (key === 'joy' || key === 'happy') tgt.happy = val; else if (key === 'sorrow') tgt.sad = val;
      else if (key === 'fun') tgt.relaxed = val; else if (key in tgt) tgt[key] = val;
    }
  }
  // カメラがない（口が取れない）ときはマイクの声で口パク: ノイズ床を追って、声の分だけ開く
  if (!faceLive && !vmcLive && cfg.lipsyncMic) {
    const db = Number.isFinite(state.mic) ? state.mic : -100;
    if (db < micFloor || now - micFloorAt > 2000) { micFloor = Math.max(-80, Math.min(db, micFloor + 1)); micFloorAt = now; }
    const v = Math.pow(clamp01((db - (micFloor + 6)) / 25), 0.7);
    tgt.aa = v * 0.75;
    tgt.oh = v * 0.15;
  }
  // 母音の合計が 1 を超えたら正規化（口が破裂しないように）
  const vsum = tgt.aa + tgt.ih + tgt.ou + tgt.ee + tgt.oh;
  if (vsum > 1) for (const k of ['aa', 'ih', 'ou', 'ee', 'oh']) tgt[k] /= vsum;

  // 自動まばたき（トラッキングがないとき）: 閉じるのは速く、開くのはゆっくり。ときどき二連
  if (!faceLive && !vmcLive) {
    if (blinkPhase < 0 && now > nextBlink) blinkPhase = 0;
    if (blinkPhase >= 0) {
      blinkPhase += dt / 0.18;
      const v = blinkPhase < 0.35 ? blinkPhase / 0.35 : Math.max(0, 1 - Math.pow((blinkPhase - 0.35) / 0.65, 0.6));
      tgt.blinkL = tgt.blinkR = v;
      if (blinkPhase >= 1) {
        blinkPhase = -1;
        if (!doubleBlink && Math.random() < 0.15) { doubleBlink = true; nextBlink = now + 120; }
        else { doubleBlink = false; nextBlink = now + 2000 + Math.random() * 4000; }
      }
    }
  }
  // ホットキーの表情は他より優先（口は動かし続ける）
  const hot = state.expression && state.expression !== 'neutral' ? state.expression : null;
  if (hot) for (const e of ['happy', 'angry', 'sad', 'relaxed', 'surprised']) tgt[e] = e === hot ? cfg.expressionStrength : 0;

  // チャンネルごとの速さ: 口は開くのが速く閉じるのは少し遅く、まばたきは即、表情はゆっくり
  for (const k of ['aa', 'ih', 'ou', 'ee', 'oh']) sm[k] = approach(sm[k], tgt[k], dt, 0.03, 0.1);
  for (const k of ['blinkL', 'blinkR']) sm[k] = faceLive ? approach(sm[k], tgt[k], dt, 0.02, 0.04) : tgt[k];
  for (const k of ['happy', 'angry', 'sad', 'relaxed', 'surprised']) sm[k] = approach(sm[k], tgt[k], dt, 0.12);

  if (em) {
    const set = (name, v) => { if (em.getExpression(name)) em.setValue(name, v); };
    set('aa', sm.aa); set('ih', sm.ih); set('ou', sm.ou); set('ee', sm.ee); set('oh', sm.oh);
    set('blinkLeft', sm.blinkL); set('blinkRight', sm.blinkR); // 表情と重なるときの抑制はモデル側の overrideBlink に任せる
    set('happy', sm.happy); set('angry', sm.angry); set('sad', sm.sad); set('relaxed', sm.relaxed); set('surprised', sm.surprised);
    // パーフェクトシンク対応モデル（ARKit 名の表情を持つ）は生の値をそのまま渡す。顔が外れたら 0 に戻す
    if (faceLive) {
      for (const [name, v] of Object.entries(state.face.blend)) {
        if (em.getExpression(name)) { em.setValue(name, v); customApplied.add(name); }
      }
    } else if (customApplied.size) {
      for (const name of customApplied) em.setValue(name, 0);
      customApplied.clear();
    }
  }

  // --- キーボード・マウスと手の連動 ---
  if (!vmcLive) {
    const inp = state.input;
    if (cfg.inputLink && inp.typingL) lastTypeL = now;
    if (cfg.inputLink && (inp.typingR || inp.mouseActive)) lastTypeR = now;
    // 上げるのは速く、下ろすのは 2.5 秒待ってからゆっくり（考え中に腕が上下しないように）
    sm.armL = approach(sm.armL, now - lastTypeL < 2500 ? 1 : 0, dt, 0.15, 0.5);
    sm.armR = approach(sm.armR, now - lastTypeR < 2500 ? 1 : 0, dt, 0.15, 0.5);
    applyArms(1 - sm.armL, 1 - sm.armR, t, inp);
  }
}

// 腕の姿勢は「どちらを向くか」で指定して、T ポーズからの回転を求める（Euler の足し算は VRM0/1 の向きでずれるため）
// 正規化ボーンは T ポーズ・ワールド軸そろいなので、子の回転 = 親の回転の逆 × ワールドでの回転
// キャラクター基準: side=+1 が本人の左、fwd=+1 が本人の前（VRM1 は左=+X・前=+Z、VRM0 はどちらも逆 → sx を掛ける）
const _v1 = new THREE.Vector3(), _v2 = new THREE.Vector3(), _qa = new THREE.Quaternion(), _qb = new THREE.Quaternion(), _qc = new THREE.Quaternion(), _qd = new THREE.Quaternion();
function armDir(side, x, y, z) { return new THREE.Vector3(side * x * sx, y, z * sx).normalize(); }
function aim(out, rest, target) { return out.setFromUnitVectors(rest, target); }

// down: 1=下ろした腕 0=机に手を置いた姿勢
function applyArms(downL, downR, t, inp = state.input) {
  const mouse = cfg.inputLink && inp.mouseActive ? (inp.mouseX - 0.5) : 0;
  for (const [name, side, down, tap, mx] of [
    ['left', 1, downL, inp.typingL ? Math.sin(t * 22) * 0.06 : 0, 0],
    ['right', -1, downR, inp.typingR ? Math.sin(t * 19 + 1) * 0.06 : 0, mouse],
  ]) {
    const upper = bone(name + 'UpperArm'), lower = bone(name + 'LowerArm');
    if (!upper || !lower) continue;
    const rest = armDir(side, 1, 0, 0); // T ポーズでの腕の向き（横）
    const k = 1 - down; // 0=下ろす 1=机
    // 二の腕: 下ろした状態は体の横、机では肘を体の横に置いて少し前
    _v1.copy(armDir(side, 0.32, -0.95, 0.02)).lerp(armDir(side, 0.22, -0.9, 0.3), k).normalize();
    // 前腕: 下ろした状態は少しだけ曲げる、机では前へ（打鍵で小刻みに上下、マウスで右手が左右に）
    _v2.copy(armDir(side, 0.18, -0.97, 0.12)).lerp(armDir(side, -0.12 + mx * 0.5 * -side, -0.18 + tap, 0.97), k).normalize();
    aim(_qa, rest, _v1);                // 二の腕のワールド回転
    aim(_qb, rest, _v2);                // 前腕のワールド回転
    upper.quaternion.copy(_qa);
    lower.quaternion.copy(_qc.copy(_qa).invert().multiply(_qb));
  }
}

// VMC のボーン回転（ローカル回転のクォータニオン、Unity 左手系）を three.js の正規化ボーンへ
function applyVmcBones(faceLive) {
  const bones = state.vmc.bones || {};
  const q = new THREE.Quaternion();
  for (const [name, v] of Object.entries(bones)) {
    const key = name.charAt(0).toLowerCase() + name.slice(1);
    if (faceLive && (key === 'head' || key === 'neck')) continue; // 頭は顔トラッキング優先
    const node = bone(key);
    if (!node) continue;
    // 左手系 → 右手系の反転に加え、VRM0 はフレームが逆向き（※VSeeFace 実機で要確認）
    if (sx < 0) q.set(-v[0], -v[1], v[2], v[3]); else q.set(v[0], -v[1], -v[2], v[3]);
    node.quaternion.slerp(q, 0.5);
  }
}
