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
                    if (h.Kind == "outline")
                    {
                        // 丸の外形の追い抜きは、金型が通る輪だけ。金型がないときも指で押せる幅にする
                        var t = ToolOf(h);
                        double m = 10 / scale, rr = (h.X1 - h.X0) / 2;
                        double dx = x - (h.X0 + h.X1) / 2, dy = y - (h.Y0 + h.Y1) / 2, d = Math.Sqrt(dx * dx + dy * dy);
                        if (d <= rr + m && d >= rr - Math.Max(t != null ? t.D ?? 0 : 0, m) - m) list.Add(new HitRef(lay.Work, i));
                        continue;
                    }
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
        /// <summary>丸の外形なら、その丸。ほかは null</summary>
        public CircleGeo OuterCircle(int k) { return Shapes[Info.Outers[k].Shape].Circle; }
        /// <summary>ワークの丸穴（丸の外形そのものは入れない）</summary>
        public List<Circle> CirclesOf(int k) { var oc = OuterCircle(k); return Circles.Where(c => Owner(c.C) == k && !(oc != null && c.Id == oc.Id)).ToList(); }

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
            var cs = CirclesOf(k);
            string src = PolysJson(HolesOf(k).Select(s => s.Poly), o) + (notches.Count > 0 ? "|N" + PolysJson(notches.Select(s => s.Poly), o) : "")
                + (cs.Count > 0 ? "|C[" + string.Join(",", cs.Select(c => { var p = P(c.C); return "[" + Js.Num(Round(p.X - o.X)) + "," + Js.Num(Round(o.Y - p.Y)) + "," + Js.Num(c.D) + "]"; })) + "]" : "");
            var oc = OuterCircle(k);
            if (oc != null) return src + "|O" + Js.Num(Round(oc.R * 2));  // 丸の外形は直径
            return src + "|" + PolyJson(Shapes[Info.Outers[k].Shape].Poly, o);
        }

        /// <summary>外形を囲む四角。F13 はこの四角を抜き、L 字などで四角から欠けた所は切り欠きとして先に抜く</summary>
        public R OuterRect(int k)
        {
            var oc = OuterCircle(k);
            if (oc != null) return new R(Round(oc.X - oc.R), Round(oc.Y - oc.R), Round(oc.X + oc.R), Round(oc.Y + oc.R));
            var poly = Shapes[Info.Outers[k].Shape].Poly;
            return new R(poly.Min(v => v.X), poly.Min(v => v.Y), poly.Max(v => v.X), poly.Max(v => v.Y));
        }

        static bool IsRectTool(Tool t) { return t.Shape == "rect" && (t.W ?? 0) != 0 && (t.H ?? 0) != 0; }
        static double Area(Tool t) { return t.W.Value * t.H.Value; }

        /// <summary>丸の外形を抜く金型: 丸の金型のうち、いちばん大きいもの</summary>
        public int? PickOutlineRound()
        {
            int? best = null;
            for (int i = 0; i < Doc.Tools.Count; i++)
            {
                var t = Doc.Tools[i];
                if (t.Shape != "round" || (t.D ?? 0) == 0) continue;
                if (best == null || t.D.Value > Doc.Tools[best.Value].D.Value) best = i;
            }
            return best;
        }

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

        /// <summary>直径 d の丸の金型（番号）。なければ null</summary>
        public int? PickRoundTool(double d)
        {
            for (int i = 0; i < Doc.Tools.Count; i++)
            {
                var t = Doc.Tools[i];
                if (t.Shape == "round" && (t.D ?? 0) != 0 && Math.Abs(t.D.Value - d) < 1e-6) return i;
            }
            return null;
        }

        /// <summary>丸の金型で抜いた単発か</summary>
        public bool RoundHit(Hit h)
        {
            if (MacroOf(h) != "単発" || h.Tool == null || h.Tool.Value < 0 || h.Tool.Value >= Doc.Tools.Count) return false;
            return Doc.Tools[h.Tool.Value].Shape == "round";
        }

        /// <summary>丸の追い抜き（F5）の板厚。未設定なら 1.6 mm</summary>
        public double NibbleT() { return Doc.Sheet.T ?? 1.6; }

        /// <summary>直径 d の丸を追い抜き（F5）で抜く金型とピッチ。なければ null。
        /// d より小さい丸の金型のうち大きいものから選ぶ。ピッチ（抜くときの円同士の直線距離）は板厚の 2 倍を目安に、
        /// 板厚以上 10 mm 以下で、金型の中心が通る円の半径を超えないようにする（入らなければ、もっと小さい金型にする）</summary>
        public Tuple<int, double> PickNibble(double d)
        {
            double t = NibbleT();
            if (t > 10) return null;
            var cands = new List<int>();
            for (int i = 0; i < Doc.Tools.Count; i++)
            {
                var q = Doc.Tools[i];
                if (q.Shape == "round" && (q.D ?? 0) != 0 && q.D.Value < d - 1e-6) cands.Add(i);
            }
            Js.Sort(cands, (a, b) => Doc.Tools[a].D.Value != Doc.Tools[b].D.Value ? Js.Cmp(Doc.Tools[b].D.Value - Doc.Tools[a].D.Value) : a - b);
            foreach (int i in cands)
            {
                double pitch = Round(Math.Min(Math.Min(2 * t, 10), (d - Doc.Tools[i].D.Value) / 2));
                if (pitch >= t - 1e-9) return Tuple.Create(i, pitch);
            }
            return null;
        }

        /// <summary>丸の金型で追い抜き（F5）する割付か</summary>
        public bool NibbleHit(Hit h)
        {
            if (MacroOf(h) != "F5" || h.Tool == null || h.Tool.Value < 0 || h.Tool.Value >= Doc.Tools.Count) return false;
            return Doc.Tools[h.Tool.Value].Shape == "round";
        }

        /// <summary>丸穴を抜く割付（単発か追い抜き）か</summary>
        public bool CircleHit(Hit h) { return RoundHit(h) || NibbleHit(h); }

        /// <summary>追い抜き（F5）で金型が抜く位置（金型の中心）。機械と同じく、中心の真上から時計回りに、ピッチ（円同士の直線距離）
        /// ごとに抜く。1 周したところで止めるので、最後の 1 つと最初の間はピッチより狭くなる</summary>
        public List<V> NibblePoints(Hit h)
        {
            var output = new List<V>();
            var t = Doc.Tools[h.Tool.Value];
            double cx = (h.X0 + h.X1) / 2, cy = (h.Y0 + h.Y1) / 2, rho = (h.X1 - h.X0 - (t.D ?? 0)) / 2, pitch = h.Pitch ?? 0;
            if (!(rho > 0) || !(pitch > 0)) return output;
            double step = 2 * Math.Asin(Math.Min(1, pitch / (2 * rho)));  // 1 回ごとに回る角度
            int n = (int)Math.Min(2000, Math.Ceiling(2 * Math.PI / step - 1e-9));
            // Y は上へ正。真上から時計回り（右 → 下 → 左）
            for (int i = 0; i < n; i++) output.Add(new V(cx + rho * Math.Sin(i * step), cy + rho * Math.Cos(i * step)));
            return output;
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
            var oc = OuterCircle(k);
            if ((oc == null || holes.Count > 0) && !Doc.Tools.Any(IsRectTool)) warn.Add("長方形の金型がありません。「金型」で決めてください。");
            // 丸の外形にかかる切り欠きは、まだ抜けない
            if (oc != null && NotchesOf(k).Count > 0) warn.Add("丸の外形にかかる切り欠き " + NotchesOf(k).Count + " 個は、まだ抜けません。抜いていません。");
            var box = OuterRect(k);
            const double E = 1e-6;
            // 丸の外形は、囲む四角を外形として穴だけを分ける（切り欠き・欠けた所はない）
            var outerPoly = oc != null ? new List<V> { new V(box.X0, box.Y0), new V(box.X1, box.Y0), new V(box.X1, box.Y1), new V(box.X0, box.Y1) } : Shapes[Info.Outers[k].Shape].Poly;
            var pieces = Geo.CutPieces(outerPoly, holes.Concat(oc != null ? new List<Shape>() : NotchesOf(k)).Select(s => s.Poly).ToList(), box);
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
            // 丸穴: 同じ直径の丸の金型があれば、中心を単発で 1 回抜く。なければ、小さい丸の金型で追い抜き（F5）する。
            // 金型ごとにまとめ（金型を替える回数を減らす）、上から下、左から右の順。どちらもできない丸は抜かない
            var circ = CirclesOf(k).Select(c =>
            {
                var rt = PickRoundTool(c.D);
                var nib = rt == null ? PickNibble(c.D) : null;
                return new { c, p = P(c.C), tool = rt ?? (nib != null ? nib.Item1 : (int?)null), pitch = nib != null ? nib.Item2 : (double?)null };
            }).ToList();
            var missD = new List<double>();
            var missN = new Dictionary<double, int>();
            foreach (var q in circ)
            {
                if (q.tool != null) continue;
                if (!missN.ContainsKey(q.c.D)) { missN[q.c.D] = 0; missD.Add(q.c.D); }
                missN[q.c.D]++;
            }
            foreach (var dd in missD) warn.Add(NibbleT() > 10
                ? "丸穴 φ" + Fmt(dd) + "（" + missN[dd] + " 個）に合う丸の金型がなく、板厚が 10 mm を超えるので追い抜きもできません。抜いていません。"
                : "丸穴 φ" + Fmt(dd) + "（" + missN[dd] + " 個）に合う丸の金型も、追い抜きできる小さい丸の金型もないので、抜いていません。「金型」で丸の金型を決めてください。");
            var cHits = circ.Where(q => q.tool != null).ToList();
            Js.Sort(cHits, (a, b) => a.tool != b.tool ? a.tool.Value - b.tool.Value : a.p.Y != b.p.Y ? Js.Cmp(b.p.Y - a.p.Y) : Js.Cmp(a.p.X - b.p.X));
            foreach (var q in cHits)
            {
                double r = q.c.D / 2;
                hits.Add(new Hit { Tool = q.tool, X0 = Round(q.p.X - r), Y0 = Round(q.p.Y - r), X1 = Round(q.p.X + r), Y1 = Round(q.p.Y + r), Macro = q.pitch != null ? "F5" : "単発", Pitch = q.pitch });
            }
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
            // 最後に輪郭を抜く。丸の外形は、丸の金型のうちいちばん大きいもので外形の外側を追い抜き（F5）する。
            // 金型が外形の外側を通るように、F5 の直径を 外形 + 金型直径 × 2 にする（金型の内側の縁が外形をなぞる）
            if (oc != null)
            {
                var tool = PickOutlineRound();
                double t = NibbleT();
                if (tool == null) warn.Add("外形の丸を抜く丸の金型がありません。「金型」で丸の金型を決めてください。");
                else if (t > 10) warn.Add("板厚が 10 mm を超えるので、外形の丸を追い抜きで抜けません。");
                else
                {
                    double dd = Doc.Tools[tool.Value].D.Value, rr = oc.R + dd;
                    double pitch = Round(Math.Min(Math.Min(2 * t, 10), oc.R + dd / 2));
                    if (pitch < t - 1e-9) warn.Add("外形の丸が小さく、板厚以上のピッチで追い抜きできません。");
                    else hits.Add(new Hit { Tool = tool, X0 = Round(oc.X - rr), Y0 = Round(oc.Y - rr), X1 = Round(oc.X + rr), Y1 = Round(oc.Y + rr), Macro = "F5", Pitch = pitch, Kind = "outline" });
                }
            }
            // 長方形などは F13 で輪郭を抜く。金型寸法のぶん外側にオフセットした長方形を入れる
            else
            {
                var t = outlineTool == null ? null : Doc.Tools[outlineTool.Value];
                double dw = t != null ? t.W.Value : 0, dh = t != null ? t.H.Value : 0;
                if (outlineTool == null) warn.Add("外形を抜く長方形の金型がありません。");
                hits.Add(new Hit { Macro = "F13", Tool = outlineTool, Mj = Doc.Mj, X0 = Round(box.X0 - dw), Y0 = Round(box.Y0 - dh), X1 = Round(box.X1 + dw), Y1 = Round(box.Y1 + dh) });
            }
            Snapshot();
            int key = WorkKey(k);
            var before = WorkOrigin(k);
            var prev = LayoutOf(k);  // F23 のピッチ・列数は割り付け直しても保つ
            Doc.Layouts = Doc.Layouts.Where(q => Owner(q.Work) != k).ToList();
            var og = Info.Outers[k].Origin;
            Doc.Layouts.Add(new Layout
            {
                Work = key, Src = HoleSrc(k), Hits = hits, Warn = warn, Org = new Org { X = og.X, Y = og.Y },
                Nx = prev?.Nx, Ny = prev?.Ny, Px = prev?.Px, Py = prev?.Py,
            });
            KeepSheetDims(k, before);
            LayoutHit = null;
            Changed();
            FlashMsg("S" + (k + 1) + " を割り付けました（穴 " + holes.Count + " 個" + (circ.Count > 0 ? "・丸穴 " + circ.Count + " 個" : "") + (notches.Count > 0 ? "・切り欠き " + notches.Count + " 個" : "") + "・外形 " + (oc != null ? "F5" : "F13") + "）");
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
            int nn = NotchesOf(k).Count + (OuterCircle(k) != null ? 0 : Geo.ComplementPieces(Shapes[Info.Outers[k].Shape].Poly, OuterRect(k)).Count);
            int nc = CirclesOf(k).Count;
            summary = "穴 " + holes.Count + " 個" + (nc > 0 ? "・丸穴 " + nc + " 個" : "") + (nn > 0 ? "・切り欠き " + nn + " 個" : "");
            if (lay != null)
            {
                Func<string, int> count = m => lay.Hits.Count(h => MacroOf(h) == m);
                summary += "\r\nF3 " + count("F3") + " 回" + (count("単発") > 0 ? "\r\n単発 " + count("単発") + " 回" : "") + (count("F5") > 0 ? "\r\nF5 " + count("F5") + " 回（丸の追い抜き）" : "") + (count("F13") > 0 ? "\r\nF13 " + count("F13") + " 回（外形）" : "");
                var used = lay.Hits.Where(h => h.Tool != null).Select(h => h.Tool.Value).Distinct().ToList();
                if (used.Count > 0) summary += "\r\n金型 " + string.Join("、", used.Select(t => "T" + (t + 1) + " " + Model.ToolText(Doc.Tools[t])));
                warn.AddRange(lay.Warn);
                if (lay.Src != HoleSrc(k)) warn.Add("割付のあとで図形が変わりました。もう一度「自動割付」を押してください。");
                if (MinToolX(lay) < X_MIN - 1e-6) warn.Add(XMinWarn(MinToolX(lay)));
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

        // ---- F23（多数個取り）: ピッチと列数 ----
        // ピッチは、書き換えていなければ 金型が抜く形を囲む四角 + 10 mm。列数は 1〜99（X は右へ、Y は下へ並べる）
        public const int F23_MAX = 99;

        public class F23Info { public double X0, Y0, X1, Y1, Px, Py; public int Nx, Ny; }

        /// <summary>機械の X は 20 mm より左（板の左端寄り）が危険。</summary>
        public const double X_MIN = 20;

        /// <summary>いちばん左を抜く金型の中心の X（板の原点から）。どの割付も、抜く範囲の左端 + 金型の幅の半分（丸は半径）が
        /// いちばん左の中心（F3・F13 は金型を範囲の内側で送り、F5 は金型の中心が 直径 − 金型直径 の円を回る）。
        /// 多数個取りは右へ並べるので、1 個目がいちばん左</summary>
        public double MinToolX(Layout lay)
        {
            double m = double.PositiveInfinity;
            foreach (var h in HitsOf(lay))
            {
                var t = ToolOf(h);
                if (t == null) continue;
                double half = t.Shape == "round" ? (t.D ?? 0) / 2 : (t.W ?? 0) / 2;
                m = Math.Min(m, Round(h.X0 + half));
            }
            return m;
        }

        public static string XMinWarn(double x) { return "いちばん左を抜く金型の中心が X" + Fmt(x) + " です。X" + Js.Num(X_MIN) + " より左は危険なので、ワークを右へ動かしてください。"; }

        public F23Info F23Of(Layout lay)
        {
            var hs = HitsOf(lay);
            var f = new F23Info();
            if (hs.Count > 0) { f.X0 = hs.Min(h => h.X0); f.X1 = hs.Max(h => h.X1); f.Y0 = hs.Min(h => h.Y0); f.Y1 = hs.Max(h => h.Y1); }
            f.Px = lay.Px ?? Round(f.X1 - f.X0 + 10);
            f.Py = lay.Py ?? Round(f.Y1 - f.Y0 + 10);
            f.Nx = lay.Nx ?? 1;
            f.Ny = lay.Ny ?? 1;
            return f;
        }

        /// <summary>コードの区画で F23 の値を書き換えたとき。列数は空なら 1、ピッチは空なら自動に戻す。</summary>
        public void SetF23(int work, string field, string text)
        {
            var lay = Doc.Layouts.Find(q => q.Work == work);
            if (lay == null) return;
            string t = (text ?? "").Trim();
            double n = 0;
            bool num = t == "" || double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out n);
            if (!num || double.IsNaN(n) || double.IsInfinity(n)) return;
            if (field == "nx" || field == "ny")
            {
                int v = t == "" ? 1 : (int)Math.Min(Math.Max(Math.Floor(n + 0.5), 1), F23_MAX);  // JS の Math.round と同じ
                int? nv = v == 1 ? (int?)null : v;
                int? cur = field == "nx" ? lay.Nx : lay.Ny;
                if (cur == nv) return;
                Snapshot();
                if (field == "nx") lay.Nx = nv; else lay.Ny = nv;
            }
            else
            {
                double? nv = t == "" ? (double?)null : Round(Math.Min(Math.Max(n, 0.1), 5000));
                double? cur = field == "px" ? lay.Px : lay.Py;
                if (cur == nv) return;
                Snapshot();
                if (field == "px") lay.Px = nv; else lay.Py = nv;
            }
            Changed();
        }

        // ---- コードを読む（手で書き換えたコードを割付に戻す。app.js の parseCode / applyCode と同じ） ----
        // 書き方は GenCode と同じ: T → F100〜F109 … F110（中に F3・F13・単発）→ F23。「;」から後ろはコメント。
        // どのワークのマクロかは、F23 のワーク原点（板の原点から）で決める。座標は、いまのワーク原点から測る。
        public class CodeError { public int Line; public string Msg; }
        const double CODE_EPS = 0.0015;  // コードの数は 0.001 mm に丸めてあるので、そのぶんは同じとみなす
        static bool NearC(double a, double b) { return Math.Abs(a - b) < CODE_EPS; }

        class PHit { public int? Tool; public double X0, Y0, X1, Y1; public string Macro; public double Mj, Pitch; }
        class PMacro { public int No, Line; public List<PHit> Hits = new List<PHit>(); }
        class PPlace { public int No, K, Nx, Ny, Line; public double Px, Py; }
        class PExp { public string Macro; public int Line; public int? Tool; public PMacro Mac; public List<double[]> Vals = new List<double[]>(); }
        class PF23 { public int Line; public List<double[]> Vals = new List<double[]>(); }
        class ParseResult
        {
            public List<CodeError> Errs = new List<CodeError>();
            public bool HasProgram; public int? Program;
            public List<PPlace> Places = new List<PPlace>();
            public List<PMacro> MacroList = new List<PMacro>();
            public Dictionary<int, PMacro> Macros = new Dictionary<int, PMacro>();
        }

        const string NUM = "(-?[0-9]+(?:\\.[0-9]+)?|-?\\.[0-9]+)";
        static readonly System.Text.RegularExpressions.Regex RE_XY = new System.Text.RegularExpressions.Regex("^X" + NUM + "Y" + NUM + "$");
        static readonly System.Text.RegularExpressions.Regex RE_R = new System.Text.RegularExpressions.Regex("^R" + NUM + "$");
        static readonly System.Text.RegularExpressions.Regex RE_N = new System.Text.RegularExpressions.Regex("^" + NUM + "$");
        static double PNum(string s) { return double.Parse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture); }

        ParseResult ParseCode(string text)
        {
            var r = new ParseResult();
            Action<int, string> err = (line, msg) => r.Errs.Add(new CodeError { Line = line, Msg = msg });
            int? tool = null;
            PMacro mac = null;
            PExp exp = null;
            PF23 f23 = null;
            Func<int?, int, Tool> rectTool = (t, line) =>
            {
                if (t == null) { err(line, "T で金型を選んでから書いてください"); return null; }
                var q = t.Value < Doc.Tools.Count ? Doc.Tools[t.Value] : null;
                if (q == null || q.Shape != "rect" || !(q.W > 0) || !(q.H > 0)) { err(line, "T" + (t + 1) + " は長方形の金型ではありません"); return null; }
                return q;
            };
            Action<PExp> finishHit = e =>
            {
                double[] st = e.Vals[0], sz = e.Vals[1], tl = e.Vals[2];
                if (!(sz[0] > 0 && sz[1] > 0)) { err(e.Line, (e.Macro == "F13" ? "外形" : "寸法") + "は 0 より大きくしてください"); return; }
                Tool t = null;
                if (tl != null)
                {
                    t = rectTool(e.Tool, e.Line);
                    if (t == null) return;
                    if (!NearC(t.W.Value, tl[0]) || !NearC(t.H.Value, tl[1]))
                    {
                        err(e.Line, "金型 X" + Fmt(tl[0]) + " Y" + Fmt(tl[1]) + " が T" + (e.Tool + 1) + "（" + Model.ToolText(t) + "）と合いません");
                        return;
                    }
                }
                var h = new PHit { Tool = t != null ? e.Tool : null, X0 = st[0], Y0 = -st[1] - sz[1], X1 = st[0] + sz[0], Y1 = -st[1] };
                if (e.Macro == "F13")
                {
                    h.Macro = "F13";
                    double rr = e.Vals[3][0];
                    if (NearC(rr, 0) || t == null) h.Mj = 0;
                    else
                    {
                        double bs = Math.Max(t.W.Value, t.H.Value);
                        if (rr <= bs + 1e-9) { err(e.Line, "角 R は 0 か、金型の長いほうの寸法 " + Fmt(bs) + " より大きくしてください"); return; }
                        h.Mj = Round(rr - bs);
                    }
                    // すぐ前の単発が、この F13 の前に抜く単発（ミクロジョイントの余り）と同じなら、F13 から作り直すので外す
                    var derived = F13Singles(new Hit { Macro = "F13", Tool = h.Tool, Mj = h.Mj, X0 = h.X0, Y0 = h.Y0, X1 = h.X1, Y1 = h.Y1 });
                    if (derived.Count > 0 && e.Mac != null && e.Mac.Hits.Count >= derived.Count)
                    {
                        var tail = e.Mac.Hits.Skip(e.Mac.Hits.Count - derived.Count).ToList();
                        bool same = true;
                        for (int i = 0; i < tail.Count && same; i++)
                            same = tail[i].Macro == "単発" && tail[i].Tool == h.Tool
                                && NearC((tail[i].X0 + tail[i].X1) / 2, (derived[i].X0 + derived[i].X1) / 2)
                                && NearC((tail[i].Y0 + tail[i].Y1) / 2, (derived[i].Y0 + derived[i].Y1) / 2);
                        if (same) e.Mac.Hits.RemoveRange(e.Mac.Hits.Count - derived.Count, derived.Count);
                    }
                }
                if (e.Mac != null) e.Mac.Hits.Add(h);
            };
            // F5（丸の追い抜き）: 中心 XY → 直径 → ピッチ → 金型直径 → 板厚
            Action<PExp> finishF5 = e =>
            {
                double[] ctr = e.Vals[0];
                double d = e.Vals[1][0], pitch = e.Vals[2][0], td = e.Vals[3][0], th = e.Vals[4][0];
                if (e.Tool == null) { err(e.Line, "T で金型を選んでから書いてください"); return; }
                var q = e.Tool.Value < Doc.Tools.Count ? Doc.Tools[e.Tool.Value] : null;
                if (q == null || q.Shape != "round" || (q.D ?? 0) == 0) { err(e.Line, "T" + (e.Tool + 1) + " は丸の金型ではありません"); return; }
                if (!NearC(q.D.Value, td)) { err(e.Line, "金型直径 " + Fmt(td) + " が T" + (e.Tool + 1) + "（" + Model.ToolText(q) + "）と合いません"); return; }
                if (!(d > q.D.Value + 1e-6)) { err(e.Line, "直径は金型直径 " + Fmt(q.D.Value) + " より大きくしてください"); return; }
                double t = NibbleT();
                if (!NearC(th, t)) { err(e.Line, "板厚 " + Fmt(th) + " が図面の板厚 " + Fmt(t) + (Doc.Sheet.T != null ? "" : "（未設定なので 1.6）") + " と合いません（板厚は上の「t」で決めてください）"); return; }
                if (pitch < t - 1e-9 || pitch > 10 + 1e-9) { err(e.Line, "ピッチは板厚 " + Fmt(t) + " 以上 10 以下にしてください"); return; }
                if (pitch > (d - q.D.Value) / 2 + 1e-9) { err(e.Line, "ピッチは " + Fmt(Round((d - q.D.Value) / 2)) + "（直径と金型直径の差の半分）以下にしてください"); return; }
                double cx = ctr[0], cy = -ctr[1];
                if (e.Mac != null) e.Mac.Hits.Add(new PHit { Tool = e.Tool, X0 = cx - d / 2, Y0 = cy - d / 2, X1 = cx + d / 2, Y1 = cy + d / 2, Macro = "F5", Pitch = pitch });
            };
            Action<PF23> finishF23 = f =>
            {
                int no = (int)f.Vals[0][0];
                double[] org = f.Vals[1], pitch = f.Vals[2], cnt = f.Vals[3];
                if (!r.Macros.ContainsKey(no)) { err(f.Line, "マクロ " + no + "（F" + no + "）がありません"); return; }
                if (r.Places.Any(p => p.No == no)) { err(f.Line, "マクロ " + no + " を 2 回配置しています"); return; }
                int k = -1;
                for (int i = 0; i < Info.Outers.Count && k < 0; i++)
                    if (NearC(SheetPos(i, "x"), org[0]) && NearC(SheetPos(i, "y"), org[1])) k = i;
                if (k < 0) { err(f.Line, "板の原点から X" + Fmt(org[0]) + " Y" + Fmt(org[1]) + " にワーク原点がありません（ワークは図面で動かしてください）"); return; }
                if (!(pitch[0] > 0 && pitch[1] > 0)) { err(f.Line, "ピッチは 0 より大きくしてください"); return; }
                if (cnt.Any(n => n < 1 || n > F23_MAX)) { err(f.Line, "列数は 1〜" + F23_MAX + " にしてください"); return; }
                r.Places.Add(new PPlace { No = no, K = k, Px = pitch[0], Py = pitch[1], Nx = (int)cnt[0], Ny = (int)cnt[1], Line = f.Line });
            };
            var rows = (text ?? "").Split('\n');
            for (int idx = 0; idx < rows.Length; idx++)
            {
                int line = idx + 1;
                string norm = rows[idx].TrimEnd('\r').Normalize(System.Text.NormalizationForm.FormKC);  // 全角の「；」もコメント
                int sc = norm.IndexOf(';');
                string body = System.Text.RegularExpressions.Regex.Replace((sc >= 0 ? norm.Substring(0, sc) : norm).ToUpperInvariant(), "\\s+", " ").Trim();
                if (body == "") continue;
                string c = body.Replace(" ", "");
                System.Text.RegularExpressions.Match m;
                if (exp != null && exp.Macro == "F5")
                {
                    int step = exp.Vals.Count;
                    if (step == 0)
                    {
                        m = RE_XY.Match(c);
                        if (!m.Success) { err(line, "中心の X Y を書いてください（例: X50 Y20）"); exp = null; continue; }
                        exp.Vals.Add(new[] { PNum(m.Groups[1].Value), PNum(m.Groups[2].Value) });
                    }
                    else
                    {
                        m = RE_N.Match(c);
                        if (!m.Success) { err(line, new[] { "直径", "ピッチ", "金型直径", "板厚" }[step - 1] + "を数字で書いてください"); exp = null; continue; }
                        exp.Vals.Add(new[] { PNum(m.Groups[1].Value) });
                    }
                    if (exp.Vals.Count == 5) { finishF5(exp); exp = null; }
                    continue;
                }
                if (exp != null)
                {
                    int step = exp.Vals.Count;
                    if (exp.Macro == "F13" && step == 3)
                    {
                        m = RE_R.Match(c);
                        if (!m.Success) { err(line, "角 R（R0 など）を書いてください"); exp = null; continue; }
                        exp.Vals.Add(new[] { PNum(m.Groups[1].Value) });
                    }
                    else if (step == 2 && c == "??") exp.Vals.Add(null);  // 入る金型がない
                    else
                    {
                        m = RE_XY.Match(c);
                        if (!m.Success)
                        {
                            err(line, new[] { "開始点", exp.Macro == "F13" ? "外形" : "寸法", "金型" }[step] + "の X Y を書いてください（例: X50 Y20）");
                            exp = null;
                            continue;
                        }
                        exp.Vals.Add(new[] { PNum(m.Groups[1].Value), PNum(m.Groups[2].Value) });
                    }
                    if (exp.Vals.Count == (exp.Macro == "F13" ? 4 : 3)) { finishHit(exp); exp = null; }
                    continue;
                }
                if (f23 != null)
                {
                    int step = f23.Vals.Count;
                    if (step == 0)
                    {
                        if (!System.Text.RegularExpressions.Regex.IsMatch(c, "^[0-9]+$")) { err(line, "マクロ番号を書いてください（例: 100）"); f23 = null; continue; }
                        f23.Vals.Add(new[] { PNum(c) });
                    }
                    else if (step < 3)
                    {
                        m = RE_XY.Match(c);
                        if (!m.Success) { err(line, (step == 1 ? "ワーク原点" : "ピッチ") + "の X Y を書いてください"); f23 = null; continue; }
                        f23.Vals.Add(new[] { PNum(m.Groups[1].Value), PNum(m.Groups[2].Value) });
                    }
                    else
                    {
                        m = System.Text.RegularExpressions.Regex.Match(body, "^([0-9]+) ([0-9]+)$");
                        if (!m.Success) { err(line, "列数を 2 つの整数で書いてください（例: 2 3）"); f23 = null; continue; }
                        f23.Vals.Add(new[] { PNum(m.Groups[1].Value), PNum(m.Groups[2].Value) });
                    }
                    if (f23.Vals.Count == 4) { finishF23(f23); f23 = null; }
                    continue;
                }
                if ((m = System.Text.RegularExpressions.Regex.Match(c, "^O([0-9]+|-+)$")).Success)
                {
                    if (m.Groups[1].Value.StartsWith("-")) { r.HasProgram = true; r.Program = null; }
                    else
                    {
                        double n = PNum(m.Groups[1].Value);
                        if (n < Model.PROGRAM_MIN || n > Model.PROGRAM_MAX) err(line, "プログラム番号は " + Model.PROGRAM_MIN + "〜" + Model.PROGRAM_MAX + " にしてください");
                        else { r.HasProgram = true; r.Program = (int)n; }
                    }
                }
                else if ((m = System.Text.RegularExpressions.Regex.Match(c, "^T([0-9]+)$")).Success)
                {
                    double n = PNum(m.Groups[1].Value);
                    if (mac != null) err(line, "マクロ（F100〜F110）の中には T を書けません");
                    else if (n < 1 || n > Doc.Tools.Count || Doc.Tools[(int)n - 1].Shape == "none") err(line, "T" + Js.Num(n) + " の金型がありません（「金型」で決めてください）");
                    else tool = (int)n - 1;
                }
                else if ((m = System.Text.RegularExpressions.Regex.Match(c, "^F([0-9]+)$")).Success)
                {
                    double f = PNum(m.Groups[1].Value);
                    if (f == 3 || f == 13)
                    {
                        if (mac == null) err(line, "F" + Js.Num(f) + " は F100〜F110 の中に書いてください");
                        exp = new PExp { Macro = f == 13 ? "F13" : "F3", Line = line, Tool = tool, Mac = mac };
                    }
                    else if (f == 5)
                    {
                        if (mac == null) err(line, "F5 は F100〜F110 の中に書いてください");
                        exp = new PExp { Macro = "F5", Line = line, Tool = tool, Mac = mac };
                    }
                    else if (f >= 100 && f <= 109)
                    {
                        if (mac != null) err(line, "F110 でマクロ " + mac.No + " を閉じてから書いてください");
                        else if (r.Macros.ContainsKey((int)f)) err(line, "マクロ " + Js.Num(f) + " が 2 回あります");
                        else { mac = new PMacro { No = (int)f, Line = line }; r.Macros[(int)f] = mac; r.MacroList.Add(mac); }
                    }
                    else if (f == 110)
                    {
                        if (mac == null) err(line, "F110 の前に F100〜F109 がありません");
                        mac = null;
                    }
                    else if (f == 23)
                    {
                        if (mac != null) err(line, "F23 は F110 のあとに書いてください");
                        f23 = new PF23 { Line = line };
                    }
                    else err(line, "F" + Js.Num(f) + " はここでは使えません（F3・F5・F13・F23・F100〜F110）");
                }
                else if ((m = RE_XY.Match(c)).Success)
                {
                    // マクロの中の X Y だけの行は単発（金型の中心）
                    if (mac == null) { err(line, "単発（X Y）は F100〜F110 の中に書いてください"); continue; }
                    // 長方形の金型か丸の金型（丸穴）
                    var q = tool == null ? null : Doc.Tools[tool.Value];
                    bool isRound = q != null && q.Shape == "round" && (q.D ?? 0) != 0;
                    var t = isRound ? q : rectTool(tool, line);
                    if (t == null) continue;
                    double tw = isRound ? q.D.Value : t.W.Value, th = isRound ? q.D.Value : t.H.Value;
                    double cx = PNum(m.Groups[1].Value), cy = -PNum(m.Groups[2].Value);
                    mac.Hits.Add(new PHit { Tool = tool, X0 = cx - tw / 2, Y0 = cy - th / 2, X1 = cx + tw / 2, Y1 = cy + th / 2, Macro = "単発" });
                }
                else err(line, "読めない行です");
            }
            if (exp != null) err(exp.Line, exp.Macro + " の続きが足りません");
            if (f23 != null) err(f23.Line, "F23 の続きが足りません");
            if (mac != null) err(mac.Line, "マクロ " + mac.No + " を F110 で閉じてください");
            foreach (var q in r.MacroList) if (!r.Places.Any(p => p.No == q.No)) err(q.Line, "マクロ " + q.No + " を配置する F23 がありません");
            return r;
        }

        /// <summary>コードの文（GenCode の行を改行でつないだもの）。</summary>
        public string CodeText(string version) { return string.Join("\n", GenCode(version).Select(l => l.Text)) + "\n"; }

        /// <summary>読んだコードを割付にする。誤りがあればその一覧を返し、何も変えない。変えたら null、変わらなければ空の一覧。</summary>
        public List<CodeError> ApplyCode(string text)
        {
            var plan = PlanCode(text);
            if (plan.Errs != null) return plan.Errs;
            return CommitCode(plan) ? null : new List<CodeError>();
        }

        /// <summary>コードを読んで決めた、変える所。Lay が null のものは、コードから消したワーク（割付を消す）。</summary>
        public class CodePlan
        {
            public List<CodeError> Errs;
            public List<Tuple<int, Layout>> Plans = new List<Tuple<int, Layout>>();
            public bool ProgramChanged; public int? Program;
        }

        /// <summary>コードを読んで、変える所を決める（まだ変えない）。</summary>
        public CodePlan PlanCode(string text)
        {
            var r = ParseCode(text);
            var groups = new Dictionary<int, List<PPlace>>();  // ワーク → そのワークの F23（書いた順）
            foreach (var p in r.Places)
            {
                List<PPlace> g;
                if (!groups.TryGetValue(p.K, out g)) { groups[p.K] = new List<PPlace> { p }; continue; }
                if (!NearC(g[0].Px, p.Px) || !NearC(g[0].Py, p.Py) || g[0].Nx != p.Nx || g[0].Ny != p.Ny)
                    r.Errs.Add(new CodeError { Line = p.Line, Msg = "S" + (p.K + 1) + " の F23 は、ピッチと列数をそろえてください" });
                g.Add(p);
            }
            if (r.Errs.Count > 0) return new CodePlan { Errs = r.Errs.OrderBy(e => e.Line).ToList() };
            Func<Hit, Hit, bool> sameHit = (a, b) => MacroOf(a) == MacroOf(b) && a.Tool == b.Tool && NearC(a.X0, b.X0) && NearC(a.Y0, b.Y0)
                && NearC(a.X1, b.X1) && NearC(a.Y1, b.Y1) && (a.Tool == null || MacroOf(a) != "F13" || NearC(a.Mj ?? 0, b.Mj ?? 0))
                && (MacroOf(a) != "F5" || NearC(a.Pitch ?? 0, b.Pitch ?? 0));
            var plans = new List<Tuple<int, Layout>>();
            for (int k = 0; k < Info.Outers.Count; k++)
            {
                var old = LayoutOf(k);
                List<PPlace> g;
                if (!groups.TryGetValue(k, out g))
                {
                    if (old != null && old.Hits.Count > 0) plans.Add(Tuple.Create(k, (Layout)null));  // コードから消したワークは、割付を消す
                    continue;
                }
                var o = WorkOrigin(k);
                var oldHits = old != null ? HitsOf(old) : new List<Hit>();
                var hits = new List<Hit>();
                foreach (var p in g)
                    foreach (var q in r.Macros[p.No].Hits)
                    {
                        var h = new Hit { Tool = q.Tool, X0 = Round(q.X0 + o.X), Y0 = Round(q.Y0 + o.Y), X1 = Round(q.X1 + o.X), Y1 = Round(q.Y1 + o.Y), Macro = q.Macro };
                        if (q.Macro == "F13") h.Mj = q.Mj;
                        if (q.Macro == "F5") h.Pitch = Round(q.Pitch);
                        // 丸の外形の追い抜きは、ピッチだけ変えても外形の印を残す
                        var was = oldHits.Find(x => sameHit(x, h)) ?? (h.Macro == "F5" ? oldHits.Find(x => x.Kind == "outline" && MacroOf(x) == "F5" && x.Tool == h.Tool
                            && NearC(x.X0, h.X0) && NearC(x.Y0, h.Y0) && NearC(x.X1, h.X1) && NearC(x.Y1, h.Y1)) : null);
                        if (was != null && was.Kind != null) h.Kind = was.Kind;  // 切り欠きの印は、同じ所を抜くものに残す
                        hits.Add(h);
                    }
                bool same = old != null && oldHits.Count == hits.Count && oldHits.Select((x, i) => sameHit(x, hits[i])).All(b => b);
                var pl = g[0];
                // ピッチ: 自動のまま書き換えていなければ自動、自動の値と同じでも自動にする
                var fo = old != null ? F23Of(old) : null;
                double bw = Round(hits.Max(h => h.X1) - hits.Min(h => h.X0) + 10), bh = Round(hits.Max(h => h.Y1) - hits.Min(h => h.Y0) + 10);
                Func<double, double?, double?, double, double?> pitch = (v, oldSet, shown, auto) =>
                    (oldSet == null && fo != null && NearC(v, shown.Value)) || NearC(v, auto) ? (double?)null : Round(v);
                var fr = Info.Outers[k];
                var lay = new Layout
                {
                    Work = old != null ? old.Work : WorkKey(k),
                    Src = old != null ? old.Src : HoleSrc(k),
                    Hits = same ? old.Hits : hits,
                    Warn = same ? old.Warn : new List<string>(),
                    Org = same ? old.Org : new Org { X = fr.Origin.X, Y = fr.Origin.Y },
                    Manual = (same ? old.Manual == true : true) ? true : (bool?)null,
                    Nx = pl.Nx > 1 ? pl.Nx : (int?)null,
                    Ny = pl.Ny > 1 ? pl.Ny : (int?)null,
                    Px = pitch(pl.Px, old?.Px, fo?.Px, bw),
                    Py = pitch(pl.Py, old?.Py, fo?.Py, bh),
                };
                if (old == null || Model.Serialize(new Doc { Layouts = new List<Layout> { lay } }) != Model.Serialize(new Doc { Layouts = new List<Layout> { old } }))
                    plans.Add(Tuple.Create(k, lay));
            }
            return new CodePlan { Plans = plans, ProgramChanged = r.HasProgram && r.Program != Doc.Program, Program = r.Program };
        }

        /// <summary>PlanCode で決めた所を変える。変えたら true。</summary>
        public bool CommitCode(CodePlan plan)
        {
            var plans = plan.Plans;
            if (plans.Count == 0 && !plan.ProgramChanged) return false;
            Snapshot();
            var before = plans.Select(q => WorkOrigin(q.Item1)).ToList();
            foreach (var q in plans)
            {
                int i = Doc.Layouts.FindIndex(x => Owner(x.Work) == q.Item1);
                if (q.Item2 == null) Doc.Layouts.RemoveAt(i);
                else if (i >= 0) Doc.Layouts[i] = q.Item2;
                else Doc.Layouts.Add(q.Item2);
            }
            for (int i = 0; i < plans.Count; i++) KeepSheetDims(plans[i].Item1, before[i]);
            if (plan.ProgramChanged) Doc.Program = plan.Program;
            LayoutHit = null;
            Changed();
            return true;
        }

        // ---- コード: 機械に手で打ち込むデータとコメントだけを並べる ----
        public List<CodeLine> GenCode(string version)
        {
            var lines = new List<CodeLine>();
            Action<string, string, bool> add = (text, key, comment) => lines.Add(new CodeLine { Text = text, Key = key, Comment = comment });
            Action<string, CodeEdit> addEdit = (text, e) => lines.Add(new CodeLine { Text = text, Edit = e });
            add("; セットプレス CAM v" + version, null, true);
            add("; 図面 " + (Doc.Name != "" ? Doc.Name : "名前なし") + "  板 " + Fmt(Doc.Sheet.W) + " x " + Fmt(Doc.Sheet.H) + " mm", null, true);
            add("; 板厚 " + (Doc.Sheet.T != null ? Fmt(Doc.Sheet.T.Value) + " mm" : "未設定"), null, true);
            add(Model.ProgramText(Doc.Program), null, false);
            for (int i = 0; i < Doc.Tools.Count; i++) add("; T" + (i + 1) + " " + Model.ToolText(Doc.Tools[i]), null, true);
            // ワークごとに F100〜（マクロ番号）と F110 で囲んでマクロに登録し、F23 で板の上の位置へ呼び出す。
            // マクロの中には T 番号を書けないので、T はマクロの前に出し、金型が変わる所でマクロを分ける
            int macroNo = 100;
            foreach (var lay in Doc.Layouts)
            {
                var k = Owner(lay.Work);
                if (k == null) continue;
                var o = WorkOrigin(k.Value);
                var hs = HitsOf(lay);
                add("", null, false);
                add("; S" + (k + 1) + " 割付  T → F100〜F110 でマクロに登録 → F23 で配置。F3: 開始点（左上）→ XY 寸法 → 金型 XY 寸法", null, true);
                if (lay.Src != HoleSrc(k.Value)) add("; ※ 割付のあとで図形が変わりました。もう一度自動割付してください", null, true);
                // 抜ける丸の金型がなくて抜いていない丸穴
                var unp = CirclesOf(k.Value).Where(c =>
                {
                    var p = P(c.C);
                    return !hs.Any(h => CircleHit(h) && NearC(h.X1 - h.X0, c.D) && NearC((h.X0 + h.X1) / 2, p.X) && NearC((h.Y0 + h.Y1) / 2, p.Y));
                }).ToList();
                foreach (var dd in unp.Select(c => c.D).Distinct())
                    add("; ※ 丸穴 φ" + Fmt(dd) + "（" + unp.Count(c => c.D == dd) + " 個）は、抜ける丸の金型がないので抜いていません", null, true);
                if (hs.Count == 0) { add("; 抜く所がありません", null, true); continue; }
                // F23 のピッチ（書き換えていなければ 金型が抜く形を囲む四角 + 10 mm）と列数
                var f = F23Of(lay);
                if (f.X0 < -1e-6 || f.Y1 > Doc.Sheet.H + 1e-6 || f.X1 + (f.Nx - 1) * f.Px > Doc.Sheet.W + 1e-6 || f.Y0 - (f.Ny - 1) * f.Py < -1e-6)
                    add("; ※ 多数個取りの並びが板からはみ出します", null, true);
                if (MinToolX(lay) < X_MIN - 1e-6) add("; ※ " + XMinWarn(MinToolX(lay)), null, true);
                // ピッチを抜く形より狭めて重ねるのは、材料を節約するためにわざとすることがあるので注意しない（プレビューで目で確かめる）
                bool pitchAuto = lay.Px == null && lay.Py == null;
                Func<double, double, string, string, string> xyi = (x, y, note, ind) => ind + ("X" + Fmt(x)).PadRight(8) + ("Y" + Fmt(y)).PadRight(8) + "; " + note;
                Func<double, double, string, string> xy = (x, y, note) => xyi(x, y, note, "  ");
                Func<double, double, string, string> mxy = (x, y, note) => xyi(x, y, note, "    ");
                Action<string, string, bool> madd = (text, key, comment) => add("  " + text, key, comment);  // マクロの中は 1 段下げる
                // 同じ金型が続くまとまりごとに、T → マクロ → F23 を出す
                // （入る金型がない所は、金型を替えないのでいまのまとまりに入れる）
                int? curTool = null;
                for (int s0 = 0; s0 < hs.Count;)
                {
                    int s1 = s0;
                    int? tool = null;
                    for (; s1 < hs.Count; s1++)
                    {
                        var tt = hs[s1].Tool;
                        if (tt == null) continue;
                        if (tool == null) tool = tt; else if (tt != tool) break;
                    }
                    int mno = macroNo++;
                    if (tool != null && tool != curTool) { add("T" + (tool + 1), null, false); curTool = tool; }
                    add("F" + mno, null, false);
                    for (int i = s0; i < s1; i++)
                    {
                        var h = hs[i];
                        var t = ToolOf(h);
                        string hk = HitKey(lay.Work, i);
                        bool f13 = MacroOf(h) == "F13";
                        bool single = MacroOf(h) == "単発";
                        if (MacroOf(h) == "F5")
                        {
                            if (h.Kind == "outline") madd("; " + (i + 1) + " 外形 φ" + Fmt(Round(h.X1 - h.X0 - 2 * (t != null ? t.D ?? 0 : 0))) + "  F5 追い抜き（金型が外形の外側を通るように、直径 = 外形 + 金型直径 × 2）: 中心 XY → 直径 → ピッチ → 金型直径 → 板厚", hk, true);
                            else madd("; " + (i + 1) + "  丸穴 φ" + Fmt(Round(h.X1 - h.X0)) + "  F5 追い抜き: 中心 XY → 直径 → ピッチ（円同士の直線距離）→ 金型直径 → 板厚", hk, true);
                            madd("F5", hk, false);
                            add(mxy((h.X0 + h.X1) / 2 - o.X, o.Y - (h.Y0 + h.Y1) / 2, "中心"), hk, false);
                            Action<double, string> nl = (v, note) => add("    " + Fmt(v).PadRight(16) + "; " + note, hk, false);
                            nl(Round(h.X1 - h.X0), "直径");
                            nl(h.Pitch ?? 0, "ピッチ");
                            nl(t != null ? t.D ?? 0 : 0, "金型直径");
                            nl(NibbleT(), Doc.Sheet.T != null ? "板厚" : "板厚（未設定なので 1.6）");
                            continue;
                        }
                        madd(f13 ? "; " + (i + 1) + " 外形  F13: 開始点（左上）→ 外形 XY → 金型 XY → 角 R（金型寸法のぶん外側）"
                            : "; " + (i + 1) + (h.Kind == "notch" ? " 切り欠き" : "") + (RoundHit(h) ? "  丸穴 φ" + Fmt(t.D ?? 0) : "") + (single ? "  単発: 金型の中心" : ""), hk, true);
                        if (f13)
                        {
                            var singles = F13Singles(h);
                            if (singles.Count > 0) madd("; 単発（短いほうの辺のつながりを、ミクロジョイントの長さまで先に抜く。F13 の帯と約 1/3 重ねる）  金型の中心", hk, true);
                            foreach (var q in singles) add(mxy((q.X0 + q.X1) / 2 - o.X, o.Y - (q.Y0 + q.Y1) / 2, "単発 中心"), hk, false);
                        }
                        if (single) { add(mxy((h.X0 + h.X1) / 2 - o.X, o.Y - (h.Y0 + h.Y1) / 2, "単発 中心"), hk, false); continue; }
                        madd(MacroOf(h), hk, false);
                        add(mxy(h.X0 - o.X, o.Y - h.Y1, "開始点"), hk, false);
                        add(mxy(h.X1 - h.X0, h.Y1 - h.Y0, f13 ? "外形" : "寸法"), hk, false);
                        add(t != null && t.W != null && t.H != null ? mxy(t.W.Value, t.H.Value, "金型") : "    ?       ?       ; 金型（入る長方形の金型がない）", hk, false);
                        if (f13) add("    " + ("R" + Fmt(F13R(h))).PadRight(16) + "; 角 R" + ((h.Mj ?? 0) != 0 ? "（金型 + ミクロジョイント " + Fmt(h.Mj.Value) + "）" : ""), hk, false);
                    }
                    add("F110", null, false);
                    // F23: マクロ番号 → 板の原点（左上）からワーク原点までの XY → ピッチ XY → 列数 XY
                    add("; F23: マクロ番号 → 板の原点からワーク原点まで XY → ピッチ XY（抜く形 + 10）→ 列数 XY（ピッチ・列数は書き換えられる）", null, true);
                    add("F23", null, false);
                    add("  " + mno.ToString().PadRight(16) + "; マクロ番号", null, false);
                    add(xy(SheetPos(k.Value, "x"), SheetPos(k.Value, "y"), "ワーク原点"), null, false);
                    string pnote = pitchAuto ? "ピッチ" : "ピッチ（書き換えた値。空にすると自動）";
                    addEdit(xy(f.Px, f.Py, pnote), new CodeEdit
                    {
                        Work = lay.Work, Fields = new[] { "px", "py" }, Vals = new[] { Fmt(f.Px), Fmt(f.Py) }, Prefix = new[] { "X", "Y" }, Note = pnote,
                    });
                    addEdit("  " + f.Nx.ToString().PadRight(8) + f.Ny.ToString().PadRight(8) + "; 列数", new CodeEdit
                    {
                        Work = lay.Work, Fields = new[] { "nx", "ny" }, Vals = new[] { f.Nx.ToString(), f.Ny.ToString() }, Prefix = new[] { "", "" }, Note = "列数",
                    });
                    s0 = s1;
                }
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
