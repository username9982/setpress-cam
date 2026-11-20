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

  // 寸法の値に合わせて、動かす側をまるごと動かす。
  // 動かす側: 原点とつながっていない側（両方つながっていないときは prefer の側）。
  // 動かす範囲: 動かす端と、関係（線・寸法）でつながった点に加え、その先に「つながっている線」も
  // 長さを保ったまま一緒に動かす。ただし、動かさない側や原点のまとまりに届く線は伸び縮みさせる。
  // uf は、この寸法を入れる前の関係で作ったもの。
  function applyValue(doc, uf, axis, a, b, value, prefer) {
    const o = doc.originId;
    const aFixed = o != null && uf.same(a, o);
    const bFixed = o != null && uf.same(b, o);
    const pa = pointById(doc, a)[axis], pb = pointById(doc, b)[axis];
    const delta = pa + value - pb;  // b を動かす量
    const moveA = bFixed || (!aFixed && prefer === 'a');
    const [mover, keep, d] = moveA ? [a, b, -delta] : [b, a, delta];
    if (Math.abs(d) < EPS) return;
    const forbid = new Set([uf.find(keep)]);
    if (o != null) forbid.add(uf.find(o));
    // まとまり（root）単位で、方向 axis に沿う線をたどる
    const along = doc.lines.filter((l) => (axis === 'x' ? l.dir === 'h' : l.dir === 'v'));
    const moving = new Set([uf.find(mover)]);
    const queue = [uf.find(mover)];
    while (queue.length) {
      const r = queue.shift();
      for (const l of along) {
        const ra = uf.find(l.a), rb = uf.find(l.b);
        const next = ra === r ? rb : rb === r ? ra : null;
        if (next == null || moving.has(next) || forbid.has(next)) continue;
        moving.add(next);
        queue.push(next);
      }
    }
    for (const p of doc.points) if (moving.has(uf.find(p.id))) p[axis] = r3(p[axis] + d);
  }

  // 寸法を加える。戻り値: null（できた）か、できない理由
  function addDim(doc, dim, prefer = 'b') {
    const uf = build(doc, dim.axis);
    const st = build(doc, dim.axis).union(dim.a, dim.b, dim.value);
    if (st !== 'new') {
      return st === 'same' ? 'この距離は、ほかの寸法や線からすでに決まっています。'
        : 'この距離は、ほかの寸法や線と合いません。';
    }
    applyValue(doc, uf, dim.axis, dim.a, dim.b, dim.value, prefer);
    doc.dims.push(dim);
    return null;
  }

  // 寸法の値を変える
  function setDimValue(doc, dimId, value, prefer = 'b') {
    const d = doc.dims.find((q) => q.id === dimId);
    if (!d) return '寸法が見つかりません。';
    const uf = build(doc, d.axis, d.id);
    if (uf.same(d.a, d.b)) {
      const now = uf.off(d.b) - uf.off(d.a);
      if (Math.abs(now - value) < EPS) { d.value = value; return null; }
      return 'この寸法は、ほかの寸法や線からも決まっているので変えられません。';
    }
    applyValue(doc, uf, d.axis, d.a, d.b, value, prefer);
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

  // ---- 閉じた図形 ----
  // 線のつながりから、閉じた図形（ひと回りする線の列）を見つける。
  // 先が行き止まりの線（ひげ）は取り除いて考える。1 つの点に 3 本以上の線が集まる所があるまとまりは扱わない。
  // 戻り値: [{lineIds, pointIds, poly:[{x, y}], area}]
  function closedShapes(doc) {
    let lines = doc.lines.slice();
    const degree = () => {
      const deg = new Map();
      for (const l of lines) { deg.set(l.a, (deg.get(l.a) || 0) + 1); deg.set(l.b, (deg.get(l.b) || 0) + 1); }
      return deg;
    };
    // ひげを取り除く
    for (;;) {
      const deg = degree();
      const next = lines.filter((l) => deg.get(l.a) > 1 && deg.get(l.b) > 1);
      if (next.length === lines.length) break;
      lines = next;
    }
    const deg = degree();
    const byPoint = new Map();
    for (const l of lines) {
      for (const p of [l.a, l.b]) { if (!byPoint.has(p)) byPoint.set(p, []); byPoint.get(p).push(l); }
    }
    const used = new Set();
    const shapes = [];
    for (const start of lines) {
      if (used.has(start.id)) continue;
      // まとまりを集める
      const comp = [];
      const stack = [start];
      const seen = new Set([start.id]);
      while (stack.length) {
        const l = stack.pop();
        comp.push(l);
        for (const p of [l.a, l.b]) for (const m of byPoint.get(p)) if (!seen.has(m.id)) { seen.add(m.id); stack.push(m); }
      }
      comp.forEach((l) => used.add(l.id));
      if (comp.some((l) => deg.get(l.a) !== 2 || deg.get(l.b) !== 2)) continue;
      // ひと回りたどる
      const pointIds = [start.a];
      const lineIds = [start.id];
      let cur = start.b, prev = start;
      while (cur !== start.a) {
        pointIds.push(cur);
        const nxt = byPoint.get(cur).find((m) => m.id !== prev.id);
        lineIds.push(nxt.id);
        cur = nxt.a === cur ? nxt.b : nxt.a;
        prev = nxt;
      }
      const poly = pointIds.map((id) => { const p = pointById(doc, id); return { x: p.x, y: p.y }; });
      const area = Math.abs(polyArea(poly));
      if (area > EPS) shapes.push({ lineIds, pointIds, poly, area });
    }
    return shapes;
  }

  function polyArea(poly) {
    let s = 0;
    for (let i = 0; i < poly.length; i++) {
      const a = poly[i], b = poly[(i + 1) % poly.length];
      s += a.x * b.y - b.x * a.y;
    }
    return s / 2;
  }

  // 点が多角形の中か: 1（中）、0（線の上）、−1（外）
  function inside(poly, x, y) {
    let c = false;
    for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
      const a = poly[i], b = poly[j];
      // 辺の上
      const onX = Math.min(a.x, b.x) - EPS <= x && x <= Math.max(a.x, b.x) + EPS;
      const onY = Math.min(a.y, b.y) - EPS <= y && y <= Math.max(a.y, b.y) + EPS;
      if (onX && onY && Math.abs((b.x - a.x) * (y - a.y) - (b.y - a.y) * (x - a.x)) < EPS) return 0;
      if ((a.y > y) !== (b.y > y) && x < ((b.x - a.x) * (y - a.y)) / (b.y - a.y) + a.x) c = !c;
    }
    return c ? 1 : -1;
  }

  // 水平・垂直の辺が、互いの内側で交わるか
  function edgesCross(p, q) {
    const edges = (poly) => poly.map((a, i) => [a, poly[(i + 1) % poly.length]]);
    for (const [a1, a2] of edges(p)) {
      for (const [b1, b2] of edges(q)) {
        const ah = Math.abs(a1.y - a2.y) < EPS, bh = Math.abs(b1.y - b2.y) < EPS;
        if (ah === bh) continue;
        const [h1, h2, v1, v2] = ah ? [a1, a2, b1, b2] : [b1, b2, a1, a2];
        const x = v1.x, y = h1.y;
        if (Math.min(h1.x, h2.x) + EPS < x && x < Math.max(h1.x, h2.x) - EPS &&
            Math.min(v1.y, v2.y) + EPS < y && y < Math.max(v1.y, v2.y) - EPS) return true;
      }
    }
    return false;
  }

  // 2 つの多角形が、面として重なるか（辺が触れているだけは重ならない）
  function overlaps(p, q) {
    if (edgesCross(p, q)) return true;
    if (q.some((v) => inside(p, v.x, v.y) > 0) || p.some((v) => inside(q, v.x, v.y) > 0)) return true;
    // 頂点がすべて相手の線の上にある場合（同じ形など）は、辺の中点で確かめる
    const mids = (poly) => poly.map((a, i) => { const b = poly[(i + 1) % poly.length]; return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 }; });
    return mids(q).some((v) => inside(p, v.x, v.y) > 0) || mids(p).some((v) => inside(q, v.x, v.y) > 0);
  }

  // 閉じた図形を、外形・穴・切り欠きに分ける。大きい順に見て、
  // それまでの外形と重なるものは抜く側（外形の内側に収まれば穴、はみ出したり辺に触れたりすれば切り欠き）、
  // 重ならないものは別の外形にする。shape.role と shape.parent（外形の番号）を付ける。
  function classifyShapes(shapes) {
    const order = shapes.map((s, i) => i).sort((i, j) => shapes[j].area - shapes[i].area);
    const bases = [];
    for (const i of order) {
      const s = shapes[i];
      const hit = bases.find((b) => overlaps(shapes[b].poly, s.poly));
      if (hit == null) { s.role = 'outer'; s.parent = null; bases.push(i); continue; }
      const base = shapes[hit].poly;
      const allInside = s.poly.every((v) => inside(base, v.x, v.y) > 0) && !edgesCross(base, s.poly);
      s.role = allInside ? 'hole' : 'notch';
      s.parent = hit;
    }
    return shapes;
  }

  return {
    EPS, AXES, r3, pointById, lineById, analyze, build, addDim, setDimValue, moveGroups,
    strokeToPolyline, cleanPolyline, deleteLines, closedShapes, classifyShapes, inside, overlaps,
  };
})();
