// 図面の編集（画面に依らない部分）: 元に戻す、板の原点からの配置、なぞった線、寸法、割付、コード（app.js の移植）。
// 座標はすべて mm。内部では板の左下を (0, 0)、Y を上向きに持ち、表示は外形ごとのワーク原点（外形の左上、Y は下へ正）からの値にする。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SetPressCam
{
    public class HitRef
    {
        public int Work, I;
        public HitRef(int work, int i) { Work = work; I = i; }
    }

    public class DimRow
    {
        public string Label, Axis, Kind, Prefer;
        public int A, B;
        public List<Sel> Refs;
        public int? SheetK;
        public int? CircleId;  // Kind "dia"（丸の直径）のときの丸
        public bool Signed, Neg;
    }

    /// <summary>なぞった跡を丸にしたもの（中心と直径）。</summary>
    public class CircleRes
    {
        public double X, Y, D;
    }

    public class DimOptions
    {
        public string Title = "", Note;
        public List<DimRow> Rows = new List<DimRow>();
        public bool IsOrigin, IsDim;
    }

    public class CodeLine
    {
        public string Text, Key;
        public bool Comment;
        /// <summary>F23 のピッチ・列数の行: 書き換えられる欄（無ければ null）。</summary>
        public CodeEdit Edit;
    }

    /// <summary>コードの行の中の、書き換えられる 2 つの値（F23 のピッチ X Y、列数 X Y）。</summary>
    public class CodeEdit
    {
        public int Work;
        public string[] Fields, Vals, Prefix;
        public string Note;
    }

    public partial class Editor
    {
        public const int UNDO_MAX = 2000;
        public const int UNDO_CHARS = 2000000;

        public Doc Doc;
        public Analysis Info;
        public List<Shape> Shapes = new List<Shape>();
        public readonly List<string> UndoStack = new List<string>();
        public List<Sel> Sel = new List<Sel>();
        public bool Dirty;
        public string Mode = "draw";
        public int? LayoutWork;
        public HitRef LayoutHit;
        public double LastMj;  // 新しい図面に使うミクロジョイント（最後に入れた値）

        /// <summary>図面が変わったとき（画面を描き直す）。</summary>
        public event Action Refreshed;
        /// <summary>自動保存するとき。</summary>
        public event Action SaveCurrent;
        /// <summary>ステータスバーに一時的に出す知らせ。</summary>
        public event Action<string> Flash;

        public Editor(double lastMj)
        {
            LastMj = lastMj;
            Doc = Model.NewDoc(lastMj);
            Info = Geo.Analyze(Doc);
        }

        public void FlashMsg(string msg) { Flash?.Invoke(msg); }

        public int NewId() { return Doc.NextId++; }

        public void Snapshot()
        {
            UndoStack.Add(Model.Serialize(Doc));
            long total = UndoStack.Sum(t => (long)t.Length);
            while (UndoStack.Count > 1 && (UndoStack.Count > UNDO_MAX || total > UNDO_CHARS))
            {
                total -= UndoStack[0].Length;
                UndoStack.RemoveAt(0);
            }
        }

        public string PopUndo()
        {
            var s = UndoStack[UndoStack.Count - 1];
            UndoStack.RemoveAt(UndoStack.Count - 1);
            return s;
        }

        public void Undo()
        {
            if (UndoStack.Count == 0) return;
            Doc = Model.Deserialize(PopUndo());
            DropMissingSelection();
            Changed();
        }

        public void Changed()
        {
            ApplyPlacements();
            Dirty = true;
            SaveCurrent?.Invoke();
            Refresh();
        }

        public void Refresh()
        {
            if (Doc.Layouts == null) Doc.Layouts = new List<Layout>();
            Info = Geo.Analyze(Doc);
            Shapes = Info.Shapes;
            DropMissingSelection();
            Refreshed?.Invoke();
        }

        /// <summary>新しい図面にする（読み込んだ図面、新規）。</summary>
        public void Load(Doc doc)
        {
            Doc = doc;
            Sel = new List<Sel>();
            UndoStack.Clear();
            Dirty = false;
            LayoutWork = null;
            LayoutHit = null;
            SaveCurrent?.Invoke();
            Refresh();
        }

        public static double Round(double v) { return Js.R3(v); }

        public Point P(int id) { return Geo.PointById(Doc, id); }
        public Line L(int id) { return Geo.LineById(Doc, id); }
        public Dim D(int id) { return Doc.Dims.Find(d => d.Id == id); }
        /// <summary>丸の一覧（無ければ空）。</summary>
        public List<Circle> Circles { get { return Doc.Circles ?? new List<Circle>(); } }
        /// <summary>中心が pid の丸。</summary>
        public Circle CircleAt(int pid) { return Doc.Circles?.Find(c => c.C == pid); }
        public int? Owner(int pid) { return Info.OwnerOf(pid); }

        // 板の原点からの寸法（kind 'sheet'）に合わせて、外形を穴・切り欠き・つながった線ごと平行移動する。
        public void ApplyPlacements()
        {
            var sd = Doc.Dims.Where(d => d.Kind == "sheet").ToList();
            if (sd.Count == 0) return;
            var a = Geo.Analyze(Doc);
            var shift = new Dictionary<int, V>();
            foreach (var d in sd)
            {
                var k = a.OwnerOf(d.B);
                if (k == null) continue;
                var lay = Doc.Layouts.Find(q => a.OwnerOf(q.Work) == k);
                var off = LayOffset(lay);
                var o = new V(a.Outers[k.Value].Origin.X + off.X, a.Outers[k.Value].Origin.Y + off.Y);
                V sh;
                if (!shift.TryGetValue(k.Value, out sh)) sh = new V(0, 0);
                if (d.Axis == "x") sh.X = d.Value - o.X; else sh.Y = (Doc.Sheet.H - d.Value) - o.Y;
                shift[k.Value] = sh;
            }
            foreach (var p in Doc.Points)
            {
                var k = a.OwnerOf(p.Id);
                V sh;
                if (k != null && shift.TryGetValue(k.Value, out sh)) { p.X = Round(p.X + sh.X); p.Y = Round(p.Y + sh.Y); }
            }
        }

        // ワーク原点: 割付の前は外形を囲む四角の左上。割付のあとは、抜く範囲を囲む四角の左上。
        public static V LayOffset(Layout lay)
        {
            if (lay == null || lay.Org == null || lay.Hits.Count == 0) return new V(0, 0);
            double x0 = lay.Hits.Min(h => h.X0), y1 = lay.Hits.Max(h => h.Y1);
            return new V(Round(Math.Min(0, x0 - lay.Org.X)), Round(Math.Max(0, y1 - lay.Org.Y)));
        }

        public V WorkOrigin(int k)
        {
            var o = Info.Outers[k].Origin;
            var off = LayOffset(LayoutOf(k));
            return new V(Round(o.X + off.X), Round(o.Y + off.Y));
        }

        /// <summary>外形の原点の、板の原点からの位置（右へ X+、下へ Y+）</summary>
        public double SheetPos(int k, string axis)
        {
            var o = WorkOrigin(k);
            return Round(axis == "x" ? o.X : Doc.Sheet.H - o.Y);
        }

        public Dim SheetDimOf(int k, string axis)
        {
            return Doc.Dims.Find(d => d.Kind == "sheet" && d.Axis == axis && Owner(d.B) == k);
        }

        public V SheetTopLeft() { return new V(0, Doc.Sheet.H); }
        public V OriginAt(double x, double y) { var k = Info.OuterAt(x, y); return k == null ? SheetTopLeft() : WorkOrigin(k.Value); }
        public static V Rel(V o, double x, double y) { return new V(Round(x - o.X), Round(o.Y - y)); }

        bool ElemExists(Sel s)
        {
            if (s.T == "origin") return s.Id < Info.Outers.Count;
            return s.T == "point" ? P(s.Id) != null : s.T == "line" ? L(s.Id) != null : D(s.Id) != null;
        }

        public void DropMissingSelection() { Sel = Sel.Where(ElemExists).ToList(); }
        public bool IsSel(string t, int id) { return Sel.Any(s => s.T == t && s.Id == id); }

        // ---- グリッドに合わせる（板の左上を基準にする） ----
        public double SnapAxis(double v, string axis, double step)
        {
            double b = axis == "x" ? 0 : Doc.Sheet.H;
            return Round(b + Js.MathRound((v - b) / step) * step);
        }

        // ---- なぞった線を図形にする ----
        public const double SNAP_PX = 18;

        public Point NearestPoint(double x, double y, double maxPx, double scale, HashSet<int> exclude = null)
        {
            Point best = null;
            double bd = maxPx / scale;
            foreach (var p in Doc.Points)
            {
                if (exclude != null && exclude.Contains(p.Id)) continue;
                double d = Geo.Hypot(p.X - x, p.Y - y);
                if (d <= bd) { best = p; bd = d; }
            }
            return best;
        }

        /// <summary>なぞった跡が丸か: 書き始めの近くまで中心のまわりを一周していて、縦横の折れ線より円のほうがよく合えば丸にする。
        /// 中心はグリッドに合わせ、直径はグリッドの間隔に丸める。丸の金型の直径に近ければその直径にする（app.js の circleResult）。</summary>
        public CircleRes CircleResult(List<V> raw, double scale, double step)
        {
            if (raw.Count < 12) return null;
            double minX = raw.Min(p => p.X), maxX = raw.Max(p => p.X), minY = raw.Min(p => p.Y), maxY = raw.Max(p => p.Y);
            double w = maxX - minX, h = maxY - minY;
            if (Math.Min(w, h) * scale < 16 || Math.Max(w, h) > Math.Min(w, h) * 1.35) return null;
            double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2, R = (w + h) / 4;
            var first = raw[0]; var last = raw[raw.Count - 1];
            if (Geo.Hypot(last.X - first.X, last.Y - first.Y) > Math.Max(SNAP_PX / scale, R * 0.6)) return null;
            // 中心のまわりを回った角度
            double sweep = 0;
            for (int i = 1; i < raw.Count; i++)
            {
                double a = Math.Atan2(raw[i].Y - cy, raw[i].X - cx) - Math.Atan2(raw[i - 1].Y - cy, raw[i - 1].X - cx);
                if (a > Math.PI) a -= 2 * Math.PI; else if (a < -Math.PI) a += 2 * Math.PI;
                sweep += a;
            }
            if (Math.Abs(sweep) < 2 * Math.PI * 0.8) return null;
            // 円からのずれと、縦横の折れ線（閉じたもの）からのずれを比べる
            double ec = raw.Sum(p => Math.Abs(Geo.Hypot(p.X - cx, p.Y - cy) - R)) / raw.Count;
            var verts = Geo.StrokeToPolyline(raw, 14 / scale);
            if (verts != null)
            {
                Func<V, V, V, double> segDist = (p, a, b) =>
                {
                    double vx = b.X - a.X, vy = b.Y - a.Y, l2 = vx * vx + vy * vy;
                    double t = l2 != 0 ? Math.Max(0, Math.Min(1, ((p.X - a.X) * vx + (p.Y - a.Y) * vy) / l2)) : 0;
                    return Geo.Hypot(p.X - a.X - t * vx, p.Y - a.Y - t * vy);
                };
                double ep = raw.Sum(p => Enumerable.Range(0, verts.Count).Min(i => segDist(p, verts[i], verts[(i + 1) % verts.Count]))) / raw.Count;
                if (!(ec < ep * 0.85)) return null;
            }
            double d = Round(Math.Max(step, Js.MathRound(2 * R / step) * step));
            // 丸の金型の直径に近ければ（25% 以内）、いちばん近い金型の直径にする（そのまま単発で抜ける）
            double? best = null;
            foreach (var t in Doc.Tools)
            {
                if (t.Shape != "round" || (t.D ?? 0) == 0 || Math.Abs(t.D.Value - 2 * R) > 2 * R * 0.25) continue;
                if (best == null || Math.Abs(t.D.Value - 2 * R) < Math.Abs(best.Value - 2 * R)) best = t.D.Value;
            }
            if (best != null) d = best.Value;
            return new CircleRes { X = SnapAxis(cx, "x", step), Y = SnapAxis(cy, "y", step), D = d };
        }

        /// <summary>なぞった跡 → 頂点の列。角はグリッドに合わせ、両端が既にある点の近くならその点につなぐ（丸の中心にはつながない）。</summary>
        public List<SV> StrokeResult(List<V> raw, double scale, double step)
        {
            double T = 14 / scale;
            var pl = Geo.StrokeToPolyline(raw, T);
            if (pl == null) return null;
            var verts = pl.Select(v => new SV(SnapAxis(v.X, "x", step), SnapAxis(v.Y, "y", step))).ToList();
            var dirs = new List<string>();
            for (int i = 1; i < verts.Count; i++) dirs.Add(Math.Abs(verts[i].Y - verts[i - 1].Y) < Geo.EPS ? "h" : "v");
            int n = verts.Count;
            var centers = new HashSet<int>(Circles.Select(c => c.C));
            var ps = NearestPoint(raw[0].X, raw[0].Y, SNAP_PX, scale, centers);
            if (ps != null)
            {
                verts[0] = new SV(ps.X, ps.Y) { Id = ps.Id };
                if (dirs[0] == "h") verts[1].Y = ps.Y; else verts[1].X = ps.X;
            }
            var last = raw[raw.Count - 1];
            var pe0 = NearestPoint(last.X, last.Y, SNAP_PX, scale, centers);
            SV pe = pe0 == null ? null : new SV(pe0.X, pe0.Y) { Id = pe0.Id };
            bool close = false;
            if (n >= 4 && Geo.Hypot(last.X - raw[0].X, last.Y - raw[0].Y) <= SNAP_PX / scale)
            {
                pe = verts[0]; close = true;
            }
            if (pe != null && !(ps != null && pe.Id == ps.Id && !close))
            {
                int k = n - 2;
                string dk = dirs[n - 2];
                bool okSingle = k > 0 || verts[0].Id == null ||
                    (dk == "h" ? Math.Abs(verts[0].Y - pe.Y) < Geo.EPS : Math.Abs(verts[0].X - pe.X) < Geo.EPS);
                if (okSingle)
                {
                    if (dk == "h") verts[k].Y = pe.Y; else verts[k].X = pe.X;
                    verts[n - 1] = new SV(pe.X, pe.Y) { Id = close ? null : pe.Id, Close = close };
                }
            }
            var merged = new List<SV>();
            foreach (var v in verts)
            {
                var prev = merged.Count > 0 ? merged[merged.Count - 1] : null;
                if (prev != null && Math.Abs(prev.X - v.X) < Geo.EPS && Math.Abs(prev.Y - v.Y) < Geo.EPS)
                {
                    if (v.Id != null) prev.Id = v.Id;
                    if (v.Close) prev.Close = true;
                    continue;
                }
                merged.Add(new SV(v.X, v.Y) { Id = v.Id, Close = v.Close });
            }
            return Geo.CleanPolyline(merged);
        }

        public bool CommitStroke(List<V> raw, double scale, double step)
        {
            var cr = CircleResult(raw, scale, step);
            if (cr != null)
            {
                Snapshot();
                int cid = NewId();
                Doc.Points.Add(new Point { Id = cid, X = cr.X, Y = cr.Y });
                if (Doc.Circles == null) Doc.Circles = new List<Circle>();
                Doc.Circles.Add(new Circle { Id = NewId(), C = cid, D = cr.D });
                Sel = new List<Sel>();
                Changed();
                return true;
            }
            var res = StrokeResult(raw, scale, step);
            if (res == null) return false;
            Snapshot();
            var ids = res.Select(v =>
            {
                if (v.Close) return (int?)null;
                if (v.Id != null) return v.Id;
                int id = NewId();
                Doc.Points.Add(new Point { Id = id, X = v.X, Y = v.Y });
                return id;
            }).ToList();
            if (res[res.Count - 1].Close) ids[ids.Count - 1] = ids[0];
            int added = 0;
            for (int i = 1; i < ids.Count; i++)
            {
                if (ids[i] == ids[i - 1]) continue;
                var a = P(ids[i - 1].Value); var b = P(ids[i].Value);
                string dir = Math.Abs(a.Y - b.Y) < Geo.EPS ? "h" : "v";
                Doc.Lines.Add(new Line { Id = NewId(), A = ids[i - 1].Value, B = ids[i].Value, Dir = dir });
                added++;
            }
            if (added == 0) { PopUndo(); return false; }
            Sel = new List<Sel>();
            Changed();
            return true;
        }

        // ---- 選ぶ ----
        public void TapSelectMany(Sel hit)
        {
            if (hit == null) Sel = new List<Sel>();
            else if (IsSel(hit.T, hit.Id)) Sel = Sel.Where(s => !(s.T == hit.T && s.Id == hit.Id)).ToList();
            else Sel = Sel.Concat(new[] { hit }).ToList();
        }

        // 四角の枠で選ぶ（「移動」）: 枠にすっぽり入るものを、いまの選択に足す。
        // 外形がまるごと入れば、その外形の原点を選ぶ（穴・切り欠き・割付・板の原点からの寸法ごと動く）。
        // ほかは、両端が枠に入る線を選ぶ
        public void BoxSelect(double x0, double y0, double x1, double y1)
        {
            Func<Point, bool> inside = p => p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1;
            var add = new List<Sel>();
            var whole = new HashSet<int>();
            for (int k = 0; k < Info.Outers.Count; k++)
            {
                var pts = Doc.Points.Where(p => Owner(p.Id) == k).ToList();
                if (pts.Count > 0 && pts.All(inside)) { whole.Add(k); add.Add(new Sel("origin", k)); }
            }
            foreach (var l in Doc.Lines)
            {
                int? ka = Owner(l.A), kb = Owner(l.B);
                if (ka != null && kb != null && whole.Contains(ka.Value) && whole.Contains(kb.Value)) continue;
                if (inside(P(l.A)) && inside(P(l.B))) add.Add(new Sel("line", l.Id));
            }
            foreach (var c in Circles)
            {
                var kc = Owner(c.C);
                if (kc != null && whole.Contains(kc.Value)) continue;
                var p = P(c.C); double r = c.D / 2;
                if (inside(new Point { X = p.X - r, Y = p.Y - r }) && inside(new Point { X = p.X + r, Y = p.Y + r })) add.Add(new Sel("point", c.C));
            }
            Sel = Sel.Concat(add.Where(q => !IsSel(q.T, q.Id))).ToList();
        }

        public void TapSelect(Sel hit)
        {
            if (hit == null) Sel = new List<Sel>();
            else if (IsSel(hit.T, hit.Id)) Sel = Sel.Where(s => !(s.T == hit.T && s.Id == hit.Id)).ToList();
            else if (hit.T == "dim" || hit.T == "origin" || Sel.Any(s => s.T == "dim" || s.T == "origin") || Sel.Count >= 2) Sel = new List<Sel> { hit };
            else Sel = Sel.Concat(new[] { hit }).ToList();
        }

        // ---- 寸法を入れる欄 ----
        static string PName(int id) { return "P" + id; }
        static string LName(int id) { return "L" + id; }
        static string DirName(Line l) { return l.Dir == "h" ? "水平" : "垂直"; }
        static string AxisName(string a) { return a == "x" ? "X" : "Y"; }
        static string RefName(Sel r) { return r.T == "point" ? PName(r.Id) : LName(r.Id); }
        static string RefName(Ref r) { return r.T == "point" ? PName(r.Id) : LName(r.Id); }
        int RepPoint(Sel r) { return r.T == "point" ? r.Id : L(r.Id).A; }

        static string DimKey(string axis, IEnumerable<(string t, int id)> refs, string kind)
        {
            var list = refs.ToList();
            if (kind == "o") return axis + ":o:" + list[1].t + list[1].id;
            var parts = list.Select(r => r.t + r.id).ToList();
            parts.Sort(string.CompareOrdinal);
            return axis + ":" + string.Join(",", parts);
        }

        public Dim FindDim(string axis, List<Sel> refs, string kind)
        {
            string k = DimKey(axis, refs.Select(r => (r.T, r.Id)), kind);
            return Doc.Dims.Find(d => DimKey(d.Axis, d.Refs.Select(r => (r.T, r.Id)), d.Kind) == k);
        }

        /// <summary>選んだものから、入れられる寸法を作る（app.js の dimOptions）</summary>
        public DimOptions GetDimOptions()
        {
            Func<bool, string> fixedTxt = ok => ok ? "確定" : "未確定";
            Func<string, int, int, string> lenPrefer = (axis, a, b) =>
                axis == "x" ? (P(a).X <= P(b).X ? "b" : "a") : (P(a).Y >= P(b).Y ? "b" : "a");
            Func<int, List<DimRow>> sheetRows = k => new[] { "x", "y" }.Select(axis =>
            {
                int rf = Info.Outers[k].RefX;
                return new DimRow
                {
                    Label = "板の原点から " + AxisName(axis), Axis = axis, A = rf, B = rf, Kind = "sheet",
                    Refs = new List<Sel> { new Sel("point", rf) }, SheetK = k, Signed = true,
                };
            }).ToList();
            if (Sel.Count == 1)
            {
                var s = Sel[0];
                if (s.T == "origin")
                    return new DimOptions
                    {
                        Title = "S" + (s.Id + 1) + " の原点（外形の左上）", Rows = sheetRows(s.Id), IsOrigin = true,
                        Note = "板の原点（板の左上）から、右へ X、下へ Y の距離を入れると、外形ごと動きます。",
                    };
                if (s.T == "dim" && D(s.Id).Kind == "sheet")
                {
                    var d = D(s.Id);
                    var k = Owner(d.B);
                    if (k == null) return new DimOptions { Title = "寸法 D" + d.Id, Note = "外形が閉じていないので、この寸法は効いていません。", IsDim = true };
                    return new DimOptions { Title = "寸法 D" + d.Id + "（S" + (k + 1) + " の原点）", Rows = sheetRows(k.Value).Where(r => r.Axis == d.Axis).ToList(), IsDim = true };
                }
                if (s.T == "line")
                {
                    var l = L(s.Id);
                    string along = l.Dir == "h" ? "x" : "y";
                    return new DimOptions
                    {
                        Title = "線 " + LName(l.Id) + "（" + DirName(l) + "・" + fixedTxt(Info.FixedLine(l)) + "）",
                        Rows = new List<DimRow> { new DimRow { Label = "長さ", Axis = along, A = l.A, B = l.B, Kind = "len", Refs = new List<Sel> { s.Clone() }, Prefer = lenPrefer(along, l.A, l.B) } },
                    };
                }
                if (s.T == "point" && CircleAt(s.Id) != null)
                {
                    var c = CircleAt(s.Id);
                    return new DimOptions
                    {
                        Title = "丸 φ" + Fmt(c.D) + "（中心 " + PName(s.Id) + "・" + fixedTxt(Info.FixedPoint(s.Id)) + "）",
                        Rows = new List<DimRow> { new DimRow { Label = "直径 φ", Kind = "dia", CircleId = c.Id, Axis = "x", A = s.Id, B = s.Id, Refs = new List<Sel>() } },
                        Note = "直径を入れて Enter か「決定」で決まります。もう 1 つ点か線を選ぶと、中心までの距離を入れられます。",
                    };
                }
                if (s.T == "point")
                    return new DimOptions
                    {
                        Title = "点 " + PName(s.Id) + "（" + fixedTxt(Info.FixedPoint(s.Id)) + "）",
                        Note = "もう 1 つ点か線を選ぶと、その間の距離を入れられます。",
                    };
                if (s.T == "dim")
                {
                    var d = D(s.Id);
                    string names = d.Kind == "o" ? "原点と " + RefName(d.Refs[1]) : string.Join(" と ", d.Refs.Select(RefName));
                    return new DimOptions
                    {
                        Title = "寸法 D" + d.Id,
                        Rows = new List<DimRow> { new DimRow
                        {
                            Label = names + " の " + AxisName(d.Axis), Axis = d.Axis, A = d.A, B = d.B, Kind = d.Kind,
                            Refs = d.Refs.Select(r => new Sel(r.T, r.Id)).ToList(),
                            Signed = d.Kind == "o", Neg = d.Kind == "o" && d.Axis == "y",
                            Prefer = d.Kind == "len" ? lenPrefer(d.Axis, d.A, d.B) : "b",
                        } },
                        IsDim = true,
                    };
                }
            }
            if (Sel.Count == 2)
            {
                var f = Sel[0]; var g = Sel[1];
                if (f.T == "origin" || g.T == "origin") return new DimOptions();
                const string prefer = "b";  // 2 つ目に選んだ側が動く
                if (f.T == "point" && g.T == "point")
                    return new DimOptions
                    {
                        Title = "点 " + PName(f.Id) + " と 点 " + PName(g.Id),
                        Rows = new[] { "x", "y" }.Select(axis => new DimRow
                        {
                            Label = AxisName(axis) + " 方向の距離", Axis = axis, A = f.Id, B = g.Id, Kind = "pp",
                            Refs = new List<Sel> { f.Clone(), g.Clone() }, Prefer = prefer,
                        }).ToList(),
                    };
                if (f.T == "line" && g.T == "line")
                {
                    var l1 = L(f.Id); var l2 = L(g.Id);
                    if (l1.Dir != l2.Dir)
                        return new DimOptions { Title = "線 " + LName(l1.Id) + " と 線 " + LName(l2.Id), Note = "直角に交わる線どうしの距離は決められません。平行な線を 2 本選んでください。" };
                    string axis = l1.Dir == "h" ? "y" : "x";
                    return new DimOptions
                    {
                        Title = "線 " + LName(l1.Id) + " と 線 " + LName(l2.Id) + "（平行）",
                        Rows = new List<DimRow> { new DimRow { Label = "線の間の距離", Axis = axis, A = l1.A, B = l2.A, Kind = "ll", Refs = new List<Sel> { f.Clone(), g.Clone() }, Prefer = prefer } },
                    };
                }
                if (f.T == "dim" || g.T == "dim") return new DimOptions();
                var line = f.T == "line" ? L(f.Id) : L(g.Id);
                string ax = line.Dir == "h" ? "y" : "x";
                return new DimOptions
                {
                    Title = RefName(f) + " と " + RefName(g),
                    Rows = new List<DimRow> { new DimRow { Label = "点と線の距離", Axis = ax, A = RepPoint(f), B = RepPoint(g), Kind = "pl", Refs = new List<Sel> { f.Clone(), g.Clone() }, Prefer = prefer } },
                };
            }
            return null;
        }

        /// <summary>欄に出す、いまの値。</summary>
        public double RowValue(DimRow r)
        {
            if (r.Kind == "dia") { var c = Circles.Find(q => q.Id == r.CircleId); return c != null ? c.D : 0; }
            if (r.Kind == "sheet") return SheetPos(r.SheetK.Value, r.Axis);
            double cur = Get(P(r.B), r.Axis) - Get(P(r.A), r.Axis);
            return Round(r.Signed ? (r.Neg ? -cur : cur) : Math.Abs(cur));
        }

        public bool RowHasDim(DimRow r)
        {
            if (r.Kind == "dia") return false;
            if (r.Kind == "sheet") return SheetDimOf(r.SheetK.Value, r.Axis) != null;
            return FindDim(r.Axis, r.Refs, r.Kind) != null;
        }

        static double Get(Point p, string axis) { return axis == "x" ? p.X : p.Y; }

        /// <summary>欄の値を寸法にする（すでにあれば値を変える）。</summary>
        public void CommitDim(int i, double v)
        {
            var opt = GetDimOptions();
            var r = opt != null && i < opt.Rows.Count ? opt.Rows[i] : null;
            if (r == null) return;
            if (double.IsNaN(v) || double.IsInfinity(v)) { Refreshed?.Invoke(); return; }
            v = Round(v);
            if (!r.Signed && v < 0) v = -v;
            if (r.Kind == "len" && v <= 0) { FlashMsg("長さは 0 より大きくしてください"); Refreshed?.Invoke(); return; }
            if (r.Kind == "dia")
            {
                // 丸の直径: 寸法ではなく、丸の大きさそのものを変える（選んだままにする）
                var c = Circles.Find(q => q.Id == r.CircleId);
                if (c == null) return;
                if (v <= 0) { FlashMsg("直径は 0 より大きくしてください"); Refreshed?.Invoke(); return; }
                if (v == c.D) { Refreshed?.Invoke(); return; }
                Snapshot();
                c.D = v;
                Changed();
                return;
            }
            if (r.Kind == "sheet")
            {
                Snapshot();
                var ex = SheetDimOf(r.SheetK.Value, r.Axis);
                if (ex != null) ex.Value = v;
                else Doc.Dims.Add(new Dim { Id = NewId(), Axis = r.Axis, A = r.A, B = r.B, Value = v, Kind = "sheet", Refs = r.Refs.Select(q => new Ref { T = q.T, Id = q.Id }).ToList() });
                if (!opt.IsOrigin) Sel = new List<Sel>();
                Changed();
                return;
            }
            double cur = Get(P(r.B), r.Axis) - Get(P(r.A), r.Axis);
            var existing = FindDim(r.Axis, r.Refs, r.Kind);
            double sign = r.Signed ? (r.Neg ? -1 : 1) : (existing != null ? (Math.Sign(existing.Value) == 0 ? 1 : Math.Sign(existing.Value)) : (Math.Sign(cur) == 0 ? 1 : Math.Sign(cur)));
            double value = Round(sign * v);
            Snapshot();
            bool flip = existing != null && existing.A != r.A;
            string prefer = flip ? (r.Prefer == "a" ? "b" : "a") : r.Prefer;
            string err = existing != null ? Geo.SetDimValue(Doc, existing.Id, value, prefer)
                : Geo.AddDim(Doc, new Dim { Id = NewId(), Axis = r.Axis, A = r.A, B = r.B, Value = value, Kind = r.Kind, Refs = r.Refs.Select(q => new Ref { T = q.T, Id = q.Id }).ToList() }, r.Prefer ?? "b");
            if (err != null)
            {
                Doc = Model.Deserialize(PopUndo());
                FlashMsg(err);
                Refresh();
                return;
            }
            Sel = new List<Sel>();
            Changed();
        }

        /// <summary>選んだものを消す: 線（とその端の点）、点（につながる線）、寸法</summary>
        public void DeleteSelected()
        {
            if (Sel.Count == 0) return;
            Snapshot();
            var lineIds = new HashSet<int>(Sel.Where(s => s.T == "line").Select(s => s.Id));
            foreach (var s in Sel)
                if (s.T == "point")
                    foreach (var l in Doc.Lines) if (l.A == s.Id || l.B == s.Id) lineIds.Add(l.Id);
            var dimIds = new HashSet<int>(Sel.Where(s => s.T == "dim").Select(s => s.Id));
            foreach (var s in Sel)
            {
                if (s.T != "origin") continue;
                foreach (var axis in new[] { "x", "y" }) { var d = SheetDimOf(s.Id, axis); if (d != null) dimIds.Add(d.Id); }
            }
            Doc.Dims = Doc.Dims.Where(d => !dimIds.Contains(d.Id)).ToList();
            // 丸は、中心の点を選んで消す（中心の点と、中心に入れた寸法も消す）
            var gone = new HashSet<int>(Sel.Where(s => s.T == "point" && CircleAt(s.Id) != null).Select(s => s.Id));
            if (gone.Count > 0)
            {
                Doc.Circles = Circles.Where(c => !gone.Contains(c.C)).ToList();
                if (Doc.Circles.Count == 0) Doc.Circles = null;
                Doc.Points = Doc.Points.Where(p => !gone.Contains(p.Id)).ToList();
                Doc.Dims = Doc.Dims.Where(d => !gone.Contains(d.A) && !gone.Contains(d.B) && !d.Refs.Any(r => r.T == "point" && gone.Contains(r.Id))).ToList();
            }
            if (lineIds.Count > 0) Geo.DeleteLines(Doc, lineIds);
            Sel = new List<Sel>();
            Changed();
        }

        /// <summary>バージョンのすぐ左: 点を 1 つ選んだら板の原点（左上）からの絶対座標、線を 1 本選んだら長さ</summary>
        public string SelInfo()
        {
            if (Sel.Count != 1) return "";
            var s = Sel[0];
            if (s.T == "point" && P(s.Id) != null)
            {
                var r = Rel(SheetTopLeft(), P(s.Id).X, P(s.Id).Y);
                var c = CircleAt(s.Id);
                return (c != null ? "丸 φ" + Fmt(c.D) + "  中心 " : "位置  ") + "X " + Fmt(r.X) + "  Y " + Fmt(r.Y);
            }
            if (s.T == "line" && L(s.Id) != null)
            {
                var l = L(s.Id); var a = P(l.A); var b = P(l.B);
                return "長さ " + Fmt(Round(Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y)));
            }
            return "";
        }

        public static string Fmt(double v) { return Js.Num(Round(v)); }
    }
}
