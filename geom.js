'use strict';

// 図形（点・線・寸法）と、寸法から位置を決める処理。画面には触らない。
//
// 線は水平か垂直だけなので、X と Y を別々に扱える。各方向で「b − a = 値」の関係
// （水平線は両端の Y が同じ、垂直線は X が同じ、寸法は入れた値）を重み付きの Union-Find でつなぐ。
// ワーク原点と同じまとまりに入った点は、その方向で位置が確定する。
// 点の座標はいつもすべての関係を満たすように保つ（寸法を入れたら、動かす側のまとまりを平行移動する）。

const Geo = (() => {
  const EPS = 1e-6;
  const AXES = ['x', 'y'];
  const r3 = (v) => Math.round(v * 1000) / 1000;

  function pointById(doc, id) { return doc.points.find((p) => p.id === id); }
  function lineById(doc, id) { return doc.lines.find((l) => l.id === id); }

  // ---- 重み付き Union-Find ----
  // off(v) = pos(v) − pos(root)
  function makeUF() {
    const parent = new Map();
    const off = new Map();
    function find(v) {
      if (!parent.has(v)) { parent.set(v, v); off.set(v, 0); return v; }
      const p = parent.get(v);
      if (p === v) return v;
      const r = find(p);
      off.set(v, off.get(v) + off.get(p));
      parent.set(v, r);
      return r;
    }
    // b − a = o を加える。'new'（別のまとまりをつないだ）/'same'（すでに同じ値で決まる）/'conflict'
    function union(a, b, o) {
      const ra = find(a), rb = find(b);
      if (ra === rb) return Math.abs(off.get(b) - off.get(a) - o) < EPS ? 'same' : 'conflict';
      parent.set(rb, ra);
      off.set(rb, o + off.get(a) - off.get(b));
      return 'new';
    }
    const same = (a, b) => find(a) === find(b);
    return { find, union, same, off: (v) => { find(v); return off.get(v); } };
  }

  // 方向 axis の関係を集める。skipDim の寸法は除く。
  function relations(doc, axis, skipDim) {
    const rel = [];
    for (const l of doc.lines) {
      if ((l.dir === 'h' && axis === 'y') || (l.dir === 'v' && axis === 'x')) rel.push({ a: l.a, b: l.b, o: 0 });
    }
    for (const d of doc.dims) {
      if (d.axis === axis && d.id !== skipDim) rel.push({ a: d.a, b: d.b, o: d.value });
    }
    return rel;
  }

  function build(doc, axis, skipDim) {
    const uf = makeUF();
    for (const p of doc.points) uf.find(p.id);
    for (const r of relations(doc, axis, skipDim)) uf.union(r.a, r.b, r.o);
    return uf;
  }

  // 解析: 方向ごとのまとまりと、確定した点
  function analyze(doc) {
    const uf = { x: build(doc, 'x'), y: build(doc, 'y') };
    const o = doc.originId;
    const fixedAxis = (pid, axis) => o != null && uf[axis].same(pid, o);
    const fixedPoint = (pid) => fixedAxis(pid, 'x') && fixedAxis(pid, 'y');
    const fixedLine = (l) => fixedPoint(l.a) && fixedPoint(l.b);
    return { uf, fixedAxis, fixedPoint, fixedLine };
  }

  // 方向 axis で v と同じまとまりの点をまとめて delta 動かす
  function shiftGroup(doc, uf, axis, v, delta) {
    if (Math.abs(delta) < EPS) return;
    const root = uf.find(v);
    for (const p of doc.points) if (uf.find(p.id) === root) p[axis] = r3(p[axis] + delta);
  }

  // 寸法を加える。動かす側は、原点とつながっていない側（両方つながっていないときは prefer の側）。
  // 戻り値: null（できた）か、できない理由
  function addDim(doc, dim, prefer = 'b') {
    const uf = build(doc, dim.axis);
    const st = uf.union(dim.a, dim.b, dim.value);
    if (st !== 'new') {
      return st === 'same' ? 'この距離は、ほかの寸法や線からすでに決まっています。'
        : 'この距離は、ほかの寸法や線と合いません。';
    }
    // union する前の状態で、動かす側を決めて動かす
    const before = build(doc, dim.axis);
    const pa = pointById(doc, dim.a)[dim.axis], pb = pointById(doc, dim.b)[dim.axis];
    const delta = pa + dim.value - pb;  // b を動かす量
    const o = doc.originId;
    const aFixed = o != null && before.same(dim.a, o);
    const bFixed = o != null && before.same(dim.b, o);
    if (bFixed || (!aFixed && prefer === 'a')) shiftGroup(doc, before, dim.axis, dim.a, -delta);
    else shiftGroup(doc, before, dim.axis, dim.b, delta);
    doc.dims.push(dim);
    return null;
  }

  // 寸法の値を変える
  function setDimValue(doc, dimId, value) {
    const d = doc.dims.find((q) => q.id === dimId);
    if (!d) return '寸法が見つかりません。';
    const uf = build(doc, d.axis, d.id);
    if (uf.same(d.a, d.b)) {
      const now = uf.off(d.b) - uf.off(d.a);
      if (Math.abs(now - value) < EPS) { d.value = value; return null; }
      return 'この寸法は、ほかの寸法や線からも決まっているので変えられません。';
    }
    const pa = pointById(doc, d.a)[d.axis], pb = pointById(doc, d.b)[d.axis];
    const delta = pa + value - pb;
    const o = doc.originId;
    if (o != null && uf.same(d.b, o)) shiftGroup(doc, uf, d.axis, d.a, -delta);
    else shiftGroup(doc, uf, d.axis, d.b, delta);
    d.value = value;
    return null;
  }

  // 点を (dx, dy) 動かす。確定している方向には動かない。ids の点のまとまりをまとめて動かす。
  function moveGroups(doc, ids, dx, dy) {
    for (const axis of AXES) {
      const delta = axis === 'x' ? dx : dy;
      const uf = build(doc, axis);
      const done = new Set();
      for (const id of ids) {
        const root = uf.find(id);
        if (done.has(root)) continue;
        done.add(root);
        if (doc.originId != null && uf.same(id, doc.originId) && id !== doc.originId) continue;
        shiftGroup(doc, uf, axis, id, delta);
      }
    }
  }

  // ---- なぞった線を水平・垂直の折れ線にする ----
  // pts: [{x, y}]（mm）。T: 曲がったと見なすずれ（mm）。戻り値: 頂点の列（2 つ以上）か null
  function strokeToPolyline(pts, T) {
    if (pts.length < 2) return null;
    const verts = [{ x: pts[0].x, y: pts[0].y }];
    let dir = null, c = 0, stable = null;
    for (const p of pts) {
      const s = verts[verts.length - 1];
      if (!dir) {
        const dx = p.x - s.x, dy = p.y - s.y;
        if (Math.max(Math.abs(dx), Math.abs(dy)) < T) continue;
        dir = Math.abs(dx) >= Math.abs(dy) ? 'h' : 'v';
        c = dir === 'h' ? s.y : s.x;
        stable = p;
        continue;
      }
      const dev = dir === 'h' ? p.y - c : p.x - c;
      if (Math.abs(dev) <= T / 2) { stable = p; continue; }
      if (Math.abs(dev) > T) {
        // 曲がった: ずれ始める前の位置を角にする
        const corner = dir === 'h' ? { x: stable.x, y: c } : { x: c, y: stable.y };
        verts.push(corner);
        dir = dir === 'h' ? 'v' : 'h';
        c = dir === 'h' ? corner.y : corner.x;
        stable = p;
      }
    }
    if (!dir) return null;
    const last = pts[pts.length - 1];
    verts.push(dir === 'h' ? { x: last.x, y: c } : { x: c, y: last.y });
    return verts;
  }

  // 頂点の列を整える: 長さ 0 の辺を消し、同じ向きに続く辺を 1 本にする
  function cleanPolyline(verts) {
    let v = verts.filter((p, i) => i === 0 || Math.abs(p.x - verts[i - 1].x) > EPS || Math.abs(p.y - verts[i - 1].y) > EPS);
    let changed = true;
    while (changed && v.length > 2) {
      changed = false;
      for (let i = 1; i < v.length - 1; i++) {
        const a = v[i - 1], b = v[i], c = v[i + 1];
        const h1 = Math.abs(a.y - b.y) < EPS, h2 = Math.abs(b.y - c.y) < EPS;
        const v1 = Math.abs(a.x - b.x) < EPS, v2 = Math.abs(b.x - c.x) < EPS;
        if ((h1 && h2) || (v1 && v2)) { v.splice(i, 1); changed = true; break; }
      }
    }
    return v.length >= 2 ? v : null;
  }

  // ---- 要素の削除 ----
  // 線を消し、使われなくなった点（原点は残す）と、それらを参照する寸法を消す
  function deleteLines(doc, lineIds) {
    const del = new Set(lineIds);
    doc.lines = doc.lines.filter((l) => !del.has(l.id));
    const used = new Set([doc.originId]);
    for (const l of doc.lines) { used.add(l.a); used.add(l.b); }
    const gone = new Set(doc.points.filter((p) => !used.has(p.id)).map((p) => p.id));
    doc.points = doc.points.filter((p) => used.has(p.id));
    doc.dims = doc.dims.filter((d) => !gone.has(d.a) && !gone.has(d.b) &&
      !d.refs.some((r) => (r.t === 'line' && del.has(r.id)) || (r.t === 'point' && gone.has(r.id))));
  }

  return {
    EPS, AXES, r3, pointById, lineById, analyze, build, addDim, setDimValue, moveGroups,
    strokeToPolyline, cleanPolyline, deleteLines,
  };
})();
