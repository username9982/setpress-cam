'use strict';

// セットプレス CAM — 動作確認版: 四角形を描いて保存する。
// 座標はすべて mm。原点は板の左下、Y は上向き（機械の座標に合わせる）。

const APP_VERSION = '0.1.1';
const STORE_KEY = 'setpress-cam:drawings';   // 保存した図面 { 名前: {savedAt, doc} }
const CURRENT_KEY = 'setpress-cam:current';  // 作業中の図面（自動保存）

const $ = (id) => document.getElementById(id);
const canvas = $('canvas');
const ctx = canvas.getContext('2d');

// ---- 図面データ ----
function newDoc() {
  return { version: 1, name: '', sheet: { w: 1000, h: 500 }, rects: [] };
}
let doc = newDoc();
let selectedId = null;
let nextId = 1;
let dirty = false;
const undoStack = [];

function snapshot() {
  undoStack.push(JSON.stringify(doc));
  if (undoStack.length > 100) undoStack.shift();
}
function undo() {
  if (!undoStack.length) return;
  doc = JSON.parse(undoStack.pop());
  if (!doc.rects.some((r) => r.id === selectedId)) selectedId = null;
  changed();
}
function changed() {
  dirty = true;
  saveCurrent();
  updateUi();
  draw();
}
function saveCurrent() {
  try { localStorage.setItem(CURRENT_KEY, JSON.stringify({ doc, dirty })); } catch (e) { /* 保存できなくても続ける */ }
}
function round(v) { return Math.round(v * 1000) / 1000; }

// ---- 表示の変換（mm ⇔ 画面） ----
// fitted: 「全体表示」のままか（指やホイールで動かしたら false）
const view = { scale: 1, ox: 0, oy: 0, fitted: true }; // 画面 = (ox + x*scale, oy - y*scale)
let cssW = 0, cssH = 0;

function toScreen(x, y) { return [view.ox + x * view.scale, view.oy - y * view.scale]; }
function toWorld(sx, sy) { return [(sx - view.ox) / view.scale, (view.oy - sy) / view.scale]; }

function fitView() {
  const m = 40;
  const s = Math.min((cssW - 2 * m) / doc.sheet.w, (cssH - 2 * m) / doc.sheet.h);
  view.scale = Math.max(s, 0.01);
  view.ox = (cssW - doc.sheet.w * view.scale) / 2;
  view.oy = (cssH + doc.sheet.h * view.scale) / 2;
  view.fitted = true;
  draw();
}

// 画面の大きさが変わったとき（iPad の回転、ツールバーの折り返し）。
// 全体表示のままなら合わせ直し、拡大して見ていたなら、画面の中心に見えていた所を中心に保つ。
function resize() {
  const rect = canvas.getBoundingClientRect();
  if (rect.width === 0 || rect.height === 0) return;
  if (rect.width === cssW && rect.height === cssH) return;
  const firstTime = cssW === 0;
  const oldW = cssW, oldH = cssH;
  cssW = rect.width; cssH = rect.height;
  const dpr = window.devicePixelRatio || 1;
  canvas.width = Math.round(cssW * dpr);
  canvas.height = Math.round(cssH * dpr);
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  if (firstTime || view.fitted) {
    fitView();
  } else {
    view.ox += (cssW - oldW) / 2;
    view.oy += (cssH - oldH) / 2;
    draw();
  }
}

// ---- 描画 ----
function gridStep() {
  // 線の間隔が 8px 以上になる 1-2-5 系列の間隔
  const steps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000];
  return steps.find((s) => s * view.scale >= 8) || 1000;
}

function draw() {
  ctx.clearRect(0, 0, cssW, cssH);
  const { w, h } = doc.sheet;
  const [x0, y0] = toScreen(0, h);
  const sw = w * view.scale, sh = h * view.scale;

  // 板
  ctx.fillStyle = '#ffffff';
  ctx.fillRect(x0, y0, sw, sh);

  // グリッド
  const step = gridStep();
  const major = step * 10;
  ctx.lineWidth = 1;
  for (let x = 0; x <= w + 1e-9; x += step) {
    const isMajor = Math.abs(x / major - Math.round(x / major)) < 1e-6;
    ctx.strokeStyle = isMajor ? '#c3ccd6' : '#eef1f4';
    const [sx] = toScreen(x, 0);
    ctx.beginPath(); ctx.moveTo(Math.round(sx) + 0.5, y0); ctx.lineTo(Math.round(sx) + 0.5, y0 + sh); ctx.stroke();
  }
  for (let y = 0; y <= h + 1e-9; y += step) {
    const isMajor = Math.abs(y / major - Math.round(y / major)) < 1e-6;
    ctx.strokeStyle = isMajor ? '#c3ccd6' : '#eef1f4';
    const [, sy] = toScreen(0, y);
    ctx.beginPath(); ctx.moveTo(x0, Math.round(sy) + 0.5); ctx.lineTo(x0 + sw, Math.round(sy) + 0.5); ctx.stroke();
  }

  // 板の縁と原点
  ctx.strokeStyle = '#4a5a6a';
  ctx.lineWidth = 2;
  ctx.strokeRect(x0, y0, sw, sh);
  const [ox, oy] = toScreen(0, 0);
  ctx.fillStyle = '#c0392b';
  ctx.beginPath(); ctx.arc(ox, oy, 5, 0, Math.PI * 2); ctx.fill();
  ctx.font = '13px sans-serif';
  ctx.fillText('原点 (0, 0)', ox + 8, oy + 16);
  ctx.fillStyle = '#4a5a6a';
  ctx.fillText(`${w} × ${h} mm`, x0, y0 - 8);

  // 四角形
  for (const r of doc.rects) drawRect(r, r.id === selectedId ? 'selected' : 'normal');
  if (drag && drag.kind === 'draw' && drag.rect) drawRect(drag.rect, 'preview');
}

function drawRect(r, style) {
  const [sx, sy] = toScreen(r.x, r.y + r.h);
  const sw = r.w * view.scale, sh = r.h * view.scale;
  ctx.fillStyle = style === 'selected' ? 'rgba(232,163,61,0.25)' : 'rgba(47,128,209,0.12)';
  ctx.fillRect(sx, sy, sw, sh);
  ctx.strokeStyle = style === 'selected' ? '#d4881c' : '#2f80d1';
  ctx.lineWidth = style === 'normal' ? 1.5 : 2.5;
  ctx.setLineDash(style === 'preview' ? [6, 4] : []);
  ctx.strokeRect(sx, sy, sw, sh);
  ctx.setLineDash([]);

  if (style !== 'normal') {
    // 寸法と位置
    ctx.font = 'bold 14px sans-serif';
    ctx.fillStyle = '#1d2b38';
    ctx.textAlign = 'center';
    ctx.fillText(`${round(r.w)}`, sx + sw / 2, sy - 6);
    ctx.save();
    ctx.translate(sx - 8, sy + sh / 2);
    ctx.rotate(-Math.PI / 2);
    ctx.fillText(`${round(r.h)}`, 0, 0);
    ctx.restore();
    ctx.textAlign = 'left';
    ctx.font = '12px sans-serif';
    ctx.fillText(`(${round(r.x)}, ${round(r.y)})`, sx + 4, sy + sh + 16);
  }
}

// ---- タッチ・マウス操作 ----
// 指1本: 描く／選ぶ・動かす。指2本: 拡大縮小と移動。
let mode = 'draw';
const pointers = new Map();
let drag = null;  // {kind:'draw'|'move'|'pinch', ...}

function snapValue(v) {
  const s = parseFloat($('snap').value);
  return round(Math.round(v / s) * s);
}
function clampToSheet(x, y) {
  return [Math.min(Math.max(x, 0), doc.sheet.w), Math.min(Math.max(y, 0), doc.sheet.h)];
}
function eventPos(e) {
  const rect = canvas.getBoundingClientRect();
  return [e.clientX - rect.left, e.clientY - rect.top];
}
function hitTest(wx, wy) {
  const tol = 10 / view.scale;  // 指で押しやすいように少し広く
  for (let i = doc.rects.length - 1; i >= 0; i--) {
    const r = doc.rects[i];
    if (wx >= r.x - tol && wx <= r.x + r.w + tol && wy >= r.y - tol && wy <= r.y + r.h + tol) return r;
  }
  return null;
}

function startPinch() {
  const [a, b] = [...pointers.values()];
  drag = {
    kind: 'pinch',
    dist: Math.hypot(a[0] - b[0], a[1] - b[1]),
    mid: [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2],
  };
}

canvas.addEventListener('pointerdown', (e) => {
  canvas.setPointerCapture(e.pointerId);
  pointers.set(e.pointerId, eventPos(e));

  if (pointers.size === 2) {
    // 2本目の指が来たら、描きかけは取り消して拡大縮小にする
    if (drag && drag.kind === 'move' && drag.moved) undo();
    startPinch();
    draw();
    return;
  }
  if (pointers.size > 2) return;

  const [sx, sy] = eventPos(e);
  const [wx, wy] = toWorld(sx, sy);

  if (mode === 'draw') {
    const [px, py] = clampToSheet(snapValue(wx), snapValue(wy));
    drag = { kind: 'draw', start: [px, py], rect: null };
  } else {
    const hit = hitTest(wx, wy);
    selectedId = hit ? hit.id : null;
    if (hit) {
      snapshot();
      drag = { kind: 'move', id: hit.id, start: [wx, wy], orig: [hit.x, hit.y], moved: false };
    } else {
      drag = null;
    }
    updateUi();
    draw();
  }
});

canvas.addEventListener('pointermove', (e) => {
  const pos = eventPos(e);
  const [wx, wy] = toWorld(pos[0], pos[1]);
  $('st-pos').textContent = `X ${round(snapValue(wx))}  Y ${round(snapValue(wy))}`;
  if (!pointers.has(e.pointerId)) return;
  pointers.set(e.pointerId, pos);
  if (!drag) return;

  if (drag.kind === 'pinch' && pointers.size === 2) {
    const [a, b] = [...pointers.values()];
    const dist = Math.hypot(a[0] - b[0], a[1] - b[1]);
    const mid = [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2];
    const k = dist / (drag.dist || 1);
    // 指の中点を中心に拡大し、中点の移動ぶん動かす
    view.ox = mid[0] - (drag.mid[0] - view.ox) * k;
    view.oy = mid[1] - (drag.mid[1] - view.oy) * k;
    view.scale *= k;
    view.fitted = false;
    drag.dist = dist; drag.mid = mid;
    draw();
  } else if (drag.kind === 'draw') {
    const [px, py] = clampToSheet(snapValue(wx), snapValue(wy));
    const [x1, y1] = drag.start;
    drag.rect = { x: Math.min(x1, px), y: Math.min(y1, py), w: round(Math.abs(px - x1)), h: round(Math.abs(py - y1)) };
    draw();
  } else if (drag.kind === 'move') {
    const r = doc.rects.find((q) => q.id === drag.id);
    if (!r) return;
    const nx = snapValue(drag.orig[0] + wx - drag.start[0]);
    const ny = snapValue(drag.orig[1] + wy - drag.start[1]);
    r.x = Math.min(Math.max(nx, 0), round(doc.sheet.w - r.w));
    r.y = Math.min(Math.max(ny, 0), round(doc.sheet.h - r.h));
    if (r.x !== drag.orig[0] || r.y !== drag.orig[1]) drag.moved = true;
    updateProps();
    draw();
  }
});

function endPointer(e) {
  if (!pointers.has(e.pointerId)) return;
  pointers.delete(e.pointerId);
  if (!drag) return;

  if (drag.kind === 'pinch') {
    if (pointers.size < 2) drag = null;  // 指を全部離すまで描き始めない
    return;
  }
  if (drag.kind === 'draw') {
    const r = drag.rect;
    if (r && r.w > 0 && r.h > 0 && e.type !== 'pointercancel') {
      snapshot();
      const rect = { id: nextId++, x: r.x, y: r.y, w: r.w, h: r.h };
      doc.rects.push(rect);
      selectedId = rect.id;
      changed();
    }
  } else if (drag.kind === 'move') {
    if (drag.moved) changed(); else undoStack.pop();  // 動かしていなければ取り消し履歴を残さない
  }
  drag = null;
  draw();
}
canvas.addEventListener('pointerup', endPointer);
canvas.addEventListener('pointercancel', endPointer);

// マウスのホイールで拡大縮小（PC での確認用）
canvas.addEventListener('wheel', (e) => {
  e.preventDefault();
  const [sx, sy] = eventPos(e);
  const k = Math.exp(-e.deltaY * 0.0015);
  view.ox = sx - (sx - view.ox) * k;
  view.oy = sy - (sy - view.oy) * k;
  view.scale *= k;
  view.fitted = false;
  draw();
}, { passive: false });

// iOS Safari のジェスチャー（ページ全体の拡大）を止める
document.addEventListener('gesturestart', (e) => e.preventDefault());

// ---- 画面の部品 ----
function setMode(m) {
  mode = m;
  document.querySelectorAll('button.mode').forEach((b) => b.classList.toggle('active', b.dataset.mode === m));
}
document.querySelectorAll('button.mode').forEach((b) => b.addEventListener('click', () => setMode(b.dataset.mode)));

function selected() { return doc.rects.find((r) => r.id === selectedId) || null; }

function updateProps() {
  const r = selected();
  $('props').hidden = !r;
  if (!r) return;
  for (const [k, id] of [['x', 'p-x'], ['y', 'p-y'], ['w', 'p-w'], ['h', 'p-h']]) {
    if (document.activeElement !== $(id)) $(id).value = round(r[k]);
  }
}

function updateUi() {
  $('btn-delete').disabled = !selected();
  $('btn-undo').disabled = !undoStack.length;
  $('sheet-w').value = doc.sheet.w;
  $('sheet-h').value = doc.sheet.h;
  $('st-name').textContent = (doc.name || '名前なし') + (dirty ? ' *' : '');
  $('st-count').textContent = `四角形 ${doc.rects.length} 個`;
  updateProps();
}

function flash(msg) {
  $('st-msg').textContent = msg;
  clearTimeout(flash.t);
  flash.t = setTimeout(() => { $('st-msg').textContent = ''; }, 3000);
}

// 数値の入力欄: Enter・フォーカスが外れたときに確定し、範囲内に直す
function numberInput(id, min, max, apply) {
  const el = $(id);
  const commit = () => {
    let v = parseFloat(el.value);
    if (!Number.isFinite(v)) { updateUi(); return; }
    v = round(Math.min(Math.max(v, min()), max()));
    apply(v);
  };
  el.addEventListener('change', commit);
  el.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); el.blur(); } });
}

numberInput('sheet-w', () => 10, () => 5000, (v) => {
  if (v === doc.sheet.w) return;
  snapshot(); doc.sheet.w = v; changed(); fitView();
});
numberInput('sheet-h', () => 10, () => 5000, (v) => {
  if (v === doc.sheet.h) return;
  snapshot(); doc.sheet.h = v; changed(); fitView();
});
function propInput(id, key, min, max) {
  numberInput(id, min, max, (v) => {
    const r = selected();
    if (!r || r[key] === v) { updateUi(); return; }
    snapshot(); r[key] = v; changed();
  });
}
propInput('p-x', 'x', () => 0, () => doc.sheet.w - (selected()?.w || 0));
propInput('p-y', 'y', () => 0, () => doc.sheet.h - (selected()?.h || 0));
propInput('p-w', 'w', () => 0.1, () => doc.sheet.w - (selected()?.x || 0));
propInput('p-h', 'h', () => 0.1, () => doc.sheet.h - (selected()?.y || 0));

$('btn-undo').addEventListener('click', undo);
$('btn-fit').addEventListener('click', fitView);
$('btn-delete').addEventListener('click', () => {
  const r = selected();
  if (!r) return;
  snapshot();
  doc.rects = doc.rects.filter((q) => q !== r);
  selectedId = null;
  changed();
});

// ---- 保存・読み込み（端末のブラウザ内に保存） ----
function loadStore() {
  try { return JSON.parse(localStorage.getItem(STORE_KEY)) || {}; } catch (e) { return {}; }
}
function writeStore(store) {
  localStorage.setItem(STORE_KEY, JSON.stringify(store));
}

function confirmBox(text, okLabel = 'OK') {
  return new Promise((resolve) => {
    const dlg = $('dlg-confirm');
    $('confirm-text').textContent = text;
    $('confirm-ok').textContent = okLabel;
    dlg.onclose = () => resolve(dlg.returnValue === 'ok');
    dlg.returnValue = '';
    dlg.showModal();
  });
}

function loadDoc(d) {
  if (!d || !d.sheet || !Array.isArray(d.rects)) throw new Error('図面の形式が違います');
  doc = {
    version: 1,
    name: String(d.name || ''),
    sheet: { w: Number(d.sheet.w) || 1000, h: Number(d.sheet.h) || 500 },
    rects: d.rects.map((r, i) => ({ id: i + 1, x: Number(r.x), y: Number(r.y), w: Number(r.w), h: Number(r.h) }))
      .filter((r) => [r.x, r.y, r.w, r.h].every(Number.isFinite) && r.w > 0 && r.h > 0),
  };
  nextId = doc.rects.length + 1;
  selectedId = null;
  undoStack.length = 0;
  dirty = false;
  saveCurrent();
  updateUi();
  fitView();
}

$('btn-save').addEventListener('click', () => {
  $('save-name').value = doc.name || '';
  $('dlg-save').returnValue = '';
  $('dlg-save').showModal();
  $('save-name').select();
});
// Enter で「保存」が押されるように、キャンセルは送信ボタンにしない（先頭の送信ボタンが Enter で押されるため）
$('save-cancel').addEventListener('click', () => $('dlg-save').close('cancel'));
$('dlg-save').addEventListener('close', async () => {
  if ($('dlg-save').returnValue !== 'ok') return;
  const name = $('save-name').value.trim();
  if (!name) return;
  const store = loadStore();
  if (store[name] && name !== doc.name && !(await confirmBox(`「${name}」はすでにあります。上書きしますか？`, '上書き'))) return;
  doc.name = name;
  store[name] = { savedAt: new Date().toISOString(), doc };
  try {
    writeStore(store);
  } catch (e) {
    await confirmBox('保存できませんでした（端末の空き容量やブラウザの設定を確かめてください）。');
    return;
  }
  dirty = false;
  saveCurrent();
  updateUi();
  flash(`「${name}」を保存しました`);
});

function renderOpenList() {
  const list = $('open-list');
  list.textContent = '';
  const entries = Object.entries(loadStore()).sort((a, b) => b[1].savedAt.localeCompare(a[1].savedAt));
  if (!entries.length) {
    const li = document.createElement('li');
    li.className = 'empty';
    li.textContent = '保存した図面はありません';
    list.append(li);
    return;
  }
  for (const [name, item] of entries) {
    const li = document.createElement('li');
    const info = document.createElement('div');
    info.className = 'info';
    info.textContent = name;
    const small = document.createElement('small');
    const d = new Date(item.savedAt);
    small.textContent = `${d.toLocaleString('ja-JP')}・四角形 ${item.doc.rects.length} 個・${item.doc.sheet.w}×${item.doc.sheet.h} mm`;
    info.append(small);
    const open = document.createElement('button');
    open.type = 'button';
    open.textContent = '開く';
    open.className = 'primary';
    open.addEventListener('click', async () => {
      if (dirty && !(await confirmBox('いまの図面は保存されていません。破棄して開きますか？', '開く'))) return;
      $('dlg-open').close();
      loadDoc(item.doc);
      flash(`「${name}」を開きました`);
    });
    const del = document.createElement('button');
    del.type = 'button';
    del.textContent = '削除';
    del.className = 'del';
    del.addEventListener('click', async () => {
      if (!(await confirmBox(`「${name}」を削除しますか？元に戻せません。`, '削除'))) return;
      const store = loadStore();
      delete store[name];
      writeStore(store);
      renderOpenList();
    });
    li.append(info, open, del);
    list.append(li);
  }
}
$('btn-open').addEventListener('click', () => { renderOpenList(); $('dlg-open').showModal(); });

$('btn-new').addEventListener('click', async () => {
  if (dirty && !(await confirmBox('いまの図面は保存されていません。破棄して新しく始めますか？', '新規'))) return;
  const sheet = { ...doc.sheet };  // 板の大きさは引き継ぐ
  loadDoc({ ...newDoc(), sheet });
});

// ファイルへの書き出し・読み込み（バックアップや別の端末へ移すため）
$('btn-export').addEventListener('click', () => {
  const data = { app: 'setpress-cam', version: 1, exportedAt: new Date().toISOString(), drawings: loadStore() };
  const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  const t = new Date();
  const p = (n) => String(n).padStart(2, '0');
  a.download = `setpress-cam-${t.getFullYear()}${p(t.getMonth() + 1)}${p(t.getDate())}.json`;
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(a.href), 10000);
});
$('btn-import').addEventListener('click', () => $('import-file').click());
$('import-file').addEventListener('change', async () => {
  const file = $('import-file').files[0];
  $('import-file').value = '';
  if (!file) return;
  try {
    const data = JSON.parse(await file.text());
    if (data.app !== 'setpress-cam' || typeof data.drawings !== 'object') throw new Error('形式が違います');
    const store = loadStore();
    const names = Object.keys(data.drawings);
    const clash = names.filter((n) => store[n]);
    if (clash.length && !(await confirmBox(`${clash.length} 件の図面が同じ名前で上書きされます。続けますか？`, '読み込む'))) return;
    for (const n of names) store[n] = data.drawings[n];
    writeStore(store);
    renderOpenList();
    flash(`${names.length} 件の図面を読み込みました`);
  } catch (e) {
    await confirmBox(`読み込めませんでした: ${e.message}`);
  }
});

// ---- 起動 ----
// 回転のあと iOS が大きさを遅れて知らせることがあるので、キャンバス自体の大きさを見張る
if ('ResizeObserver' in window) new ResizeObserver(resize).observe(canvas);
else window.addEventListener('resize', resize);
window.addEventListener('beforeunload', (e) => { if (dirty) { e.preventDefault(); e.returnValue = ''; } });

(function start() {
  $('st-ver').textContent = `v${APP_VERSION}`;
  try {
    const cur = JSON.parse(localStorage.getItem(CURRENT_KEY));
    if (cur && cur.doc) { resize(); loadDoc(cur.doc); dirty = !!cur.dirty; saveCurrent(); updateUi(); return; }
  } catch (e) { /* 壊れていたら新しく始める */ }
  updateUi();
  resize();
})();

if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('sw.js').catch((e) => console.warn('Service Worker を登録できませんでした', e));
}
// ホーム画面のアプリでも、保存した図面が消されないように頼む
if (navigator.storage && navigator.storage.persist) navigator.storage.persist().catch(() => {});
