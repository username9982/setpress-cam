// 割付とコード（app.js の「割付」「コードの区画」の移植）。
// 「割付」で図形（外形）を選び、「自動割付」で穴・切り欠きを F3 / 単発、外形を F13 で抜くプログラムを作る。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SetPressCam
{
    public partial class Editor
    {
        public static string HitKey(int work, int i) { return "hit:" + work + ":" + i; }
        public static string MacroOf(Hit h) { return h.Macro ?? "F3"; }

        Tool ToolOf(Hit h) { return h.Tool == null || h.Tool.Value >= Doc.Tools.Count ? null : Doc.Tools[h.Tool.Value]; }

        /// <summary>(x, y) にある割付の長方形（F13 は輪郭のまわりの帯だけ）。scale は画面の拡大率。</summary>
        public List<HitRef> HitsAt(double x, double y, double scale)
        {
            var list = new List<HitRef>();
            foreach (var lay in Doc.Layouts)
            {
                if (Owner(lay.Work) == null) continue;
                var hs = HitsOf(lay);
                for (int i = 0; i < hs.Count; i++)
                {
                    var h = hs[i];
                    if (MacroOf(h) == "F13")
                    {
                        var t = ToolOf(h);
                        double m = 10 / scale;
                        double dw = Math.Max(t != null ? t.W ?? 0 : 0, m), dh = Math.Max(t != null ? t.H ?? 0 : 0, m);
                        bool o = x >= h.X0 - m && x <= h.X1 + m && y >= h.Y0 - m && y <= h.Y1 + m;
                        bool inner = x > h.X0 + dw && x < h.X1 - dw && y > h.Y0 + dh && y < h.Y1 - dh;
                        if (o && !inner) list.Add(new HitRef(lay.Work, i));
                        continue;
                    }
                    if (x >= h.X0 - 1e-6 && x <= h.X1 + 1e-6 && y >= h.Y0 - 1e-6 && y <= h.Y1 + 1e-6) list.Add(new HitRef(lay.Work, i));
                }
            }
            return list;
        }

        public void SelectHit(HitRef h)
        {
            LayoutHit = h;
            if (h != null) LayoutWork = Owner(h.Work) ?? LayoutWork;
        }

        /// <summary>「割付」でタップした: 割付の長方形があれば次々に選び、なければワークを選ぶ。</summary>
        public void TapWork(Sel hit, V world, double scale)
        {
            var cands = hit != null && hit.T == "origin" ? new List<HitRef>() : HitsAt(world.X, world.Y, scale);
            if (cands.Count > 0)
            {
                int cur = LayoutHit == null ? -1 : cands.FindIndex(c => c.Work == LayoutHit.Work && c.I == LayoutHit.I);
                SelectHit(cands[(cur + 1) % cands.Count]);
                return;
            }
            LayoutHit = null;
            int? k = hit != null && hit.T == "origin" ? hit.Id : Info.OuterAt(world.X, world.Y);
            if (k == null && hit != null && hit.T == "line") k = Owner(L(hit.Id).A);
            if (k == null && hit != null && hit.T == "point") k = Owner(hit.Id);
            LayoutWork = k;
        }

        /// <summary>割付の長方形を、いまのワークの位置に合わせて返す（ワークを動かしたら一緒に動かす）。</summary>
        public List<Hit> HitsOf(Layout lay)
        {
            var k = Owner(lay.Work);
            if (k == null || k.Value >= Info.Outers.Count) return lay.Hits;
            var o = Info.Outers[k.Value].Origin;
            if (lay.Org == null) lay.Org = new Org { X = o.X, Y = o.Y };  // 古い図面: いまの位置で割り付けたものとみなす
            double dx = Round(o.X - lay.Org.X), dy = Round(o.Y - lay.Org.Y);
            if (dx == 0 && dy == 0) return lay.Hits;
            return lay.Hits.Select(h =>
            {
                var c = h.Clone();
                c.X0 = Round(h.X0 + dx); c.X1 = Round(h.X1 + dx); c.Y0 = Round(h.Y0 + dy); c.Y1 = Round(h.Y1 + dy);
                return c;
            }).ToList();
        }

        public int WorkKey(int k) { return Info.Outers[k].RefX; }
        public Layout LayoutOf(int k) { return Doc.Layouts.Find(q => Owner(q.Work) == k); }
        public List<Shape> HolesOf(int k) { return Shapes.Where(s => s.Role == "hole" && s.Outer == k).ToList(); }
        public List<Shape> NotchesOf(int k) { return Shapes.Where(s => s.Role == "notch" && s.Outer == k).ToList(); }

        // JSON.stringify と同じ書き方
        static string PolysJson(IEnumerable<List<V>> polys, V o)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var poly in polys)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(PolyJson(poly, o));
            }
            return sb.Append(']').ToString();
        }

        static string PolyJson(List<V> poly, V o)
        {
            return "[" + string.Join(",", poly.Select(v => "[" + Js.Num(Round(v.X - o.X)) + "," + Js.Num(Round(o.Y - v.Y)) + "]")) + "]";
        }

        /// <summary>割付したときの形（変わったか確かめる）。</summary>
        public string HoleSrc(int k)
        {
            var o = Info.Outers[k].Origin;
            var notches = NotchesOf(k);
            string src = PolysJson(HolesOf(k).Select(s => s.Poly), o) + (notches.Count > 0 ? "|N" + PolysJson(notches.Select(s => s.Poly), o) : "");
            return src + "|" + PolyJson(Shapes[Info.Outers[k].Shape].Poly, o);
        }

        /// <summary>外形を囲む四角。F13 はこの四角を抜き、L 字などで四角から欠けた所は切り欠きとして先に抜く</summary>
        public R OuterRect(int k)
        {
            var poly = Shapes[Info.Outers[k].Shape].Poly;
            return new R(poly.Min(v => v.X), poly.Min(v => v.Y), poly.Max(v => v.X), poly.Max(v => v.Y));
        }

        static bool IsRectTool(Tool t) { return t.Shape == "rect" && (t.W ?? 0) != 0 && (t.H ?? 0) != 0; }
        static double Area(Tool t) { return t.W.Value * t.H.Value; }

        /// <summary>外形の輪郭を抜く金型: 長方形の金型のうち、いちばん大きいもの</summary>
        public int? PickOutlineTool()
        {
            int? best = null;
            for (int i = 0; i < Doc.Tools.Count; i++)
            {
                var t = Doc.Tools[i];
                if (!IsRectTool(t)) continue;
                if (best == null || Area(t) > Area(Doc.Tools[best.Value])) best = i;
            }
            return best;
        }

        /// <summary>長方形の金型（番号）のうち、w × h の長方形に入るいちばん大きいもの</summary>
        public int? PickRectTool(double w, double h)
        {
            int? best = null;
            for (int i = 0; i < Doc.Tools.Count; i++)
            {
                var t = Doc.Tools[i];
                if (!IsRectTool(t)) continue;
                if (t.W > w + 1e-6 || t.H > h + 1e-6) continue;
                if (best == null || Area(t) > Area(Doc.Tools[best.Value])) best = i;
            }
            return best;
        }

        Hit RectHit(R r, string kind)
        {
            double w = Round(r.X1 - r.X0), h = Round(r.Y1 - r.Y0);
            var tool = PickRectTool(w, h);
            var t = tool == null ? null : Doc.Tools[tool.Value];
            bool single = t != null && Math.Abs(t.W.Value - w) < 1e-6 && Math.Abs(t.H.Value - h) < 1e-6;
            return new Hit { Tool = tool, X0 = Round(r.X0), Y0 = Round(r.Y0), X1 = Round(r.X1), Y1 = Round(r.Y1), Macro = single ? "単発" : null, Kind = kind };
        }

        class Split { public List<R> Rects; public int Open; public double[] Score; }

        // 穴を長方形に分け、継ぎ目で金型寸法の約 1/3 重ねる（app.js の splitHole）。
        // fix: 切り欠きで、長方形を外形の外へ広げる処理（広げたあとの大きさで金型・重ねる量・良し悪しを決める）
        Split SplitHole(List<V> poly, Func<R, R> fix = null)
        {
            fix = fix ?? (r => r);
            const double E = 1e-6;
            Func<double, double> step = v => Math.Max(0.1, Js.MathRound(v * 10) / 10);
            Func<R, string, double> ovOf = (r, d) =>
            {
                var f = fix(r);
                var t = PickRectTool(Round(f.X1 - f.X0), Round(f.Y1 - f.Y0));
                double size = t == null ? (d == "x" ? f.X1 - f.X0 : f.Y1 - f.Y0) : (d == "x" ? Doc.Tools[t.Value].W.Value : Doc.Tools[t.Value].H.Value);
                return step(size / 3);
            };
            Func<R, bool> noTool = r => PickRectTool(Round(r.X1 - r.X0), Round(r.Y1 - r.Y0)) == null;
            Split best = null;
            foreach (var part in Geo.RectPartitions(poly))
            {
                var bas = part.Select(r => r.Clone()).ToList();
                var rects = part.Select(r => r.Clone()).ToList();
                int open = 0;
                for (int i = 0; i < bas.Count; i++)
                {
                    for (int j = 0; j < bas.Count; j++)
                    {
                        var p = bas[i]; var q = bas[j];
                        bool hSeam = Math.Abs(p.Y0 - q.Y1) < E && Math.Min(p.X1, q.X1) - Math.Max(p.X0, q.X0) > E;
                        bool vSeam = Math.Abs(p.X1 - q.X0) < E && Math.Min(p.Y1, q.Y1) - Math.Max(p.Y0, q.Y0) > E;
                        if (!hSeam && !vSeam) continue;
                        var tries = hSeam
                            ? new List<(int k, string key, int dir, double depth, string d, bool fits)>
                            {
                                (j, "y1", 1, p.Y1 - p.Y0, "y", q.X0 >= p.X0 - E && q.X1 <= p.X1 + E),
                                (i, "y0", -1, q.Y1 - q.Y0, "y", p.X0 >= q.X0 - E && p.X1 <= q.X1 + E),
                            }
                            : new List<(int k, string key, int dir, double depth, string d, bool fits)>
                            {
                                (i, "x1", 1, q.X1 - q.X0, "x", p.Y0 >= q.Y0 - E && p.Y1 <= q.Y1 + E),
                                (j, "x0", -1, p.X1 - p.X0, "x", q.Y0 >= p.Y0 - E && q.Y1 <= p.Y1 + E),
                            };
                        Js.Sort(tries, (u, w) => (w.fits ? 1 : 0) - (u.fits ? 1 : 0));
                        bool done = false;
                        foreach (var tr in tries)
                        {
                            double ov = Math.Min(ovOf(rects[tr.k], tr.d), tr.depth);
                            var t = rects[tr.k].With(tr.key, rects[tr.k].Get(tr.key) + tr.dir * ov);
                            if (Geo.RectInside(poly, t)) { rects[tr.k] = t; done = true; break; }
                        }
                        if (!done) open++;
                    }
                }
                // 金型が入らない小さい長方形は、穴からはみ出さない所まで広げる。そのあと外へ広げる（切り欠き）
                for (int k = 0; k < rects.Count; k++)
                {
                    if (noTool(fix(rects[k]))) rects[k] = Geo.RectGrow(poly, rects[k]);
                    rects[k] = fix(rects[k]);
                }
                int missing = rects.Count(noTool);
                double area = Round(rects.Sum(r => (r.X1 - r.X0) * (r.Y1 - r.Y0)));
                var score = new double[] { open, missing, rects.Count, area };
                bool better = best == null;
                if (!better)
                    for (int n = 0; n < score.Length; n++)
                        if (score[n] != best.Score[n]) { better = score[n] < best.Score[n]; break; }
                if (better) best = new Split { Rects = rects, Open = open, Score = score };
            }
            return best ?? new Split { Rects = new List<R>(), Open = 0 };
        }

        // 切り欠きの長方形 r を、外形を囲む四角 box の辺に乗っている側（r0 で判断）だけ外へ広げて、長方形の金型が
        // ちょうど入る大きさにする。はみ出す量がいちばん少ないものを選ぶ（同じなら大きい金型）
        R GrowOutward(R r, R r0, R box, List<List<V>> others)
        {
            const double E = 1e-6;
            bool cx0 = Math.Abs(r0.X0 - box.X0) < E, cx1 = Math.Abs(r0.X1 - box.X1) < E, cy0 = Math.Abs(r0.Y0 - box.Y0) < E, cy1 = Math.Abs(r0.Y1 - box.Y1) < E;
            R bestG = null;
            double bestExtra = 0, bestArea = 0;
            foreach (var t in Doc.Tools)
            {
                if (!IsRectTool(t)) continue;
                var g = r.Clone();
                double needW = t.W.Value - (r.X1 - r.X0), needH = t.H.Value - (r.Y1 - r.Y0);
                if (needW > E) { if (cx0) g.X0 -= needW; else if (cx1) g.X1 += needW; else continue; }
                if (needH > E) { if (cy0) g.Y0 -= needH; else if (cy1) g.Y1 += needH; else continue; }
                var gp = g.Poly();
                if (others.Any(p => Geo.Overlaps(p, gp))) continue;
                double extra = (g.X1 - g.X0) * (g.Y1 - g.Y0) - (r.X1 - r.X0) * (r.Y1 - r.Y0);
                if (bestG == null || extra < bestExtra - E || (Math.Abs(extra - bestExtra) < E && Area(t) > bestArea))
                {
                    bestG = g; bestExtra = extra; bestArea = Area(t);
                }
            }
            return bestG == null ? null : new R(Round(bestG.X0), Round(bestG.Y0), Round(bestG.X1), Round(bestG.Y1));
        }

        public void AutoLayout()
        {
            if (LayoutWork == null || LayoutWork.Value >= Info.Outers.Count) return;
            int k = LayoutWork.Value;
            var holes = HolesOf(k);
            var hits = new List<Hit>();
            var warn = new List<string>();
            if (!Doc.Tools.Any(IsRectTool)) warn.Add("長方形の金型がありません。「金型」で決めてください。");
            var box = OuterRect(k);
            const double E = 1e-6;
            var outerPoly = Shapes[Info.Outers[k].Shape].Poly;
            var pieces = Geo.CutPieces(outerPoly, holes.Concat(NotchesOf(k)).Select(s => s.Poly).ToList(), box);
            Func<List<V>, bool> onBox = poly => poly.Any(v => Math.Abs(v.X - box.X0) < E || Math.Abs(v.X - box.X1) < E || Math.Abs(v.Y - box.Y0) < E || Math.Abs(v.Y - box.Y1) < E);
            var holePieces = pieces.Where(p => !onBox(p)).ToList();
            for (int i = 0; i < holePieces.Count; i++)
            {
                var sp = SplitHole(holePieces[i]);
                if (sp.Open > 0) warn.Add("穴 " + (i + 1) + " の継ぎ目 " + sp.Open + " か所は、抜かない所にぶつかるので重ねていません。");
                foreach (var r in sp.Rects)
                {
                    double w = Round(r.X1 - r.X0), h = Round(r.Y1 - r.Y0);
                    var tool = PickRectTool(w, h);
                    if (tool == null) warn.Add("穴 " + (i + 1) + " の " + Js.Num(w) + " × " + Js.Num(h) + " の部分に入る長方形の金型がありません。");
                    hits.Add(new Hit { Tool = tool, X0 = Round(r.X0), Y0 = Round(r.Y0), X1 = Round(r.X1), Y1 = Round(r.Y1) });
                }
            }
            // 上から下、左から右の順に抜く
            Comparison<Hit> order = (p, q) => q.Y1 != p.Y1 ? Js.Cmp(q.Y1 - p.Y1) : Js.Cmp(p.X0 - q.X0);
            Js.Sort(hits, order);
            // 切り欠き: 外形と重なる所を、単発（金型とちょうど同じ大きさ）か F3 で抜く。ほかのワークにぶつからなければ、
            // 外形の辺の外へ広げて四角く抜き、あとから抜く F13 に金型寸法の約 1/3 かぶるようにする
            var notches = pieces.Where(onBox).ToList();
            var outlineTool = PickOutlineTool();
            if (notches.Count > 0)
            {
                var ot = outlineTool == null ? null : Doc.Tools[outlineTool.Value];
                Func<double, double> step1 = v => Math.Max(0.1, Js.MathRound(v * 10) / 10);
                double ovx = ot != null ? step1(ot.W.Value / 3) : 0, ovy = ot != null ? step1(ot.H.Value / 3) : 0;
                var others = Info.Outers.Where((o, j) => j != k).Select(o => Shapes[o.Shape].Poly).ToList();
                var nh = new List<Hit>();
                for (int i = 0; i < notches.Count; i++)
                {
                    var poly = notches[i];
                    if (poly == null) continue;
                    Func<R, R> fix = r0 =>
                    {
                        var r = r0.Clone();
                        r.Blocked = false;
                        if (Math.Abs(r0.X0 - box.X0) < E) r.X0 -= ovx;
                        if (Math.Abs(r0.X1 - box.X1) < E) r.X1 += ovx;
                        if (Math.Abs(r0.Y0 - box.Y0) < E) r.Y0 -= ovy;
                        if (Math.Abs(r0.Y1 - box.Y1) < E) r.Y1 += ovy;
                        var rp = r.Poly();
                        bool hitOther = others.Any(p => Geo.Overlaps(p, rp));
                        var use = hitOther ? r0.Clone() : r;
                        if (PickRectTool(Round(use.X1 - use.X0), Round(use.Y1 - use.Y0)) == null)
                        {
                            var grown = GrowOutward(use, r0, box, others);
                            if (grown != null) use = grown;
                        }
                        use.Blocked = hitOther;
                        return use;
                    };
                    var sp = SplitHole(poly, fix);
                    if (sp.Open > 0) warn.Add("切り欠き " + (i + 1) + " の継ぎ目 " + sp.Open + " か所は、抜かない所にぶつかるので重ねていません。");
                    bool blocked = false;
                    foreach (var use in sp.Rects)
                    {
                        if (use.Blocked) blocked = true;
                        var hit = RectHit(use, "notch");
                        if (hit.Tool == null) warn.Add("切り欠き " + (i + 1) + " の " + Js.Num(Round(use.X1 - use.X0)) + " × " + Js.Num(Round(use.Y1 - use.Y0)) + " の部分に入る長方形の金型がありません。");
                        nh.Add(hit);
                    }
                    if (blocked) warn.Add("切り欠き " + (i + 1) + " は、ほかのワークにぶつかるので外へ広げていない所があります（F13 と重なりません）。");
                }
                Js.Sort(nh, order);
                hits.AddRange(nh);
            }
            // 最後に F13 で輪郭を抜く。金型寸法のぶん外側にオフセットした長方形を入れる
            {
                var t = outlineTool == null ? null : Doc.Tools[outlineTool.Value];
                double dw = t != null ? t.W.Value : 0, dh = t != null ? t.H.Value : 0;
                if (outlineTool == null) warn.Add("外形を抜く長方形の金型がありません。");
                hits.Add(new Hit { Macro = "F13", Tool = outlineTool, Mj = Doc.Mj, X0 = Round(box.X0 - dw), Y0 = Round(box.Y0 - dh), X1 = Round(box.X1 + dw), Y1 = Round(box.Y1 + dh) });
            }
            Snapshot();
            int key = WorkKey(k);
            var before = WorkOrigin(k);
            Doc.Layouts = Doc.Layouts.Where(q => Owner(q.Work) != k).ToList();
            var og = Info.Outers[k].Origin;
            Doc.Layouts.Add(new Layout { Work = key, Src = HoleSrc(k), Hits = hits, Warn = warn, Org = new Org { X = og.X, Y = og.Y } });
            KeepSheetDims(k, before);
            LayoutHit = null;
            Changed();
            FlashMsg("S" + (k + 1) + " を割り付けました（穴 " + holes.Count + " 個" + (notches.Count > 0 ? "・切り欠き " + notches.Count + " 個" : "") + "・外形 F13）");
        }

        public void ClearLayout()
        {
            if (LayoutWork == null || LayoutOf(LayoutWork.Value) == null) return;
            int k = LayoutWork.Value;
            Snapshot();
            var before = WorkOrigin(k);
            Doc.Layouts = Doc.Layouts.Where(q => Owner(q.Work) != k).ToList();
            KeepSheetDims(k, before);
            LayoutHit = null;
            Changed();
        }

        // ワーク原点が before から動いたぶん、板の原点からの寸法の値を直す（ワークの位置は変えない）
        void KeepSheetDims(int k, V before)
        {
            Info = Geo.Analyze(Doc);  // 割付を変えたあとの原点で測る
            Shapes = Info.Shapes;
            var now = WorkOrigin(k);
            foreach (var axis in new[] { "x", "y" })
            {
                var d = SheetDimOf(k, axis);
                if (d != null) d.Value = Round(d.Value + (axis == "x" ? now.X - before.X : before.Y - now.Y));
            }
        }

        public Hit SelF13()
        {
            var lay = LayoutWork == null ? null : LayoutOf(LayoutWork.Value);
            return lay?.Hits.Find(q => MacroOf(q) == "F13");
        }

        /// <summary>ミクロジョイントの入力の上限。</summary>
        public double MjMax()
        {
            var h = SelF13();
            var t = h == null ? null : ToolOf(h);
            return h != null && t != null ? Math.Max(0, Math.Min(h.X1 - h.X0, h.Y1 - h.Y0) / 2 - Math.Max(t.W ?? 0, t.H ?? 0)) : 50;
        }

        /// <summary>ミクロジョイントを変える。値は図面に保存し、割り付け直しても、ほかのワークでも同じ値を使う。</summary>
        public void SetMj(double v)
        {
            var h = SelF13();
            if (v == Doc.Mj && (h == null || v == (h.Mj ?? 0))) { Refreshed?.Invoke(); return; }
            Snapshot();
            Doc.Mj = v;
            if (h != null) h.Mj = v;
            LastMj = v;
            Changed();
        }

        public double F13R(Hit h)
        {
            var t = ToolOf(h);
            return (h.Mj ?? 0) != 0 && t != null ? Round(Math.Max(t.W ?? 0, t.H ?? 0) + h.Mj.Value) : 0;
        }

        // 長方形の金型では、角 R を長いほうの金型寸法に合わせると、短いほうの辺ではつながりが長く残る。
        // その余りを F13 の前に単発で抜いておく
        public List<R> F13Singles(Hit h)
        {
            var t = ToolOf(h);
            var output = new List<R>();
            if (t == null || (h.Mj ?? 0) == 0 || t.W == null || t.H == null || Math.Abs(t.W.Value - t.H.Value) < 1e-6) return output;
            double tw = t.W.Value, th = t.H.Value;
            double mj = h.Mj.Value;
            double r = F13R(h);
            Func<double, double, double> ovOf = (len, strip) => Math.Min(Math.Max(0.1, Js.MathRound(len / 3 * 10) / 10), Math.Max(0, strip / 2));
            Func<double, double, double, List<double>> spots = (a, b, len) =>
            {
                double st = len - len / 3;
                int n = b - a <= len + 1e-6 ? 1 : 1 + (int)Math.Ceiling((b - a - len) / st - 1e-6);
                if (n == 1) return new List<double> { a };
                double gap = Math.Floor((b - a - len) / (n - 1) * 10 + 1e-6) / 10;
                return Enumerable.Range(0, n).Select(i => i == n - 1 ? b - len : a + gap * i).ToList();
            };
            if (tw > th)
            {
                double ov = ovOf(th, (h.Y1 - h.Y0) - 2 * r);
                var ranges = new[] { (h.Y1 - tw - mj - ov, h.Y1 - th - mj), (h.Y0 + th + mj, h.Y0 + tw + mj + ov) };
                foreach (var xs in new[] { h.X0, h.X1 - tw })
                    foreach (var (a, b) in ranges)
                        foreach (var y in spots(a, b, th)) output.Add(new R(xs, y, xs + tw, y + th));
            }
            else
            {
                double ov = ovOf(tw, (h.X1 - h.X0) - 2 * r);
                var ranges = new[] { (h.X0 + tw + mj, h.X0 + th + mj + ov), (h.X1 - th - mj - ov, h.X1 - tw - mj) };
                foreach (var ys in new[] { h.Y1 - th, h.Y0 })
                    foreach (var (a, b) in ranges)
                        foreach (var x in spots(a, b, tw)) output.Add(new R(x, ys, x + tw, ys + th));
            }
            return output.Select(q => new R(Round(q.X0), Round(q.Y0), Round(q.X1), Round(q.Y1))).ToList();
        }

        /// <summary>F13 で実際に抜く帯: 入れた長方形の内側を金型の幅で回り、各辺の両端は角 R のぶん残す</summary>
        public List<R> F13Strips(Hit h)
        {
            var t = ToolOf(h);
            if (t == null || t.W == null || t.H == null) return new List<R>();
            double r = F13R(h);
            return new List<R>
            {
                new R(h.X0 + r, h.Y1 - t.H.Value, h.X1 - r, h.Y1),
                new R(h.X0 + r, h.Y0, h.X1 - r, h.Y0 + t.H.Value),
                new R(h.X0, h.Y0 + r, h.X0 + t.W.Value, h.Y1 - r),
                new R(h.X1 - t.W.Value, h.Y0 + r, h.X1, h.Y1 - r),
            }.Where(q => q.X1 - q.X0 > 1e-6 && q.Y1 - q.Y0 > 1e-6).ToList();
        }

        /// <summary>割付の区画の中身: ワークの名前、まとめ、注意</summary>
        public void LayoutPanelInfo(out string work, out string summary, out List<string> warn)
        {
            if (LayoutWork != null && LayoutWork.Value >= Info.Outers.Count) LayoutWork = null;
            warn = new List<string>();
            summary = "";
            work = LayoutWork == null ? "図形をクリックして選ぶ" : "S" + (LayoutWork + 1);
            if (LayoutWork == null) return;
            int k = LayoutWork.Value;
            var lay = LayoutOf(k);
            var holes = HolesOf(k);
            int nn = NotchesOf(k).Count + Geo.ComplementPieces(Shapes[Info.Outers[k].Shape].Poly, OuterRect(k)).Count;
            summary = "穴 " + holes.Count + " 個" + (nn > 0 ? "・切り欠き " + nn + " 個" : "");
            if (lay != null)
            {
                Func<string, int> count = m => lay.Hits.Count(h => MacroOf(h) == m);
                summary += "\r\nF3 " + count("F3") + " 回" + (count("単発") > 0 ? "\r\n単発 " + count("単発") + " 回" : "") + (count("F13") > 0 ? "\r\nF13 " + count("F13") + " 回（外形）" : "");
                var used = lay.Hits.Where(h => h.Tool != null).Select(h => h.Tool.Value).Distinct().ToList();
                if (used.Count > 0) summary += "\r\n金型 " + string.Join("、", used.Select(t => "T" + (t + 1) + " " + Model.ToolText(Doc.Tools[t])));
                warn.AddRange(lay.Warn);
                if (lay.Src != HoleSrc(k)) warn.Add("割付のあとで図形が変わりました。もう一度「自動割付」を押してください。");
            }
            else
            {
                summary += "\r\nまだ割り付けていません";
            }
        }

        /// <summary>割付の区画のミクロジョイントの欄に出す値。</summary>
        public double MjShown()
        {
            var f13 = SelF13();
            return f13 != null ? f13.Mj ?? 0 : Doc.Mj;
        }

        // ---- コード: 機械に手で打ち込むデータとコメントだけを並べる ----
        public List<CodeLine> GenCode(string version)
        {
            var lines = new List<CodeLine>();
            Action<string, string, bool> add = (text, key, comment) => lines.Add(new CodeLine { Text = text, Key = key, Comment = comment });
            add("; セットプレス CAM v" + version, null, true);
            add("; 図面 " + (Doc.Name != "" ? Doc.Name : "名前なし") + "  板 " + Fmt(Doc.Sheet.W) + " x " + Fmt(Doc.Sheet.H) + " mm", null, true);
            add("; 板厚 " + (Doc.Sheet.T != null ? Fmt(Doc.Sheet.T.Value) + " mm" : "未設定"), null, true);
            add(Model.ProgramText(Doc.Program), null, false);
            for (int i = 0; i < Doc.Tools.Count; i++) add("; T" + (i + 1) + " " + Model.ToolText(Doc.Tools[i]), null, true);
            // ワークごとに F100〜（マクロ番号）と F110 で囲んでマクロに登録し、F23 で板の上の位置へ呼び出す
            int macroNo = 100;
            foreach (var lay in Doc.Layouts)
            {
                var k = Owner(lay.Work);
                if (k == null) continue;
                var o = WorkOrigin(k.Value);
                int mno = macroNo++;
                add("", null, false);
                add("; S" + (k + 1) + " 割付  F" + mno + "〜F110 でマクロ " + mno + " に登録。F3: 開始点（左上）→ XY 寸法 → 金型 XY 寸法", null, true);
                add("F" + mno, null, false);
                if (lay.Src != HoleSrc(k.Value)) add("; ※ 割付のあとで図形が変わりました。もう一度自動割付してください", null, true);
                Func<double, double, string, string> xy = (x, y, note) => "  " + ("X" + Fmt(x)).PadRight(8) + ("Y" + Fmt(y)).PadRight(8) + "; " + note;
                int? curTool = null;
                var hs = HitsOf(lay);
                for (int i = 0; i < hs.Count; i++)
                {
                    var h = hs[i];
                    var t = ToolOf(h);
                    string hk = HitKey(lay.Work, i);
                    bool f13 = MacroOf(h) == "F13";
                    bool single = MacroOf(h) == "単発";
                    add(f13 ? "; " + (i + 1) + " 外形  F13: 開始点（左上）→ 外形 XY → 金型 XY → 角 R（金型寸法のぶん外側）"
                        : "; " + (i + 1) + (h.Kind == "notch" ? " 切り欠き" : "") + (single ? "  単発: 金型の中心" : ""), hk, true);
                    if (h.Tool != null && h.Tool != curTool) { add("T" + (h.Tool + 1), hk, false); curTool = h.Tool; }
                    if (f13)
                    {
                        var singles = F13Singles(h);
                        if (singles.Count > 0) add("; 単発（短いほうの辺のつながりを、ミクロジョイントの長さまで先に抜く。F13 の帯と約 1/3 重ねる）  金型の中心", hk, true);
                        foreach (var q in singles) add(xy((q.X0 + q.X1) / 2 - o.X, o.Y - (q.Y0 + q.Y1) / 2, "単発 中心"), hk, false);
                    }
                    if (single) { add(xy((h.X0 + h.X1) / 2 - o.X, o.Y - (h.Y0 + h.Y1) / 2, "単発 中心"), hk, false); continue; }
                    add(MacroOf(h), hk, false);
                    add(xy(h.X0 - o.X, o.Y - h.Y1, "開始点"), hk, false);
                    add(xy(h.X1 - h.X0, h.Y1 - h.Y0, f13 ? "外形" : "寸法"), hk, false);
                    add(t != null && t.W != null && t.H != null ? xy(t.W.Value, t.H.Value, "金型") : "  ?       ?       ; 金型（入る長方形の金型がない）", hk, false);
                    if (f13) add("  " + ("R" + Fmt(F13R(h))).PadRight(16) + "; 角 R" + ((h.Mj ?? 0) != 0 ? "（金型 + ミクロジョイント " + Fmt(h.Mj.Value) + "）" : ""), hk, false);
                }
                if (lay.Hits.Count == 0) add("; 抜く所がありません", null, true);
                add("F110", null, false);
                // F23: マクロ番号 → 板の原点（左上）からワーク原点までの XY → ピッチ（抜く形を囲む四角 + 10 mm）→ 列数 XY
                double bw = hs.Count > 0 ? hs.Max(q => q.X1) - hs.Min(q => q.X0) : 0;
                double bh = hs.Count > 0 ? hs.Max(q => q.Y1) - hs.Min(q => q.Y0) : 0;
                add("; S" + (k + 1) + " 配置  F23: マクロ番号 → 板の原点からワーク原点まで XY → ピッチ XY（抜く形 + 10）→ 列数 XY", null, true);
                add("F23", null, false);
                add("  " + mno.ToString().PadRight(16) + "; マクロ番号", null, false);
                add(xy(SheetPos(k.Value, "x"), SheetPos(k.Value, "y"), "ワーク原点"), null, false);
                add(xy(bw + 10, bh + 10, "ピッチ"), null, false);
                add("  " + "1".PadRight(8) + "1".PadRight(8) + "; 列数", null, false);
            }
            if (!Doc.Layouts.Any(q => Owner(q.Work) != null))
            {
                add("", null, false);
                add("; まだ割り付けていません（「割付」→ 図形をクリック →「自動割付」）", null, true);
            }
            return lines;
        }
    }
}
