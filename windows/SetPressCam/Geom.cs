// 図形（点・線・寸法）と、寸法から位置を決める処理。画面には触らない（Web 版 geom.js の移植）。
//
// 線は水平か垂直だけなので、X と Y を別々に扱える。各方向で「b − a = 値」の関係
// （水平線は両端の Y が同じ、垂直線は X が同じ、寸法は入れた値）を重み付きの Union-Find でつなぐ。
// ワーク原点は閉じた図形の外形ごとに持ち、外形を囲む四角の左上に置く（Y は下へ正）。
// 結果が Web 版と同じになるように、並べ替えは安定なものを使い、丸めは JavaScript の Math.round と同じにする。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SetPressCam
{
    /// <summary>平面の点（mm）。</summary>
    public struct V
    {
        public double X, Y;
        public V(double x, double y) { X = x; Y = y; }
        public double Get(string axis) { return axis == "x" ? X : Y; }
    }

    /// <summary>長方形 {x0, y0, x1, y1}（x0 &lt; x1、y0 &lt; y1、Y は上が正）。</summary>
    public class R
    {
        public double X0, Y0, X1, Y1;
        public bool Blocked;  // 切り欠き: ほかのワークにぶつかるので外へ広げなかった
        public R() { }
        public R(double x0, double y0, double x1, double y1) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
        public R Clone() { return new R(X0, Y0, X1, Y1) { Blocked = Blocked }; }
        public List<V> Poly() { return new List<V> { new V(X0, Y0), new V(X1, Y0), new V(X1, Y1), new V(X0, Y1) }; }
        public double Get(string key)
        {
            switch (key) { case "x0": return X0; case "x1": return X1; case "y0": return Y0; default: return Y1; }
        }
        public R With(string key, double v)
        {
            var r = Clone();
            switch (key) { case "x0": r.X0 = v; break; case "x1": r.X1 = v; break; case "y0": r.Y0 = v; break; default: r.Y1 = v; break; }
            return r;
        }
    }

    /// <summary>JavaScript と同じ丸めと数の書き方。</summary>
    public static class Js
    {
        /// <summary>Math.round(v)（.5 は大きいほうへ）。</summary>
        public static double MathRound(double v) { return Math.Floor(v + 0.5); }
        /// <summary>Math.round(v * 1000) / 1000。</summary>
        public static double R3(double v) { return Math.Floor(v * 1000 + 0.5) / 1000; }
        /// <summary>String(v)（−0 は 0）。</summary>
        public static string Num(double v)
        {
            if (v == 0 || double.IsNaN(v)) return "0";
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>安定な並べ替え（JavaScript の Array.prototype.sort と同じく、等しいものの順は変えない）。</summary>
        public static void Sort<T>(List<T> list, Comparison<T> cmp)
        {
            var sorted = list.Select((x, i) => new KeyValuePair<int, T>(i, x)).ToList();
            sorted.Sort((a, b) => { int c = cmp(a.Value, b.Value); return c != 0 ? c : a.Key.CompareTo(b.Key); });
            for (int i = 0; i < list.Count; i++) list[i] = sorted[i].Value;
        }

        /// <summary>数の差を並べ替えの比較にする（JavaScript の (p, q) => p − q）。</summary>
        public static int Cmp(double d) { return d < 0 ? -1 : d > 0 ? 1 : 0; }
    }

    /// <summary>閉じた図形。</summary>
    public class Shape
    {
        public List<int> LineIds, PointIds;
        public List<V> Poly;
        public double Area;
        public string Role;  // outer / hole / notch / circle（外形にならない丸）
        public int? Parent;  // 抜く側: 重なる外形（shapes の番号）
        public int? Outer;   // 属する外形（outers の番号）
        public CircleGeo Circle;  // 図面の丸（null は線で閉じた図形）
    }

    /// <summary>図形としての丸（中心と半径）。</summary>
    public class CircleGeo
    {
        public int Id;
        public double X, Y, R;
    }

    public class OuterFrame
    {
        public int Shape;
        public V Origin;
        public int RefX, RefY;
    }

    /// <summary>重み付き Union-Find。off(v) = pos(v) − pos(root)。</summary>
    public class UF
    {
        readonly Dictionary<int, int> parent = new Dictionary<int, int>();
        readonly Dictionary<int, double> off = new Dictionary<int, double>();

        public int Find(int v)
        {
            if (!parent.ContainsKey(v)) { parent[v] = v; off[v] = 0; return v; }
            int p = parent[v];
            if (p == v) return v;
            int r = Find(p);
            off[v] = off[v] + off[p];
            parent[v] = r;
            return r;
        }

        /// <summary>b − a = o を加える。"new"（別のまとまりをつないだ）/"same"（すでに同じ値で決まる）/"conflict"</summary>
        public string Union(int a, int b, double o)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return Math.Abs(off[b] - off[a] - o) < Geo.EPS ? "same" : "conflict";
            parent[rb] = ra;
            off[rb] = o + off[a] - off[b];
            return "new";
        }

        public bool Same(int a, int b) { return Find(a) == Find(b); }
        public double Off(int v) { Find(v); return off[v]; }
    }

    public class Analysis
    {
        public UF UfX, UfY;
        public List<Shape> Shapes;
        public List<OuterFrame> Outers;
        public Dictionary<int, int> Owner;
        public Func<double, double, int?> OuterAt;

        public UF Uf(string axis) { return axis == "x" ? UfX : UfY; }
        public int? OwnerOf(int pid) { int k; return Owner.TryGetValue(pid, out k) ? k : (int?)null; }

        public bool FixedAxis(int pid, string axis)
        {
            int k;
            if (!Owner.TryGetValue(pid, out k)) return false;
            var o = Outers[k];
            return Uf(axis).Same(pid, axis == "x" ? o.RefX : o.RefY);
        }

        public bool FixedPoint(int pid) { return FixedAxis(pid, "x") && FixedAxis(pid, "y"); }
        public bool FixedLine(Line l) { return FixedPoint(l.A) && FixedPoint(l.B); }
    }

    public static class Geo
    {
        public const double EPS = 1e-6;

        public static Point PointById(Doc doc, int id) { return doc.Points.Find(p => p.Id == id); }
        public static Line LineById(Doc doc, int id) { return doc.Lines.Find(l => l.Id == id); }

        struct Rel { public int A, B; public double O; }

        // 方向 axis の関係を集める。skipDim の寸法は除く。
        static List<Rel> Relations(Doc doc, string axis, int? skipDim)
        {
            var rel = new List<Rel>();
            foreach (var l in doc.Lines)
                if ((l.Dir == "h" && axis == "y") || (l.Dir == "v" && axis == "x")) rel.Add(new Rel { A = l.A, B = l.B, O = 0 });
            foreach (var d in doc.Dims)
                // 板の原点からの寸法（kind 'sheet'）は、外形をまるごと平行移動して合わせるので、ここには入れない
                if (d.Axis == axis && d.Id != skipDim && d.Kind != "sheet") rel.Add(new Rel { A = d.A, B = d.B, O = d.Value });
            return rel;
        }

        public static UF Build(Doc doc, string axis, int? skipDim = null)
        {
            var uf = new UF();
            foreach (var p in doc.Points) uf.Find(p.Id);
            foreach (var r in Relations(doc, axis, skipDim)) uf.Union(r.A, r.B, r.O);
            return uf;
        }

        // 最小のもの（同じなら先のもの）。JavaScript の slice().sort(f)[0] と同じ
        static T First<T>(List<T> list, Comparison<T> cmp)
        {
            T best = list[0];
            for (int i = 1; i < list.Count; i++) if (cmp(list[i], best) < 0) best = list[i];
            return best;
        }

        // 外形ごとの原点と、点がどの外形に属するか
        public static void OuterFrames(Doc doc, List<Shape> shapes, out List<OuterFrame> outers, out Dictionary<int, int> owner, out Func<double, double, int?> outerAt)
        {
            var outs = new List<OuterFrame>();
            var outerOf = new Dictionary<int, int>();
            for (int i = 0; i < shapes.Count; i++)
            {
                var s = shapes[i];
                if (s.Role != "outer") continue;
                var pts = s.PointIds.Select(id => PointById(doc, id)).ToList();
                double minX = pts.Min(p => p.X), maxY = pts.Max(p => p.Y);
                outerOf[i] = outs.Count;
                // 丸の外形: 原点は丸を囲む四角の左上、基準は X・Y とも丸の中心
                var c = s.Circle;
                if (c != null) { outs.Add(new OuterFrame { Shape = i, Origin = new V(Js.R3(c.X - c.R), Js.R3(c.Y + c.R)), RefX = s.PointIds[0], RefY = s.PointIds[0] }); continue; }
                outs.Add(new OuterFrame
                {
                    Shape = i,
                    Origin = new V(minX, maxY),
                    RefX = First(pts, (p, q) => p.X != q.X ? Js.Cmp(p.X - q.X) : Js.Cmp(q.Y - p.Y)).Id,
                    RefY = First(pts, (p, q) => p.Y != q.Y ? Js.Cmp(q.Y - p.Y) : Js.Cmp(p.X - q.X)).Id,
                });
            }
            var own = new Dictionary<int, int>();
            var order = new List<int>();  // 入れた順（JavaScript の Map と同じ順でたどる）
            for (int i = 0; i < shapes.Count; i++)
            {
                var s = shapes[i];
                int o;
                s.Outer = outerOf.TryGetValue(s.Role == "outer" ? i : (s.Parent ?? -1), out o) ? o : (int?)null;
                if (s.Role == "circle") continue;  // 外形にならない丸（丸穴）は、中心が内側にある外形に入れる（下の at）
                foreach (var id in s.PointIds)
                {
                    if (s.Outer == null) continue;
                    if (!own.ContainsKey(id)) order.Add(id);
                    own[id] = s.Outer.Value;
                }
            }
            // 線でつながった点は、同じ外形に入れる
            var queue = new Queue<int>(order);
            while (queue.Count > 0)
            {
                int id = queue.Dequeue();
                foreach (var l in doc.Lines)
                {
                    int? other = l.A == id ? l.B : l.B == id ? l.A : (int?)null;
                    if (other == null || own.ContainsKey(other.Value)) continue;
                    own[other.Value] = own[id];
                    queue.Enqueue(other.Value);
                }
            }
            // 残りは、内側にある外形（いちばん小さいもの）に入れる
            Func<double, double, int?> at = (x, y) =>
            {
                int? best = null;
                for (int k = 0; k < outs.Count; k++)
                {
                    var sh = shapes[outs[k].Shape];
                    bool inn = sh.Circle != null ? Dist(x - sh.Circle.X, y - sh.Circle.Y) <= sh.Circle.R + EPS : Inside(sh.Poly, x, y) >= 0;
                    if (inn && (best == null || sh.Area < shapes[outs[best.Value].Shape].Area)) best = k;
                }
                return best;
            };
            foreach (var p in doc.Points)
                if (!own.ContainsKey(p.Id)) { var k = at(p.X, p.Y); if (k != null) own[p.Id] = k.Value; }
            outers = outs;
            owner = own;
            outerAt = at;
        }

        // 解析: 閉じた図形、外形ごとの原点、方向ごとのまとまりと確定した点
        public static Analysis Analyze(Doc doc)
        {
            var a = new Analysis { UfX = Build(doc, "x"), UfY = Build(doc, "y") };
            a.Shapes = AllShapes(doc);
            List<OuterFrame> outers; Dictionary<int, int> owner; Func<double, double, int?> outerAt;
            OuterFrames(doc, a.Shapes, out outers, out owner, out outerAt);
            a.Outers = outers; a.Owner = owner; a.OuterAt = outerAt;
            return a;
        }

        static HashSet<int> AnchorRoots(Doc doc, UF uf, string axis)
        {
            List<OuterFrame> outers; Dictionary<int, int> owner; Func<double, double, int?> at;
            OuterFrames(doc, AllShapes(doc), out outers, out owner, out at);
            return new HashSet<int>(outers.Select(o => uf.Find(axis == "x" ? o.RefX : o.RefY)));
        }

        static double Get(Point p, string axis) { return axis == "x" ? p.X : p.Y; }
        static void Set(Point p, string axis, double v) { if (axis == "x") p.X = v; else p.Y = v; }

        // 寸法の値に合わせて、動かす側をまるごと動かす（geom.js の applyValue）
        static void ApplyValue(Doc doc, UF uf, string axis, int a, int b, double value, string prefer)
        {
            var anchors = AnchorRoots(doc, uf, axis);
            bool aFixed = anchors.Contains(uf.Find(a));
            bool bFixed = anchors.Contains(uf.Find(b));
            double pa = Get(PointById(doc, a), axis), pb = Get(PointById(doc, b), axis);
            double delta = pa + value - pb;  // b を動かす量
            bool moveA = bFixed || (!aFixed && prefer == "a");
            int mover = moveA ? a : b, keep = moveA ? b : a;
            double d = moveA ? -delta : delta;
            if (Math.Abs(d) < EPS) return;
            var forbid = new HashSet<int>(anchors) { uf.Find(keep) };
            var along = doc.Lines.Where(l => axis == "x" ? l.Dir == "h" : l.Dir == "v").ToList();
            var moving = new HashSet<int> { uf.Find(mover) };
            var queue = new Queue<int>();
            queue.Enqueue(uf.Find(mover));
            while (queue.Count > 0)
            {
                int r = queue.Dequeue();
                foreach (var l in along)
                {
                    int ra = uf.Find(l.A), rb = uf.Find(l.B);
                    int? next = ra == r ? rb : rb == r ? ra : (int?)null;
                    if (next == null || moving.Contains(next.Value) || forbid.Contains(next.Value)) continue;
                    moving.Add(next.Value);
                    queue.Enqueue(next.Value);
                }
            }
            foreach (var p in doc.Points) if (moving.Contains(uf.Find(p.Id))) Set(p, axis, Js.R3(Get(p, axis) + d));
        }

        /// <summary>寸法を加える。戻り値: null（できた）か、できない理由</summary>
        public static string AddDim(Doc doc, Dim dim, string prefer = "b")
        {
            var uf = Build(doc, dim.Axis);
            var st = Build(doc, dim.Axis).Union(dim.A, dim.B, dim.Value);
            if (st != "new")
                return st == "same" ? "この距離は、ほかの寸法や線からすでに決まっています。" : "この距離は、ほかの寸法や線と合いません。";
            ApplyValue(doc, uf, dim.Axis, dim.A, dim.B, dim.Value, prefer);
            doc.Dims.Add(dim);
            return null;
        }

        /// <summary>寸法の値を変える</summary>
        public static string SetDimValue(Doc doc, int dimId, double value, string prefer = "b")
        {
            var d = doc.Dims.Find(q => q.Id == dimId);
            if (d == null) return "寸法が見つかりません。";
            var uf = Build(doc, d.Axis, d.Id);
            if (uf.Same(d.A, d.B))
            {
                double now = uf.Off(d.B) - uf.Off(d.A);
                if (Math.Abs(now - value) < EPS) { d.Value = value; return null; }
                return "この寸法は、ほかの寸法や線からも決まっているので変えられません。";
            }
            ApplyValue(doc, uf, d.Axis, d.A, d.B, value, prefer);
            d.Value = value;
            return null;
        }

        /// <summary>選んだ線・点をまとめて (dx, dy) 動かす。長さが 0 になる線ができるときは動かさず false。</summary>
        public static bool MoveItems(Doc doc, List<Sel> items, double dx, double dy)
        {
            var rootsX = new HashSet<int>();
            var rootsY = new HashSet<int>();
            var ufx = Build(doc, "x");
            var ufy = Build(doc, "y");
            foreach (var it in items)
            {
                if (it.T == "line")
                {
                    var l = LineById(doc, it.Id);
                    if (l == null) continue;
                    if (l.Dir == "h") rootsY.Add(ufy.Find(l.A)); else rootsX.Add(ufx.Find(l.A));
                }
                else if (it.T == "point" && PointById(doc, it.Id) != null)
                {
                    rootsX.Add(ufx.Find(it.Id));
                    rootsY.Add(ufy.Find(it.Id));
                }
            }
            var old = doc.Points.Select(p => new V(p.X, p.Y)).ToList();
            foreach (var p in doc.Points)
            {
                if (rootsX.Contains(ufx.Find(p.Id))) p.X = Js.R3(p.X + dx);
                if (rootsY.Contains(ufy.Find(p.Id))) p.Y = Js.R3(p.Y + dy);
            }
            bool zero = doc.Lines.Any(m =>
            {
                var a = PointById(doc, m.A); var b = PointById(doc, m.B);
                return Math.Abs(a.X - b.X) < EPS && Math.Abs(a.Y - b.Y) < EPS;
            });
            if (zero) for (int i = 0; i < doc.Points.Count; i++) { doc.Points[i].X = old[i].X; doc.Points[i].Y = old[i].Y; }
            return !zero;
        }

        // ---- なぞった線を水平・垂直の折れ線にする ----
        public static List<V> StrokeToPolyline(List<V> pts, double T)
        {
            if (pts.Count < 2) return null;
            var verts = new List<V> { pts[0] };
            string dir = null;
            double c = 0;
            V K = default(V);
            foreach (var p in pts)
            {
                var s = verts[verts.Count - 1];
                if (dir == null)
                {
                    double ddx = p.X - s.X, ddy = p.Y - s.Y;
                    if (Hypot(ddx, ddy) < T) continue;
                    dir = Math.Abs(ddx) >= Math.Abs(ddy) ? "h" : "v";
                    c = dir == "h" ? s.Y : s.X;
                    K = p;
                    continue;
                }
                double vx = p.X - K.X, vy = p.Y - K.Y;
                double along = dir == "h" ? vx : vy, across = dir == "h" ? vy : vx;
                if (Math.Abs(across) <= Math.Abs(along)) { K = p; continue; }
                if (Hypot(vx, vy) < T) continue;
                var corner = dir == "h" ? new V(K.X, c) : new V(c, K.Y);
                verts.Add(corner);
                dir = dir == "h" ? "v" : "h";
                c = dir == "h" ? corner.Y : corner.X;
                K = p;
            }
            if (dir == null) return null;
            verts.Add(dir == "h" ? new V(K.X, c) : new V(c, K.Y));
            return verts;
        }

        public static double Hypot(double x, double y) { return Math.Sqrt(x * x + y * y); }

        // ---- 要素の削除 ----
        public static void DeleteLines(Doc doc, IEnumerable<int> lineIds)
        {
            var del = new HashSet<int>(lineIds);
            doc.Lines = doc.Lines.Where(l => !del.Contains(l.Id)).ToList();
            var used = new HashSet<int>();
            foreach (var l in doc.Lines) { used.Add(l.A); used.Add(l.B); }
            if (doc.Circles != null) foreach (var c in doc.Circles) used.Add(c.C);  // 丸の中心の点は残す
            var gone = new HashSet<int>(doc.Points.Where(p => !used.Contains(p.Id)).Select(p => p.Id));
            doc.Points = doc.Points.Where(p => used.Contains(p.Id)).ToList();
            doc.Dims = doc.Dims.Where(d => !gone.Contains(d.A) && !gone.Contains(d.B) &&
                !d.Refs.Any(r => (r.T == "line" && del.Contains(r.Id)) || (r.T == "point" && gone.Contains(r.Id)))).ToList();
        }

        // ---- 閉じた図形 ----
        public static List<Shape> ClosedShapes(Doc doc)
        {
            var lines = doc.Lines.ToList();
            Func<Dictionary<int, int>> degree = () =>
            {
                var dg = new Dictionary<int, int>();
                foreach (var l in lines)
                {
                    dg[l.A] = (dg.TryGetValue(l.A, out int x) ? x : 0) + 1;
                    dg[l.B] = (dg.TryGetValue(l.B, out int y) ? y : 0) + 1;
                }
                return dg;
            };
            for (;;)  // ひげを取り除く
            {
                var dg = degree();
                var next = lines.Where(l => dg[l.A] > 1 && dg[l.B] > 1).ToList();
                if (next.Count == lines.Count) break;
                lines = next;
            }
            var deg = degree();
            var byPoint = new Dictionary<int, List<Line>>();
            foreach (var l in lines)
                foreach (var p in new[] { l.A, l.B })
                {
                    if (!byPoint.ContainsKey(p)) byPoint[p] = new List<Line>();
                    byPoint[p].Add(l);
                }
            var used = new HashSet<int>();
            var shapes = new List<Shape>();
            foreach (var start in lines)
            {
                if (used.Contains(start.Id)) continue;
                var comp = new List<Line>();
                var stack = new Stack<Line>();
                stack.Push(start);
                var seen = new HashSet<int> { start.Id };
                while (stack.Count > 0)
                {
                    var l = stack.Pop();
                    comp.Add(l);
                    foreach (var p in new[] { l.A, l.B })
                        foreach (var m in byPoint[p])
                            if (!seen.Contains(m.Id)) { seen.Add(m.Id); stack.Push(m); }
                }
                foreach (var l in comp) used.Add(l.Id);
                if (comp.Any(l => deg[l.A] != 2 || deg[l.B] != 2)) continue;
                var pointIds = new List<int> { start.A };
                var lineIds = new List<int> { start.Id };
                int cur = start.B;
                var prev = start;
                while (cur != start.A)
                {
                    pointIds.Add(cur);
                    var pv = prev;
                    var nxt = byPoint[cur].Find(m => m.Id != pv.Id);
                    lineIds.Add(nxt.Id);
                    cur = nxt.A == cur ? nxt.B : nxt.A;
                    prev = nxt;
                }
                var poly = pointIds.Select(id => { var p = PointById(doc, id); return new V(p.X, p.Y); }).ToList();
                double area = Math.Abs(PolyArea(poly));
                if (area > EPS) shapes.Add(new Shape { LineIds = lineIds, PointIds = pointIds, Poly = poly, Area = area });
            }
            return shapes;
        }

        public static double PolyArea(List<V> poly)
        {
            double s = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i]; var b = poly[(i + 1) % poly.Count];
                s += a.X * b.Y - b.X * a.Y;
            }
            return s / 2;
        }

        /// <summary>点が多角形の中か: 1（中）、0（線の上）、−1（外）</summary>
        public static int Inside(List<V> poly, double x, double y)
        {
            bool c = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[i]; var b = poly[j];
                bool onX = Math.Min(a.X, b.X) - EPS <= x && x <= Math.Max(a.X, b.X) + EPS;
                bool onY = Math.Min(a.Y, b.Y) - EPS <= y && y <= Math.Max(a.Y, b.Y) + EPS;
                if (onX && onY && Math.Abs((b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X)) < EPS) return 0;
                if ((a.Y > y) != (b.Y > y) && x < ((b.X - a.X) * (y - a.Y)) / (b.Y - a.Y) + a.X) c = !c;
            }
            return c ? 1 : -1;
        }

        // 水平・垂直の辺が、互いの内側で交わるか
        public static bool EdgesCross(List<V> p, List<V> q)
        {
            for (int i = 0; i < p.Count; i++)
            {
                var a1 = p[i]; var a2 = p[(i + 1) % p.Count];
                for (int j = 0; j < q.Count; j++)
                {
                    var b1 = q[j]; var b2 = q[(j + 1) % q.Count];
                    bool ah = Math.Abs(a1.Y - a2.Y) < EPS, bh = Math.Abs(b1.Y - b2.Y) < EPS;
                    if (ah == bh) continue;
                    V h1, h2, v1, v2;
                    if (ah) { h1 = a1; h2 = a2; v1 = b1; v2 = b2; } else { h1 = b1; h2 = b2; v1 = a1; v2 = a2; }
                    double x = v1.X, y = h1.Y;
                    if (Math.Min(h1.X, h2.X) + EPS < x && x < Math.Max(h1.X, h2.X) - EPS &&
                        Math.Min(v1.Y, v2.Y) + EPS < y && y < Math.Max(v1.Y, v2.Y) - EPS) return true;
                }
            }
            return false;
        }

        static List<V> Mids(List<V> poly)
        {
            var r = new List<V>();
            for (int i = 0; i < poly.Count; i++) { var a = poly[i]; var b = poly[(i + 1) % poly.Count]; r.Add(new V((a.X + b.X) / 2, (a.Y + b.Y) / 2)); }
            return r;
        }

        /// <summary>2 つの多角形が、面として重なるか（辺が触れているだけは重ならない）</summary>
        public static bool Overlaps(List<V> p, List<V> q)
        {
            if (EdgesCross(p, q)) return true;
            if (q.Any(v => Inside(p, v.X, v.Y) > 0) || p.Any(v => Inside(q, v.X, v.Y) > 0)) return true;
            return Mids(q).Any(v => Inside(p, v.X, v.Y) > 0) || Mids(p).Any(v => Inside(q, v.X, v.Y) > 0);
        }

        // 図面の丸も、閉じた図形と同じ形で並べる。ほかと重ならない丸は外形になる。
        // Poly は 72 角形で近似したもの（塗る・ほかのワークとの重なりを見るなど）。外形・穴の分け方は丸のまま計る
        public static List<Shape> CircleShapes(Doc doc)
        {
            var output = new List<Shape>();
            foreach (var c in doc.Circles ?? new List<Circle>())
            {
                var p = PointById(doc, c.C);
                double r = c.D / 2;
                if (p == null || !(r > 0)) continue;
                var poly = new List<V>();
                for (int i = 0; i < 72; i++) poly.Add(new V(p.X + r * Math.Cos(i * Math.PI / 36), p.Y + r * Math.Sin(i * Math.PI / 36)));
                output.Add(new Shape { LineIds = new List<int>(), PointIds = new List<int> { c.C }, Poly = poly, Area = Math.PI * r * r, Circle = new CircleGeo { Id = c.Id, X = p.X, Y = p.Y, R = r } });
            }
            return output;
        }
        public static List<Shape> AllShapes(Doc doc) { return ClassifyShapes(ClosedShapes(doc).Concat(CircleShapes(doc)).ToList()); }
        static double Dist(double dx, double dy) { return Math.Sqrt(dx * dx + dy * dy); }
        // 点 (x, y) から線分 ab までの距離
        static double SegDist(V a, V b, double x, double y)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
            double t = len > 0 ? Math.Max(0, Math.Min(1, ((x - a.X) * dx + (y - a.Y) * dy) / len)) : 0;
            return Dist(a.X + t * dx - x, a.Y + t * dy - y);
        }
        static double EdgeDist(List<V> poly, double x, double y)
        {
            double m = double.PositiveInfinity;
            for (int i = 0; i < poly.Count; i++) m = Math.Min(m, SegDist(poly[i], poly[(i + 1) % poly.Count], x, y));
            return m;
        }
        // 図形 s が、外形 b に対してどうか: none（重ならない）、inside（内側に収まる）、cross（はみ出す・辺に触れる）
        static string Relation(Shape b, Shape s)
        {
            if (b.Circle == null && s.Circle == null)
            {
                if (!Overlaps(b.Poly, s.Poly)) return "none";
                return s.Poly.All(v => Inside(b.Poly, v.X, v.Y) > 0) && !EdgesCross(b.Poly, s.Poly) ? "inside" : "cross";
            }
            if (b.Circle != null && s.Circle != null)
            {
                double d = Dist(s.Circle.X - b.Circle.X, s.Circle.Y - b.Circle.Y);
                if (d >= b.Circle.R + s.Circle.R - EPS) return "none";
                return d + s.Circle.R < b.Circle.R - EPS ? "inside" : "cross";
            }
            if (b.Circle != null)
            {
                double x = b.Circle.X, y = b.Circle.Y, r = b.Circle.R;
                if (s.Poly.All(v => Dist(v.X - x, v.Y - y) < r - EPS)) return "inside";
                return Inside(s.Poly, x, y) >= 0 || EdgeDist(s.Poly, x, y) < r - EPS ? "cross" : "none";
            }
            {
                double x = s.Circle.X, y = s.Circle.Y, r = s.Circle.R;
                int ins = Inside(b.Poly, x, y);
                double ed = EdgeDist(b.Poly, x, y);
                if (ins > 0 && ed > r + EPS) return "inside";
                return ins >= 0 || ed < r - EPS ? "cross" : "none";
            }
        }

        // 閉じた図形を、外形・穴・切り欠きに分ける。丸は、外形にならなければ circle（丸穴）
        public static List<Shape> ClassifyShapes(List<Shape> shapes)
        {
            var order = Enumerable.Range(0, shapes.Count).ToList();
            Js.Sort(order, (i, j) => Js.Cmp(shapes[j].Area - shapes[i].Area));
            var bases = new List<int>();
            foreach (int i in order)
            {
                var s = shapes[i];
                int? hb = null;
                string rel = "none";
                foreach (int b in bases) { rel = Relation(shapes[b], s); if (rel != "none") { hb = b; break; } }
                if (hb == null) { s.Role = "outer"; s.Parent = null; bases.Add(i); continue; }
                s.Role = s.Circle != null ? "circle" : rel == "inside" ? "hole" : "notch";
                s.Parent = hb;
            }
            return shapes;
        }

        // ---- 割付: 水平・垂直の辺だけの多角形を長方形で覆う ----
        public static bool RectInside(List<V> poly, R r)
        {
            if (Inside(poly, (r.X0 + r.X1) / 2, (r.Y0 + r.Y1) / 2) < 0) return false;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i]; var b = poly[(i + 1) % poly.Count];
                if (Math.Abs(a.Y - b.Y) < EPS)
                {
                    if (a.Y > r.Y0 + EPS && a.Y < r.Y1 - EPS && Math.Max(a.X, b.X) > r.X0 + EPS && Math.Min(a.X, b.X) < r.X1 - EPS) return false;
                }
                else if (a.X > r.X0 + EPS && a.X < r.X1 - EPS && Math.Max(a.Y, b.Y) > r.Y0 + EPS && Math.Min(a.Y, b.Y) < r.Y1 - EPS)
                {
                    return false;
                }
            }
            return true;
        }

        static List<double> Cuts(IEnumerable<double> vals)
        {
            var list = new List<double>();
            foreach (var v in vals) if (!list.Contains(v)) list.Add(v);
            list.Sort();
            return list;
        }

        // 帯に切って長方形に分ける。axis 'y' なら水平の帯（頂点の Y で切る）、'x' なら垂直の帯
        static List<R> Slabs(List<V> poly, string axis)
        {
            string other = axis == "y" ? "x" : "y";
            var cuts = Cuts(poly.Select(v => Js.R3(v.Get(axis))));
            var rects = new List<R>();
            for (int i = 0; i + 1 < cuts.Count; i++)
            {
                double lo = cuts[i], hi = cuts[i + 1], mid = (lo + hi) / 2;
                var xs = new List<double>();
                for (int j = 0; j < poly.Count; j++)
                {
                    var a = poly[j]; var b = poly[(j + 1) % poly.Count];
                    if (Math.Abs(a.Get(other) - b.Get(other)) < EPS && (a.Get(axis) > mid) != (b.Get(axis) > mid)) xs.Add(a.Get(other));
                }
                Js.Sort(xs, (p, q) => Js.Cmp(p - q));
                for (int k = 0; k + 1 < xs.Count; k += 2)
                    rects.Add(axis == "y" ? new R(xs[k], lo, xs[k + 1], hi) : new R(lo, xs[k], hi, xs[k + 1]));
            }
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < rects.Count && !merged; i++)
                {
                    for (int j = 0; j < rects.Count; j++)
                    {
                        if (i == j) continue;
                        var p = rects[i]; var q = rects[j];
                        bool same = axis == "y"
                            ? Math.Abs(p.X0 - q.X0) < EPS && Math.Abs(p.X1 - q.X1) < EPS && Math.Abs(p.Y1 - q.Y0) < EPS
                            : Math.Abs(p.Y0 - q.Y0) < EPS && Math.Abs(p.Y1 - q.Y1) < EPS && Math.Abs(p.X1 - q.X0) < EPS;
                        if (same)
                        {
                            rects[i] = axis == "y" ? p.With("y1", q.Y1) : p.With("x1", q.X1);
                            rects.RemoveAt(j);
                            merged = true;
                            break;
                        }
                    }
                }
            }
            return rects;
        }

        /// <summary>長方形を、多角形からはみ出さない所まで 4 方向に広げる</summary>
        public static R RectGrow(List<V> poly, R rect)
        {
            var r = rect.Clone();
            var xs = Cuts(poly.Select(p => Js.R3(p.X)));
            var ys = Cuts(poly.Select(p => Js.R3(p.Y)));
            foreach (var (key, cands, dir) in new[] { ("x0", xs, -1), ("x1", xs, 1), ("y0", ys, -1), ("y1", ys, 1) })
            {
                var list = cands.Where(c => dir < 0 ? c < r.Get(key) - EPS : c > r.Get(key) + EPS).ToList();
                if (dir < 0) list.Reverse();
                foreach (var c in list)
                {
                    if (!RectInside(poly, r.With(key, c))) break;
                    r = r.With(key, c);
                }
            }
            return r;
        }

        /// <summary>水平・垂直の辺だけの多角形を、長方形 box で切り取る（Sutherland–Hodgman）。面積がなければ null</summary>
        public static List<V> ClipToRect(List<V> poly, R box)
        {
            var pts = poly.ToList();
            var sides = new (Func<V, bool> isIn, Func<V, V, V> cut)[]
            {
                (v => v.X >= box.X0 - EPS, (a, b) => new V(box.X0, a.Y + (b.Y - a.Y) * (box.X0 - a.X) / (b.X - a.X))),
                (v => v.X <= box.X1 + EPS, (a, b) => new V(box.X1, a.Y + (b.Y - a.Y) * (box.X1 - a.X) / (b.X - a.X))),
                (v => v.Y >= box.Y0 - EPS, (a, b) => new V(a.X + (b.X - a.X) * (box.Y0 - a.Y) / (b.Y - a.Y), box.Y0)),
                (v => v.Y <= box.Y1 + EPS, (a, b) => new V(a.X + (b.X - a.X) * (box.Y1 - a.Y) / (b.Y - a.Y), box.Y1)),
            };
            foreach (var (isIn, cut) in sides)
            {
                var o = new List<V>();
                for (int i = 0; i < pts.Count; i++)
                {
                    var a = pts[(i + pts.Count - 1) % pts.Count]; var b = pts[i];
                    if (isIn(b)) { if (!isIn(a)) o.Add(cut(a, b)); o.Add(b); }
                    else if (isIn(a)) o.Add(cut(a, b));
                }
                pts = o;
                if (pts.Count == 0) return null;
            }
            pts = pts.Select(v => new V(Js.R3(v.X), Js.R3(v.Y))).ToList();
            bool changed = true;
            while (changed && pts.Count >= 3)
            {
                changed = false;
                for (int i = 0; i < pts.Count; i++)
                {
                    var a = pts[(i + pts.Count - 1) % pts.Count]; var b = pts[i]; var c = pts[(i + 1) % pts.Count];
                    bool same = Math.Abs(a.X - b.X) < EPS && Math.Abs(a.Y - b.Y) < EPS;
                    bool line = (Math.Abs(a.X - b.X) < EPS && Math.Abs(b.X - c.X) < EPS) || (Math.Abs(a.Y - b.Y) < EPS && Math.Abs(b.Y - c.Y) < EPS);
                    if (same || line) { pts.RemoveAt(i); changed = true; break; }
                }
            }
            if (pts.Count < 4) return null;
            return Math.Abs(PolyArea(pts)) > EPS ? pts : null;
        }

        public static List<List<V>> ComplementPieces(List<V> poly, R box) { return CutPieces(poly, new List<List<V>>(), box); }

        /// <summary>外形を囲む四角 box の中で抜く所（外形 poly の外か、抜く図形 cutters のどれかの中）を、つながった所ごとの多角形で返す</summary>
        public static List<List<V>> CutPieces(List<V> poly, List<List<V>> cutters, R box)
        {
            var all = new List<List<V>> { poly };
            all.AddRange(cutters);
            var xsAll = new List<double> { box.X0, box.X1 };
            xsAll.AddRange(all.SelectMany(q => q.Select(v => Js.R3(v.X))));
            var ysAll = new List<double> { box.Y0, box.Y1 };
            ysAll.AddRange(all.SelectMany(q => q.Select(v => Js.R3(v.Y))));
            var xs = Cuts(xsAll.Where(x => x >= box.X0 - EPS && x <= box.X1 + EPS));
            var ys = Cuts(ysAll.Where(y => y >= box.Y0 - EPS && y <= box.Y1 + EPS));
            int nx = xs.Count - 1, ny = ys.Count - 1;
            Func<double, double, bool> cut = (cx, cy) => Inside(poly, cx, cy) < 0 || cutters.Any(q => Inside(q, cx, cy) > 0);
            var memo = new Dictionary<long, bool>();
            Func<int, int, bool> outF = (i, j) =>
            {
                if (i < 0 || j < 0 || i >= nx || j >= ny) return false;
                long key = (long)i * 100000 + j;
                bool v;
                if (!memo.TryGetValue(key, out v)) { v = cut((xs[i] + xs[i + 1]) / 2, (ys[j] + ys[j + 1]) / 2); memo[key] = v; }
                return v;
            };
            Func<int, int, long> K = (i, j) => (long)(i + 1) * 100000 + (j + 1);  // −1 も入るようにずらす
            var comp = new Dictionary<long, int>();
            var compOrder = new List<long>();
            int n = 0;
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                {
                    if (!outF(i, j) || comp.ContainsKey(K(i, j))) continue;
                    var stack = new Stack<(int, int)>();
                    stack.Push((i, j));
                    comp[K(i, j)] = n; compOrder.Add(K(i, j));
                    while (stack.Count > 0)
                    {
                        var (a, b) = stack.Pop();
                        foreach (var (c, d) in new[] { (a + 1, b), (a - 1, b), (a, b + 1), (a, b - 1) })
                            if (outF(c, d) && !comp.ContainsKey(K(c, d))) { comp[K(c, d)] = n; compOrder.Add(K(c, d)); stack.Push((c, d)); }
                    }
                    n++;
                }
            var pieces = new List<List<V>>();
            for (int cc = 0; cc < n; cc++)
            {
                int c0 = cc;
                Func<int, int, bool> inC = (i, j) => comp.TryGetValue(K(i, j), out int v) && v == c0;
                var next = new Dictionary<long, long>();
                long? start = null;
                Action<long, long> set = (from, to) => { if (start == null) start = from; next[from] = to; };
                foreach (var key in compOrder)
                {
                    if (comp[key] != c0) continue;
                    int i = (int)(key / 100000) - 1, j = (int)(key % 100000) - 1;
                    if (!inC(i, j - 1)) set(K(i, j), K(i + 1, j));
                    if (!inC(i + 1, j)) set(K(i + 1, j), K(i + 1, j + 1));
                    if (!inC(i, j + 1)) set(K(i + 1, j + 1), K(i, j + 1));
                    if (!inC(i - 1, j)) set(K(i, j + 1), K(i, j));
                }
                if (start == null) continue;
                var loop = new List<V>();
                long cur = start.Value;
                int guard = 0;
                do
                {
                    int i = (int)(cur / 100000) - 1, j = (int)(cur % 100000) - 1;
                    loop.Add(new V(xs[i], ys[j]));
                    long nn;
                    if (!next.TryGetValue(cur, out nn)) break;
                    cur = nn;
                } while (cur != start.Value && ++guard < 10000);
                var cleaned = ClipToRect(loop, box);
                if (cleaned != null) pieces.Add(cleaned);
            }
            return pieces;
        }

        /// <summary>多角形を、重ならずに接する長方形に分ける。水平の帯と垂直の帯の 2 通りを返す</summary>
        public static List<List<R>> RectPartitions(List<V> poly)
        {
            return new List<List<R>> { Slabs(poly, "y"), Slabs(poly, "x") };
        }

        /// <summary>頂点の列を整える: 長さ 0 の辺を消し、同じ向きに続く辺を 1 本にする</summary>
        public static List<SV> CleanPolyline(List<SV> verts)
        {
            var v = verts.Where((p, i) => i == 0 || Math.Abs(p.X - verts[i - 1].X) > EPS || Math.Abs(p.Y - verts[i - 1].Y) > EPS).ToList();
            bool changed = true;
            while (changed && v.Count > 2)
            {
                changed = false;
                for (int i = 1; i < v.Count - 1; i++)
                {
                    var a = v[i - 1]; var b = v[i]; var c = v[i + 1];
                    bool h1 = Math.Abs(a.Y - b.Y) < EPS, h2 = Math.Abs(b.Y - c.Y) < EPS;
                    bool v1 = Math.Abs(a.X - b.X) < EPS, v2 = Math.Abs(b.X - c.X) < EPS;
                    if ((h1 && h2) || (v1 && v2)) { v.RemoveAt(i); changed = true; break; }
                }
            }
            return v.Count >= 2 ? v : null;
        }
    }

    /// <summary>なぞった線の頂点（既にある点につなぐときは Id、描き始めに戻って閉じるときは Close）。</summary>
    public class SV
    {
        public double X, Y;
        public int? Id;
        public bool Close;
        public SV(double x, double y) { X = x; Y = y; }
    }
}
