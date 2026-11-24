'use strict';

// セットプレス CAM — 図形を描いて寸法を決める画面。図形のデータと解き方は geom.js。
// 座標はすべて mm。内部では板の左下を (0, 0)、Y を上向きに持ち、表示は外形ごとのワーク原点（外形の左上、Y は下へ正）からの値にする。

const APP_VERSION = '0.19.1';
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
// dims: [{id, axis:'x'|'y', a, b, value, kind, refs:[{t:'point'|'line', id}]}]（b − a = value）、
// circles: [{id, c: 中心の点, d: 直径}]（丸穴。中心は points の点なので、寸法・移動は点と同じ。丸が無い図面には持たない）
function newDoc() {
  return {
    version: 4,
    name: '',
    program: null,  // プログラム番号
    tools: Array.from({ length: TOOL_COUNT }, emptyTool),
    sheet: { w: 1000, h: 500, t: null },  // t: 板厚（mm、未設定は null）
    mj: lastMj(),  // ミクロジョイント（mm、0 はつなげない）
    layouts: [],  // 割付 [{work: 外形の点, src: 割付したときの穴の形, hits: [{tool, x0, y0, x1, y1}], warn: [], nx?, ny?: F23 の列数, px?, py?: F23 のピッチ（無ければ自動）}]
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
    const lay = doc.layouts.find((q) => a.owner.get(q.work) === k);
    const off = layOffset(lay);
    const o = { x: a.outers[k].origin.x + off.x, y: a.outers[k].origin.y + off.y };
    const sh = shift.get(k) || { x: 0, y: 0 };
    if (d.axis === 'x') sh.x = d.value - o.x; else sh.y = (doc.sheet.h - d.value) - o.y;
    shift.set(k, sh);
  }
  for (const p of doc.points) {
    const sh = shift.get(a.owner.get(p.id));
    if (sh) { p.x = round(p.x + sh.x); p.y = round(p.y + sh.y); }
  }
}
// ワーク原点: 割付の前は外形を囲む四角の左上。割付のあとは、抜く範囲（F13 の外側など）を囲む四角の左上。
// プログラムの座標・原点の印・板の原点からの寸法は、このワーク原点を使う
// （info.outers[k].origin は形の位置を表すもので、割付を一緒に動かすのに使う）
function layOffset(lay) {
  if (!lay || !lay.org || !lay.hits.length) return { x: 0, y: 0 };
  const x0 = Math.min(...lay.hits.map((h) => h.x0)), y1 = Math.max(...lay.hits.map((h) => h.y1));
  return { x: round(Math.min(0, x0 - lay.org.x)), y: round(Math.max(0, y1 - lay.org.y)) };
}
function workOrigin(k) {
  const o = info.outers[k].origin;
  const off = layOffset(layoutOf(k));
  return { x: round(o.x + off.x), y: round(o.y + off.y) };
}
// 外形の原点の、板の原点からの位置（右へ X+、下へ Y+）
function sheetPos(k, axis) {
  const o = workOrigin(k);
  return round(axis === 'x' ? o.x : doc.sheet.h - o.y);
}
function sheetDimOf(k, axis) { return doc.dims.find((d) => d.kind === 'sheet' && d.axis === axis && info.owner.get(d.b) === k); }
function refresh() {
  if (!Array.isArray(doc.layouts)) doc.layouts = [];  // v0.14 より前の履歴から戻したとき
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
const circles = () => doc.circles || [];
const circleAt = (pid) => circles().find((c) => c.c === pid) || null;  // 中心が pid の丸
const L = (id) => Geo.lineById(doc, id);
const D = (id) => doc.dims.find((d) => d.id === id);
// ワーク原点は外形ごとに、外形を囲む四角の左上。座標は原点から右へ X+、下へ Y+ で表す。
// どの外形にも属さないものは、板の左上から測る。
const sheetTopLeft = () => ({ x: 0, y: doc.sheet.h });
function originAt(x, y) { const k = info.outerAt(x, y); return k == null ? sheetTopLeft() : workOrigin(k); }
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
  drawCopies();
  if (mode === 'layout') drawLayout();
  dimHits = [];
  originHits = [];
  layoutDims().forEach((lv, i) => { if (doc.dims[i].kind === 'sheet') drawSheetDim(doc.dims[i]); else drawDim(doc.dims[i], lv); });
  for (const l of doc.lines) drawLine(l);
  for (const c of circles()) drawCircle(c);
  for (const p of doc.points) drawPoint(p);
  info.outers.forEach((fr, k) => drawOrigin(workOrigin(k), k));
  if (drag && drag.kind === 'stroke') drawStroke();
  if (drag && drag.kind === 'box') drawBox();
}

// 「移動」で何もない所からなぞった、選ぶ範囲の四角
function drawBox() {
  const [ax, ay] = drag.start, [bx, by] = drag.cur;
  ctx.save();
  ctx.fillStyle = 'rgba(232, 137, 12, 0.10)';
  ctx.strokeStyle = COLOR.selected;
  ctx.lineWidth = 1.5;
  ctx.setLineDash([6, 4]);
  ctx.fillRect(Math.min(ax, bx), Math.min(ay, by), Math.abs(bx - ax), Math.abs(by - ay));
  ctx.strokeRect(Math.min(ax, bx), Math.min(ay, by), Math.abs(bx - ax), Math.abs(by - ay));
  ctx.restore();
}

// F23 の多数個取りのプレビュー: 2 個目からの位置に、ワークの線と抜く範囲を点線で描く（板からはみ出すものは赤）
function drawCopies() {
  for (const lay of doc.layouts) {
    const k = info.owner.get(lay.work);
    if (k == null || !lay.hits.length) continue;
    const f = f23Of(lay);
    if (f.nx * f.ny <= 1) continue;
    const ls = doc.lines.filter((l) => info.owner.get(l.a) === k && info.owner.get(l.b) === k);
    ctx.save();
    ctx.setLineDash([6, 4]);
    for (let j = 0; j < f.ny; j++) for (let i = 0; i < f.nx; i++) {
      if (!i && !j) continue;
      const dx = i * f.px, dy = -j * f.py;  // Y は下へ並べる
      const [ax, ay] = toScreen(f.x0 + dx, f.y1 + dy), [bx, by] = toScreen(f.x1 + dx, f.y0 + dy);
      const out = f.x0 + dx < -1e-6 || f.x1 + dx > doc.sheet.w + 1e-6 || f.y0 + dy < -1e-6 || f.y1 + dy > doc.sheet.h + 1e-6;
      ctx.fillStyle = out ? 'rgba(200, 60, 40, 0.10)' : 'rgba(47, 128, 209, 0.07)';
      ctx.fillRect(ax, ay, bx - ax, by - ay);
      ctx.strokeStyle = out ? 'rgba(200, 60, 40, 0.7)' : 'rgba(47, 128, 209, 0.45)';
      ctx.lineWidth = 1;
      ctx.strokeRect(ax, ay, bx - ax, by - ay);
      ctx.strokeStyle = out ? 'rgba(200, 60, 40, 0.85)' : 'rgba(47, 128, 209, 0.8)';
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      for (const l of ls) {
        const a = P(l.a), b = P(l.b);
        const [p1, q1] = toScreen(a.x + dx, a.y + dy), [p2, q2] = toScreen(b.x + dx, b.y + dy);
        ctx.moveTo(p1, q1);
        ctx.lineTo(p2, q2);
      }
      for (const c of circlesOf(k)) {
        const p = P(c.c), [cx, cy] = toScreen(p.x + dx, p.y + dy), r = c.d / 2 * view.scale;
        ctx.moveTo(cx + r, cy);
        ctx.arc(cx, cy, r, 0, Math.PI * 2);
      }
      ctx.stroke();
      ctx.fillStyle = ctx.strokeStyle;
      ctx.font = '11px sans-serif';
      ctx.textBaseline = 'top';
      ctx.fillText(`S${k + 1}-${j * f.nx + i + 1}`, ax + 3, ay + 2);
    }
    ctx.restore();
  }
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
  for (const c of circles()) {
    const p = P(c.c), [x, y] = toScreen(p.x, p.y);
    f.beginPath(); f.arc(x, y, c.d / 2 * view.scale, 0, Math.PI * 2); f.fill();
  }
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

// 丸（丸穴）: 円と、右上に直径 φ。中心の点を選ぶと丸ごと選んだことになる
function drawCircle(c) {
  const p = P(c.c);
  const [x, y] = toScreen(p.x, p.y);
  const r = c.d / 2 * view.scale;
  const s = isSel('point', c.c);
  ctx.save();
  ctx.strokeStyle = s ? COLOR.selected : info.fixedPoint(c.c) ? COLOR.fixed : COLOR.free;
  ctx.lineWidth = s ? 4 : 2.5;
  ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI * 2); ctx.stroke();
  ctx.fillStyle = s ? COLOR.selected : COLOR.dim;
  ctx.font = DIM.font;
  ctx.textAlign = 'left';
  ctx.textBaseline = 'bottom';
  ctx.fillText(`φ${fmt(c.d)}`, x + r * 0.71 + 3, y - r * 0.71 - 2);
  ctx.restore();
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
  const o = workOrigin(k);
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
  const cr = circleResult(raw);
  if (cr) {
    // 丸になる: できあがる円（点線）と直径
    const [x, y] = toScreen(cr.x, cr.y), r = cr.d / 2 * view.scale;
    ctx.strokeStyle = COLOR.free;
    ctx.lineWidth = 2.5;
    ctx.setLineDash([7, 5]);
    ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI * 2); ctx.stroke();
    ctx.setLineDash([]);
    ctx.font = '12px sans-serif';
    ctx.fillStyle = COLOR.free;
    ctx.textAlign = 'left';
    ctx.fillText(`φ${fmt(cr.d)}`, x + r * 0.71 + 3, y - r * 0.71 - 2);
    return;
  }
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

// なぞった跡が丸か: 書き始めの近くまで中心のまわりを一周していて、縦横の折れ線より円のほうがよく合えば丸にする。
// 戻り値 {x, y, d}（中心はグリッドに合わせる。直径はグリッドの間隔に丸め、丸の金型の直径に近ければその直径にする）か null
function circleResult(raw) {
  if (raw.length < 12) return null;
  const xs = raw.map((p) => p.x), ys = raw.map((p) => p.y);
  const minX = Math.min(...xs), maxX = Math.max(...xs), minY = Math.min(...ys), maxY = Math.max(...ys);
  const w = maxX - minX, h = maxY - minY;
  if (Math.min(w, h) * view.scale < 16 || Math.max(w, h) > Math.min(w, h) * 1.35) return null;
  const cx = (minX + maxX) / 2, cy = (minY + maxY) / 2, R = (w + h) / 4;
  const first = raw[0], last = raw[raw.length - 1];
  if (Math.hypot(last.x - first.x, last.y - first.y) > Math.max(SNAP_PX / view.scale, R * 0.6)) return null;
  // 中心のまわりを回った角度
  let sweep = 0;
  for (let i = 1; i < raw.length; i++) {
    let a = Math.atan2(raw[i].y - cy, raw[i].x - cx) - Math.atan2(raw[i - 1].y - cy, raw[i - 1].x - cx);
    if (a > Math.PI) a -= 2 * Math.PI; else if (a < -Math.PI) a += 2 * Math.PI;
    sweep += a;
  }
  if (Math.abs(sweep) < 2 * Math.PI * 0.8) return null;
  // 円からのずれと、縦横の折れ線（閉じたもの）からのずれを比べる
  const ec = raw.reduce((s, p) => s + Math.abs(Math.hypot(p.x - cx, p.y - cy) - R), 0) / raw.length;
  const verts = Geo.strokeToPolyline(raw, 14 / view.scale);
  if (verts) {
    const segs = verts.map((v, i) => [v, verts[(i + 1) % verts.length]]);
    const segDist = (p, a, b) => {
      const vx = b.x - a.x, vy = b.y - a.y, l2 = vx * vx + vy * vy;
      const t = l2 ? Math.max(0, Math.min(1, ((p.x - a.x) * vx + (p.y - a.y) * vy) / l2)) : 0;
      return Math.hypot(p.x - a.x - t * vx, p.y - a.y - t * vy);
    };
    const ep = raw.reduce((s, p) => s + Math.min(...segs.map(([a, b]) => segDist(p, a, b))), 0) / raw.length;
    if (!(ec < ep * 0.85)) return null;
  }
  const step = snapStep();
  let d = round(Math.max(step, Math.round(2 * R / step) * step));
  // 丸の金型の直径に近ければ（25% 以内）、いちばん近い金型の直径にする（そのまま単発で抜ける）
  let best = null;
  for (const t of doc.tools) {
    if (t.shape !== 'round' || !t.d || Math.abs(t.d - 2 * R) > 2 * R * 0.25) continue;
    if (best == null || Math.abs(t.d - 2 * R) < Math.abs(best - 2 * R)) best = t.d;
  }
  if (best != null) d = best;
  return { x: snapAxis(cx, 'x'), y: snapAxis(cy, 'y'), d };
}

// なぞった跡 → {verts: [{x, y, id?, close?}]}。
// 角はグリッドに合わせ、両端が既にある点の近くならその点につなぐ（丸の中心にはつながない）。
function strokeResult(raw) {
  const T = 14 / view.scale;
  let verts = Geo.strokeToPolyline(raw, T);
  if (!verts) return null;
  verts = verts.map((v) => ({ x: snapAxis(v.x, 'x'), y: snapAxis(v.y, 'y') }));
  const dirs = [];
  for (let i = 1; i < verts.length; i++) dirs.push(Math.abs(verts[i].y - verts[i - 1].y) < Geo.EPS ? 'h' : 'v');
  const n = verts.length;
  const centers = new Set(circles().map((c) => c.c));
  // 描き始め
  const ps = nearestPoint(raw[0].x, raw[0].y, SNAP_PX, centers);
  if (ps) {
    verts[0] = { x: ps.x, y: ps.y, id: ps.id };
    if (dirs[0] === 'h') verts[1].y = ps.y; else verts[1].x = ps.x;
  }
  // 描き終わり（描き始めの近くなら閉じる）
  const last = raw[raw.length - 1];
  let pe = nearestPoint(last.x, last.y, SNAP_PX, centers);
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
  const cr = circleResult(raw);
  if (cr) {
    snapshot();
    const id = newId();
    doc.points.push({ id, x: cr.x, y: cr.y });
    if (!doc.circles) doc.circles = [];
    doc.circles.push({ id: newId(), c: id, d: cr.d });
    sel = [];
    changed();
    return true;
  }
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
  if (best) return { t: 'line', id: best.id };
  // 丸: 円の線の近くか内側をタップすると、中心の点を選ぶ（丸ごと選ぶ）
  for (const c of circles()) {
    const p = P(c.c), d = Math.hypot(wx - p.x, wy - p.y);
    if (d <= c.d / 2 + 12 / view.scale) return { t: 'point', id: c.c };
  }
  return null;
}

// ---- タッチ・マウス操作 ----
// 指1本: 作図ではなぞって描く、寸法ではタップで選ぶ、移動ではつかんで動かす。指2本: 拡大縮小と画面の移動。
let mode = 'draw';
const pointers = new Map();
let drag = null;  // {kind:'stroke'|'press'|'move'|'box'|'pinch', ...}

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
  } else if (drag.kind === 'box') {
    drag.cur = pos;
    draw();
  } else if (drag.kind === 'press') {
    // 「移動」で点か線を押したまま 8px 動いたら、選んだものをまとめて動かし始める。
    // 選んでいないものをつかんだときは、それだけを選び直して動かす
    const h = drag.hit;
    // 何もない所から 8px なぞったら、四角の枠で選ぶ
    if (mode === 'move' && !h && Math.hypot(pos[0] - drag.start[0], pos[1] - drag.start[1]) > 8) {
      drag = { kind: 'box', start: drag.start, cur: pos };
      draw();
      return;
    }
    if (mode === 'move' && h && Math.hypot(pos[0] - drag.start[0], pos[1] - drag.start[1]) > 8) {
      if (!isSel(h.t, h.id)) sel = [h];
      snapshot();
      // ワーク原点を選んでいれば、その外形（穴・切り欠き・つながった線を含む）をまるごと動かす。
      // その外形に入る線・点は、二重に動かさないように外す
      const families = sel.filter((q) => q.t === 'origin').map((q) => ({
        pids: new Set(doc.points.filter((p) => info.owner.get(p.id) === q.id).map((p) => p.id)),
        sheet: ['x', 'y'].map((axis) => sheetDimOf(q.id, axis)).filter(Boolean),
        origin: workOrigin(q.id),
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
    updateSelInfo();
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
    else if (mode === 'layout') tapWork(d.hit, d.world);
  } else if (d.kind === 'box') {
    if (e.type !== 'pointercancel') boxSelect(d.start, d.cur);
    draw();
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
// 四角の枠で選ぶ（「移動」）: 枠にすっぽり入るものを、いまの選択に足す。
// 外形がまるごと入れば、その外形の原点を選ぶ（穴・切り欠き・割付・板の原点からの寸法ごと動く）。
// ほかは、両端が枠に入る線を選ぶ
function boxSelect(a, b) {
  const [x0, y1] = toWorld(Math.min(a[0], b[0]), Math.min(a[1], b[1]));
  const [x1, y0] = toWorld(Math.max(a[0], b[0]), Math.max(a[1], b[1]));
  const inside = (p) => p.x >= x0 && p.x <= x1 && p.y >= y0 && p.y <= y1;
  const add = [];
  const whole = new Set();
  info.outers.forEach((fr, k) => {
    const pts = doc.points.filter((p) => info.owner.get(p.id) === k);
    if (pts.length && pts.every(inside)) { whole.add(k); add.push({ t: 'origin', id: k }); }
  });
  for (const l of doc.lines) {
    if (whole.has(info.owner.get(l.a)) && whole.has(info.owner.get(l.b))) continue;
    if (inside(P(l.a)) && inside(P(l.b))) add.push({ t: 'line', id: l.id });
  }
  for (const c of circles()) {
    if (whole.has(info.owner.get(c.c))) continue;
    const p = P(c.c), r = c.d / 2;
    if (inside({ x: p.x - r, y: p.y - r }) && inside({ x: p.x + r, y: p.y + r })) add.push({ t: 'point', id: c.c });
  }
  sel = [...sel, ...add.filter((q) => !isSel(q.t, q.id))];
  updateUi();
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
  $('layout-panel').hidden = m !== 'layout';  // 割付のときだけ左に割付の区画を出す
  if (m !== 'layout' && layoutHit) { layoutHit = null; renderCode(); }
  updateLayoutPanel();
  draw();
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
    if (s.t === 'point' && circleAt(s.id)) {
      const c = circleAt(s.id);
      return { title: `丸 φ${fmt(c.d)}（中心 ${pName(s.id)}・${fixedTxt(info.fixedPoint(s.id))}）`,
        rows: [{ label: '直径 φ', kind: 'dia', circle: c.id }],
        note: '直径を入れて Enter か「決定」で決まります。もう 1 つ点か線を選ぶと、中心までの距離を入れられます。' };
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
      lab.innerHTML = `<span class="pl"></span><input type="number" inputmode="decimal" step="any" data-i="${i}"><span class="unit">mm</span><button type="button" class="ok">決定</button><span class="badge"></span>`;
      lab.querySelector('.pl').textContent = r.label;
      const input = lab.querySelector('input');
      // 値を変えたとき（change）だけでなく、そのままの値でも Enter か「決定」で寸法にできる。
      // 同じ入力で 2 度決めないように、決めたら次に触るまで止める
      let done = false;
      const go = () => { if (done) return; done = true; commitDim(i, input); };
      input.addEventListener('focus', () => { done = false; });
      input.addEventListener('input', () => { done = false; });
      input.addEventListener('change', go);
      input.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); go(); input.blur(); } });
      const ok = lab.querySelector('.ok');
      ok.addEventListener('pointerdown', (e) => e.preventDefault());  // 入力欄からフォーカスを外さない（外すと change で先に決まる）
      ok.addEventListener('click', (e) => { e.preventDefault(); done = false; go(); });
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
    (opt.rows.length ? '数字を入れて Enter か「決定」で決まります。いまの値のままでも決められます。紫の「寸法」は決めた寸法。' : '');
  opt.rows.forEach((r, i) => {
    const input = box.querySelector(`input[data-i="${i}"]`);
    if (r.kind === 'dia') {
      const c = circles().find((q) => q.id === r.circle);
      if (c && document.activeElement !== input) input.value = c.d;
      input.parentElement.querySelector('.badge').textContent = '';
      return;
    }
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
  if (r.kind === 'dia') {
    // 丸の直径: 寸法ではなく、丸の大きさそのものを変える（選んだままにする）
    const c = circles().find((q) => q.id === r.circle);
    if (!c) return;
    if (v <= 0) { flash('直径は 0 より大きくしてください'); updateUi(); return; }
    if (v === c.d) { updateUi(); return; }
    snapshot();
    c.d = v;
    changed();
    return;
  }
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
  // 丸は、中心の点を選んで消す（中心の点と、中心に入れた寸法も消す）
  const gone = new Set(sel.filter((s) => s.t === 'point' && circleAt(s.id)).map((s) => s.id));
  if (gone.size) {
    doc.circles = circles().filter((c) => !gone.has(c.c));
    if (!doc.circles.length) delete doc.circles;
    doc.points = doc.points.filter((p) => !gone.has(p.id));
    doc.dims = doc.dims.filter((d) => !gone.has(d.a) && !gone.has(d.b) && !d.refs.some((r) => r.t === 'point' && gone.has(r.id)));
  }
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
  $('st-count').textContent = `線 ${doc.lines.length} 本（確定 ${fixedLines}）${circles().length ? `・丸 ${circles().length} 個` : ''}・寸法 ${doc.dims.length}`;
  $('st-prog').textContent = doc.program == null ? 'プログラム番号なし' : programText(doc.program);
  $('st-tools').textContent = doc.tools.map((t, i) => (t.shape === 'none' ? null : `T${i + 1} ${toolText(t)}`))
    .filter(Boolean).join('　') || '金型なし';
  updateSelInfo();
  updateProps();
  updateLayoutPanel();
  renderCode();
}
// バージョンのすぐ左: 点を 1 つ選んだら板の原点（左上）からの絶対座標、線を 1 本選んだら長さ
function updateSelInfo() {
  const el = $('st-sel');
  if (!el) return;
  let text = '';
  if (sel.length === 1) {
    const s = sel[0];
    if (s.t === 'point' && P(s.id)) {
      const r = rel(sheetTopLeft(), P(s.id).x, P(s.id).y);
      const c = circleAt(s.id);
      text = `${c ? `丸 φ${fmt(c.d)}  中心 ` : '位置  '}X ${fmt(r.x)}  Y ${fmt(r.y)}`;
    } else if (s.t === 'line' && L(s.id)) {
      const l = L(s.id), a = P(l.a), b = P(l.b);
      text = `長さ ${fmt(round(Math.abs(b.x - a.x) + Math.abs(b.y - a.y)))}`;
    }
  }
  el.textContent = text;
}

// ---- 割付 ----
// 「割付」で図形（外形）をタップして選び、「自動割付」で穴を F3 で抜くプログラムを作る。
let layoutWork = null;  // 選んだ外形の番号（info.outers の番号）
let layoutHit = null;  // ハイライトする F3 {work: 外形を表す点, i: 何回目か}
const hitKey = (work, i) => `hit:${work}:${i}`;
// 割付したあとは、抜く長方形をタップすると、その F3 1 回で抜く範囲をハイライトする。
// 長方形が重なる所は、タップするたびに次の長方形に移る。
function hitsAt(x, y) {
  const list = [];
  for (const lay of doc.layouts) {
    if (info.owner.get(lay.work) == null) continue;
    hitsOf(lay).forEach((h, i) => {
      if (macroOf(h) === 'F13') {
        // F13 は輪郭のまわりの帯（金型が通る所）だけ。金型がないときも指で押せる幅にする
        const t = h.tool == null ? null : doc.tools[h.tool];
        const m = 10 / view.scale;
        const dw = Math.max(t ? t.w : 0, m), dh = Math.max(t ? t.h : 0, m);
        const out = x >= h.x0 - m && x <= h.x1 + m && y >= h.y0 - m && y <= h.y1 + m;
        const inner = x > h.x0 + dw && x < h.x1 - dw && y > h.y0 + dh && y < h.y1 - dh;
        if (out && !inner) list.push({ work: lay.work, i });
        return;
      }
      if (x >= h.x0 - 1e-6 && x <= h.x1 + 1e-6 && y >= h.y0 - 1e-6 && y <= h.y1 + 1e-6) list.push({ work: lay.work, i });
    });
  }
  return list;
}
function selectHit(h) {
  layoutHit = h;
  if (h) layoutWork = info.owner.get(h.work) ?? layoutWork;
  updateLayoutPanel();
  renderCode();
  draw();
}
function tapWork(hit, world) {
  const cands = hit && hit.t === 'origin' ? [] : hitsAt(world[0], world[1]);
  if (cands.length) {
    const cur = layoutHit ? cands.findIndex((c) => c.work === layoutHit.work && c.i === layoutHit.i) : -1;
    selectHit(cands[(cur + 1) % cands.length]);
    return;
  }
  layoutHit = null;
  let k = hit && hit.t === 'origin' ? hit.id : info.outerAt(world[0], world[1]);
  if (k == null && hit && hit.t === 'line') k = info.owner.get(L(hit.id).a) ?? null;
  if (k == null && hit && hit.t === 'point') k = info.owner.get(hit.id) ?? null;
  layoutWork = k;
  updateLayoutPanel();
  renderCode();
  draw();
}
// 割付の長方形を、いまのワークの位置に合わせて返す。割付は割り付けたときのワーク原点（org）と一緒に持ち、
// ワークを動かしたら、原点が動いたぶんだけ一緒に動かす
function hitsOf(lay) {
  const k = info.owner.get(lay.work);
  if (k == null || !info.outers[k]) return lay.hits;
  const o = info.outers[k].origin;
  if (!lay.org) lay.org = { x: o.x, y: o.y };  // 古い図面: いまの位置で割り付けたものとみなす
  const dx = round(o.x - lay.org.x), dy = round(o.y - lay.org.y);
  if (!dx && !dy) return lay.hits;
  return lay.hits.map((h) => ({ ...h, x0: round(h.x0 + dx), x1: round(h.x1 + dx), y0: round(h.y0 + dy), y1: round(h.y1 + dy) }));
}
function workKey(k) { return info.outers[k].ref.x; }  // 外形を表す点（外形のいちばん左の点）
function layoutOf(k) { return doc.layouts.find((q) => info.owner.get(q.work) === k) || null; }
// 外形の穴（いまの形）。割付したときの形と比べて、変わっていないか確かめる
function holesOf(k) { return shapes.filter((s) => s.role === 'hole' && s.outer === k); }
function notchesOf(k) { return shapes.filter((s) => s.role === 'notch' && s.outer === k); }
function circlesOf(k) { return circles().filter((c) => info.owner.get(c.c) === k); }
function holeSrc(k) {
  const o = info.outers[k].origin;
  const rel2 = (poly) => poly.map((v) => [round(v.x - o.x), round(o.y - v.y)]);
  const cs = circlesOf(k);
  const src = JSON.stringify(holesOf(k).map((s) => rel2(s.poly)))
    + (notchesOf(k).length ? `|N${JSON.stringify(notchesOf(k).map((s) => rel2(s.poly)))}` : '')
    + (cs.length ? `|C${JSON.stringify(cs.map((c) => [...rel2([P(c.c)])[0], c.d]))}` : '');
  // 外形を F13 で抜くときは、外形の形も比べる
  return outerRect(k) ? `${src}|${JSON.stringify(rel2(shapes[info.outers[k].shape].poly))}` : src;
}
// 外形を囲む四角。F13 はこの四角を抜き、L 字などで四角から欠けた所は切り欠きとして先に抜く
function outerRect(k) {
  const poly = shapes[info.outers[k].shape].poly;
  const xs = poly.map((v) => v.x), ys = poly.map((v) => v.y);
  return { x0: Math.min(...xs), y0: Math.min(...ys), x1: Math.max(...xs), y1: Math.max(...ys) };
}
// 外形の輪郭を抜く金型: 長方形の金型のうち、いちばん大きいもの
function pickOutlineTool() {
  let best = null;
  doc.tools.forEach((t, i) => {
    if (t.shape !== 'rect' || !t.w || !t.h) return;
    if (best == null || t.w * t.h > doc.tools[best].w * doc.tools[best].h) best = i;
  });
  return best;
}
const macroOf = (h) => h.macro || 'F3';
function rectHit(r, extra = {}) {
  const w = round(r.x1 - r.x0), h = round(r.y1 - r.y0);
  const tool = pickRectTool(w, h);
  const t = tool == null ? null : doc.tools[tool];
  const single = t && Math.abs(t.w - w) < 1e-6 && Math.abs(t.h - h) < 1e-6;
  return { tool, x0: round(r.x0), y0: round(r.y0), x1: round(r.x1), y1: round(r.y1), ...(single ? { macro: '単発' } : {}), ...extra };
}
// 直径 d の丸の金型（番号）。なければ null
function pickRoundTool(d) {
  const i = doc.tools.findIndex((t) => t.shape === 'round' && t.d && Math.abs(t.d - d) < 1e-6);
  return i < 0 ? null : i;
}
// 丸の金型で抜いた単発か
const roundHit = (h) => macroOf(h) === '単発' && h.tool != null && doc.tools[h.tool] && doc.tools[h.tool].shape === 'round';
// 丸の追い抜き（F5）の板厚。未設定なら 1.6 mm
const nibbleT = () => doc.sheet.t || 1.6;
// 直径 d の丸を追い抜き（F5）で抜く金型とピッチ {tool, pitch}。なければ null。
// d より小さい丸の金型のうち大きいものから選ぶ。ピッチ（抜くときの円同士の直線距離）は板厚の 2 倍を目安に、
// 板厚以上 10 mm 以下で、金型の中心が通る円の半径を超えないようにする（入らなければ、もっと小さい金型にする）
function pickNibble(d) {
  const t = nibbleT();
  if (t > 10) return null;
  const cands = doc.tools.map((q, i) => ({ q, i }))
    .filter(({ q }) => q.shape === 'round' && q.d && q.d < d - 1e-6)
    .sort((a, b) => b.q.d - a.q.d || a.i - b.i);
  for (const { q, i } of cands) {
    const pitch = round(Math.min(2 * t, 10, (d - q.d) / 2));
    if (pitch >= t - 1e-9) return { tool: i, pitch };
  }
  return null;
}
// 丸穴を抜く割付（単発か追い抜き）か
const nibbleHit = (h) => macroOf(h) === 'F5' && h.tool != null && doc.tools[h.tool] && doc.tools[h.tool].shape === 'round';
const circleHit = (h) => roundHit(h) || nibbleHit(h);
// 追い抜き（F5）で金型が抜く位置（金型の中心）。機械と同じく、中心の真上から時計回りに、ピッチ（円同士の直線距離）
// ごとに抜く。1 周したところで止めるので、最後の 1 つと最初の間はピッチより狭くなる
function nibblePoints(h) {
  const t = doc.tools[h.tool];
  const cx = (h.x0 + h.x1) / 2, cy = (h.y0 + h.y1) / 2, rho = (h.x1 - h.x0 - t.d) / 2;
  if (!(rho > 0) || !(h.pitch > 0)) return [];
  const step = 2 * Math.asin(Math.min(1, h.pitch / (2 * rho)));  // 1 回ごとに回る角度
  const n = Math.min(2000, Math.ceil(2 * Math.PI / step - 1e-9));
  // Y は上へ正。真上から時計回り（右 → 下 → 左）
  return Array.from({ length: n }, (v, i) => [cx + rho * Math.sin(i * step), cy + rho * Math.cos(i * step)]);
}
// 長方形の金型（番号）のうち、w × h の長方形に入るいちばん大きいもの
function pickRectTool(w, h) {
  let best = null;
  doc.tools.forEach((t, i) => {
    if (t.shape !== 'rect' || !t.w || !t.h) return;
    if (t.w > w + 1e-6 || t.h > h + 1e-6) return;
    if (best == null || t.w * t.h > doc.tools[best].w * doc.tools[best].h) best = i;
  });
  return best;
}
// 穴を長方形に分け、継ぎ目で金型寸法の約 1/3 重ねる。
// 抜いた輪郭とこれから抜く輪郭がぴったり接すると金型に良くないため。重ねた先が穴からはみ出す（抜かない所に
// ぶつかる）ときは、反対側の長方形を伸ばす。どちらもはみ出すときは重ねず、数を返す（open）。
// 水平の帯・垂直の帯の 2 通りで試し、重ねられない継ぎ目が少なく、金型が入らない所が少なく、長方形が少ないほうを使う。
// fix: 切り欠きで、長方形を外形の外へ広げる処理（広げたあとの大きさで金型・重ねる量・良し悪しを決める）
function splitHole(poly, fix = (r) => r) {
  const E = 1e-6, step = (v) => Math.max(0.1, Math.round(v * 10) / 10);
  // 長方形の向き d（'x' か 'y'）の、重ねる量。金型があれば金型、なければ長方形の寸法の 1/3
  const ovOf = (r, d) => {
    const f = fix(r);
    const t = pickRectTool(round(f.x1 - f.x0), round(f.y1 - f.y0));
    const size = t == null ? (d === 'x' ? f.x1 - f.x0 : f.y1 - f.y0) : (d === 'x' ? doc.tools[t].w : doc.tools[t].h);
    return step(size / 3);
  };
  const noTool = (r) => pickRectTool(round(r.x1 - r.x0), round(r.y1 - r.y0)) == null;
  let best = null;
  for (const part of Geo.rectPartitions(poly)) {
    const base = part.map((r) => ({ ...r }));
    const rects = part.map((r) => ({ ...r }));
    let open = 0;
    for (let i = 0; i < base.length; i++) {
      for (let j = 0; j < base.length; j++) {
        const p = base[i], q = base[j];
        // p が上・q が下で水平の継ぎ目（Y は上が正）
        const hSeam = Math.abs(p.y0 - q.y1) < E && Math.min(p.x1, q.x1) - Math.max(p.x0, q.x0) > E;
        // p が左・q が右で垂直の継ぎ目
        const vSeam = Math.abs(p.x1 - q.x0) < E && Math.min(p.y1, q.y1) - Math.max(p.y0, q.y0) > E;
        if (!hSeam && !vSeam) continue;
        // 伸ばす候補: 継ぎ目に沿った幅が相手に収まるほうを先に
        const tries = hSeam
          ? [[j, 'y1', 1, p.y1 - p.y0, 'y', q.x0 >= p.x0 - E && q.x1 <= p.x1 + E], [i, 'y0', -1, q.y1 - q.y0, 'y', p.x0 >= q.x0 - E && p.x1 <= q.x1 + E]]
          : [[i, 'x1', 1, q.x1 - q.x0, 'x', p.y0 >= q.y0 - E && p.y1 <= q.y1 + E], [j, 'x0', -1, p.x1 - p.x0, 'x', q.y0 >= p.y0 - E && q.y1 <= p.y1 + E]];
        tries.sort((u, v) => v[5] - u[5]);
        let done = false;
        for (const [k, key, dir, depth, d] of tries) {
          const ov = Math.min(ovOf(rects[k], d), depth);
          const t = { ...rects[k], [key]: rects[k][key] + dir * ov };
          if (Geo.rectInside(poly, t)) { rects[k] = t; done = true; break; }
        }
        if (!done) open++;
      }
    }
    // 金型が入らない小さい長方形は、穴からはみ出さない所まで広げる。そのあと外へ広げる（切り欠き）
    for (let k = 0; k < rects.length; k++) {
      if (noTool(fix(rects[k]))) rects[k] = Geo.rectGrow(poly, rects[k]);
      rects[k] = fix(rects[k]);
    }
    const missing = rects.filter(noTool).length;
    const area = round(rects.reduce((a, r) => a + (r.x1 - r.x0) * (r.y1 - r.y0), 0));  // 2 度抜く所が少ないほうがよい
    const score = [open, missing, rects.length, area];
    const better = !best || (() => { for (let n = 0; n < score.length; n++) if (score[n] !== best.score[n]) return score[n] < best.score[n]; return false; })();
    if (better) best = { rects, open, score };
  }
  return best || { rects: [], open: 0 };
}
// 切り欠きの長方形 r を、外形を囲む四角 box の辺に乗っている側（r0 で判断）だけ外へ広げて、長方形の金型が
// ちょうど入る大きさにする。はみ出す量がいちばん少ないものを選ぶ（同じなら大きい金型）。ほかのワークにぶつかるものは使わない
function growOutward(r, r0, box, others) {
  const E = 1e-6;
  const can = { x0: Math.abs(r0.x0 - box.x0) < E, x1: Math.abs(r0.x1 - box.x1) < E, y0: Math.abs(r0.y0 - box.y0) < E, y1: Math.abs(r0.y1 - box.y1) < E };
  let best = null;
  doc.tools.forEach((t) => {
    if (t.shape !== 'rect' || !t.w || !t.h) return;
    const g = { ...r };
    const needW = t.w - (r.x1 - r.x0), needH = t.h - (r.y1 - r.y0);
    if (needW > E) { if (can.x0) g.x0 -= needW; else if (can.x1) g.x1 += needW; else return; }
    if (needH > E) { if (can.y0) g.y0 -= needH; else if (can.y1) g.y1 += needH; else return; }
    const gp = [{ x: g.x0, y: g.y0 }, { x: g.x1, y: g.y0 }, { x: g.x1, y: g.y1 }, { x: g.x0, y: g.y1 }];
    if (others.some((p) => Geo.overlaps(p, gp))) return;
    const extra = (g.x1 - g.x0) * (g.y1 - g.y0) - (r.x1 - r.x0) * (r.y1 - r.y0);
    if (!best || extra < best.extra - E || (Math.abs(extra - best.extra) < E && t.w * t.h > best.area)) best = { g, extra, area: t.w * t.h };
  });
  return best && { x0: round(best.g.x0), y0: round(best.g.y0), x1: round(best.g.x1), y1: round(best.g.y1) };
}
function autoLayout() {
  const k = layoutWork;
  if (k == null || !info.outers[k]) return;
  const holes = holesOf(k);
  const hits = [], warn = [];
  if (!doc.tools.some((t) => t.shape === 'rect' && t.w && t.h)) warn.push('長方形の金型がありません。「金型」で決めてください。');
  // 穴・切り欠き・L 字などで外形を囲む四角から欠けた所を、重なりや接する所ごと 1 つにまとめてから分ける。
  // 四角の辺に届くものは切り欠き、届かないものは穴として抜く
  const box = outerRect(k);
  const E = 1e-6;
  const pieces = Geo.cutPieces(shapes[info.outers[k].shape].poly, [...holes, ...notchesOf(k)].map((s) => s.poly), box);
  const onBox = (poly) => poly.some((v) => Math.abs(v.x - box.x0) < E || Math.abs(v.x - box.x1) < E || Math.abs(v.y - box.y0) < E || Math.abs(v.y - box.y1) < E);
  const holePieces = pieces.filter((p) => !onBox(p));
  holePieces.forEach((hpoly, i) => {
    const { rects, open } = splitHole(hpoly);
    if (open) warn.push(`穴 ${i + 1} の継ぎ目 ${open} か所は、抜かない所にぶつかるので重ねていません。`);
    for (const r of rects) {
      const w = round(r.x1 - r.x0), h = round(r.y1 - r.y0);
      const tool = pickRectTool(w, h);
      if (tool == null) warn.push(`穴 ${i + 1} の ${w} × ${h} の部分に入る長方形の金型がありません。`);
      hits.push({ tool, x0: round(r.x0), y0: round(r.y0), x1: round(r.x1), y1: round(r.y1) });
    }
  });
  // 上から下、左から右の順に抜く
  hits.sort((p, q) => q.y1 - p.y1 || p.x0 - q.x0);
  // 丸穴: 同じ直径の丸の金型があれば、中心を単発で 1 回抜く。なければ、小さい丸の金型で追い抜き（F5）する。
  // 金型ごとにまとめ（金型を替える回数を減らす）、上から下、左から右の順。どちらもできない丸は抜かない
  const circ = circlesOf(k).map((c) => {
    const tool = pickRoundTool(c.d);
    const nib = tool == null ? pickNibble(c.d) : null;
    return { c, p: P(c.c), tool: tool != null ? tool : nib ? nib.tool : null, pitch: nib ? nib.pitch : null };
  });
  const missing = new Map();
  for (const q of circ) if (q.tool == null) missing.set(q.c.d, (missing.get(q.c.d) || 0) + 1);
  for (const [d, n] of missing) warn.push(nibbleT() > 10
    ? `丸穴 φ${fmt(d)}（${n} 個）に合う丸の金型がなく、板厚が 10 mm を超えるので追い抜きもできません。抜いていません。`
    : `丸穴 φ${fmt(d)}（${n} 個）に合う丸の金型も、追い抜きできる小さい丸の金型もないので、抜いていません。「金型」で丸の金型を決めてください。`);
  circ.filter((q) => q.tool != null)
    .sort((a, b) => a.tool - b.tool || b.p.y - a.p.y || a.p.x - b.p.x)
    .forEach(({ c, p, tool, pitch }) => {
      const r = c.d / 2;
      hits.push({ tool, x0: round(p.x - r), y0: round(p.y - r), x1: round(p.x + r), y1: round(p.y + r), ...(pitch != null ? { macro: 'F5', pitch } : { macro: '単発' }) });
    });
  // 切り欠き: 外形と重なる所を、単発（金型とちょうど同じ大きさ）か F3 で抜く。ほかのワークにぶつからなければ、
  // 外形の辺の外へ広げて四角く抜き、あとから抜く F13 に金型寸法の約 1/3 かぶるようにする
  // （抜いた輪郭とこれから抜く輪郭がぴったり接すると金型に良くないため）。
  // L 字のような外形は、囲む四角から欠けた所も切り欠きとして抜く
  const notches = pieces.filter((p) => onBox(p));
  const outlineTool = pickOutlineTool();
  if (notches.length) {
    const ot = outlineTool == null ? null : doc.tools[outlineTool];
    const step1 = (v) => Math.max(0.1, Math.round(v * 10) / 10);
    const ovx = ot ? step1(ot.w / 3) : 0, ovy = ot ? step1(ot.h / 3) : 0;
    const others = info.outers.filter((o, j) => j !== k).map((o) => shapes[o.shape].poly);
    const nh = [];
    notches.forEach((poly, i) => {
      // 外形を囲む四角の中の部分（実際に抜く所）を長方形に分け、四角の辺に乗っている側を外へ約 1/3 広げる。
      // 描いた切り欠きがどこまで外へ出ていても、広げる量は同じ
      if (!poly) return;
      // 外形を囲む四角の辺に乗っている側を外へ約 1/3 広げる。入る長方形の金型がないときは、1/3 にこだわらず
      // 外へはみ出させて金型が入る大きさにする。ほかのワークにぶつかるときは広げない（blocked）。
      // 分け方を選ぶ前にこの広げ方を当てるので、細い所も外へ広げたあとの形で、継ぎ目の重ねが約 1/3 になる分け方を選べる
      const fix = (r0) => {
        const r = { ...r0 };
        if (Math.abs(r0.x0 - box.x0) < E) r.x0 -= ovx;
        if (Math.abs(r0.x1 - box.x1) < E) r.x1 += ovx;
        if (Math.abs(r0.y0 - box.y0) < E) r.y0 -= ovy;
        if (Math.abs(r0.y1 - box.y1) < E) r.y1 += ovy;
        const rp = [{ x: r.x0, y: r.y0 }, { x: r.x1, y: r.y0 }, { x: r.x1, y: r.y1 }, { x: r.x0, y: r.y1 }];
        const hitOther = others.some((p) => Geo.overlaps(p, rp));
        let use = hitOther ? { ...r0 } : r;
        if (pickRectTool(round(use.x1 - use.x0), round(use.y1 - use.y0)) == null) {
          const grown = growOutward(use, r0, box, others);
          if (grown) use = grown;
        }
        if (hitOther) use.blocked = true;
        return use;
      };
      const { rects, open } = splitHole(poly, fix);
      if (open) warn.push(`切り欠き ${i + 1} の継ぎ目 ${open} か所は、抜かない所にぶつかるので重ねていません。`);
      let blocked = false;
      for (const use of rects) {
        if (use.blocked) blocked = true;
        const hit = rectHit(use, { kind: 'notch' });
        if (hit.tool == null) warn.push(`切り欠き ${i + 1} の ${round(use.x1 - use.x0)} × ${round(use.y1 - use.y0)} の部分に入る長方形の金型がありません。`);
        nh.push(hit);
      }
      if (blocked) warn.push(`切り欠き ${i + 1} は、ほかのワークにぶつかるので外へ広げていない所があります（F13 と重なりません）。`);
    });
    nh.sort((p, q) => q.y1 - p.y1 || p.x0 - q.x0);
    hits.push(...nh);
  }
  // 外形が単純な長方形なら、最後に F13 で輪郭を抜く。F13 は入れた長方形の内側に沿って金型を送るので、
  // 金型寸法のぶん外側にオフセットした長方形を入れる（開始点 左上 → 外形 XY → 金型 XY → 角 R）
  if (box) {
    const tool = outlineTool;
    const t = tool == null ? null : doc.tools[tool];
    const dw = t ? t.w : 0, dh = t ? t.h : 0;
    if (tool == null) warn.push('外形を抜く長方形の金型がありません。');
    hits.push({ macro: 'F13', tool, mj: doc.mj || 0, x0: round(box.x0 - dw), y0: round(box.y0 - dh), x1: round(box.x1 + dw), y1: round(box.y1 + dh) });
  }
  snapshot();
  const key = workKey(k);
  const before = workOrigin(k);  // 前の割付があれば、その原点
  const prev = layoutOf(k);  // F23 のピッチ・列数は割り付け直しても保つ
  doc.layouts = doc.layouts.filter((q) => info.owner.get(q.work) !== k);
  const o = info.outers[k].origin;
  const keep = {};
  for (const f of F23_FIELDS) if (prev && prev[f] != null) keep[f] = prev[f];
  doc.layouts.push({ work: key, src: holeSrc(k), hits, warn, org: { x: o.x, y: o.y }, ...keep });
  keepSheetDims(k, before);
  layoutHit = null;
  changed();
  flash(`S${k + 1} を割り付けました（穴 ${holes.length} 個${circ.length ? `・丸穴 ${circ.length} 個` : ''}${notches.length ? `・切り欠き ${notches.length} 個` : ''}・外形 F13）`);
}
function clearLayout() {
  const k = layoutWork;
  if (k == null || !layoutOf(k)) return;
  snapshot();
  const before = workOrigin(k);
  doc.layouts = doc.layouts.filter((q) => info.owner.get(q.work) !== k);
  keepSheetDims(k, before);
  layoutHit = null;
  changed();
}
// ワーク原点が before から動いたぶん、板の原点からの寸法の値を直す（ワークの位置は変えない）
function keepSheetDims(k, before) {
  const now = workOrigin(k);
  for (const axis of ['x', 'y']) {
    const d = sheetDimOf(k, axis);
    if (d) d.value = round(d.value + (axis === 'x' ? now.x - before.x : before.y - now.y));
  }
}
$('btn-autolayout').addEventListener('click', async () => {
  // コードで手直しした割付は、確かめてから割り付け直す
  const lay = layoutWork == null ? null : layoutOf(layoutWork);
  if (lay && lay.manual && !(await confirmBox(`S${layoutWork + 1} の割付はコードで手直ししてあります。自動割付をやり直すと、手直しした所は消えます。続けますか？`, '割り付け直す'))) return;
  autoLayout();
});
$('btn-clearlayout').addEventListener('click', clearLayout);
// ミクロジョイント: 外形の角を少しだけつなげたままにする（mm）。0 ならつなげない（角 R 0）。
// F13 は角 R のぶん各辺の両端を開けて抜くので、角 R = 金型寸法 + ミクロジョイント を入れる
// （例: 15 mm 角の金型でミクロジョイント 0.2 なら角 R 15.2。角が 0.2 mm ずつつながる）。
const selF13 = () => { const lay = layoutWork == null ? null : layoutOf(layoutWork); return lay && lay.hits.find((q) => macroOf(q) === 'F13'); };
// 値は図面（doc.mj）に保存し、「自動割付」をやり直しても、ほかのワークでも同じ値を使う。新しい図面には最後に使った値
const MJ_KEY = 'setpress-cam:mj';
function lastMj() { try { const v = Number(localStorage.getItem('setpress-cam:mj')); return Number.isFinite(v) && v > 0 ? v : 0; } catch (e) { return 0; } }
numberInput('lp-mj', () => 0, () => {
  const h = selF13();
  const t = h && h.tool != null ? doc.tools[h.tool] : null;
  return h && t ? Math.max(0, Math.min(h.x1 - h.x0, h.y1 - h.y0) / 2 - Math.max(t.w, t.h)) : 50;
}, (v) => {
  const h = selF13();
  if (v === (doc.mj || 0) && (!h || v === (h.mj || 0))) { updateUi(); return; }
  snapshot();
  doc.mj = v;
  if (h) h.mj = v;
  try { localStorage.setItem(MJ_KEY, String(v)); } catch (e) { /* 保存できなくても図面には残る */ }
  changed();
});
function f13R(h) {
  const t = h.tool == null ? null : doc.tools[h.tool];
  return h.mj && t ? round(Math.max(t.w, t.h) + h.mj) : 0;
}
// 長方形の金型では、角 R を長いほうの金型寸法に合わせると、短いほうの辺ではつながりが長く残る。
// その余りを F13 の前に単発で抜いておく（金型の中心の座標を入れる）。4 つの角に、余りの長さ ÷ 金型寸法 回ずつ。
function f13Singles(h) {
  const t = h.tool == null ? null : doc.tools[h.tool];
  if (!t || !h.mj || Math.abs(t.w - t.h) < 1e-6) return [];
  const out = [];
  const mj = h.mj;
  const r = f13R(h);
  // 重ねる量: 送りの向きの金型寸法の約 1/3（0.1 mm に丸める）。F13 で抜く帯の側へ伸ばす
  // （ミクロジョイントの側は抜かない所なので伸ばさない）。帯の半分より長くはしない
  const ovOf = (len, strip) => Math.min(Math.max(0.1, Math.round(len / 3 * 10) / 10), Math.max(0, strip / 2));
  // 並べる向きの金型寸法 len で、区間 [a, b] を端から端まで覆う位置。両端にそろえ、
  // 単発どうしも約 1/3 以上重なるように数を決め、間は等しく重ねる
  const spots = (a, b, len) => {
    const step = len - len / 3;
    const n = b - a <= len + 1e-6 ? 1 : 1 + Math.ceil((b - a - len) / step - 1e-6);
    if (n === 1) return [a];
    const gap = Math.floor((b - a - len) / (n - 1) * 10 + 1e-6) / 10;  // 0.1 mm 刻み（切り捨てなので重なりは減らない）
    return Array.from({ length: n }, (_, i) => (i === n - 1 ? b - len : a + gap * i));
  };
  if (t.w > t.h) {
    // 左右の帯（縦に送る、送りの向きの寸法は t.h）の、角に近い所に余りが残る
    const ov = ovOf(t.h, (h.y1 - h.y0) - 2 * r);
    const ranges = [[h.y1 - t.w - mj - ov, h.y1 - t.h - mj], [h.y0 + t.h + mj, h.y0 + t.w + mj + ov]];
    for (const xs of [h.x0, h.x1 - t.w]) {
      for (const [a, b] of ranges) for (const y of spots(a, b, t.h)) out.push({ x0: xs, x1: xs + t.w, y0: y, y1: y + t.h });
    }
  } else {
    // 上下の帯（横に送る、送りの向きの寸法は t.w）
    const ov = ovOf(t.w, (h.x1 - h.x0) - 2 * r);
    const ranges = [[h.x0 + t.w + mj, h.x0 + t.h + mj + ov], [h.x1 - t.h - mj - ov, h.x1 - t.w - mj]];
    for (const ys of [h.y1 - t.h, h.y0]) {
      for (const [a, b] of ranges) for (const x of spots(a, b, t.w)) out.push({ x0: x, x1: x + t.w, y0: ys, y1: ys + t.h });
    }
  }
  return out.map((q) => ({ x0: round(q.x0), x1: round(q.x1), y0: round(q.y0), y1: round(q.y1) }));
}
// F13 で実際に抜く帯: 入れた長方形の内側を金型の幅で回り、各辺の両端は角 R のぶん残す
function f13Strips(h) {
  const t = h.tool == null ? null : doc.tools[h.tool];
  if (!t) return [];
  const r = f13R(h);
  return [
    { x0: h.x0 + r, x1: h.x1 - r, y0: h.y1 - t.h, y1: h.y1 },  // 上
    { x0: h.x0 + r, x1: h.x1 - r, y0: h.y0, y1: h.y0 + t.h },  // 下
    { x0: h.x0, x1: h.x0 + t.w, y0: h.y0 + r, y1: h.y1 - r },  // 左
    { x0: h.x1 - t.w, x1: h.x1, y0: h.y0 + r, y1: h.y1 - r },  // 右
  ].filter((q) => q.x1 - q.x0 > 1e-6 && q.y1 - q.y0 > 1e-6);
}

function updateLayoutPanel() {
  const panel = $('layout-panel');
  if (!panel || panel.hidden) return;
  if (layoutWork != null && !info.outers[layoutWork]) layoutWork = null;
  const k = layoutWork;
  $('lp-work').textContent = k == null ? '図形をタップして選ぶ' : `S${k + 1}`;
  $('btn-autolayout').disabled = k == null;
  const lay = k == null ? null : layoutOf(k);
  $('btn-clearlayout').disabled = !lay;
  const warn = [];
  let summary = '';
  if (k != null) {
    const holes = holesOf(k);
    const nn = notchesOf(k).length + Geo.complementPieces(shapes[info.outers[k].shape].poly, outerRect(k)).length;
    const nc = circlesOf(k).length;
    summary = `穴 ${holes.length} 個` + (nc ? `・丸穴 ${nc} 個` : '') + (nn ? `・切り欠き ${nn} 個` : '');
    if (lay) {
      const count = (m) => lay.hits.filter((h) => macroOf(h) === m).length;
      summary += `\nF3 ${count('F3')} 回` + (count('単発') ? `\n単発 ${count('単発')} 回` : '') + (count('F5') ? `\nF5 ${count('F5')} 回（丸の追い抜き）` : '') + (count('F13') ? `\nF13 ${count('F13')} 回（外形）` : '');
      const used = [...new Set(lay.hits.map((h) => h.tool).filter((t) => t != null))];
      if (used.length) summary += `\n金型 ${used.map((t) => `T${t + 1} ${toolText(doc.tools[t])}`).join('、')}`;
      warn.push(...lay.warn);
      if (lay.src !== holeSrc(k)) warn.push('割付のあとで図形が変わりました。もう一度「自動割付」を押してください。');
    } else {
      summary += '\nまだ割り付けていません';
    }
  }
  $('lp-summary').textContent = summary;
  const f13 = lay && lay.hits.find((h) => macroOf(h) === 'F13');
  $('lp-mj-row').hidden = k == null;  // ワークを選んだら、割り付ける前でも入れられる
  if (k != null && document.activeElement !== $('lp-mj')) $('lp-mj').value = f13 ? f13.mj || 0 : doc.mj || 0;
  const ul = $('lp-warn');
  ul.textContent = '';
  for (const w of warn) { const li = document.createElement('li'); li.textContent = w; ul.append(li); }
}

// 割付の表示: 選んだ外形を太く縁取り、F3 で抜く長方形（破線）と、開始点（左上）に置く金型（塗り）を描く
const LAYOUT_COLOR = '#1f8a4c';
function drawLayout() {
  const k = layoutWork;
  if (k != null && info.outers[k]) {
    const sh = shapes[info.outers[k].shape];
    ctx.save();
    ctx.strokeStyle = COLOR.selected;
    ctx.lineWidth = 6;
    ctx.globalAlpha = 0.45;
    ctx.beginPath();
    sh.poly.forEach((v, i) => { const [x, y] = toScreen(v.x, v.y); if (i) ctx.lineTo(x, y); else ctx.moveTo(x, y); });
    ctx.closePath();
    ctx.stroke();
    ctx.restore();
  }
  for (const lay of doc.layouts) {
    if (info.owner.get(lay.work) == null) continue;
    hitsOf(lay).forEach((h, i) => {
      const [ax, ay] = toScreen(h.x0, h.y1), [bx, by] = toScreen(h.x1, h.y0);
      const col = h.tool == null ? '#b3261e' : LAYOUT_COLOR;
      ctx.save();
      ctx.strokeStyle = col;
      ctx.lineWidth = 1.5;
      ctx.setLineDash([5, 3]);
      const round1 = roundHit(h);
      const nib = nibbleHit(h);
      if (macroOf(h) === 'F13' && h.mj) {
        // ミクロジョイントで角が残るので、抜く帯と単発の形で描く
        for (const q of [...f13Strips(h), ...f13Singles(h)]) {
          const [px, py] = toScreen(q.x0, q.y1), [qx, qy] = toScreen(q.x1, q.y0);
          ctx.strokeRect(px, py, qx - px, qy - py);
        }
      } else if (round1 || nib) {
        ctx.beginPath(); ctx.arc((ax + bx) / 2, (ay + by) / 2, (bx - ax) / 2, 0, Math.PI * 2); ctx.stroke();
      } else {
        ctx.strokeRect(ax, ay, bx - ax, by - ay);
      }
      ctx.setLineDash([]);
      const t = h.tool == null ? null : doc.tools[h.tool];
      if (round1) {
        ctx.fillStyle = 'rgba(31, 138, 76, 0.28)';
        ctx.beginPath(); ctx.arc((ax + bx) / 2, (ay + by) / 2, t.d / 2 * view.scale, 0, Math.PI * 2); ctx.fill();
      } else if (nib) {
        // 追い抜き: ピッチごとに金型が抜く丸を、まとめて 1 つの形として塗る
        ctx.fillStyle = 'rgba(31, 138, 76, 0.28)';
        ctx.beginPath();
        for (const [x, y] of nibblePoints(h)) {
          const [sx, sy] = toScreen(x, y), r = t.d / 2 * view.scale;
          ctx.moveTo(sx + r, sy);
          ctx.arc(sx, sy, r, 0, Math.PI * 2);
        }
        ctx.fill();
      } else if (t && t.shape === 'rect') {
        ctx.fillStyle = 'rgba(31, 138, 76, 0.28)';
        ctx.fillRect(ax, ay, t.w * view.scale, t.h * view.scale);
      }
      ctx.fillStyle = col;
      ctx.font = 'bold 12px sans-serif';
      ctx.textBaseline = 'top';
      ctx.fillText(macroOf(h) === 'F13' || macroOf(h) === 'F5' ? `${i + 1} ${macroOf(h)}` : `${i + 1}`, ax + 3, ay + 2);
      ctx.restore();
    });
  }
  // ハイライト: 選んだ F3 1 回で抜く範囲
  const lay = layoutHit && doc.layouts.find((q) => q.work === layoutHit.work);
  const h = lay && hitsOf(lay)[layoutHit.i];
  if (h && info.owner.get(lay.work) != null) {
    const [ax, ay] = toScreen(h.x0, h.y1), [bx, by] = toScreen(h.x1, h.y0);
    ctx.save();
    ctx.fillStyle = 'rgba(232, 137, 12, 0.35)';
    ctx.strokeStyle = COLOR.selected;
    ctx.lineWidth = 3;
    const t = h.tool == null ? null : doc.tools[h.tool];
    if (macroOf(h) === 'F13' && t) {
      // F13: 金型が通る輪郭の帯と、前に抜く単発を塗る。ミクロジョイントで残る所は塗らない
      ctx.beginPath();
      for (const q of [...f13Strips(h), ...f13Singles(h)]) {
        const [px, py] = toScreen(q.x0, q.y1), [qx, qy] = toScreen(q.x1, q.y0);
        ctx.rect(px, py, qx - px, qy - py);
      }
      ctx.fill();
      ctx.lineWidth = 2;
      ctx.stroke();
    } else if (circleHit(h)) {
      ctx.beginPath(); ctx.arc((ax + bx) / 2, (ay + by) / 2, (bx - ax) / 2, 0, Math.PI * 2);
      ctx.fill();
      ctx.stroke();
    } else {
      ctx.fillRect(ax, ay, bx - ax, by - ay);
      ctx.strokeRect(ax, ay, bx - ax, by - ay);
    }
    ctx.fillStyle = COLOR.selected;
    ctx.font = 'bold 13px sans-serif';
    ctx.textBaseline = 'bottom';
    ctx.fillText(`${macroOf(h)} ${layoutHit.i + 1}`, ax, ay - 3);
    ctx.restore();
  }
}

// ---- コードの区画 ----
// 機械に手で打ち込むデータ（プログラム番号・割付の F3）とコメントだけを並べる。
function fmt(v) {
  return String(round(v));
}
// ---- F23（多数個取り）: ピッチと列数 ----
// ピッチは、書き換えていなければ 金型が抜く形を囲む四角 + 10 mm。列数は 1〜99（X は右へ、Y は下へ並べる）
const F23_FIELDS = ['nx', 'ny', 'px', 'py'];
const F23_MAX = 99;
function f23Count(v) { const n = Number(v); return Number.isInteger(n) && n > 1 && n <= F23_MAX ? n : null; }
function f23Pitch(v) { const n = Number(v); return v != null && v !== '' && Number.isFinite(n) && n > 0 ? round(Math.min(n, 5000)) : null; }
// 図面ファイルから読んだ値を直す（既定の値は書かない）
function f23Fields(q) {
  const r = {};
  for (const f of ['nx', 'ny']) { const v = f23Count(q[f]); if (v != null) r[f] = v; }
  for (const f of ['px', 'py']) { const v = f23Pitch(q[f]); if (v != null) r[f] = v; }
  return r;
}
function f23Of(lay) {
  const hs = hitsOf(lay);
  const x0 = hs.length ? Math.min(...hs.map((h) => h.x0)) : 0, x1 = hs.length ? Math.max(...hs.map((h) => h.x1)) : 0;
  const y0 = hs.length ? Math.min(...hs.map((h) => h.y0)) : 0, y1 = hs.length ? Math.max(...hs.map((h) => h.y1)) : 0;
  return {
    x0, y0, x1, y1,
    px: lay.px ?? round(x1 - x0 + 10), py: lay.py ?? round(y1 - y0 + 10),
    nx: lay.nx || 1, ny: lay.ny || 1,
  };
}
// コードの区画で F23 の値を書き換えたとき。列数は空なら 1、ピッチは空なら自動に戻す
function setF23(work, field, text) {
  const lay = doc.layouts.find((q) => q.work === work);
  if (!lay) return;
  const t = String(text).trim();
  let v;
  if (field === 'nx' || field === 'ny') {
    const n = t === '' ? 1 : Math.round(Number(t));
    if (!Number.isFinite(n)) return;
    v = Math.min(Math.max(n, 1), F23_MAX);
    if (v === 1) v = null;
  } else {
    if (t !== '' && !Number.isFinite(Number(t))) return;
    v = t === '' ? null : f23Pitch(Math.max(Number(t), 0.1));
  }
  if ((lay[field] ?? null) === v) return;
  snapshot();
  lay[field] = v;
  // 図面ファイルでの並びを nx, ny, px, py にそろえる（Windows 版と同じ JSON にする）
  for (const f of F23_FIELDS) { const x = lay[f]; delete lay[f]; if (x != null) lay[f] = x; }
  changed();
}

// ---- コードを読む（手で書き換えたコードを割付に戻す） ----
// 書き方は genCode と同じ: T → F100〜F109 … F110（中に F3・F13・単発）→ F23。「;」から後ろはコメント。
// どのワークのマクロかは、F23 のワーク原点（板の原点から）で決める。座標は、いまのワーク原点から測る。
// 読めない所があれば、行ごとの誤りを返して何も変えない。
const CODE_EPS = 0.0015;  // コードの数は 0.001 mm に丸めてあるので、そのぶんは同じとみなす
const nearC = (a, b) => Math.abs(a - b) < CODE_EPS;
function parseCode(text) {
  const errs = [];
  const err = (line, msg) => errs.push({ line, msg });
  const NUM = '(-?\\d+(?:\\.\\d+)?|-?\\.\\d+)';
  const reXY = new RegExp(`^X${NUM}Y${NUM}$`);
  const reR = new RegExp(`^R${NUM}$`);
  const reN = new RegExp(`^${NUM}$`);
  let program;           // O の行がなければ undefined（変えない）
  let tool = null;       // いまの T（0 から）
  let mac = null;        // 書いている途中のマクロ {no, hits, line}
  const macros = new Map();
  let exp = null;        // F3・F13 の続き {macro, line, vals, tool, mac}
  let f23 = null;        // F23 の続き {line, vals}
  const places = [];     // F23 {no, k, px, py, nx, ny, line}
  const rectTool = (t, line) => {
    if (t == null) { err(line, 'T で金型を選んでから書いてください'); return null; }
    const q = doc.tools[t];
    if (!q || q.shape !== 'rect' || !q.w || !q.h) { err(line, `T${t + 1} は長方形の金型ではありません`); return null; }
    return q;
  };
  // F3・F13 を書き終えたら、ワーク原点を (0, 0) とした座標（Y は上へ正）の長方形にする
  const finishHit = (e) => {
    const [st, sz, tl] = e.vals;
    if (!(sz[0] > 0 && sz[1] > 0)) { err(e.line, `${e.macro === 'F13' ? '外形' : '寸法'}は 0 より大きくしてください`); return; }
    let t = null;
    if (tl) {
      t = rectTool(e.tool, e.line);
      if (!t) return;
      if (!nearC(t.w, tl[0]) || !nearC(t.h, tl[1])) {
        err(e.line, `金型 X${fmt(tl[0])} Y${fmt(tl[1])} が T${e.tool + 1}（${toolText(t)}）と合いません`);
        return;
      }
    }
    const h = { tool: t ? e.tool : null, x0: st[0], y0: -st[1] - sz[1], x1: st[0] + sz[0], y1: -st[1] };
    if (e.macro === 'F13') {
      h.macro = 'F13';
      const r = e.vals[3];
      if (nearC(r, 0) || !t) h.mj = 0;
      else {
        const base = Math.max(t.w, t.h);
        if (r <= base + 1e-9) { err(e.line, `角 R は 0 か、金型の長いほうの寸法 ${fmt(base)} より大きくしてください`); return; }
        h.mj = round(r - base);
      }
      // すぐ前の単発が、この F13 の前に抜く単発（ミクロジョイントの余り）と同じなら、F13 から作り直すので外す
      const derived = f13Singles(h);
      if (derived.length && e.mac) {
        const tail = e.mac.hits.slice(-derived.length);
        const same = tail.length === derived.length && tail.every((q, i) => q.macro === '単発' && q.tool === h.tool
          && nearC((q.x0 + q.x1) / 2, (derived[i].x0 + derived[i].x1) / 2) && nearC((q.y0 + q.y1) / 2, (derived[i].y0 + derived[i].y1) / 2));
        if (same) e.mac.hits.splice(-derived.length);
      }
    }
    if (e.mac) e.mac.hits.push(h);
  };
  // F5（丸の追い抜き）: 中心 XY → 直径 → ピッチ → 金型直径 → 板厚
  const finishF5 = (e) => {
    const [ctr, d, pitch, td, th] = e.vals;
    if (e.tool == null) { err(e.line, 'T で金型を選んでから書いてください'); return; }
    const q = doc.tools[e.tool];
    if (!q || q.shape !== 'round' || !q.d) { err(e.line, `T${e.tool + 1} は丸の金型ではありません`); return; }
    if (!nearC(q.d, td)) { err(e.line, `金型直径 ${fmt(td)} が T${e.tool + 1}（${toolText(q)}）と合いません`); return; }
    if (!(d > q.d + 1e-6)) { err(e.line, `直径は金型直径 ${fmt(q.d)} より大きくしてください`); return; }
    const t = nibbleT();
    if (!nearC(th, t)) { err(e.line, `板厚 ${fmt(th)} が図面の板厚 ${fmt(t)}${doc.sheet.t ? '' : '（未設定なので 1.6）'} と合いません（板厚は上の「t」で決めてください）`); return; }
    if (pitch < t - 1e-9 || pitch > 10 + 1e-9) { err(e.line, `ピッチは板厚 ${fmt(t)} 以上 10 以下にしてください`); return; }
    if (pitch > (d - q.d) / 2 + 1e-9) { err(e.line, `ピッチは ${fmt(round((d - q.d) / 2))}（直径と金型直径の差の半分）以下にしてください`); return; }
    const cx = ctr[0], cy = -ctr[1];
    if (e.mac) e.mac.hits.push({ tool: e.tool, x0: cx - d / 2, y0: cy - d / 2, x1: cx + d / 2, y1: cy + d / 2, macro: 'F5', pitch });
  };
  const finishF23 = (f) => {
    const [no, org, pitch, cnt] = f.vals;
    if (!macros.has(no)) { err(f.line, `マクロ ${no}（F${no}）がありません`); return; }
    if (places.some((p) => p.no === no)) { err(f.line, `マクロ ${no} を 2 回配置しています`); return; }
    const k = info.outers.findIndex((fr, i) => nearC(sheetPos(i, 'x'), org[0]) && nearC(sheetPos(i, 'y'), org[1]));
    if (k < 0) { err(f.line, `板の原点から X${fmt(org[0])} Y${fmt(org[1])} にワーク原点がありません（ワークは図面で動かしてください）`); return; }
    if (!(pitch[0] > 0 && pitch[1] > 0)) { err(f.line, 'ピッチは 0 より大きくしてください'); return; }
    if (cnt.some((n) => n < 1 || n > F23_MAX)) { err(f.line, `列数は 1〜${F23_MAX} にしてください`); return; }
    places.push({ no, k, px: pitch[0], py: pitch[1], nx: cnt[0], ny: cnt[1], line: f.line });
  };
  String(text).split(/\r?\n/).forEach((raw, idx) => {
    const line = idx + 1;
    const body = raw.normalize('NFKC').split(';')[0].toUpperCase().replace(/\s+/g, ' ').trim();  // 全角の「；」もコメント
    if (!body) return;
    const c = body.replace(/ /g, '');
    let m;
    if (exp && exp.macro === 'F5') {
      const step = exp.vals.length;
      if (step === 0) {
        if (!(m = c.match(reXY))) { err(line, '中心の X Y を書いてください（例: X50 Y20）'); exp = null; return; }
        exp.vals.push([Number(m[1]), Number(m[2])]);
      } else {
        if (!(m = c.match(reN))) { err(line, `${['直径', 'ピッチ', '金型直径', '板厚'][step - 1]}を数字で書いてください`); exp = null; return; }
        exp.vals.push(Number(m[1]));
      }
      if (exp.vals.length === 5) { finishF5(exp); exp = null; }
      return;
    }
    if (exp) {
      const step = exp.vals.length;
      if (exp.macro === 'F13' && step === 3) {
        if (!(m = c.match(reR))) { err(line, '角 R（R0 など）を書いてください'); exp = null; return; }
        exp.vals.push(Number(m[1]));
      } else if (step === 2 && c === '??') {
        exp.vals.push(null);  // 入る金型がない
      } else {
        if (!(m = c.match(reXY))) {
          err(line, `${['開始点', exp.macro === 'F13' ? '外形' : '寸法', '金型'][step]}の X Y を書いてください（例: X50 Y20）`);
          exp = null;
          return;
        }
        exp.vals.push([Number(m[1]), Number(m[2])]);
      }
      if (exp.vals.length === (exp.macro === 'F13' ? 4 : 3)) { finishHit(exp); exp = null; }
      return;
    }
    if (f23) {
      const step = f23.vals.length;
      if (step === 0) {
        if (!/^\d+$/.test(c)) { err(line, 'マクロ番号を書いてください（例: 100）'); f23 = null; return; }
        f23.vals.push(Number(c));
      } else if (step < 3) {
        if (!(m = c.match(reXY))) { err(line, `${step === 1 ? 'ワーク原点' : 'ピッチ'}の X Y を書いてください`); f23 = null; return; }
        f23.vals.push([Number(m[1]), Number(m[2])]);
      } else {
        if (!(m = body.match(/^(\d+) (\d+)$/))) { err(line, '列数を 2 つの整数で書いてください（例: 2 3）'); f23 = null; return; }
        f23.vals.push([Number(m[1]), Number(m[2])]);
      }
      if (f23.vals.length === 4) { finishF23(f23); f23 = null; }
      return;
    }
    if ((m = c.match(/^O(\d+|-+)$/))) {
      if (m[1].startsWith('-')) program = null;
      else {
        const n = Number(m[1]);
        if (n < PROGRAM_MIN || n > PROGRAM_MAX) err(line, `プログラム番号は ${PROGRAM_MIN}〜${PROGRAM_MAX} にしてください`);
        else program = n;
      }
    } else if ((m = c.match(/^T(\d+)$/))) {
      const n = Number(m[1]);
      if (mac) err(line, 'マクロ（F100〜F110）の中には T を書けません');
      else if (n < 1 || n > doc.tools.length || doc.tools[n - 1].shape === 'none') err(line, `T${n} の金型がありません（「金型」で決めてください）`);
      else tool = n - 1;
    } else if ((m = c.match(/^F(\d+)$/))) {
      const f = Number(m[1]);
      if (f === 3 || f === 13) {
        if (!mac) err(line, `F${f} は F100〜F110 の中に書いてください`);
        exp = { macro: f === 13 ? 'F13' : 'F3', line, vals: [], tool, mac };
      } else if (f === 5) {
        if (!mac) err(line, 'F5 は F100〜F110 の中に書いてください');
        exp = { macro: 'F5', line, vals: [], tool, mac };
      } else if (f >= 100 && f <= 109) {
        if (mac) err(line, `F110 でマクロ ${mac.no} を閉じてから書いてください`);
        else if (macros.has(f)) err(line, `マクロ ${f} が 2 回あります`);
        else { mac = { no: f, hits: [], line }; macros.set(f, mac); }
      } else if (f === 110) {
        if (!mac) err(line, 'F110 の前に F100〜F109 がありません');
        mac = null;
      } else if (f === 23) {
        if (mac) err(line, 'F23 は F110 のあとに書いてください');
        f23 = { line, vals: [] };
      } else err(line, `F${f} はここでは使えません（F3・F5・F13・F23・F100〜F110）`);
    } else if ((m = c.match(reXY))) {
      // マクロの中の X Y だけの行は単発（金型の中心）
      if (!mac) { err(line, '単発（X Y）は F100〜F110 の中に書いてください'); return; }
      // 長方形の金型か丸の金型（丸穴）
      const q = tool == null ? null : doc.tools[tool];
      const isRound = q && q.shape === 'round' && q.d;
      const t = isRound ? q : rectTool(tool, line);
      if (!t) return;
      const w = isRound ? q.d : t.w, h = isRound ? q.d : t.h;
      const cx = Number(m[1]), cy = -Number(m[2]);
      mac.hits.push({ tool, x0: cx - w / 2, y0: cy - h / 2, x1: cx + w / 2, y1: cy + h / 2, macro: '単発' });
    } else err(line, '読めない行です');
  });
  if (exp) err(exp.line, `${exp.macro} の続きが足りません`);
  if (f23) err(f23.line, 'F23 の続きが足りません');
  if (mac) err(mac.line, `マクロ ${mac.no} を F110 で閉じてください`);
  for (const [no, q] of macros) if (!places.some((p) => p.no === no)) err(q.line, `マクロ ${no} を配置する F23 がありません`);
  return { errs, program, places, macros };
}

// 読んだコードを割付にする。誤りがあれば [{line, msg}] を返し、何も変えない。変えたら null、変わらなければ []
function applyCode(text) {
  const plan = planCode(text);
  if (plan.errs) return plan.errs;
  return commitCode(plan) ? null : [];
}
// コードを読んで、変える所を決める（まだ変えない）。誤りがあれば {errs}、なければ {plans: [{k, lay}], program}。
// lay が null のものは、コードから消したワーク（割付を消す）
function planCode(text) {
  const r = parseCode(text);
  const groups = new Map();  // ワーク → そのワークの F23（書いた順）
  for (const p of r.places) {
    const g = groups.get(p.k);
    if (!g) { groups.set(p.k, [p]); continue; }
    if (!nearC(g[0].px, p.px) || !nearC(g[0].py, p.py) || g[0].nx !== p.nx || g[0].ny !== p.ny)
      r.errs.push({ line: p.line, msg: `S${p.k + 1} の F23 は、ピッチと列数をそろえてください` });
    g.push(p);
  }
  if (r.errs.length) return { errs: r.errs.sort((a, b) => a.line - b.line) };
  const sameHit = (a, b) => macroOf(a) === macroOf(b) && a.tool === b.tool && nearC(a.x0, b.x0) && nearC(a.y0, b.y0)
    && nearC(a.x1, b.x1) && nearC(a.y1, b.y1) && (a.tool == null || macroOf(a) !== 'F13' || nearC(a.mj || 0, b.mj || 0))
    && (macroOf(a) !== 'F5' || nearC(a.pitch, b.pitch));
  const plans = [];
  info.outers.forEach((fr, k) => {
    const old = layoutOf(k);
    const g = groups.get(k);
    if (!g) {
      if (old && old.hits.length) plans.push({ k, lay: null });  // コードから消したワークは、割付を消す
      return;
    }
    const o = workOrigin(k);
    const oldHits = old ? hitsOf(old) : [];
    const hits = [];
    for (const p of g) {
      for (const q of r.macros.get(p.no).hits) {
        const h = { tool: q.tool, x0: round(q.x0 + o.x), y0: round(q.y0 + o.y), x1: round(q.x1 + o.x), y1: round(q.y1 + o.y) };
        if (q.macro) h.macro = q.macro;
        if (q.macro === 'F13') h.mj = q.mj;
        if (q.macro === 'F5') h.pitch = round(q.pitch);
        const was = oldHits.find((x) => sameHit(x, h));
        if (was && was.kind) h.kind = was.kind;  // 切り欠きの印は、同じ所を抜くものに残す
        hits.push(h);
      }
    }
    const same = old && oldHits.length === hits.length && oldHits.every((x, i) => sameHit(x, hits[i]));
    const p = g[0];
    // ピッチ: 自動のまま書き換えていなければ自動、自動の値と同じでも自動にする
    const fo = old ? f23Of(old) : null;
    const bw = round(Math.max(...hits.map((h) => h.x1)) - Math.min(...hits.map((h) => h.x0)) + 10);
    const bh = round(Math.max(...hits.map((h) => h.y1)) - Math.min(...hits.map((h) => h.y0)) + 10);
    const pitch = (v, oldSet, shown, auto) => ((oldSet == null && fo && nearC(v, shown)) || nearC(v, auto) ? null : round(v));
    const px = pitch(p.px, old && old.px, fo && fo.px, bw), py = pitch(p.py, old && old.py, fo && fo.py, bh);
    const lay = {
      work: old ? old.work : workKey(k),
      src: old ? old.src : holeSrc(k),
      hits: same ? old.hits : hits,
      warn: same ? old.warn : [],
      org: same ? old.org : { x: fr.origin.x, y: fr.origin.y },
      ...((same ? old.manual : true) ? { manual: true } : {}),
      ...(p.nx > 1 ? { nx: p.nx } : {}), ...(p.ny > 1 ? { ny: p.ny } : {}),
      ...(px != null ? { px } : {}), ...(py != null ? { py } : {}),
    };
    if (!old || JSON.stringify(lay) !== JSON.stringify(old)) plans.push({ k, lay });
  });
  return { plans, program: r.program !== undefined && r.program !== doc.program ? r.program : undefined };
}
// planCode で決めた所を変える。変えたら true
function commitCode({ plans, program }) {
  if (!plans.length && program === undefined) return false;
  snapshot();
  const before = plans.map((q) => workOrigin(q.k));
  for (const q of plans) {
    const i = doc.layouts.findIndex((x) => info.owner.get(x.work) === q.k);
    if (q.lay == null) doc.layouts.splice(i, 1);
    else if (i >= 0) doc.layouts[i] = q.lay;
    else doc.layouts.push(q.lay);
  }
  plans.forEach((q, i) => keepSheetDims(q.k, before[i]));
  if (program !== undefined) doc.program = program;
  layoutHit = null;
  changed();
  return true;
}

function genCode() {
  const lines = [];  // {text, key?, comment?, edit?: F23 の書き換えられる行 {work, fields, vals, prefix, note}}
  const add = (text, key, comment = false, edit = null) => lines.push({ text, key, comment, edit });
  add(`; セットプレス CAM v${APP_VERSION}`, null, true);
  add(`; 図面 ${doc.name || '名前なし'}  板 ${fmt(doc.sheet.w)} x ${fmt(doc.sheet.h)} mm`, null, true);
  add(`; 板厚 ${doc.sheet.t ? `${fmt(doc.sheet.t)} mm` : '未設定'}`, null, true);
  add(programText(doc.program), null);
  doc.tools.forEach((t, i) => add(`; T${i + 1} ${toolText(t)}`, null, true));
  // 割付（いちばん上に出す）: 機械に手で打ち込む順に並べる。F3 のあとに 左上の開始点 → XY 寸法 → 金型の XY 寸法
  // ワークごとに F100〜（マクロ番号）と F110 で囲んでマクロに登録し、F23 で板の上の位置へ呼び出す。
  // マクロの中には T 番号を書けないので、T はマクロの前に出し、金型が変わる所でマクロを分ける
  let macroNo = 100;
  for (const lay of doc.layouts) {
    const k = info.owner.get(lay.work);
    if (k == null) continue;
    const o = workOrigin(k);  // 抜く範囲の左上
    const hs = hitsOf(lay);
    add('', null);
    add(`; S${k + 1} 割付  T → F100〜F110 でマクロに登録 → F23 で配置。F3: 開始点（左上）→ XY 寸法 → 金型 XY 寸法`, null, true);
    if (lay.src !== holeSrc(k)) add('; ※ 割付のあとで図形が変わりました。もう一度自動割付してください', null, true);
    // 抜ける丸の金型がなくて抜いていない丸穴
    const unpunched = circlesOf(k).filter((c) => { const p = P(c.c); return !hs.some((h) => circleHit(h) && nearC(h.x1 - h.x0, c.d) && nearC((h.x0 + h.x1) / 2, p.x) && nearC((h.y0 + h.y1) / 2, p.y)); });
    for (const d of [...new Set(unpunched.map((c) => c.d))])
      add(`; ※ 丸穴 φ${fmt(d)}（${unpunched.filter((c) => c.d === d).length} 個）は、抜ける丸の金型がないので抜いていません`, null, true);
    if (!hs.length) { add('; 抜く所がありません', null, true); continue; }
    // F23 のピッチ（書き換えていなければ 金型が抜く形を囲む四角 + 10 mm）と列数
    const f = f23Of(lay);
    if (f.x0 < -1e-6 || f.y1 > doc.sheet.h + 1e-6 || f.x1 + (f.nx - 1) * f.px > doc.sheet.w + 1e-6 || f.y0 - (f.ny - 1) * f.py < -1e-6)
      add('; ※ 多数個取りの並びが板からはみ出します', null, true);
    // ピッチを抜く形より狭めて重ねるのは、材料を節約するためにわざとすることがあるので注意しない（プレビューで目で確かめる）
    const pitchAuto = lay.px == null && lay.py == null;
    const xy = (x, y, note, ind = '  ') => `${ind}${('X' + fmt(x)).padEnd(8)}${('Y' + fmt(y)).padEnd(8)}; ${note}`;
    // 同じ金型が続くまとまりごとに、T → マクロ → F23 を出す
    // （入る金型がない所は、金型を替えないのでいまのまとまりに入れる）
    let curTool = null;
    for (let s0 = 0; s0 < hs.length;) {
      let s1 = s0, tool = null;
      for (; s1 < hs.length; s1++) {
        const tt = hs[s1].tool;
        if (tt == null) continue;
        if (tool == null) tool = tt; else if (tt !== tool) break;
      }
      const mno = macroNo++;
      if (tool != null && tool !== curTool) { add(`T${tool + 1}`, null); curTool = tool; }
      add(`F${mno}`, null);
      const madd = (text, key, comment = false) => add('  ' + text, key, comment);  // マクロの中は 1 段下げる
      const mxy = (x, y, note) => xy(x, y, note, '    ');
      for (let i = s0; i < s1; i++) {
        const h = hs[i];
        const t = h.tool == null ? null : doc.tools[h.tool];
        const hk = hitKey(lay.work, i);  // この F3 のまとまりの行をタップすると、抜く範囲をハイライトする
        const f13 = macroOf(h) === 'F13';
        const single = macroOf(h) === '単発';
        if (macroOf(h) === 'F5') {
          madd(`; ${i + 1}  丸穴 φ${fmt(round(h.x1 - h.x0))}  F5 追い抜き: 中心 XY → 直径 → ピッチ（円同士の直線距離）→ 金型直径 → 板厚`, hk, true);
          madd('F5', hk);
          add(mxy((h.x0 + h.x1) / 2 - o.x, o.y - (h.y0 + h.y1) / 2, '中心'), hk);
          const nl = (v, note) => add(`    ${fmt(v).padEnd(16)}; ${note}`, hk);
          nl(round(h.x1 - h.x0), '直径');
          nl(h.pitch, 'ピッチ');
          nl(t ? t.d : 0, '金型直径');
          nl(nibbleT(), doc.sheet.t ? '板厚' : '板厚（未設定なので 1.6）');
          continue;
        }
        madd(f13 ? `; ${i + 1} 外形  F13: 開始点（左上）→ 外形 XY → 金型 XY → 角 R（金型寸法のぶん外側）`
          : `; ${i + 1}${h.kind === 'notch' ? ' 切り欠き' : ''}${roundHit(h) ? `  丸穴 φ${fmt(t.d)}` : ''}${single ? '  単発: 金型の中心' : ''}`, hk, true);
        if (f13) {
          const singles = f13Singles(h);
          if (singles.length) madd('; 単発（短いほうの辺のつながりを、ミクロジョイントの長さまで先に抜く。F13 の帯と約 1/3 重ねる）  金型の中心', hk, true);
          for (const q of singles) add(mxy((q.x0 + q.x1) / 2 - o.x, o.y - (q.y0 + q.y1) / 2, '単発 中心'), hk);
        }
        if (single) { add(mxy((h.x0 + h.x1) / 2 - o.x, o.y - (h.y0 + h.y1) / 2, '単発 中心'), hk); continue; }
        madd(macroOf(h), hk);
        add(mxy(h.x0 - o.x, o.y - h.y1, '開始点'), hk);
        add(mxy(h.x1 - h.x0, h.y1 - h.y0, f13 ? '外形' : '寸法'), hk);
        add(t ? mxy(t.w, t.h, '金型') : '    ?       ?       ; 金型（入る長方形の金型がない）', hk);
        if (f13) add(`    ${('R' + fmt(f13R(h))).padEnd(16)}; 角 R${h.mj ? `（金型 + ミクロジョイント ${fmt(h.mj)}）` : ''}`, hk);
      }
      add('F110', null);
      // F23: マクロ番号 → 板の原点（左上）からワーク原点までの XY → ピッチ XY → 列数 XY
      add('; F23: マクロ番号 → 板の原点からワーク原点まで XY → ピッチ XY（抜く形 + 10）→ 列数 XY（ピッチ・列数は書き換えられる）', null, true);
      add('F23', null);
      add(`  ${String(mno).padEnd(16)}; マクロ番号`, null);
      add(xy(sheetPos(k, 'x'), sheetPos(k, 'y'), 'ワーク原点'), null);
      const pnote = pitchAuto ? 'ピッチ' : 'ピッチ（書き換えた値。空にすると自動）';
      add(xy(f.px, f.py, pnote), null, false,
        { work: lay.work, fields: ['px', 'py'], vals: [fmt(f.px), fmt(f.py)], prefix: ['X', 'Y'], note: pnote });
      add(`  ${String(f.nx).padEnd(8)}${String(f.ny).padEnd(8)}; 列数`, null, false,
        { work: lay.work, fields: ['nx', 'ny'], vals: [String(f.nx), String(f.ny)], prefix: ['', ''], note: '列数' });
      s0 = s1;
    }
  }
  // 機械に入力するデータとコメントだけを出す（図形の点・線・寸法の一覧は出さない）
  if (!doc.layouts.some((q) => info.owner.get(q.work) != null)) {
    add('', null);
    add('; まだ割り付けていません（「割付」→ 図形をタップ →「自動割付」）', null, true);
  }
  return lines;
}

let lastCodeSel = '';
let holdCode = false;
let codeEditBase = null;  // 文字で書き換えはじめたとき（反映したとき）のコード。null なら書き換えていない
function renderCode(force = false) {
  const box = $('code');
  if (!box || holdCode) return;  // 更新の途中で古い index.html と組み合わさったとき
  if (codeEditBase != null) {
    // 文字で書き換えている途中: まだ書き換えていなければ、図面の変更に合わせて作り直す
    const ta = $('code-text');
    if (ta.value === codeEditBase) { codeEditBase = codeText(); ta.value = codeEditBase; }
    return;
  }
  // F23 の欄を書き換えている途中は作り直さない（書いている文字が消えるので）。確定したら作り直し、同じ欄に戻す
  const act = document.activeElement;
  const editing = act && act.classList && act.classList.contains('code-in') && box.contains(act) ? act.dataset.field : null;
  if (editing && !force) return;
  const keep = editing ? { value: act.value, s: act.selectionStart, e: act.selectionEnd } : null;
  const lines = genCode();
  const selKeys = new Set(sel.map((s) => `${s.t}:${s.id}`));
  if (layoutHit) selKeys.add(hitKey(layoutHit.work, layoutHit.i));
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
    if (l.edit) {
      // F23 のピッチ・列数: 書き換えられる欄にする（コピーするときは l.text の同じ値を使う）
      tx.append('  ');
      l.edit.fields.forEach((f, j) => {
        if (l.edit.prefix[j]) tx.append(l.edit.prefix[j]);
        const inp = document.createElement('input');
        inp.className = 'code-in';
        inp.inputMode = 'decimal';
        inp.value = l.edit.vals[j];
        inp.dataset.field = `${l.edit.work}:${f}`;
        inp.addEventListener('change', () => {
          // Tab で次の欄へ移るときは、移り終わってから作り直す（先に作り直すと移る先の欄が消える）
          holdCode = true;
          try { setF23(l.edit.work, f, inp.value); } finally { holdCode = false; }
          setTimeout(() => renderCode(true), 0);
        });
        inp.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); inp.blur(); } });
        inp.addEventListener('focus', () => inp.select());  // タップしたら全部を選び、そのまま打ち直せるようにする
        tx.append(inp, ' ');
      });
      tx.append(`; ${l.edit.note}`);
    } else {
      tx.textContent = l.text;
    }
    row.append(no, tx);
    box.append(row);
    if (!firstSel && on) firstSel = row;
  });
  // 書き換えている途中の欄は、書いた文字と選んだ範囲もそのまま戻す
  const back = editing && box.querySelector(`.code-in[data-field="${editing}"]`);
  if (back) { back.value = keep.value; back.focus(); back.setSelectionRange(keep.s, keep.e); }
  // 選んだものが変わったときだけ、その行まで送る
  const sig = [...selKeys].join();
  if (sig !== lastCodeSel && firstSel) firstSel.scrollIntoView({ block: 'nearest' });
  lastCodeSel = sig;
}

// 行をタップすると、「寸法」にしてその要素を選ぶ
$('code')?.addEventListener('click', (e) => {
  const row = e.target.closest('.ln.rect');
  if (!row || (window.getSelection && String(window.getSelection()).length)) return;
  if (row.dataset.key.startsWith('hit:')) {  // F3 のまとまり: 「割付」にして抜く範囲をハイライトする
    const [, work, i] = row.dataset.key.split(':').map(Number);
    if (mode !== 'layout') setMode('layout');
    selectHit({ work, i });
    return;
  }
  const [t, id] = row.dataset.key.split(':');
  setMode('dim');
  sel = [{ t, id: Number(id) }];
  updateUi();
  draw();
});

// ---- コードを文字で書き換える（「編集」→「反映」） ----
const codeText = () => genCode().map((l) => l.text).join('\n') + '\n';
function setCodeEditing(on) {
  codeEditBase = on ? codeText() : null;
  if ($('code-edit')) $('code-edit').hidden = !on;
  $('code').hidden = on;
  $('btn-code-edit').hidden = on;
  if (on) { $('code-text').value = codeEditBase; showCodeErrors([]); $('code-text').focus(); }
  else renderCode();
}
function showCodeErrors(errs) {
  const ul = $('code-err');
  ul.textContent = '';
  for (const e of errs.slice(0, 20)) {
    const li = document.createElement('li');
    li.textContent = `${e.line} 行目: ${e.msg}`;
    li.addEventListener('click', () => {
      // その行を選ぶ
      const ta = $('code-text');
      const rows = ta.value.split('\n');
      const start = rows.slice(0, e.line - 1).reduce((n, s) => n + s.length + 1, 0);
      ta.focus();
      ta.setSelectionRange(start, start + (rows[e.line - 1] || '').length);
    });
    ul.append(li);
  }
  if (errs.length > 20) { const li = document.createElement('li'); li.textContent = `ほかに ${errs.length - 20} 件`; ul.append(li); }
}
async function applyCodeText() {
  const plan = planCode($('code-text').value);
  if (plan.errs) { showCodeErrors(plan.errs); flash(`コードに ${plan.errs.length} 件の誤りがあります`); return; }
  // ワークの割付をまるごと消すときは確かめる（全部消してしまったときなど）
  const gone = plan.plans.filter((q) => q.lay == null).map((q) => `S${q.k + 1}`);
  if (gone.length && !(await confirmBox(`コードに ${gone.join('・')} の F23 がないので、${gone.join('・')} の割付を消します。続けますか？`, '消す'))) return;
  const done = commitCode(plan);
  showCodeErrors([]);
  codeEditBase = codeText();
  $('code-text').value = codeEditBase;  // 読んだ結果を、ふだんの書き方に直して見せる
  flash(done ? 'コードを反映しました' : '変わった所はありません');
}
$('btn-code-edit')?.addEventListener('click', () => setCodeEditing(true));
$('btn-code-cancel')?.addEventListener('click', () => setCodeEditing(false));
$('btn-code-apply')?.addEventListener('click', applyCodeText);
$('code-text')?.addEventListener('keydown', (e) => {
  if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); applyCodeText(); }
});

$('btn-copy')?.addEventListener('click', async () => {
  const text = codeEditBase != null ? $('code-text').value : genCode().map((l) => l.text).join('\n') + '\n';
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
  // 丸（中心の点があって、直径が 0 より大きいもの。同じ点を中心にする丸は 1 つだけ）
  const centerUsed = new Set();
  const circ = (Array.isArray(d.circles) ? d.circles : [])
    .map((c) => ({ id: Number(c && c.id), c: Number(c && c.c), d: posNum(c && c.d) }))
    .filter((c) => Number.isInteger(c.id) && ids.has(c.c) && c.d != null && !centerUsed.has(c.c) && centerUsed.add(c.c));
  const maxId = Math.max(0, ...points.map((p) => p.id), ...lines.map((l) => l.id), ...dims.map((q) => q.id), ...circ.map((c) => c.id));
  doc = {
    version: 4,
    name: String(d.name || ''),
    program: Number.isInteger(prog) && prog >= PROGRAM_MIN && prog <= PROGRAM_MAX ? prog : null,
    tools: normalizeTools(d.tools),
    sheet: { w: Number(d.sheet.w) || 1000, h: Number(d.sheet.h) || 500, t: posNum(d.sheet.t) },
    mj: posNum(d.mj) || 0,
    points, lines, dims,
    layouts: (Array.isArray(d.layouts) ? d.layouts : []).filter((q) => q && ids.has(Number(q.work))).map((q) => ({
      work: Number(q.work), src: String(q.src || ''),
      hits: (Array.isArray(q.hits) ? q.hits : []).map((h) => ({
        tool: Number.isInteger(h.tool) ? h.tool : null, x0: num(h.x0), y0: num(h.y0), x1: num(h.x1), y1: num(h.y1),
        ...(h.macro === 'F13' ? { macro: 'F13', mj: Number.isFinite(num(h.mj)) && num(h.mj) > 0 ? num(h.mj) : 0 } : {}),
        ...(h.macro === '単発' ? { macro: '単発' } : {}),
        ...(h.macro === 'F5' ? { macro: 'F5', pitch: Number.isFinite(num(h.pitch)) && num(h.pitch) > 0 ? num(h.pitch) : 3.2 } : {}),
        ...(h.kind === 'notch' ? { kind: 'notch' } : {}),
      })).filter((h) => [h.x0, h.y0, h.x1, h.y1].every(Number.isFinite)),
      warn: (Array.isArray(q.warn) ? q.warn : []).map(String),
      org: q.org && Number.isFinite(Number(q.org.x)) && Number.isFinite(Number(q.org.y)) ? { x: Number(q.org.x), y: Number(q.org.y) } : null,
      ...(q.manual === true ? { manual: true } : {}),
      ...f23Fields(q),
    })),
    nextId: Math.max(Number(d.nextId) || 1, maxId + 1),
    ...(circ.length ? { circles: circ } : {}),
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

// Windows 版のインストーラー: いちばん新しいリリースのファイルへ直接つなぐ。
// 取れないとき（オフラインなど）は、リリースのページを開くリンクのまま。
const RELEASES_API = 'https://api.github.com/repos/username9982/setpress-cam/releases/latest';
const DOWNLOAD_PREFIX = 'https://github.com/username9982/setpress-cam/releases/download/';
async function findWindowsInstaller() {
  try {
    const res = await fetch(RELEASES_API, { headers: { Accept: 'application/vnd.github+json' } });
    if (!res.ok) return;
    const rel = await res.json();
    const asset = (rel.assets || []).find((a) => /^SetPressCam-.+-setup\.exe$/i.test(a.name || ''));
    const url = asset && asset.browser_download_url;
    if (typeof url !== 'string' || !url.startsWith(DOWNLOAD_PREFIX)) return;
    const link = $('dl-windows');
    link.href = url;
    link.removeAttribute('target');
    link.textContent = `Windows 版をダウンロード（${rel.tag_name}）`;
  } catch (e) { /* オフライン */ }
}
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
  if (navigator.onLine !== false) findWindowsInstaller();
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
