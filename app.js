'use strict';

// セットプレス CAM — 図形を描いて寸法を決める画面。図形のデータと解き方は geom.js。
// 座標はすべて mm。内部では板の左下を (0, 0)、Y を上向きに持ち、表示は外形ごとのワーク原点（外形の左上、Y は下へ正）からの値にする。

const APP_VERSION = '0.13.0';
const STORE_KEY = 'setpress-cam:drawings';   // 保存した図面 { 名前: {savedAt, doc} }
const CURRENT_KEY = 'setpress-cam:current';  // 作業中の図面（自動保存）
const UNDO_KEY = 'setpress-cam:undo';  // 作業中の図面の「元に戻す」の履歴（再読み込みしても戻せるように）
const UNDO_MAX = 2000;  // 履歴の数の上限
const UNDO_CHARS = 2000000;  // 履歴の文字数の上限（localStorage はおよそ 500 万文字まで）。超えたら古いものから捨てる

const $ = (id) => document.getElementById(id);
const canvas = $('canvas');
const ctx = canvas.getContext('2d');

// ---- 図面データ ----
// 金型の形: 長方形（幅・高さ）、丸（直径）、異形（外形の幅・高さとメモ）、使わない
const SHAPES = {
  rect: '長方形',
  round: '丸',
  special: '異形',
  none: '使わない',
};
const TOOL_COUNT = 3;  // T1〜T3
const PROGRAM_MIN = 1, PROGRAM_MAX = 9999;

function emptyTool() { return { shape: 'none', w: null, h: null, d: null, note: '' }; }
// points: [{id, x, y}]、lines: [{id, a, b, dir:'h'|'v'}]、
// dims: [{id, axis:'x'|'y', a, b, value, kind, refs:[{t:'point'|'line', id}]}]（b − a = value）
function newDoc() {
  return {
    version: 4,
    name: '',
    program: null,  // プログラム番号
    tools: Array.from({ length: TOOL_COUNT }, emptyTool),
    sheet: { w: 1000, h: 500, t: null },  // t: 板厚（mm、未設定は null）
    points: [],
    lines: [],
    dims: [],
    nextId: 1,
  };
}
function posNum(v) {
  const n = Number(v);
  return v !== null && v !== '' && Number.isFinite(n) && n > 0 ? round(n) : null;
}
// 保存データの金型を整える（古い図面には金型が無い）
function normalizeTools(tools) {
  return Array.from({ length: TOOL_COUNT }, (unused, i) => {
    const t = (Array.isArray(tools) && tools[i]) || {};
    return {
      shape: SHAPES[t.shape] ? t.shape : 'none',
      w: posNum(t.w), h: posNum(t.h), d: posNum(t.d),
      note: String(t.note || '').slice(0, 40),
    };
  });
}
// 金型の説明（例: "長方形 20 x 10"、"丸 φ8"）
function toolText(t) {
  if (t.shape === 'rect') return `長方形 ${t.w} x ${t.h}`;
  if (t.shape === 'round') return `丸 φ${t.d}`;
  if (t.shape === 'special') return `異形 ${t.w} x ${t.h}${t.note ? ` (${t.note})` : ''}`;
  return '使わない';
}
function programText(p) { return p == null ? 'O----' : 'O' + String(p).padStart(4, '0'); }

let doc = newDoc();
let sel = [];  // 選んだ要素 [{t:'point'|'line'|'dim', id}]（寸法では 2 つまで、移動ではいくつでも）
let dirty = false;
let info = Geo.analyze(doc);  // 確定しているか（図形を変えるたびに作り直す）
let shapes = [];  // 閉じた図形 [{poly, area, role:'outer'|'hole'|'notch', parent, outer}]（info.shapes と同じ）
const undoStack = [];

function newId() { return doc.nextId++; }
function snapshot() {
  undoStack.push(JSON.stringify(doc));
  let total = undoStack.reduce((n, t) => n + t.length, 0);
  while (undoStack.length > 1 && (undoStack.length > UNDO_MAX || total > UNDO_CHARS)) total -= undoStack.shift().length;
}
function undo() {
  if (!undoStack.length) return;
  doc = JSON.parse(undoStack.pop());
  dropMissingSelection();
  changed();
}
function changed() {
  applyPlacements();
  dirty = true;
  saveCurrent();
  refresh();
}
// 板の原点からの寸法（kind 'sheet'）に合わせて、外形を穴・切り欠き・つながった線ごと平行移動する。
// 値は板の左上から右へ X+、下へ Y+。寸法の b は外形の点で、その点が属する外形を動かす。
function applyPlacements() {
  const sd = doc.dims.filter((d) => d.kind === 'sheet');
  if (!sd.length) return;
  const a = Geo.analyze(doc);
  const shift = new Map();
  for (const d of sd) {
    const k = a.owner.get(d.b);
    if (k == null) continue;
    const o = a.outers[k].origin;
    const sh = shift.get(k) || { x: 0, y: 0 };
    if (d.axis === 'x') sh.x = d.value - o.x; else sh.y = (doc.sheet.h - d.value) - o.y;
    shift.set(k, sh);
  }
  for (const p of doc.points) {
    const sh = shift.get(a.owner.get(p.id));
    if (sh) { p.x = round(p.x + sh.x); p.y = round(p.y + sh.y); }
  }
}
// 外形の原点の、板の原点からの位置（右へ X+、下へ Y+）
function sheetPos(k, axis) {
  const o = info.outers[k].origin;
  return round(axis === 'x' ? o.x : doc.sheet.h - o.y);
}
function sheetDimOf(k, axis) { return doc.dims.find((d) => d.kind === 'sheet' && d.axis === axis && info.owner.get(d.b) === k); }
function refresh() {
  info = Geo.analyze(doc);
  shapes = info.shapes;
  updateUi();
  draw();
}
function saveCurrent() {
  try { localStorage.setItem(CURRENT_KEY, JSON.stringify({ doc, dirty })); } catch (e) { /* 保存できなくても続ける */ }
  saveUndo();
}
// 履歴を保存する。入りきらなければ古いものから半分ずつ捨てて入れ直す（図面のほうを先に保存してある）
function saveUndo() {
  for (;;) {
    try { localStorage.setItem(UNDO_KEY, JSON.stringify(undoStack)); return; } catch (e) {
      if (!undoStack.length) return;
      undoStack.splice(0, Math.ceil(undoStack.length / 2));
    }
  }
}
function loadUndo() {
  try {
    const st = JSON.parse(localStorage.getItem(UNDO_KEY));
    return Array.isArray(st) ? st.filter((t) => typeof t === 'string') : [];
  } catch (e) { return []; }
}
function round(v) { return Math.round(v * 1000) / 1000; }

const P = (id) => Geo.pointById(doc, id);
const L = (id) => Geo.lineById(doc, id);
const D = (id) => doc.dims.find((d) => d.id === id);
// ワーク原点は外形ごとに、外形を囲む四角の左上。座標は原点から右へ X+、下へ Y+ で表す。
// どの外形にも属さないものは、板の左上から測る。
const sheetTopLeft = () => ({ x: 0, y: doc.sheet.h });
function originAt(x, y) { const k = info.outerAt(x, y); return k == null ? sheetTopLeft() : info.outers[k].origin; }
function rel(o, x, y) { return { x: round(x - o.x), y: round(o.y - y) }; }
function elemExists(s) {
  if (s.t === 'origin') return s.id < info.outers.length;
  return s.t === 'point' ? !!P(s.id) : s.t === 'line' ? !!L(s.id) : !!D(s.id);
}
function dropMissingSelection() { sel = sel.filter(elemExists); }
function isSel(t, id) { return sel.some((s) => s.t === t && s.id === id); }

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

// ---- グリッドに合わせる（板の左上を基準にする。外形の原点もグリッドの上に乗る） ----
function snapStep() { return parseFloat($('snap').value); }
function snapAxis(v, axis) {
  const base = sheetTopLeft()[axis];
  const s = snapStep();
  return round(base + Math.round((v - base) / s) * s);
}

// ---- 描画 ----
const COLOR = {
  fixed: '#1d2b38',     // 確定
  free: '#2f80d1',      // まだ動く
  selected: '#e8890c',
  dim: '#7a4fb3',
  origin: '#c0392b',
  preview: '#8a96a3',
  fill: 'rgba(47, 128, 209, 0.18)',  // 閉じた図形の内側
};
let dimHits = [];  // 寸法の数字の位置（タップ用）[{id, x, y, w, h}]
let originHits = [];  // 外形の原点の名前の位置（タップ用）[{id: 外形の番号, x, y, w, h}]

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

  // グリッド（板の左上を基準にする）
  const bx = 0, by = h;
  const step = gridStep();
  const major = step * 10;
  ctx.lineWidth = 1;
  const isMajor = (v) => Math.abs(v / major - Math.round(v / major)) < 1e-6;
  for (let k = Math.ceil(-bx / step); bx + k * step <= w + 1e-9; k++) {
    ctx.strokeStyle = isMajor(k * step) ? '#c3ccd6' : '#eef1f4';
    const [sx] = toScreen(bx + k * step, 0);
    ctx.beginPath(); ctx.moveTo(Math.round(sx) + 0.5, y0); ctx.lineTo(Math.round(sx) + 0.5, y0 + sh); ctx.stroke();
  }
  for (let k = Math.ceil(-by / step); by + k * step <= h + 1e-9; k++) {
    ctx.strokeStyle = isMajor(k * step) ? '#c3ccd6' : '#eef1f4';
    const [, sy] = toScreen(0, by + k * step);
    ctx.beginPath(); ctx.moveTo(x0, Math.round(sy) + 0.5); ctx.lineTo(x0 + sw, Math.round(sy) + 0.5); ctx.stroke();
  }
  ctx.strokeStyle = '#4a5a6a';
  ctx.lineWidth = 2;
  ctx.strokeRect(x0, y0, sw, sh);
  ctx.font = '13px sans-serif';
  ctx.fillStyle = '#4a5a6a';
  ctx.textAlign = 'left';
  ctx.textAlign = 'right';
  ctx.fillText(`板 ${w} × ${h} mm${doc.sheet.t ? `  板厚 ${doc.sheet.t} mm` : ''}`, x0 + sw, y0 - 8);
  ctx.textAlign = 'left';
  drawSheetOrigin();

  // 閉じた図形の塗り → 寸法 → 線 → 点 → 原点 の順に重ねる
  drawFills();
  dimHits = [];
  originHits = [];
  layoutDims().forEach((lv, i) => { if (doc.dims[i].kind === 'sheet') drawSheetDim(doc.dims[i]); else drawDim(doc.dims[i], lv); });
  for (const l of doc.lines) drawLine(l);
  for (const p of doc.points) drawPoint(p);
  info.outers.forEach((fr, k) => drawOrigin(fr.origin, k));
  if (drag && drag.kind === 'stroke') drawStroke();
}

// 閉じた図形の内側を半透明で塗る。外形を塗ってから、穴・切り欠きの形で抜く。
// 抜く形が外形からはみ出していても、重なった所だけが抜ける。
const fillLayer = document.createElement('canvas');
const fillCtx = fillLayer.getContext('2d');
function drawFills() {
  if (!shapes.some((s) => s.role === 'outer')) return;
  if (fillLayer.width !== canvas.width || fillLayer.height !== canvas.height) {
    fillLayer.width = canvas.width;
    fillLayer.height = canvas.height;
  }
  const f = fillCtx;
  f.setTransform(1, 0, 0, 1, 0, 0);
  f.clearRect(0, 0, fillLayer.width, fillLayer.height);
  f.setTransform(ctx.getTransform());
  const path = (s) => {
    f.beginPath();
    s.poly.forEach((v, i) => { const [x, y] = toScreen(v.x, v.y); i ? f.lineTo(x, y) : f.moveTo(x, y); });
    f.closePath();
  };
  f.globalCompositeOperation = 'source-over';
  f.fillStyle = COLOR.fill;
  for (const s of shapes) if (s.role === 'outer') { path(s); f.fill(); }
  f.globalCompositeOperation = 'destination-out';
  f.fillStyle = '#000';
  for (const s of shapes) if (s.role !== 'outer') { path(s); f.fill(); }
  f.globalCompositeOperation = 'source-over';
  ctx.save();
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.drawImage(fillLayer, 0, 0);
  ctx.restore();
}

function drawLine(l) {
  const a = P(l.a), b = P(l.b);
  const [ax, ay] = toScreen(a.x, a.y), [bx, by] = toScreen(b.x, b.y);
  ctx.strokeStyle = isSel('line', l.id) ? COLOR.selected : info.fixedLine(l) ? COLOR.fixed : COLOR.free;
  ctx.lineWidth = isSel('line', l.id) ? 4 : 2.5;
  ctx.lineCap = 'round';
  ctx.beginPath(); ctx.moveTo(ax, ay); ctx.lineTo(bx, by); ctx.stroke();
}

function drawPoint(p) {
  const [x, y] = toScreen(p.x, p.y);
  const s = isSel('point', p.id);
  const fixed = info.fixedPoint(p.id);
  ctx.beginPath(); ctx.arc(x, y, s ? 7 : 5, 0, Math.PI * 2);
  ctx.fillStyle = s ? COLOR.selected : fixed ? COLOR.fixed : '#ffffff';
  ctx.fill();
  ctx.lineWidth = 2;
  ctx.strokeStyle = s ? COLOR.selected : fixed ? COLOR.fixed : COLOR.free;
  ctx.stroke();
}

// 外形の原点（外形を囲む四角の左上）。名前は印の右上（外形の上の辺の外側）に書く。
function drawOrigin(o, k) {
  const [x, y] = toScreen(o.x, o.y);
  ctx.strokeStyle = COLOR.origin;
  ctx.fillStyle = COLOR.origin;
  ctx.lineWidth = 2;
  ctx.beginPath(); ctx.arc(x, y, 9, 0, Math.PI * 2); ctx.stroke();
  ctx.beginPath(); ctx.moveTo(x - 14, y); ctx.lineTo(x + 14, y); ctx.moveTo(x, y - 14); ctx.lineTo(x, y + 14); ctx.stroke();
  ctx.font = 'bold 13px sans-serif';
  ctx.textAlign = 'left';
  const label = `S${k + 1} 原点`;
  const sl = isSel('origin', k);
  ctx.fillStyle = sl ? COLOR.selected : COLOR.origin;
  ctx.fillText(label, x + 12, y - 10);
  if (sl) {
    ctx.strokeStyle = COLOR.selected;
    ctx.beginPath(); ctx.arc(x, y, 13, 0, Math.PI * 2); ctx.stroke();
  }
  originHits.push({ id: k, x: x + 8, y: y - 26, w: ctx.measureText(label).width + 8, h: 22 });
}
// 板の原点（板の左上）。外形の原点の位置は、ここから右へ X+、下へ Y+ で測る
function drawSheetOrigin() {
  const [x, y] = toScreen(0, doc.sheet.h);
  ctx.strokeStyle = COLOR.origin;
  ctx.lineWidth = 1.5;
  ctx.beginPath(); ctx.moveTo(x - 8, y); ctx.lineTo(x + 8, y); ctx.moveTo(x, y - 8); ctx.lineTo(x, y + 8); ctx.stroke();
}

// 板の原点から外形の原点までの寸法。X は板の上の辺の外側、Y は左の辺の外側に引く
// 板の原点からの寸法線と板の辺との間（mm）。「移動」で動かしたら d.off、そうでなければ自動で段にする
function sheetGap(d) {
  if (d.off != null) return d.off;
  // 同じ向きの板の原点からの寸法を、外へ段にして並べる（短いものを内側）
  const n = doc.dims.filter((q) => q.kind === 'sheet' && q.axis === d.axis &&
    (Math.abs(q.value) < Math.abs(d.value) || (q.value === d.value && q.id < d.id))).length;
  return (22 + 26 * n) / view.scale;
}
function drawSheetDim(d) {
  const k = info.owner.get(d.b);
  if (k == null) return;
  const o = info.outers[k].origin;
  const s = isSel('dim', d.id);
  const gap = sheetGap(d);
  const h = doc.sheet.h;
  let p1, p2, e;
  if (d.axis === 'x') { p1 = [0, h + gap]; p2 = [o.x, h + gap]; e = [o.x, o.y]; }
  else { p1 = [-gap, h]; p2 = [-gap, o.y]; e = [o.x, o.y]; }
  const S = (q) => toScreen(q[0], q[1]);
  dimension(d, S(p1), S(p2), S([0, h]), S(e), s);
}

// 原点からの寸法の表示の値（Y は下へ正）
function dimShown(d) {
  if (d.kind === 'sheet') return d.value;  // 板の原点からの距離（右へ X+、下へ Y+）で持っている
  return d.kind === 'o' ? (d.axis === 'y' ? -d.value : d.value) : Math.abs(d.value);
}

// 寸法線を置く位置（寸法線と直角な方向の座標、mm）を決める。
// 線の長さは、その線のすぐ外側（つながった線と反対の側）。
// ほかの寸法は図形全体の外側に段にして並べる（X 方向は下、Y 方向は右）。
function layoutDims() {
  const pts = doc.points;
  if (!pts.length) return [];
  const minY = Math.min(...pts.map((p) => p.y)), maxX = Math.max(...pts.map((p) => p.x));
  const step = 30 / view.scale;
  // 並べる寸法は、短いものを内側（図形の近く）、長いものを外側にする（寸法線と寸法補助線が交わらないように）
  // 外側の辺（いちばん下の水平線・いちばん右の垂直線）の長さも、同じ列に並べる
  const onEdge = (d) => {
    if (d.kind !== 'len') return false;
    const l = L(d.refs[0].id);
    return l.dir === 'h' ? Math.abs(P(l.a).y - minY) < 1e-6 : Math.abs(P(l.a).x - maxX) < 1e-6;
  };
  const stacked = (d) => d.kind !== 'sheet' && (d.kind !== 'len' || onEdge(d));
  const rank = new Map();
  for (const axis of ['x', 'y']) {
    doc.dims.filter((d) => d.axis === axis && stacked(d))
      .sort((p, q) => Math.abs(p.value) - Math.abs(q.value) || p.id - q.id)
      .forEach((d, i) => rank.set(d.id, i + 1));
  }
  return doc.dims.map((d) => {
    if (d.kind === 'sheet') return null;  // 板の外側に別に描く
    // 「移動」で動かした寸法は、寸法の a の点からのずれ（d.off、mm）の位置に置く。図形を動かしても付いてくる
    if (d.off != null) return P(d.a)[d.axis === 'x' ? 'y' : 'x'] + d.off;
    if (d.kind === 'len' && !rank.has(d.id)) {
      const l = L(d.refs[0].id);
      const across = l.dir === 'h' ? 'y' : 'x';
      const here = P(l.a)[across];
      // つながった線の反対の端が、どちら側にあるか
      let side = 0;
      for (const m of doc.lines) {
        if (m.id === l.id) continue;
        for (const [u, v] of [[m.a, m.b], [m.b, m.a]]) {
          if (u === l.a || u === l.b) side += Math.sign(P(v)[across] - here);
        }
      }
      const out = side > 0 ? -1 : 1;  // つながった線が上（右）なら下（左）に置く
      return here + out * 22 / view.scale;
    }
    const n = rank.get(d.id);
    return d.axis === 'x' ? minY - (16 / view.scale + step * n) : maxX + (16 / view.scale + step * n);
  });
}

// 要素の、寸法線と直角な方向の座標で、寸法線に近い側の端
function refNear(r, across, level) {
  if (r.t === 'point') return P(r.id)[across];
  const l = L(r.id), a = P(l.a)[across], b = P(l.b)[across];
  return Math.abs(a - level) < Math.abs(b - level) ? a : b;
}

// 寸法線: axis の方向の距離を、要素から引き出した寸法補助線と数字で描く
function drawDim(d, level) {
  const ax = d.axis;
  const across = ax === 'x' ? 'y' : 'x';
  const va = P(d.a)[ax], vb = P(d.b)[ax];
  const refsAB = d.kind === 'len' ? [d.refs[0], d.refs[0]] : d.refs;
  const ea = refNear(refsAB[0], across, level), eb = refNear(refsAB[1], across, level);
  let p1, p2, e1, e2;
  if (ax === 'x') {
    p1 = [va, level]; p2 = [vb, level]; e1 = [va, ea]; e2 = [vb, eb];
  } else {
    p1 = [level, va]; p2 = [level, vb]; e1 = [ea, va]; e2 = [eb, vb];
  }
  const S = (q) => toScreen(q[0], q[1]);
  dimension(d, S(p1), S(p2), S(e1), S(e2), isSel('dim', d.id));
}

// JIS B 0001（機械製図）の寸法記入にならって描く（画面の座標で受け取る）。
// - 寸法線・寸法補助線は細い実線。寸法補助線は形体からわずかにすき間をあけ、寸法線を少し越えるまで引く
// - 端末記号は塗りつぶした矢印（開き角 約 30°）。間がせまいときは矢印を外側から当てる
// - 寸法数値は寸法線の上側に、寸法線に沿って書く。縦の寸法は図面の右側から読む向き（左へ 90° 回して寸法線の左側）
// - 単位 mm は書かない。数値を枠で囲まない（枠は理論的に正確な寸法の記号になるため）
// s1, s2: 寸法線の両端。f1, f2: 寸法補助線を引き出す形体の点（寸法線上と同じなら引かない）
const DIM = { gap: 3, over: 6, arrowL: 9, arrowW: 2.4, textGap: 3, font: '13px sans-serif' };
function dimension(d, s1, s2, f1, f2, selected) {
  const col = selected ? COLOR.selected : COLOR.dim;
  ctx.save();
  ctx.strokeStyle = col; ctx.fillStyle = col; ctx.lineWidth = 1;
  ctx.setLineDash([]);
  // 寸法補助線
  ctx.beginPath();
  for (const [f, sp] of [[f1, s1], [f2, s2]]) {
    const len = Math.hypot(sp[0] - f[0], sp[1] - f[1]);
    if (len < DIM.gap + 1) continue;
    const ux = (sp[0] - f[0]) / len, uy = (sp[1] - f[1]) / len;
    ctx.moveTo(f[0] + ux * DIM.gap, f[1] + uy * DIM.gap);
    ctx.lineTo(sp[0] + ux * DIM.over, sp[1] + uy * DIM.over);
  }
  ctx.stroke();
  // 寸法線と矢印
  const len = Math.hypot(s2[0] - s1[0], s2[1] - s1[1]);
  const vertical = Math.abs(s2[0] - s1[0]) < Math.abs(s2[1] - s1[1]);
  ctx.font = DIM.font;
  const text = `${round(dimShown(d))}`;
  const tw = ctx.measureText(text).width, th = 13;
  const narrow = len < DIM.arrowL * 2 + 4;
  let ux = 1, uy = 0;
  if (len > 0.01) { ux = (s2[0] - s1[0]) / len; uy = (s2[1] - s1[1]) / len; } else if (vertical) { ux = 0; uy = 1; }
  ctx.beginPath();
  if (narrow) {
    // 外側から矢印を当て、寸法線を両側へ延ばす
    const ext = DIM.arrowL + 8;
    ctx.moveTo(s1[0] - ux * ext, s1[1] - uy * ext); ctx.lineTo(s2[0] + ux * ext, s2[1] + uy * ext);
  } else {
    ctx.moveTo(s1[0], s1[1]); ctx.lineTo(s2[0], s2[1]);
  }
  ctx.stroke();
  if (len > 0.01) {
    if (narrow) { arrowTip(s1, -ux, -uy); arrowTip(s2, ux, uy); } else { arrowTip(s1, ux, uy); arrowTip(s2, -ux, -uy); }
  }
  // 寸法数値: 寸法線の中央の上側。入らないときは寸法線の先（右・上）の外に出す
  let cx = (s1[0] + s2[0]) / 2, cy = (s1[1] + s2[1]) / 2;
  const room = len - 2 * DIM.arrowL - 4;
  if (tw > room) {
    const far = vertical ? (s1[1] < s2[1] ? s1 : s2) : (s1[0] > s2[0] ? s1 : s2);
    const off = (narrow ? DIM.arrowL + 8 : 0) + tw / 2 + 4;
    if (vertical) { cy = far[1] - off; } else { cx = far[0] + off; }
  }
  let box;
  if (vertical) {
    // 左へ 90° 回す（図面の右側から読む向き）。寸法線の左側に置く
    box = { id: d.id, x: cx - DIM.textGap - th - 2, y: cy - tw / 2 - 2, w: th + 4, h: tw + 4 };
  } else {
    box = { id: d.id, x: cx - tw / 2 - 2, y: cy - DIM.textGap - th - 2, w: tw + 4, h: th + 4 };
  }
  if (selected) { ctx.fillStyle = 'rgba(232, 137, 12, 0.18)'; ctx.fillRect(box.x, box.y, box.w, box.h); ctx.fillStyle = col; }
  ctx.textAlign = 'center';
  ctx.textBaseline = 'bottom';
  if (vertical) {
    ctx.translate(cx - DIM.textGap, cy);
    ctx.rotate(-Math.PI / 2);
    ctx.textBaseline = 'middle';
    ctx.fillText(text, 0, -th / 2 + 1);
  } else {
    ctx.fillText(text, cx, cy - DIM.textGap);
  }
  ctx.restore();
  dimHits.push(box);
}
// 矢印（先端 p、向き (ux, uy) へ開く）
function arrowTip(p, ux, uy) {
  ctx.beginPath();
  ctx.moveTo(p[0], p[1]);
  ctx.lineTo(p[0] + ux * DIM.arrowL - uy * DIM.arrowW, p[1] + uy * DIM.arrowL + ux * DIM.arrowW);
  ctx.lineTo(p[0] + ux * DIM.arrowL + uy * DIM.arrowW, p[1] + uy * DIM.arrowL - ux * DIM.arrowW);
  ctx.closePath();
  ctx.fill();
}

// なぞっている途中: 指の跡（灰色）と、できあがる折れ線（点線）
function drawStroke() {
  const raw = drag.raw;
  if (raw.length < 2) return;
  ctx.strokeStyle = 'rgba(138,150,163,0.5)';
  ctx.lineWidth = 6;
  ctx.lineJoin = 'round';
  ctx.beginPath();
  raw.forEach((p, i) => { const [x, y] = toScreen(p.x, p.y); if (i) ctx.lineTo(x, y); else ctx.moveTo(x, y); });
  ctx.stroke();
  const res = strokeResult(raw);
  if (!res) return;
  ctx.strokeStyle = COLOR.free;
  ctx.lineWidth = 2.5;
  ctx.setLineDash([7, 5]);
  ctx.beginPath();
  res.verts.forEach((v, i) => { const [x, y] = toScreen(v.x, v.y); if (i) ctx.lineTo(x, y); else ctx.moveTo(x, y); });
  ctx.stroke();
  ctx.setLineDash([]);
  // 長さの目安
  ctx.font = '12px sans-serif';
  ctx.fillStyle = COLOR.free;
  ctx.textAlign = 'center';
  for (let i = 1; i < res.verts.length; i++) {
    const a = res.verts[i - 1], b = res.verts[i];
    const [x, y] = toScreen((a.x + b.x) / 2, (a.y + b.y) / 2);
    ctx.fillText(`${round(Math.abs(b.x - a.x) + Math.abs(b.y - a.y))}`, x + 10, y - 8);
  }
  ctx.textAlign = 'left';
}

// ---- なぞった線を図形にする ----
const SNAP_PX = 18;  // 既にある点につなぐ距離（画面の px）

function nearestPoint(x, y, maxPx, exclude) {
  let best = null, bd = maxPx / view.scale;
  for (const p of doc.points) {
    if (exclude && exclude.has(p.id)) continue;
    const d = Math.hypot(p.x - x, p.y - y);
    if (d <= bd) { best = p; bd = d; }
  }
  return best;
}

// なぞった跡 → {verts: [{x, y, id?, close?}]}。
// 角はグリッドに合わせ、両端が既にある点の近くならその点につなぐ。
function strokeResult(raw) {
  const T = 14 / view.scale;
  let verts = Geo.strokeToPolyline(raw, T);
  if (!verts) return null;
  verts = verts.map((v) => ({ x: snapAxis(v.x, 'x'), y: snapAxis(v.y, 'y') }));
  const dirs = [];
  for (let i = 1; i < verts.length; i++) dirs.push(Math.abs(verts[i].y - verts[i - 1].y) < Geo.EPS ? 'h' : 'v');
  const n = verts.length;
  // 描き始め
  const ps = nearestPoint(raw[0].x, raw[0].y, SNAP_PX);
  if (ps) {
    verts[0] = { x: ps.x, y: ps.y, id: ps.id };
    if (dirs[0] === 'h') verts[1].y = ps.y; else verts[1].x = ps.x;
  }
  // 描き終わり（描き始めの近くなら閉じる）
  const last = raw[raw.length - 1];
  let pe = nearestPoint(last.x, last.y, SNAP_PX);
  let close = false;
  if (n >= 4 && Math.hypot(last.x - raw[0].x, last.y - raw[0].y) <= SNAP_PX / view.scale) {
    pe = verts[0]; close = true;
  }
  if (pe && !(ps && pe.id === ps.id && !close)) {
    const k = n - 2;  // 最後の辺の始まり
    const dk = dirs[n - 2];
    const okSingle = k > 0 || !verts[0].id ||
      (dk === 'h' ? Math.abs(verts[0].y - pe.y) < Geo.EPS : Math.abs(verts[0].x - pe.x) < Geo.EPS);
    if (okSingle) {
      if (dk === 'h') verts[k].y = pe.y; else verts[k].x = pe.x;
      verts[n - 1] = { x: pe.x, y: pe.y, id: close ? undefined : pe.id, close };
    }
  }
  // 長さ 0 の辺を消し、同じ向きに続く辺をまとめる（端の id は残す）
  const merged = [];
  for (const v of verts) {
    const prev = merged[merged.length - 1];
    if (prev && Math.abs(prev.x - v.x) < Geo.EPS && Math.abs(prev.y - v.y) < Geo.EPS) {
      if (v.id != null) prev.id = v.id;
      if (v.close) prev.close = true;
      continue;
    }
    merged.push(v);
  }
  const cleaned = Geo.cleanPolyline(merged);
  if (!cleaned) return null;
  return { verts: cleaned };
}

function commitStroke(raw) {
  const res = strokeResult(raw);
  if (!res) return false;
  snapshot();
  const ids = res.verts.map((v, i) => {
    if (v.close) return null;  // 描き始めの点と同じ（あとで入れる）
    if (v.id != null) return v.id;
    const id = newId();
    doc.points.push({ id, x: v.x, y: v.y });
    return id;
  });
  if (res.verts[res.verts.length - 1].close) ids[ids.length - 1] = ids[0];
  let added = 0;
  for (let i = 1; i < ids.length; i++) {
    if (ids[i] === ids[i - 1]) continue;
    const a = P(ids[i - 1]), b = P(ids[i]);
    const dir = Math.abs(a.y - b.y) < Geo.EPS ? 'h' : 'v';
    doc.lines.push({ id: newId(), a: ids[i - 1], b: ids[i], dir });
    added++;
  }
  if (!added) { undoStack.pop(); return false; }
  sel = [];
  changed();
  return true;
}

// ---- 当たり判定 ----
function hitTest(sx, sy) {
  const [wx, wy] = toWorld(sx, sy);
  // 外形の原点は、名前（S1 原点）をタップして選ぶ（印は角の点と重なるため）
  for (const b of originHits) {
    if (sx >= b.x - 4 && sx <= b.x + b.w + 4 && sy >= b.y - 4 && sy <= b.y + b.h + 4) return { t: 'origin', id: b.id };
  }
  const p = nearestPoint(wx, wy, 16);
  if (p) return { t: 'point', id: p.id };
  for (let i = dimHits.length - 1; i >= 0; i--) {
    const b = dimHits[i];
    if (sx >= b.x - 4 && sx <= b.x + b.w + 4 && sy >= b.y - 4 && sy <= b.y + b.h + 4) return { t: 'dim', id: b.id };
  }
  let best = null, bd = 12 / view.scale;
  for (const l of doc.lines) {
    const a = P(l.a), b = P(l.b);
    const minx = Math.min(a.x, b.x), maxx = Math.max(a.x, b.x), miny = Math.min(a.y, b.y), maxy = Math.max(a.y, b.y);
    const dx = wx < minx ? minx - wx : wx > maxx ? wx - maxx : 0;
    const dy = wy < miny ? miny - wy : wy > maxy ? wy - maxy : 0;
    const d = Math.hypot(dx, dy);
    if (d <= bd) { best = l; bd = d; }
  }
  return best ? { t: 'line', id: best.id } : null;
}

// ---- タッチ・マウス操作 ----
// 指1本: 作図ではなぞって描く、寸法ではタップで選ぶ、移動ではつかんで動かす。指2本: 拡大縮小と画面の移動。
let mode = 'draw';
const pointers = new Map();
let drag = null;  // {kind:'stroke'|'press'|'move'|'pinch', ...}

function eventPos(e) {
  const rect = canvas.getBoundingClientRect();
  return [e.clientX - rect.left, e.clientY - rect.top];
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
    // 2本目の指が来たら、描きかけ・動かしかけは取り消して拡大縮小にする
    if (drag && drag.kind === 'move') { doc = JSON.parse(undoStack.pop()); refresh(); }
    startPinch();
    draw();
    return;
  }
  if (pointers.size > 2) return;

  const [sx, sy] = eventPos(e);
  const [wx, wy] = toWorld(sx, sy);

  if (mode === 'draw') {
    drag = { kind: 'stroke', raw: [{ x: wx, y: wy }] };
  } else {
    drag = { kind: 'press', hit: hitTest(sx, sy), start: [sx, sy], world: [wx, wy] };
  }
});

canvas.addEventListener('pointermove', (e) => {
  const pos = eventPos(e);
  const [wx, wy] = toWorld(pos[0], pos[1]);
  {
    const sx = snapAxis(wx, 'x'), sy = snapAxis(wy, 'y');
    const k = info.outerAt(sx, sy);
    const r = rel(originAt(sx, sy), sx, sy);
    $('st-pos').textContent = `${k == null ? '板' : `S${k + 1}`}  X ${r.x}  Y ${r.y}`;
  }
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
  } else if (drag.kind === 'stroke') {
    drag.raw.push({ x: wx, y: wy });
    draw();
  } else if (drag.kind === 'press') {
    // 「移動」で点か線を押したまま 8px 動いたら、選んだものをまとめて動かし始める。
    // 選んでいないものをつかんだときは、それだけを選び直して動かす
    const h = drag.hit;
    if (mode === 'move' && h && Math.hypot(pos[0] - drag.start[0], pos[1] - drag.start[1]) > 8) {
      if (!isSel(h.t, h.id)) sel = [h];
      snapshot();
      // ワーク原点を選んでいれば、その外形（穴・切り欠き・つながった線を含む）をまるごと動かす。
      // その外形に入る線・点は、二重に動かさないように外す
      const families = sel.filter((q) => q.t === 'origin').map((q) => ({
        pids: new Set(doc.points.filter((p) => info.owner.get(p.id) === q.id).map((p) => p.id)),
        sheet: ['x', 'y'].map((axis) => sheetDimOf(q.id, axis)).filter(Boolean),
        origin: { ...info.outers[q.id].origin },
      }));
      const inFamily = (pid) => families.some((f) => f.pids.has(pid));
      const items = sel.filter((q) => (q.t === 'line' ? !(inFamily(L(q.id).a) && inFamily(L(q.id).b))
        : q.t === 'point' ? !inFamily(q.id) : false)).map((q) => ({ ...q }));
      // 寸法は、寸法線を寸法線と直角な方向に動かす（値は変わらない）。動かし始めの位置を覚えておく
      const levels = layoutDims();
      const dims = sel.filter((q) => q.t === 'dim' && D(q.id)).map((q) => {
        const d = D(q.id);
        const start = d.kind === 'sheet' ? sheetGap(d) : levels[doc.dims.indexOf(d)] - P(d.a)[d.axis === 'x' ? 'y' : 'x'];
        return { id: d.id, start, before: d.off };
      });
      drag = {
        kind: 'move', items, families, dims,
        world: drag.world, orig: doc.points.map((p) => ({ ...p })), last: [0, 0],
      };
    }
  }
  if (drag && drag.kind === 'move') {
    // 元の位置から、グリッドの間隔ずつ動かす
    doc.points = drag.orig.map((p) => ({ ...p }));
    const s = snapStep();
    const dx = round(Math.round((wx - drag.world[0]) / s) * s);
    const dy = round(Math.round((wy - drag.world[1]) / s) * s);
    // 長さ 0 の線ができる位置には止めず、動ける方向だけ動かすか、その手前の位置のままにする
    const [lx, ly] = drag.last;
    const tries = [[dx, dy], [dx, ly], [lx, dy], [lx, ly]];
    if (drag.items.length) {
      for (const [tx, ty] of tries) {
        if (Geo.moveItems(doc, drag.items, tx, ty)) { drag.last = [tx, ty]; break; }
      }
    }
    // ワーク原点を動かした外形は、まるごと平行移動する。板の原点からの寸法があれば、その値も新しい位置にする
    for (const f of drag.families) {
      for (const p of doc.points) if (f.pids.has(p.id)) { p.x = round(p.x + dx); p.y = round(p.y + dy); }
      for (const d of f.sheet) d.value = round(d.axis === 'x' ? f.origin.x + dx : doc.sheet.h - (f.origin.y + dy));
    }
    // 寸法線はグリッドに合わせず、指の動きのまま動かす
    const rdx = wx - drag.world[0], rdy = wy - drag.world[1];
    for (const m of drag.dims) {
      const d = D(m.id);
      if (!d) continue;
      // 板の原点からの寸法: X は板の上（+Y 側）へ離れるほど、Y は板の左（−X 側）へ離れるほど間が広がる
      const delta = d.kind === 'sheet' ? (d.axis === 'x' ? rdy : -rdx) : (d.axis === 'x' ? rdy : rdx);
      d.off = round(m.start + delta);
    }
    applyPlacements();  // 板の原点から位置を決めた外形は、その位置に保つ
    refresh();  // 外形の原点や塗りも動かしながら描き直す
    // 画面の Y は上が正なので、表示は下へ正にそろえる
    const [mx, my] = drag.items.length ? drag.last : [dx, dy];
    $('st-pos').textContent = `移動  X ${round(mx)}  Y ${round(-my)}`;
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
  const d = drag;
  drag = null;
  if (d.kind === 'stroke') {
    if (e.type !== 'pointercancel') commitStroke(d.raw);
    draw();
  } else if (d.kind === 'press') {
    if (mode === 'dim') tapSelect(d.hit);
    else if (mode === 'move') tapSelectMany(d.hit);
  } else if (d.kind === 'move') {
    const movedAny = doc.points.some((p, i) => p.x !== d.orig[i].x || p.y !== d.orig[i].y) ||
      d.dims.some((m) => { const q = D(m.id); return q && q.off !== m.before; });
    if (movedAny) changed(); else { undoStack.pop(); draw(); }
  }
}
canvas.addEventListener('pointerup', endPointer);
canvas.addEventListener('pointercancel', endPointer);

// タップで選ぶ: 同じものは外す、2 つまで足す、3 つ目は選び直し。寸法は 1 つだけ選ぶ。
// 「移動」ではいくつでも選べる（タップで足す・外す）。寸法の数字も選べる（寸法線の位置を動かす）。何もない所で全部外す。
function tapSelectMany(hit) {
  if (!hit) sel = [];
  else if (isSel(hit.t, hit.id)) sel = sel.filter((s) => !(s.t === hit.t && s.id === hit.id));
  else sel = [...sel, hit];
  updateUi();
  draw();
}
function tapSelect(hit) {
  if (!hit) sel = [];
  else if (isSel(hit.t, hit.id)) sel = sel.filter((s) => !(s.t === hit.t && s.id === hit.id));
  else if (hit.t === 'dim' || hit.t === 'origin' || sel.some((s) => s.t === 'dim' || s.t === 'origin') || sel.length >= 2) sel = [hit];
  else sel = [...sel, hit];
  updateUi();
  draw();
}

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
// ダブルタップ・ダブルクリックでの拡大を止める（touch-action が効かない古い環境の予備）
document.addEventListener('dblclick', (e) => e.preventDefault(), { passive: false });

// ---- 画面の部品 ----
// 作図: なぞって線を描く。寸法: 要素をタップして寸法を入れる。移動: 線・点を選んでつかみ、まとめて動かす。
// 寸法は 2 つまで選んで寸法を入れ、移動はいくつでも選んでまとめて動かす。
function setMode(m) {
  mode = m;
  if (sel.length) { sel = []; updateUi(); draw(); }  // 寸法と移動では選び方が違うので、切り替えたら外す
  document.querySelectorAll('button.mode').forEach((b) => b.classList.toggle('active', b.dataset.mode === m));
  canvas.dataset.mode = m;
}
document.querySelectorAll('button.mode').forEach((b) => b.addEventListener('click', () => setMode(b.dataset.mode)));

// ---- 寸法を入れる欄 ----
const pName = (id) => `P${id}`;
const lName = (id) => `L${id}`;
const dirName = (l) => (l.dir === 'h' ? '水平' : '垂直');
const axisName = (a) => (a === 'x' ? 'X' : 'Y');
function refName(r) { return r.t === 'point' ? pName(r.id) : lName(r.id); }
// 要素の、方向 axis での代表の点（線は端の点。線の向きと直角な方向では両端が同じ値）
function repPoint(r) { return r.t === 'point' ? r.id : L(r.id).a; }
// 原点からの寸法は、基準の点が変わっても同じ寸法として見る
function dimKey(axis, refs, kind) {
  return kind === 'o' ? `${axis}:o:${refs[1].t}${refs[1].id}` : axis + ':' + refs.map((r) => r.t + r.id).sort().join(',');
}
function findDim(axis, refs, kind) { const k = dimKey(axis, refs, kind); return doc.dims.find((d) => dimKey(d.axis, d.refs, d.kind) === k); }

// 選んだものから、入れられる寸法を作る
// 戻り値 {title, rows:[{label, axis, a, b, kind, refs, prefer, signed, neg}], note?}
// signed: 向きのある値（原点からの距離）。neg: 表示の値が b − a の逆（原点からの Y は下へ正）
function dimOptions() {
  const fixedTxt = (ok) => (ok ? '確定' : '未確定');
  // 線の長さは左・上の端を残す
  const lenPrefer = (axis, a, b) => (axis === 'x' ? (P(a).x <= P(b).x ? 'b' : 'a') : (P(a).y >= P(b).y ? 'b' : 'a'));
  // 板の原点から外形の原点までの寸法の欄
  const sheetRows = (k) => ['x', 'y'].map((axis) => {
    const ref = info.outers[k].ref.x;
    return { label: `板の原点から ${axisName(axis)}`, axis, a: ref, b: ref, kind: 'sheet', refs: [{ t: 'point', id: ref }], sheet: k, signed: true };
  });
  if (sel.length === 1) {
    const s = sel[0];
    if (s.t === 'origin') {
      return { title: `S${s.id + 1} の原点（外形の左上）`, rows: sheetRows(s.id), isOrigin: true,
        note: '板の原点（板の左上）から、右へ X、下へ Y の距離を入れると、外形ごと動きます。' };
    }
    if (s.t === 'dim' && D(s.id).kind === 'sheet') {
      const d = D(s.id);
      const k = info.owner.get(d.b);
      if (k == null) return { title: `寸法 D${d.id}`, rows: [], note: '外形が閉じていないので、この寸法は効いていません。', isDim: true };
      return { title: `寸法 D${d.id}（S${k + 1} の原点）`, rows: sheetRows(k).filter((r) => r.axis === d.axis), isDim: true };
    }
    if (s.t === 'line') {
      const l = L(s.id);
      const along = l.dir === 'h' ? 'x' : 'y';
      const rows = [{ label: '長さ', axis: along, a: l.a, b: l.b, kind: 'len', refs: [s], prefer: lenPrefer(along, l.a, l.b) }];
      return { title: `線 ${lName(l.id)}（${dirName(l)}・${fixedTxt(info.fixedLine(l))}）`, rows };
    }
    if (s.t === 'point') {
      // 外形の原点からの距離の欄はなくした（v0.10.3）。ほかの点・線を選ぶと距離を入れられる
      return { title: `点 ${pName(s.id)}（${fixedTxt(info.fixedPoint(s.id))}）`, rows: [],
        note: 'もう 1 つ点か線を選ぶと、その間の距離を入れられます。' };
    }
    if (s.t === 'dim') {
      const d = D(s.id);
      const names = d.kind === 'o' ? `原点と ${refName(d.refs[1])}` : d.refs.map(refName).join(' と ');
      return {
        title: `寸法 D${d.id}`,
        rows: [{
          label: `${names} の ${axisName(d.axis)}`, axis: d.axis, a: d.a, b: d.b, kind: d.kind, refs: d.refs,
          signed: d.kind === 'o', neg: d.kind === 'o' && d.axis === 'y',
          prefer: d.kind === 'len' ? lenPrefer(d.axis, d.a, d.b) : 'b',
        }],
        isDim: true,
      };
    }
  }
  if (sel.length === 2) {
    const [f, g] = sel;
    if (f.t === 'origin' || g.t === 'origin') return { title: '', rows: [] };
    const prefer = 'b';  // 2 つ目に選んだ側が動く
    const kind = null, signed = false;
    if (f.t === 'point' && g.t === 'point') {
      return {
        title: `点 ${pName(f.id)} と 点 ${pName(g.id)}`,
        rows: ['x', 'y'].map((axis) => ({ label: `${axisName(axis)} 方向の距離`, axis, a: f.id, b: g.id, kind: kind || 'pp', refs: [f, g], prefer, signed })),
      };
    }
    if (f.t === 'line' && g.t === 'line') {
      const l1 = L(f.id), l2 = L(g.id);
      if (l1.dir !== l2.dir) return { title: `線 ${lName(l1.id)} と 線 ${lName(l2.id)}`, rows: [], note: '直角に交わる線どうしの距離は決められません。平行な線を 2 本選んでください。' };
      const axis = l1.dir === 'h' ? 'y' : 'x';
      return { title: `線 ${lName(l1.id)} と 線 ${lName(l2.id)}（平行）`, rows: [{ label: '線の間の距離', axis, a: l1.a, b: l2.a, kind: 'll', refs: [f, g], prefer }] };
    }
    if (f.t === 'dim' || g.t === 'dim') return { title: '', rows: [] };
    // 点と線
    const line = f.t === 'line' ? L(f.id) : L(g.id);
    const axis = line.dir === 'h' ? 'y' : 'x';
    return {
      title: `${refName(f)} と ${refName(g)}`,
      rows: [{ label: '点と線の距離', axis, a: repPoint(f), b: repPoint(g), kind: kind || 'pl', refs: [f, g], prefer, signed }],
    };
  }
  return null;
}

// 欄を作り直す（選んだものが変わったとき・図形が変わったとき）
let propsSig = '';
function updateProps() {
  const opt = mode === 'dim' ? dimOptions() : null;  // 寸法の欄は「寸法」のときだけ
  const box = $('props');
  box.hidden = !opt;
  if (!opt) { propsSig = ''; return; }
  const sig = JSON.stringify(sel);
  const rebuild = sig !== propsSig;
  propsSig = sig;
  if (rebuild) {
    box.textContent = '';
    const title = document.createElement('div');
    title.className = 'props-title';
    box.append(title);
    opt.rows.forEach((r, i) => {
      const lab = document.createElement('label');
      lab.className = 'props-row';
      lab.innerHTML = `<span class="pl"></span><input type="number" inputmode="decimal" step="any" data-i="${i}"><span class="unit">mm</span><span class="badge"></span>`;
      lab.querySelector('.pl').textContent = r.label;
      const input = lab.querySelector('input');
      input.addEventListener('change', () => commitDim(i, input));
      input.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); input.blur(); } });
      box.append(lab);
    });
    const note = document.createElement('div');
    note.className = 'props-note';
    box.append(note);
    const btns = document.createElement('div');
    btns.className = 'props-buttons';
    if (opt.isDim) {
      const del = document.createElement('button');
      del.type = 'button';
      del.textContent = '寸法を消す';
      del.addEventListener('click', deleteSelected);
      btns.append(del);
    }
    box.append(btns);
  }
  box.querySelector('.props-title').textContent = opt.title;
  box.querySelector('.props-note').textContent = opt.note ||
    (opt.rows.length ? '数字を入れて Enter で決まります。紫の数字は決めた寸法。' : '');
  opt.rows.forEach((r, i) => {
    const input = box.querySelector(`input[data-i="${i}"]`);
    if (r.kind === 'sheet') {
      if (document.activeElement !== input) input.value = sheetPos(r.sheet, r.axis);
      input.parentElement.querySelector('.badge').textContent = sheetDimOf(r.sheet, r.axis) ? '寸法' : '';
      return;
    }
    const cur = P(r.b)[r.axis] - P(r.a)[r.axis];
    const existing = findDim(r.axis, r.refs, r.kind);
    if (document.activeElement !== input) input.value = round(r.signed ? (r.neg ? -cur : cur) : Math.abs(cur));
    const badge = input.parentElement.querySelector('.badge');
    badge.textContent = existing ? '寸法' : '';
  });
}

// 欄の値を寸法にする（すでにあれば値を変える）
function commitDim(i, input) {
  const opt = dimOptions();
  const r = opt && opt.rows[i];
  if (!r) return;
  let v = parseFloat(input.value);
  if (!Number.isFinite(v)) { updateUi(); return; }
  v = round(v);
  if (!r.signed && v < 0) v = -v;
  if (r.kind === 'len' && v <= 0) { flash('長さは 0 より大きくしてください'); updateUi(); return; }
  if (r.kind === 'sheet') {
    // 板の原点から外形の原点までの寸法: 値を覚えて、外形ごと平行移動する
    snapshot();
    const existing = sheetDimOf(r.sheet, r.axis);
    if (existing) existing.value = v;
    else doc.dims.push({ id: newId(), axis: r.axis, a: r.a, b: r.b, value: v, kind: 'sheet', refs: r.refs.map((q) => ({ ...q })) });
    // 原点だけを選んでいるときは、続けてもう一方の向きを入れられるように選んだままにする
    if (!opt.isOrigin) sel = [];
    changed();
    return;
  }
  const cur = P(r.b)[r.axis] - P(r.a)[r.axis];
  const existing = findDim(r.axis, r.refs, r.kind);
  // 向き（どちら側か）はいまの位置のまま。原点からの寸法は入れた符号のまま（Y は下へ正）
  const sign = r.signed ? (r.neg ? -1 : 1) : (existing ? Math.sign(existing.value) || 1 : Math.sign(cur) || 1);
  const value = round(sign * v);
  snapshot();
  // 寸法の a・b が欄の a・b と逆向きなら、動かす側も入れ替える
  const flip = existing && existing.a !== r.a;
  const prefer = flip ? (r.prefer === 'a' ? 'b' : 'a') : r.prefer;
  const err = existing ? Geo.setDimValue(doc, existing.id, value, prefer)
    : Geo.addDim(doc, { id: newId(), axis: r.axis, a: r.a, b: r.b, value, kind: r.kind, refs: r.refs.map((q) => ({ ...q })) }, r.prefer);
  if (err) {
    doc = JSON.parse(undoStack.pop());
    flash(err);
    updateUi();
    return;
  }
  sel = [];  // 決まったら選択を解く（続けて次の要素を選べるように）
  changed();
}

// 選んだものを消す: 線（とその端の点）、点（につながる線）、寸法
function deleteSelected() {
  if (!sel.length) return;
  snapshot();
  const lineIds = new Set(sel.filter((s) => s.t === 'line').map((s) => s.id));
  for (const s of sel) {
    if (s.t === 'point') {
      for (const l of doc.lines) if (l.a === s.id || l.b === s.id) lineIds.add(l.id);
    }
  }
  const dimIds = new Set(sel.filter((s) => s.t === 'dim').map((s) => s.id));
  // 外形の原点を選んで消すと、板の原点からの寸法を消す（外形はそこに残る）
  for (const s of sel) {
    if (s.t !== 'origin') continue;
    for (const axis of ['x', 'y']) { const d = sheetDimOf(s.id, axis); if (d) dimIds.add(d.id); }
  }
  doc.dims = doc.dims.filter((d) => !dimIds.has(d.id));
  if (lineIds.size) Geo.deleteLines(doc, [...lineIds]);
  sel = [];
  changed();
}

function updateUi() {
  dropMissingSelection();
  $('btn-delete').disabled = !sel.length;
  $('btn-undo').disabled = !undoStack.length;
  $('sheet-w').value = doc.sheet.w;
  $('sheet-h').value = doc.sheet.h;
  if (document.activeElement !== $('sheet-t')) $('sheet-t').value = doc.sheet.t ?? '';
  $('st-name').textContent = (doc.name || '名前なし') + (dirty ? ' *' : '');
  const fixedLines = doc.lines.filter((l) => info.fixedLine(l)).length;
  $('st-count').textContent = `線 ${doc.lines.length} 本（確定 ${fixedLines}）・寸法 ${doc.dims.length}`;
  $('st-prog').textContent = doc.program == null ? 'プログラム番号なし' : programText(doc.program);
  $('st-tools').textContent = doc.tools.map((t, i) => (t.shape === 'none' ? null : `T${i + 1} ${toolText(t)}`))
    .filter(Boolean).join('　') || '金型なし';
  updateProps();
  renderCode();
}

// ---- 図形データの区画 ----
// いまは図形の確認用に、点・線・寸法を原点からの座標で並べる（プログラムの自動作成は次の段階）。
function fmt(v) {
  return String(round(v));
}
function genCode() {
  const lines = [];  // {text, key?, comment?}
  const add = (text, key, comment = false) => lines.push({ text, key, comment });
  add(`; セットプレス CAM v${APP_VERSION}`, null, true);
  add(`; 図面 ${doc.name || '名前なし'}  板 ${fmt(doc.sheet.w)} x ${fmt(doc.sheet.h)} mm`, null, true);
  add(`; 板厚 ${doc.sheet.t ? `${fmt(doc.sheet.t)} mm` : '未設定'}`, null, true);
  add(programText(doc.program), null);
  doc.tools.forEach((t, i) => add(`; T${i + 1} ${toolText(t)}`, null, true));
  add('', null);
  // 外形ごとに、原点からの座標で並べる（Y は下へ正）。外形に入らないものは最後に板の左上から。
  const roleName = { hole: '穴', notch: '切り欠き' };
  const groups = info.outers.map((fr, k) => ({ k, origin: fr.origin }));
  groups.push({ k: null, origin: sheetTopLeft() });
  const ownerOf = (pid) => { const k = info.owner.get(pid); return k == null ? null : k; };
  const span = (o, poly) => {
    const a = rel(o, Math.min(...poly.map((v) => v.x)), Math.max(...poly.map((v) => v.y)));
    const b = rel(o, Math.max(...poly.map((v) => v.x)), Math.min(...poly.map((v) => v.y)));
    return `X${fmt(a.x)}〜${fmt(b.x)} Y${fmt(a.y)}〜${fmt(b.y)}`;
  };
  if (!info.outers.length) add('; 閉じた図形がありません（原点は閉じた図形の左上に付きます）', null, true);
  for (const g of groups) {
    const pts = doc.points.filter((p) => ownerOf(p.id) === g.k);
    const lns = doc.lines.filter((l) => ownerOf(l.a) === g.k);
    if (g.k == null && !pts.length) continue;
    add('', null);
    if (g.k != null) {
      const sh = shapes[info.outers[g.k].shape];
      const tl = rel(sheetTopLeft(), g.origin.x, g.origin.y);
      const fixedTxt = ['x', 'y'].map((ax) => (sheetDimOf(g.k, ax) ? '●' : '○')).join('');
      add(`; S${g.k + 1} 外形 ${sh.poly.length}角  原点は板原点から X${fmt(tl.x)} Y${fmt(tl.y)} ${fixedTxt}`, null, true);
      add(`S${g.k + 1}  外形   ${span(g.origin, sh.poly)}`, null);
      for (const c of shapes) if (c.role !== 'outer' && c.outer === g.k) add(`S${g.k + 1}  ${roleName[c.role].padEnd(4, '　')} ${span(g.origin, c.poly)}`, null);
    } else {
      add('; 閉じた図形の外（板の左上から）', null, true);
    }
    add('; 点  ● 確定', null, true);
    for (const p of pts) {
      const mark = info.fixedPoint(p.id) ? '●' : '○';
      const r = rel(g.origin, p.x, p.y);
      add(`${pName(p.id).padEnd(5)}${mark} X${fmt(r.x).padEnd(8)}Y${fmt(r.y)}`, `point:${p.id}`);
    }
    add('; 線', null, true);
    for (const l of lns) {
      const a = P(l.a), b = P(l.b);
      const len = Math.abs(b.x - a.x) + Math.abs(b.y - a.y);
      const mark = info.fixedLine(l) ? '●' : '○';
      add(`${lName(l.id).padEnd(5)}${mark} ${dirName(l)} ${pName(l.a)}-${pName(l.b)}  長さ ${fmt(len)}`, `line:${l.id}`);
    }
  }
  if (doc.dims.length) {
    add('', null);
    add('; 寸法', null, true);
    for (const d of doc.dims) {
      const v = dimShown(d);
      const k = info.owner.get(d.b);
      const what = d.kind === 'len' ? `${refName(d.refs[0])} の長さ`
        : d.kind === 'sheet' ? `板原点-${k == null ? '(閉じていない外形)' : `S${k + 1} 原点`} ${axisName(d.axis)}`
        : d.kind === 'o' ? `${k == null ? '' : `S${k + 1} `}原点-${refName(d.refs[1])} ${axisName(d.axis)}`
        : `${d.refs.map(refName).join('-')} ${axisName(d.axis)}`;
      add(`D${String(d.id).padEnd(4)}${what}  ${fmt(v)}`, `dim:${d.id}`);
    }
  }
  return lines;
}

let lastCodeSel = '';
function renderCode() {
  const box = $('code');
  if (!box) return;  // 更新の途中で古い index.html と組み合わさったとき
  const lines = genCode();
  const selKeys = new Set(sel.map((s) => `${s.t}:${s.id}`));
  box.textContent = '';
  let firstSel = null;
  lines.forEach((l, i) => {
    const row = document.createElement('div');
    const on = l.key && selKeys.has(l.key);
    row.className = 'ln' + (l.comment ? ' cmt' : '') + (l.key ? ' rect' : '') + (on ? ' sel' : '');
    if (l.key) row.dataset.key = l.key;
    const no = document.createElement('span');
    no.className = 'no';
    no.textContent = i + 1;
    const tx = document.createElement('span');
    tx.className = 'tx';
    tx.textContent = l.text;
    row.append(no, tx);
    box.append(row);
    if (!firstSel && on) firstSel = row;
  });
  // 選んだものが変わったときだけ、その行まで送る
  const sig = [...selKeys].join();
  if (sig !== lastCodeSel && firstSel) firstSel.scrollIntoView({ block: 'nearest' });
  lastCodeSel = sig;
}

// 行をタップすると、「寸法」にしてその要素を選ぶ
$('code')?.addEventListener('click', (e) => {
  const row = e.target.closest('.ln.rect');
  if (!row || (window.getSelection && String(window.getSelection()).length)) return;
  const [t, id] = row.dataset.key.split(':');
  setMode('dim');
  sel = [{ t, id: Number(id) }];
  updateUi();
  draw();
});

$('btn-copy')?.addEventListener('click', async () => {
  const text = genCode().map((l) => l.text).join('\n') + '\n';
  try {
    await navigator.clipboard.writeText(text);
    flash('コピーしました');
  } catch (e) {
    flash('コピーできませんでした');
  }
});

function flash(msg) {
  $('st-msg').textContent = msg;
  clearTimeout(flash.t);
  flash.t = setTimeout(() => { $('st-msg').textContent = ''; }, 4000);
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
// 板厚: 0.1〜50 mm。空にすると未設定に戻す
{
  const el = $('sheet-t');
  el.addEventListener('change', () => {
    let v = el.value.trim() === '' ? null : parseFloat(el.value);
    if (v !== null && !Number.isFinite(v)) { updateUi(); return; }
    if (v !== null) v = round(Math.min(Math.max(v, 0.1), 50));
    if (v === (doc.sheet.t ?? null)) { updateUi(); return; }
    snapshot(); doc.sheet.t = v; changed();
  });
  el.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); el.blur(); } });
}

$('btn-undo').addEventListener('click', undo);
$('btn-fit').addEventListener('click', fitView);
$('btn-delete').addEventListener('click', deleteSelected);

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

// 保存データを読み込む。v0.4 までの四角穴（rects）はテスト用だったので読み込まない。
function loadDoc(d) {
  if (!d || !d.sheet) throw new Error('図面の形式が違います');
  const prog = Number(d.program);
  const num = (v) => round(Number(v));
  const points = (Array.isArray(d.points) ? d.points : [])
    .map((p) => ({ id: Number(p.id), x: num(p.x), y: num(p.y) }))
    .filter((p) => Number.isInteger(p.id) && Number.isFinite(p.x) && Number.isFinite(p.y));
  const ids = new Set(points.map((p) => p.id));
  const lines = (Array.isArray(d.lines) ? d.lines : [])
    .map((l) => ({ id: Number(l.id), a: Number(l.a), b: Number(l.b), dir: l.dir === 'v' ? 'v' : 'h' }))
    .filter((l) => ids.has(l.a) && ids.has(l.b) && l.a !== l.b);
  const lineIds = new Set(lines.map((l) => l.id));
  const refOk = (r) => r && (r.t === 'point' ? ids.has(r.id) : r.t === 'line' && lineIds.has(r.id));
  let dims = (Array.isArray(d.dims) ? d.dims : [])
    .map((q) => ({
      id: Number(q.id), axis: q.axis === 'y' ? 'y' : 'x', a: Number(q.a), b: Number(q.b), value: num(q.value),
      kind: String(q.kind || 'pp'), refs: (q.refs || []).map((r) => ({ t: r.t, id: Number(r.id) })),
      ...(Number.isFinite(Number(q.off)) && q.off !== null ? { off: num(q.off) } : {}),
    }))
    .filter((q) => ids.has(q.a) && ids.has(q.b) && Number.isFinite(q.value) && q.refs.length && q.refs.every(refOk));
  // v0.6 までの図面のワーク原点（図面に 1 つ）は、線がつながっていればふつうの点として残し、
  // つながっていなければ消す。原点からの寸法は、点と点の寸法にする。
  const oldOrigin = Number(d.originId);
  if (ids.has(oldOrigin)) {
    if (!lines.some((l) => l.a === oldOrigin || l.b === oldOrigin)) {
      points.splice(points.findIndex((p) => p.id === oldOrigin), 1);
      ids.delete(oldOrigin);
      dims = dims.filter((q) => q.a !== oldOrigin && q.b !== oldOrigin && !q.refs.some((r) => r.t === 'point' && r.id === oldOrigin));
    }
    for (const q of dims) if (q.kind === 'o') q.kind = 'pp';
  }
  const maxId = Math.max(0, ...points.map((p) => p.id), ...lines.map((l) => l.id), ...dims.map((q) => q.id));
  doc = {
    version: 4,
    name: String(d.name || ''),
    program: Number.isInteger(prog) && prog >= PROGRAM_MIN && prog <= PROGRAM_MAX ? prog : null,
    tools: normalizeTools(d.tools),
    sheet: { w: Number(d.sheet.w) || 1000, h: Number(d.sheet.h) || 500, t: posNum(d.sheet.t) },
    points, lines, dims,
    nextId: Math.max(Number(d.nextId) || 1, maxId + 1),
  };
  sel = [];
  undoStack.length = 0;
  dirty = false;
  saveCurrent();
  setMode('draw');
  refresh();
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
    const prog = item.doc.program != null ? `${programText(item.doc.program)}・` : '';
    const nLines = Array.isArray(item.doc.lines) ? item.doc.lines.length : 0;
    small.textContent = `${prog}${d.toLocaleString('ja-JP')}・線 ${nLines} 本・${item.doc.sheet.w}×${item.doc.sheet.h} mm`;
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

// ---- プログラム番号と金型（新規作成のとき・「金型」ボタン） ----
// 新規: いまの図面の金型と板の大きさを初めの値にする（同じ金型を続けて使うことが多いため）
let projectMode = 'new';

function buildToolRows() {
  const box = $('tool-rows');
  box.textContent = '';
  for (let i = 0; i < TOOL_COUNT; i++) {
    const row = document.createElement('div');
    row.className = 'tool-row';
    row.dataset.i = i;
    const opts = Object.entries(SHAPES).map(([k, v]) => `<option value="${k}">${v}</option>`).join('');
    // 形ごとの入力欄を全部作っておき、選んだ形の欄だけ見せる（形を切り替えても入れた値が残る）
    row.innerHTML = `
      <span class="tno">T${i + 1}</span>
      <select class="shape" aria-label="T${i + 1} の形">${opts}</select>
      <span class="dims" data-for="rect">
        <label>幅 <input class="w" type="number" inputmode="decimal" step="any" min="0.01"></label>
        <label>高さ <input class="h" type="number" inputmode="decimal" step="any" min="0.01"></label> mm
      </span>
      <span class="dims" data-for="round">
        <label>直径 φ <input class="d" type="number" inputmode="decimal" step="any" min="0.01"></label> mm
      </span>
      <span class="dims" data-for="special">
        <label>幅 <input class="w" type="number" inputmode="decimal" step="any" min="0.01"></label>
        <label>高さ <input class="h" type="number" inputmode="decimal" step="any" min="0.01"></label> mm
        <label class="note">メモ <input class="note" type="text" maxlength="40" placeholder="形の説明"></label>
      </span>`;
    row.querySelector('.shape').addEventListener('change', () => showDims(row));
    box.append(row);
  }
}
function showDims(row) {
  const shape = row.querySelector('.shape').value;
  row.querySelectorAll('.dims').forEach((s) => { s.hidden = s.dataset.for !== shape; });
}

function openProjectDialog(mode) {
  projectMode = mode;
  $('project-title').textContent = mode === 'new' ? '新規作成' : 'プログラム番号と金型';
  $('project-ok').textContent = mode === 'new' ? '作成' : '決定';
  $('project-err').textContent = '';
  $('program-no').value = mode === 'new' ? '' : (doc.program ?? '');
  doc.tools.forEach((t, i) => {
    const row = $('tool-rows').children[i];
    row.querySelector('.shape').value = t.shape;
    row.querySelectorAll('input.w').forEach((el) => { el.value = t.w ?? ''; });
    row.querySelectorAll('input.h').forEach((el) => { el.value = t.h ?? ''; });
    row.querySelector('input.d').value = t.d ?? '';
    row.querySelector('input.note').value = t.note;
    showDims(row);
  });
  $('dlg-project').showModal();
  if (mode === 'new') $('program-no').focus();
}

// 入力を確かめて {program, tools} を返す。だめなら理由の文字列を返す。
function readProjectDialog() {
  const p = Number($('program-no').value);
  if (!Number.isInteger(p) || p < PROGRAM_MIN || p > PROGRAM_MAX) {
    return `プログラム番号は ${PROGRAM_MIN}〜${PROGRAM_MAX} の整数で入れてください。`;
  }
  const tools = [];
  for (const row of $('tool-rows').children) {
    const no = `T${Number(row.dataset.i) + 1}`;
    const shape = row.querySelector('.shape').value;
    const dims = row.querySelector(`.dims[data-for="${shape}"]`);
    const val = (cls) => posNum(dims.querySelector(`input.${cls}`).value);
    const t = emptyTool();
    t.shape = shape;
    if (shape === 'rect' || shape === 'special') {
      t.w = val('w'); t.h = val('h');
      if (t.w == null || t.h == null) return `${no} の幅と高さを入れてください（0 より大きい数）。`;
      if (shape === 'special') t.note = dims.querySelector('input.note').value.trim();
    } else if (shape === 'round') {
      t.d = val('d');
      if (t.d == null) return `${no} の直径を入れてください（0 より大きい数）。`;
    }
    tools.push(t);
  }
  if (tools.every((t) => t.shape === 'none')) return '金型を 1 つ以上選んでください。';
  return { program: p, tools };
}

$('project-cancel').addEventListener('click', () => $('dlg-project').close('cancel'));
$('project-form').addEventListener('submit', (e) => {
  e.preventDefault();
  const r = readProjectDialog();
  if (typeof r === 'string') { $('project-err').textContent = r; return; }
  $('dlg-project').close('ok');
  if (projectMode === 'new') {
    const sheet = { ...doc.sheet };  // 板の大きさは引き継ぐ
    loadDoc({ ...newDoc(), sheet, program: r.program, tools: r.tools });
    flash(`${programText(r.program)} を作りました`);
  } else {
    snapshot();
    doc.program = r.program;
    doc.tools = r.tools;
    changed();
  }
});

$('btn-new').addEventListener('click', async () => {
  if (dirty && !(await confirmBox('いまの図面は保存されていません。破棄して新しく始めますか？', '新規'))) return;
  openProjectDialog('new');
});
$('btn-tools').addEventListener('click', () => openProjectDialog('edit'));
buildToolRows();

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
    if (cur && cur.doc) {
      const history = loadUndo();  // loadDoc が履歴を空にして保存する前に読んでおく
      resize(); loadDoc(cur.doc);
      undoStack.push(...history);
      dirty = !!cur.dirty; saveCurrent(); updateUi();
      return;
    }
  } catch (e) { /* 壊れていたら新しく始める */ }
  setMode('draw');
  refresh();
  resize();
  openProjectDialog('new');  // 初めて開いたときは、プログラム番号と金型から決める
})();

if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('sw.js').catch((e) => console.warn('Service Worker を登録できませんでした', e));
}
// ホーム画面のアプリでも、保存した図面が消されないように頼む
if (navigator.storage && navigator.storage.persist) navigator.storage.persist().catch(() => {});
