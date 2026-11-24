// 図を描く欄（app.js の描画とタッチ操作の移植）。
// マウスの左ボタン: 作図ではなぞって描く、寸法・割付ではクリックで選ぶ、移動ではつかんで動かす。
// 右ボタンか中ボタンでドラッグすると画面を動かし、ホイールで拡大縮小する。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace SetPressCam
{
    public class DrawCanvas : Control
    {
        readonly Editor ed;
        public new double Scale = 1;
        public double Ox, Oy;
        public bool Fitted = true;
        public Func<double> SnapStep = () => 5;
        /// <summary>カーソルの位置・動かした量（ステータスバー）。</summary>
        public event Action<string> PosText;
        /// <summary>選んだものが変わった（欄・コードを作り直す）。</summary>
        public event Action SelectionChanged;

        // ドラッグの状態
        class Drag
        {
            public string Kind;  // stroke / press / move / box / pan
            public List<V> Raw;
            public Sel Hit;
            public System.Drawing.Point Start;
            public System.Drawing.Point Cur;  // box: いまのマウスの位置
            public V World;
            // move
            public List<Sel> Items;
            public List<Family> Families;
            public List<DimMove> Dims;
            public List<Point> Orig;
            public V Last;
            // pan
            public double Ox, Oy;
        }

        class Family { public HashSet<int> Pids; public List<Dim> Sheet; public V Origin; }
        class DimMove { public int Id; public double Start; public double? Before; }

        Drag drag;
        readonly List<(int id, RectangleF box)> dimHits = new List<(int, RectangleF)>();
        readonly List<(int id, RectangleF box)> originHits = new List<(int, RectangleF)>();

        static readonly Font F13 = new Font("Yu Gothic UI", 13f, GraphicsUnit.Pixel);
        static readonly Font F12 = new Font("Yu Gothic UI", 12f, GraphicsUnit.Pixel);
        static readonly Font F13B = new Font("Yu Gothic UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
        static readonly Font F12B = new Font("Yu Gothic UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
        static readonly StringFormat TYPO = MakeTypo();

        static StringFormat MakeTypo()
        {
            var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
            sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            return sf;
        }

        static class COLOR
        {
            public static readonly Color Fixed = Ui.Hex("#1d2b38"), Free = Ui.Hex("#2f80d1"), Selected = Ui.Hex("#e8890c"),
                Dim = Ui.Hex("#7a4fb3"), Origin = Ui.Hex("#c0392b"), Fill = Color.FromArgb(46, 47, 128, 209),
                Layout = Ui.Hex("#1f8a4c"), Missing = Ui.Hex("#b3261e");
        }

        public DrawCanvas(Editor editor)
        {
            ed = editor;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Ui.Hex("#e9edf1");
            TabStop = true;
        }

        // 画面の拡大率（Windows の表示スケール）。中では Web 版と同じ CSS px で描き・測る
        static readonly float K = Ui.K;
        double CW { get { return Width / K; } }
        double CH { get { return Height / K; } }
        static int Mx(MouseEventArgs e) { return (int)Math.Round(e.X / K); }
        static int My(MouseEventArgs e) { return (int)Math.Round(e.Y / K); }
        static System.Drawing.Point Mp(MouseEventArgs e) { return new System.Drawing.Point(Mx(e), My(e)); }

        public PointF ToScreen(double x, double y) { return new PointF((float)(Ox + x * Scale), (float)(Oy - y * Scale)); }
        public V ToWorld(double sx, double sy) { return new V((sx - Ox) / Scale, (Oy - sy) / Scale); }

        public void FitView()
        {
            const double m = 40;
            var sh = ed.Doc.Sheet;
            double s = Math.Min((CW - 2 * m) / sh.W, (CH - 2 * m) / sh.H);
            Scale = Math.Max(s, 0.01);
            Ox = (CW - sh.W * Scale) / 2;
            Oy = (CH + sh.H * Scale) / 2;
            Fitted = true;
            Invalidate();
        }

        SizeF lastSize;
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (CW == 0 || CH == 0) return;
            if (lastSize.Width == 0 || Fitted) FitView();
            else { Ox += (CW - lastSize.Width) / 2.0; Oy += (CH - lastSize.Height) / 2.0; }
            lastSize = new SizeF((float)CW, (float)CH);
        }

        // ---- 描画 ----
        double GridStep()
        {
            var steps = new[] { 0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000 };
            foreach (var s in steps) if (s * Scale >= 8) return s;
            return 1000;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.ScaleTransform(K, K);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var doc = ed.Doc;
            double w = doc.Sheet.W, h = doc.Sheet.H;
            var tl = ToScreen(0, h);
            float sw = (float)(w * Scale), sh = (float)(h * Scale);
            g.FillRectangle(Brushes.White, tl.X, tl.Y, sw, sh);
            // グリッド（板の左上を基準にする）
            double step = GridStep(), major = step * 10;
            Func<double, bool> isMajor = v => Math.Abs(v / major - Js.MathRound(v / major)) < 1e-6;
            g.SmoothingMode = SmoothingMode.None;
            using (var pMajor = new Pen(Ui.Hex("#c3ccd6")))
            using (var pMinor = new Pen(Ui.Hex("#eef1f4")))
            {
                // 見えている範囲だけ引く
                int kx0 = Math.Max(0, (int)Math.Floor((-Ox) / Scale / step)), kx1 = (int)Math.Ceiling((CW - Ox) / Scale / step);
                for (int k = kx0; k * step <= w + 1e-9 && k <= kx1; k++)
                {
                    float sx = (float)Math.Round(ToScreen(k * step, 0).X);
                    g.DrawLine(isMajor(k * step) ? pMajor : pMinor, sx, tl.Y, sx, tl.Y + sh);
                }
                int ky0 = Math.Max(0, (int)Math.Floor((Oy - CH) / Scale / step) - 1), ky1 = (int)Math.Ceiling(Oy / Scale / step) + 1;
                for (int k = 0; k * step <= h + 1e-9; k++)
                {
                    double y = h - k * step;  // 板の左上から下へ
                    double fromBottom = y / step;
                    if (fromBottom < ky0 - 1 || fromBottom > ky1 + 1) continue;
                    float sy = (float)Math.Round(ToScreen(0, y).Y);
                    g.DrawLine(isMajor(k * step) ? pMajor : pMinor, tl.X, sy, tl.X + sw, sy);
                }
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Ui.Hex("#4a5a6a"), 2)) g.DrawRectangle(p, tl.X, tl.Y, sw, sh);
            string label = "板 " + Js.Num(w) + " × " + Js.Num(h) + " mm" + (doc.Sheet.T != null ? "  板厚 " + Js.Num(doc.Sheet.T.Value) + " mm" : "");
            using (var b = new SolidBrush(Ui.Hex("#4a5a6a")))
            {
                var sz = g.MeasureString(label, F13, PointF.Empty, TYPO);
                g.DrawString(label, F13, b, tl.X + sw - sz.Width, tl.Y - 8 - sz.Height, TYPO);
            }
            DrawSheetOrigin(g);
            DrawFills(g);
            DrawCopies(g);
            if (ed.Mode == "layout") DrawLayout(g);
            dimHits.Clear();
            originHits.Clear();
            var levels = LayoutDims();
            for (int i = 0; i < doc.Dims.Count; i++)
            {
                if (doc.Dims[i].Kind == "sheet") DrawSheetDim(g, doc.Dims[i]);
                else DrawDim(g, doc.Dims[i], levels[i] ?? 0);
            }
            foreach (var l in doc.Lines) DrawLine(g, l);
            foreach (var c in ed.Circles) DrawCircle(g, c);
            foreach (var p in doc.Points) DrawPoint(g, p);
            for (int k = 0; k < ed.Info.Outers.Count; k++) DrawOrigin(g, ed.WorkOrigin(k), k);
            if (drag != null && drag.Kind == "stroke") DrawStroke(g);
            if (drag != null && drag.Kind == "box") DrawBox(g);
        }

        GraphicsPath PolyPath(List<V> poly)
        {
            var path = new GraphicsPath();
            path.AddPolygon(poly.Select(v => ToScreen(v.X, v.Y)).ToArray());
            return path;
        }

        // 閉じた図形の内側を半透明で塗る。外形を塗ってから、穴・切り欠きの形で抜く
        void DrawFills(Graphics g)
        {
            var shapes = ed.Shapes;
            if (!shapes.Any(s => s.Role == "outer")) return;
            using (var region = new Region(new GraphicsPath()))
            {
                foreach (var s in shapes) if (s.Role == "outer") using (var p = PolyPath(s.Poly)) region.Union(p);
                foreach (var s in shapes) if (s.Role != "outer") using (var p = PolyPath(s.Poly)) region.Exclude(p);
                foreach (var c in ed.Circles)
                {
                    var cp = ed.P(c.C); var sc = ToScreen(cp.X, cp.Y); float r = (float)(c.D / 2 * Scale);
                    using (var p = new GraphicsPath()) { p.AddEllipse(sc.X - r, sc.Y - r, 2 * r, 2 * r); region.Exclude(p); }
                }
                using (var b = new SolidBrush(COLOR.Fill)) g.FillRegion(b, region);
            }
        }

        void DrawLine(Graphics g, Line l)
        {
            var a = ed.P(l.A); var b = ed.P(l.B);
            bool s = ed.IsSel("line", l.Id);
            var col = s ? COLOR.Selected : ed.Info.FixedLine(l) ? COLOR.Fixed : COLOR.Free;
            using (var p = new Pen(col, s ? 4f : 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(p, ToScreen(a.X, a.Y), ToScreen(b.X, b.Y));
        }

        // 丸（丸穴）: 円と、右上に直径 φ。中心の点を選ぶと丸ごと選んだことになる
        void DrawCircle(Graphics g, Circle c)
        {
            var cp = ed.P(c.C);
            var sc = ToScreen(cp.X, cp.Y);
            float r = (float)(c.D / 2 * Scale);
            bool s = ed.IsSel("point", c.C);
            var col = s ? COLOR.Selected : ed.Info.FixedPoint(c.C) ? COLOR.Fixed : COLOR.Free;
            using (var p = new Pen(col, s ? 4f : 2.5f)) g.DrawEllipse(p, sc.X - r, sc.Y - r, 2 * r, 2 * r);
            string t = "φ" + Editor.Fmt(c.D);
            var sz = g.MeasureString(t, F13, PointF.Empty, TYPO);
            using (var b = new SolidBrush(s ? COLOR.Selected : COLOR.Dim)) g.DrawString(t, F13, b, sc.X + r * 0.71f + 3, sc.Y - r * 0.71f - 2 - sz.Height, TYPO);
        }

        void DrawPoint(Graphics g, Point pt)
        {
            var c = ToScreen(pt.X, pt.Y);
            bool s = ed.IsSel("point", pt.Id);
            bool fx = ed.Info.FixedPoint(pt.Id);
            float r = s ? 7 : 5;
            using (var b = new SolidBrush(s ? COLOR.Selected : fx ? COLOR.Fixed : Color.White)) g.FillEllipse(b, c.X - r, c.Y - r, 2 * r, 2 * r);
            using (var p = new Pen(s ? COLOR.Selected : fx ? COLOR.Fixed : COLOR.Free, 2)) g.DrawEllipse(p, c.X - r, c.Y - r, 2 * r, 2 * r);
        }

        // 外形の原点（ワーク原点）。名前は印の右上に書く
        void DrawOrigin(Graphics g, V o, int k)
        {
            var c = ToScreen(o.X, o.Y);
            using (var p = new Pen(COLOR.Origin, 2))
            {
                g.DrawEllipse(p, c.X - 9, c.Y - 9, 18, 18);
                g.DrawLine(p, c.X - 14, c.Y, c.X + 14, c.Y);
                g.DrawLine(p, c.X, c.Y - 14, c.X, c.Y + 14);
            }
            string label = "S" + (k + 1) + " 原点";
            bool sl = ed.IsSel("origin", k);
            var sz = g.MeasureString(label, F13B, PointF.Empty, TYPO);
            using (var b = new SolidBrush(sl ? COLOR.Selected : COLOR.Origin)) g.DrawString(label, F13B, b, c.X + 12, c.Y - 10 - sz.Height + 2, TYPO);
            if (sl) using (var p = new Pen(COLOR.Selected, 2)) g.DrawEllipse(p, c.X - 13, c.Y - 13, 26, 26);
            originHits.Add((k, new RectangleF(c.X + 8, c.Y - 26, sz.Width + 8, 22)));
        }

        void DrawSheetOrigin(Graphics g)
        {
            var c = ToScreen(0, ed.Doc.Sheet.H);
            using (var p = new Pen(COLOR.Origin, 1.5f))
            {
                g.DrawLine(p, c.X - 8, c.Y, c.X + 8, c.Y);
                g.DrawLine(p, c.X, c.Y - 8, c.X, c.Y + 8);
            }
        }

        // 板の原点からの寸法線と板の辺との間（mm）
        public double SheetGap(Dim d)
        {
            if (d.Off != null) return d.Off.Value;
            int n = ed.Doc.Dims.Count(q => q.Kind == "sheet" && q.Axis == d.Axis &&
                (Math.Abs(q.Value) < Math.Abs(d.Value) || (q.Value == d.Value && q.Id < d.Id)));
            return (22 + 26 * n) / Scale;
        }

        void DrawSheetDim(Graphics g, Dim d)
        {
            var k = ed.Owner(d.B);
            if (k == null) return;
            var o = ed.WorkOrigin(k.Value);
            double gap = SheetGap(d), h = ed.Doc.Sheet.H;
            V p1, p2, e;
            if (d.Axis == "x") { p1 = new V(0, h + gap); p2 = new V(o.X, h + gap); e = o; }
            else { p1 = new V(-gap, h); p2 = new V(-gap, o.Y); e = o; }
            Dimension(g, d, S(p1), S(p2), S(new V(0, h)), S(e), ed.IsSel("dim", d.Id));
        }

        PointF S(V v) { return ToScreen(v.X, v.Y); }

        static double DimShown(Dim d)
        {
            if (d.Kind == "sheet") return d.Value;
            return d.Kind == "o" ? (d.Axis == "y" ? -d.Value : d.Value) : Math.Abs(d.Value);
        }

        // 寸法線を置く位置（寸法線と直角な方向の座標、mm）を決める（app.js の layoutDims）
        public List<double?> LayoutDims()
        {
            var doc = ed.Doc;
            var pts = doc.Points;
            if (pts.Count == 0) return doc.Dims.Select(d => (double?)null).ToList();
            double minY = pts.Min(p => p.Y), maxX = pts.Max(p => p.X);
            double step = 30 / Scale;
            Func<Dim, bool> onEdge = d =>
            {
                if (d.Kind != "len") return false;
                var l = ed.L(d.Refs[0].Id);
                return l.Dir == "h" ? Math.Abs(ed.P(l.A).Y - minY) < 1e-6 : Math.Abs(ed.P(l.A).X - maxX) < 1e-6;
            };
            Func<Dim, bool> stacked = d => d.Kind != "sheet" && (d.Kind != "len" || onEdge(d));
            var rank = new Dictionary<int, int>();
            foreach (var axis in new[] { "x", "y" })
            {
                var list = doc.Dims.Where(d => d.Axis == axis && stacked(d)).ToList();
                Js.Sort(list, (p, q) => Math.Abs(p.Value) != Math.Abs(q.Value) ? Js.Cmp(Math.Abs(p.Value) - Math.Abs(q.Value)) : p.Id - q.Id);
                for (int i = 0; i < list.Count; i++) rank[list[i].Id] = i + 1;
            }
            return doc.Dims.Select(d =>
            {
                if (d.Kind == "sheet") return (double?)null;
                if (d.Off != null) return (d.Axis == "x" ? ed.P(d.A).Y : ed.P(d.A).X) + d.Off.Value;
                if (d.Kind == "len" && !rank.ContainsKey(d.Id))
                {
                    var l = ed.L(d.Refs[0].Id);
                    bool acrossY = l.Dir == "h";
                    Func<Point, double> ac = p => acrossY ? p.Y : p.X;
                    double here = ac(ed.P(l.A));
                    int side = 0;
                    foreach (var m in doc.Lines)
                    {
                        if (m.Id == l.Id) continue;
                        foreach (var (u, v) in new[] { (m.A, m.B), (m.B, m.A) })
                            if (u == l.A || u == l.B) side += Math.Sign(ac(ed.P(v)) - here);
                    }
                    int outSide = side > 0 ? -1 : 1;
                    return here + outSide * 22 / Scale;
                }
                int n = rank[d.Id];
                return d.Axis == "x" ? minY - (16 / Scale + step * n) : maxX + (16 / Scale + step * n);
            }).ToList();
        }

        double RefNear(Ref r, string across, double level)
        {
            Func<Point, double> ac = p => across == "x" ? p.X : p.Y;
            if (r.T == "point") return ac(ed.P(r.Id));
            var l = ed.L(r.Id);
            double a = ac(ed.P(l.A)), b = ac(ed.P(l.B));
            return Math.Abs(a - level) < Math.Abs(b - level) ? a : b;
        }

        void DrawDim(Graphics g, Dim d, double level)
        {
            string ax = d.Axis, across = ax == "x" ? "y" : "x";
            double va = ax == "x" ? ed.P(d.A).X : ed.P(d.A).Y, vb = ax == "x" ? ed.P(d.B).X : ed.P(d.B).Y;
            var refsAB = d.Kind == "len" ? new[] { d.Refs[0], d.Refs[0] } : d.Refs.ToArray();
            double ea = RefNear(refsAB[0], across, level), eb = RefNear(refsAB[1], across, level);
            V p1, p2, e1, e2;
            if (ax == "x") { p1 = new V(va, level); p2 = new V(vb, level); e1 = new V(va, ea); e2 = new V(vb, eb); }
            else { p1 = new V(level, va); p2 = new V(level, vb); e1 = new V(ea, va); e2 = new V(eb, vb); }
            Dimension(g, d, S(p1), S(p2), S(e1), S(e2), ed.IsSel("dim", d.Id));
        }

        // JIS B 0001（機械製図）の寸法記入にならって描く（画面の座標で受け取る）
        const float GAP = 3, OVER = 6, ARROW_L = 9, ARROW_W = 2.4f, TEXT_GAP = 3;

        void Dimension(Graphics g, Dim d, PointF s1, PointF s2, PointF f1, PointF f2, bool selected)
        {
            var col = selected ? COLOR.Selected : COLOR.Dim;
            using (var pen = new Pen(col, 1))
            using (var brush = new SolidBrush(col))
            {
                foreach (var (f, sp) in new[] { (f1, s1), (f2, s2) })
                {
                    double len0 = Geo.Hypot(sp.X - f.X, sp.Y - f.Y);
                    if (len0 < GAP + 1) continue;
                    float ux0 = (float)((sp.X - f.X) / len0), uy0 = (float)((sp.Y - f.Y) / len0);
                    g.DrawLine(pen, f.X + ux0 * GAP, f.Y + uy0 * GAP, sp.X + ux0 * OVER, sp.Y + uy0 * OVER);
                }
                float len = (float)Geo.Hypot(s2.X - s1.X, s2.Y - s1.Y);
                bool vertical = Math.Abs(s2.X - s1.X) < Math.Abs(s2.Y - s1.Y);
                string text = Js.Num(Js.R3(DimShown(d)));
                var tsz = g.MeasureString(text, F13, PointF.Empty, TYPO);
                float tw = tsz.Width, th = 13;
                bool narrow = len < ARROW_L * 2 + 4;
                float ux = 1, uy = 0;
                if (len > 0.01) { ux = (s2.X - s1.X) / len; uy = (s2.Y - s1.Y) / len; } else if (vertical) { ux = 0; uy = 1; }
                if (narrow)
                {
                    float ext = ARROW_L + 8;
                    g.DrawLine(pen, s1.X - ux * ext, s1.Y - uy * ext, s2.X + ux * ext, s2.Y + uy * ext);
                }
                else g.DrawLine(pen, s1, s2);
                if (len > 0.01)
                {
                    if (narrow) { ArrowTip(g, brush, s1, -ux, -uy); ArrowTip(g, brush, s2, ux, uy); }
                    else { ArrowTip(g, brush, s1, ux, uy); ArrowTip(g, brush, s2, -ux, -uy); }
                }
                float cx = (s1.X + s2.X) / 2, cy = (s1.Y + s2.Y) / 2;
                float room = len - 2 * ARROW_L - 4;
                if (tw > room)
                {
                    var far = vertical ? (s1.Y < s2.Y ? s1 : s2) : (s1.X > s2.X ? s1 : s2);
                    float off = (narrow ? ARROW_L + 8 : 0) + tw / 2 + 4;
                    if (vertical) cy = far.Y - off; else cx = far.X + off;
                }
                RectangleF box = vertical
                    ? new RectangleF(cx - TEXT_GAP - th - 2, cy - tw / 2 - 2, th + 4, tw + 4)
                    : new RectangleF(cx - tw / 2 - 2, cy - TEXT_GAP - th - 2, tw + 4, th + 4);
                if (selected) using (var hb = new SolidBrush(Color.FromArgb(46, 232, 137, 12))) g.FillRectangle(hb, box);
                if (vertical)
                {
                    var st = g.Save();
                    g.TranslateTransform(cx - TEXT_GAP, cy);
                    g.RotateTransform(-90);
                    g.DrawString(text, F13, brush, -tw / 2, -th - 1, TYPO);
                    g.Restore(st);
                }
                else g.DrawString(text, F13, brush, cx - tw / 2, cy - TEXT_GAP - th - 1, TYPO);
                dimHits.Add((d.Id, box));
            }
        }

        static void ArrowTip(Graphics g, Brush b, PointF p, float ux, float uy)
        {
            g.FillPolygon(b, new[]
            {
                p,
                new PointF(p.X + ux * ARROW_L - uy * ARROW_W, p.Y + uy * ARROW_L + ux * ARROW_W),
                new PointF(p.X + ux * ARROW_L + uy * ARROW_W, p.Y + uy * ARROW_L - ux * ARROW_W),
            });
        }

        // 「移動」で何もない所からなぞった、選ぶ範囲の四角
        void DrawBox(Graphics g)
        {
            var r = new RectangleF(Math.Min(drag.Start.X, drag.Cur.X), Math.Min(drag.Start.Y, drag.Cur.Y),
                Math.Abs(drag.Cur.X - drag.Start.X), Math.Abs(drag.Cur.Y - drag.Start.Y));
            using (var b = new SolidBrush(Color.FromArgb(26, COLOR.Selected))) g.FillRectangle(b, r);
            using (var p = new Pen(COLOR.Selected, 1.5f) { DashPattern = new[] { 6 / 1.5f, 4 / 1.5f } }) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
        }

        // なぞっている途中: 指の跡（灰色）と、できあがる折れ線（点線）
        void DrawStroke(Graphics g)
        {
            var raw = drag.Raw;
            if (raw.Count < 2) return;
            using (var p = new Pen(Color.FromArgb(128, 138, 150, 163), 6) { LineJoin = LineJoin.Round })
                g.DrawLines(p, raw.Select(v => ToScreen(v.X, v.Y)).ToArray());
            var cr = ed.CircleResult(raw, Scale, SnapStep());
            if (cr != null)
            {
                // 丸になる: できあがる円（点線）と直径
                var sc = ToScreen(cr.X, cr.Y);
                float r = (float)(cr.D / 2 * Scale);
                using (var p = new Pen(COLOR.Free, 2.5f) { DashPattern = new[] { 7 / 2.5f, 5 / 2.5f } }) g.DrawEllipse(p, sc.X - r, sc.Y - r, 2 * r, 2 * r);
                string t = "φ" + Editor.Fmt(cr.D);
                var sz = g.MeasureString(t, F12, PointF.Empty, TYPO);
                using (var b = new SolidBrush(COLOR.Free)) g.DrawString(t, F12, b, sc.X + r * 0.71f + 3, sc.Y - r * 0.71f - 2 - sz.Height, TYPO);
                return;
            }
            var res = ed.StrokeResult(raw, Scale, SnapStep());
            if (res == null) return;
            using (var p = new Pen(COLOR.Free, 2.5f) { DashPattern = new[] { 7 / 2.5f, 5 / 2.5f } })
                g.DrawLines(p, res.Select(v => ToScreen(v.X, v.Y)).ToArray());
            using (var b = new SolidBrush(COLOR.Free))
                for (int i = 1; i < res.Count; i++)
                {
                    var a = res[i - 1]; var c = res[i];
                    var m = ToScreen((a.X + c.X) / 2, (a.Y + c.Y) / 2);
                    string t = Js.Num(Js.R3(Math.Abs(c.X - a.X) + Math.Abs(c.Y - a.Y)));
                    var sz = g.MeasureString(t, F12, PointF.Empty, TYPO);
                    g.DrawString(t, F12, b, m.X + 10 - sz.Width / 2, m.Y - 8 - sz.Height + 2, TYPO);
                }
        }

        RectangleF ScreenRect(double x0, double y0, double x1, double y1)
        {
            var a = ToScreen(x0, y1); var b = ToScreen(x1, y0);
            return new RectangleF(a.X, a.Y, b.X - a.X, b.Y - a.Y);
        }

        // 割付の表示: 選んだ外形を太く縁取り、抜く長方形（破線）と、開始点（左上）に置く金型（塗り）を描く
        // F23 の多数個取りのプレビュー: 2 個目からの位置に、ワークの線と抜く範囲を点線で描く（板からはみ出すものは赤）
        static readonly Font F11 = new Font("Yu Gothic UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
        void DrawCopies(Graphics g)
        {
            var doc = ed.Doc;
            foreach (var lay in doc.Layouts)
            {
                var k = ed.Owner(lay.Work);
                if (k == null || lay.Hits.Count == 0) continue;
                var f = ed.F23Of(lay);
                if (f.Nx * f.Ny <= 1) continue;
                var ls = doc.Lines.Where(l => ed.Owner(l.A) == k && ed.Owner(l.B) == k).ToList();
                for (int j = 0; j < f.Ny; j++)
                    for (int i = 0; i < f.Nx; i++)
                    {
                        if (i == 0 && j == 0) continue;
                        double dx = i * f.Px, dy = -j * f.Py;  // Y は下へ並べる
                        var r = ScreenRect(f.X0 + dx, f.Y0 + dy, f.X1 + dx, f.Y1 + dy);
                        bool o = f.X0 + dx < -1e-6 || f.X1 + dx > doc.Sheet.W + 1e-6 || f.Y0 + dy < -1e-6 || f.Y1 + dy > doc.Sheet.H + 1e-6;
                        using (var b = new SolidBrush(o ? Color.FromArgb(26, 200, 60, 40) : Color.FromArgb(18, 47, 128, 209))) g.FillRectangle(b, r);
                        using (var p = new Pen(o ? Color.FromArgb(179, 200, 60, 40) : Color.FromArgb(115, 47, 128, 209), 1f) { DashPattern = new[] { 6f, 4f } })
                            g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                        var col = o ? Color.FromArgb(217, 200, 60, 40) : Color.FromArgb(204, 47, 128, 209);
                        using (var p = new Pen(col, 1.5f) { DashPattern = new[] { 6 / 1.5f, 4 / 1.5f } })
                        {
                            foreach (var l in ls)
                            {
                                var a = ed.P(l.A); var c = ed.P(l.B);
                                g.DrawLine(p, ToScreen(a.X + dx, a.Y + dy), ToScreen(c.X + dx, c.Y + dy));
                            }
                            foreach (var c in ed.CirclesOf(k.Value))
                            {
                                var cp = ed.P(c.C); var sc = ToScreen(cp.X + dx, cp.Y + dy); float rr = (float)(c.D / 2 * Scale);
                                g.DrawEllipse(p, sc.X - rr, sc.Y - rr, 2 * rr, 2 * rr);
                            }
                        }
                        using (var b = new SolidBrush(col)) g.DrawString("S" + (k + 1) + "-" + (j * f.Nx + i + 1), F11, b, r.X + 3, r.Y + 2, TYPO);
                    }
            }
        }

        void DrawLayout(Graphics g)
        {
            var k = ed.LayoutWork;
            if (k != null && k.Value < ed.Info.Outers.Count)
            {
                var sh = ed.Shapes[ed.Info.Outers[k.Value].Shape];
                using (var p = new Pen(Color.FromArgb(115, COLOR.Selected), 6) { LineJoin = LineJoin.Round })
                using (var path = PolyPath(sh.Poly)) g.DrawPath(p, path);
            }
            foreach (var lay in ed.Doc.Layouts)
            {
                if (ed.Owner(lay.Work) == null) continue;
                var hs = ed.HitsOf(lay);
                for (int i = 0; i < hs.Count; i++)
                {
                    var h = hs[i];
                    var r = ScreenRect(h.X0, h.Y0, h.X1, h.Y1);
                    var col = h.Tool == null ? COLOR.Missing : COLOR.Layout;
                    bool round1 = ed.RoundHit(h);
                    using (var p = new Pen(col, 1.5f) { DashPattern = new[] { 5 / 1.5f, 3 / 1.5f } })
                    {
                        if (Editor.MacroOf(h) == "F13" && (h.Mj ?? 0) != 0)
                            foreach (var q in ed.F13Strips(h).Concat(ed.F13Singles(h)))
                            {
                                var qr = ScreenRect(q.X0, q.Y0, q.X1, q.Y1);
                                g.DrawRectangle(p, qr.X, qr.Y, qr.Width, qr.Height);
                            }
                        else if (round1) g.DrawEllipse(p, r.X, r.Y, r.Width, r.Height);
                        else g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                    }
                    var t = h.Tool == null ? null : ed.Doc.Tools[h.Tool.Value];
                    if (round1)
                    {
                        float rr = (float)(t.D.Value / 2 * Scale), cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                        using (var b = new SolidBrush(Color.FromArgb(71, 31, 138, 76))) g.FillEllipse(b, cx - rr, cy - rr, 2 * rr, 2 * rr);
                    }
                    else if (t != null && t.W != null && t.H != null)
                        using (var b = new SolidBrush(Color.FromArgb(71, 31, 138, 76))) g.FillRectangle(b, r.X, r.Y, (float)(t.W.Value * Scale), (float)(t.H.Value * Scale));
                    using (var b = new SolidBrush(col))
                        g.DrawString(Editor.MacroOf(h) == "F13" ? (i + 1) + " F13" : (i + 1).ToString(), F12B, b, r.X + 3, r.Y + 2, TYPO);
                }
            }
            // ハイライト: 選んだ 1 回で抜く範囲
            var lh = ed.LayoutHit;
            var hl = lh == null ? null : ed.Doc.Layouts.Find(q => q.Work == lh.Work);
            if (hl != null && ed.Owner(hl.Work) != null)
            {
                var hs = ed.HitsOf(hl);
                if (lh.I < hs.Count)
                {
                    var h = hs[lh.I];
                    var r = ScreenRect(h.X0, h.Y0, h.X1, h.Y1);
                    var t = h.Tool == null ? null : ed.Doc.Tools[h.Tool.Value];
                    using (var fill = new SolidBrush(Color.FromArgb(89, 232, 137, 12)))
                    {
                        if (Editor.MacroOf(h) == "F13" && t != null)
                        {
                            using (var p = new Pen(COLOR.Selected, 2))
                                foreach (var q in ed.F13Strips(h).Concat(ed.F13Singles(h)))
                                {
                                    var qr = ScreenRect(q.X0, q.Y0, q.X1, q.Y1);
                                    g.FillRectangle(fill, qr);
                                    g.DrawRectangle(p, qr.X, qr.Y, qr.Width, qr.Height);
                                }
                        }
                        else if (ed.RoundHit(h))
                        {
                            g.FillEllipse(fill, r);
                            using (var p = new Pen(COLOR.Selected, 3)) g.DrawEllipse(p, r.X, r.Y, r.Width, r.Height);
                        }
                        else
                        {
                            g.FillRectangle(fill, r);
                            using (var p = new Pen(COLOR.Selected, 3)) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                        }
                    }
                    string label = Editor.MacroOf(h) + " " + (lh.I + 1);
                    var sz = g.MeasureString(label, F13B, PointF.Empty, TYPO);
                    using (var b = new SolidBrush(COLOR.Selected)) g.DrawString(label, F13B, b, r.X, r.Y - 3 - sz.Height, TYPO);
                }
            }
        }

        // ---- 当たり判定 ----
        public Sel HitTest(double sx, double sy)
        {
            var w = ToWorld(sx, sy);
            foreach (var (id, b) in originHits)
                if (sx >= b.X - 4 && sx <= b.Right + 4 && sy >= b.Y - 4 && sy <= b.Bottom + 4) return new Sel("origin", id);
            var p = ed.NearestPoint(w.X, w.Y, 16, Scale);
            if (p != null) return new Sel("point", p.Id);
            for (int i = dimHits.Count - 1; i >= 0; i--)
            {
                var b = dimHits[i].box;
                if (sx >= b.X - 4 && sx <= b.Right + 4 && sy >= b.Y - 4 && sy <= b.Bottom + 4) return new Sel("dim", dimHits[i].id);
            }
            Line best = null;
            double bd = 12 / Scale;
            foreach (var l in ed.Doc.Lines)
            {
                var a = ed.P(l.A); var b = ed.P(l.B);
                double minx = Math.Min(a.X, b.X), maxx = Math.Max(a.X, b.X), miny = Math.Min(a.Y, b.Y), maxy = Math.Max(a.Y, b.Y);
                double dx = w.X < minx ? minx - w.X : w.X > maxx ? w.X - maxx : 0;
                double dy = w.Y < miny ? miny - w.Y : w.Y > maxy ? w.Y - maxy : 0;
                double d = Geo.Hypot(dx, dy);
                if (d <= bd) { best = l; bd = d; }
            }
            if (best != null) return new Sel("line", best.Id);
            // 丸: 円の線の近くか内側をクリックすると、中心の点を選ぶ（丸ごと選ぶ）
            foreach (var c in ed.Circles)
            {
                var cp = ed.P(c.C);
                if (Geo.Hypot(w.X - cp.X, w.Y - cp.Y) <= c.D / 2 + 12 / Scale) return new Sel("point", c.C);
            }
            return null;
        }

        // ---- マウス ----
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle)
            {
                if (drag != null && drag.Kind == "move") CancelMove();
                drag = new Drag { Kind = "pan", Start = Mp(e), Ox = Ox, Oy = Oy };
                Capture = true;
                return;
            }
            if (e.Button != MouseButtons.Left || drag != null) return;
            var w = ToWorld(Mx(e), My(e));
            Capture = true;
            if (ed.Mode == "draw") drag = new Drag { Kind = "stroke", Raw = new List<V> { w } };
            else drag = new Drag { Kind = "press", Hit = HitTest(Mx(e), My(e)), Start = Mp(e), World = w };
        }

        void CancelMove()
        {
            ed.Doc = Model.Deserialize(ed.PopUndo());
            ed.Refresh();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var w = ToWorld(Mx(e), My(e));
            {
                double step = SnapStep();
                double sx = ed.SnapAxis(w.X, "x", step), sy = ed.SnapAxis(w.Y, "y", step);
                var k = ed.Info.OuterAt(sx, sy);
                var r = Editor.Rel(ed.OriginAt(sx, sy), sx, sy);
                PosText?.Invoke((k == null ? "板" : "S" + (k + 1)) + "  X " + Js.Num(r.X) + "  Y " + Js.Num(r.Y));
            }
            if (drag == null) return;
            if (drag.Kind == "pan")
            {
                Ox = drag.Ox + (Mx(e) - drag.Start.X);
                Oy = drag.Oy + (My(e) - drag.Start.Y);
                Fitted = false;
                Invalidate();
                return;
            }
            if (drag.Kind == "stroke")
            {
                drag.Raw.Add(w);
                Invalidate();
                return;
            }
            if (drag.Kind == "box")
            {
                drag.Cur = Mp(e);
                Invalidate();
                return;
            }
            if (drag.Kind == "press")
            {
                var h = drag.Hit;
                // 何もない所から 8px なぞったら、四角の枠で選ぶ
                if (ed.Mode == "move" && h == null && Geo.Hypot(Mx(e) - drag.Start.X, My(e) - drag.Start.Y) > 8)
                {
                    drag = new Drag { Kind = "box", Start = drag.Start, Cur = Mp(e) };
                    Invalidate();
                    return;
                }
                if (ed.Mode == "move" && h != null && Geo.Hypot(Mx(e) - drag.Start.X, My(e) - drag.Start.Y) > 8) StartMove(h);
            }
            if (drag != null && drag.Kind == "move") DoMove(w);
        }

        // 「移動」で点か線を押したまま 8px 動いたら、選んだものをまとめて動かし始める
        void StartMove(Sel h)
        {
            if (!ed.IsSel(h.T, h.Id)) ed.Sel = new List<Sel> { h };
            ed.Snapshot();
            var families = ed.Sel.Where(q => q.T == "origin").Select(q => new Family
            {
                Pids = new HashSet<int>(ed.Doc.Points.Where(p => ed.Owner(p.Id) == q.Id).Select(p => p.Id)),
                Sheet = new[] { "x", "y" }.Select(axis => ed.SheetDimOf(q.Id, axis)).Where(d => d != null).ToList(),
                Origin = ed.WorkOrigin(q.Id),
            }).ToList();
            Func<int, bool> inFamily = pid => families.Any(f => f.Pids.Contains(pid));
            var items = ed.Sel.Where(q => q.T == "line" ? !(inFamily(ed.L(q.Id).A) && inFamily(ed.L(q.Id).B))
                : q.T == "point" ? !inFamily(q.Id) : false).Select(q => q.Clone()).ToList();
            var levels = LayoutDims();
            var dims = ed.Sel.Where(q => q.T == "dim" && ed.D(q.Id) != null).Select(q =>
            {
                var d = ed.D(q.Id);
                double start = d.Kind == "sheet" ? SheetGap(d) : (levels[ed.Doc.Dims.IndexOf(d)] ?? 0) - (d.Axis == "x" ? ed.P(d.A).Y : ed.P(d.A).X);
                return new DimMove { Id = d.Id, Start = start, Before = d.Off };
            }).ToList();
            drag = new Drag
            {
                Kind = "move", Items = items, Families = families, Dims = dims, World = drag.World,
                Orig = ed.Doc.Points.Select(p => new Point { Id = p.Id, X = p.X, Y = p.Y }).ToList(), Last = new V(0, 0),
            };
        }

        void DoMove(V w)
        {
            var doc = ed.Doc;
            doc.Points = drag.Orig.Select(p => new Point { Id = p.Id, X = p.X, Y = p.Y }).ToList();
            double s = SnapStep();
            double dx = Editor.Round(Js.MathRound((w.X - drag.World.X) / s) * s);
            double dy = Editor.Round(Js.MathRound((w.Y - drag.World.Y) / s) * s);
            double lx = drag.Last.X, ly = drag.Last.Y;
            if (drag.Items.Count > 0)
                foreach (var (tx, ty) in new[] { (dx, dy), (dx, ly), (lx, dy), (lx, ly) })
                    if (Geo.MoveItems(doc, drag.Items, tx, ty)) { drag.Last = new V(tx, ty); break; }
            foreach (var f in drag.Families)
            {
                foreach (var p in doc.Points) if (f.Pids.Contains(p.Id)) { p.X = Editor.Round(p.X + dx); p.Y = Editor.Round(p.Y + dy); }
                foreach (var d in f.Sheet)
                {
                    var dd = ed.D(d.Id);
                    if (dd != null) dd.Value = Editor.Round(d.Axis == "x" ? f.Origin.X + dx : doc.Sheet.H - (f.Origin.Y + dy));
                }
            }
            double rdx = w.X - drag.World.X, rdy = w.Y - drag.World.Y;
            foreach (var m in drag.Dims)
            {
                var d = ed.D(m.Id);
                if (d == null) continue;
                double delta = d.Kind == "sheet" ? (d.Axis == "x" ? rdy : -rdx) : (d.Axis == "x" ? rdy : rdx);
                d.Off = Editor.Round(m.Start + delta);
            }
            ed.ApplyPlacements();
            ed.Refresh();
            var mv = drag.Items.Count > 0 ? drag.Last : new V(dx, dy);
            PosText?.Invoke("移動  X " + Js.Num(Editor.Round(mv.X)) + "  Y " + Js.Num(Editor.Round(-mv.Y)));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (drag == null) return;
            if (drag.Kind == "pan")
            {
                if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle) { drag = null; Capture = false; }
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            var d = drag;
            drag = null;
            Capture = false;
            if (d.Kind == "stroke")
            {
                ed.CommitStroke(d.Raw, Scale, SnapStep());
                Invalidate();
            }
            else if (d.Kind == "press")
            {
                if (ed.Mode == "dim") ed.TapSelect(d.Hit);
                else if (ed.Mode == "move") ed.TapSelectMany(d.Hit);
                else if (ed.Mode == "layout") ed.TapWork(d.Hit, d.World, Scale);
                SelectionChanged?.Invoke();
            }
            else if (d.Kind == "box")
            {
                var a = ToWorld(Math.Min(d.Start.X, d.Cur.X), Math.Max(d.Start.Y, d.Cur.Y));
                var b = ToWorld(Math.Max(d.Start.X, d.Cur.X), Math.Min(d.Start.Y, d.Cur.Y));
                ed.BoxSelect(a.X, a.Y, b.X, b.Y);
                SelectionChanged?.Invoke();
                Invalidate();
            }
            else if (d.Kind == "move")
            {
                bool movedAny = false;
                for (int i = 0; i < ed.Doc.Points.Count && i < d.Orig.Count; i++)
                    if (ed.Doc.Points[i].X != d.Orig[i].X || ed.Doc.Points[i].Y != d.Orig[i].Y) { movedAny = true; break; }
                if (!movedAny) movedAny = d.Dims.Any(m => { var q = ed.D(m.Id); return q != null && q.Off != m.Before; });
                if (movedAny) ed.Changed();
                else { ed.PopUndo(); ed.Refresh(); }
            }
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            // ほかのウィンドウに取られたら、描きかけ・動かしかけをやめる
            // （WinForms はボタンを離したとき OnMouseUp の前にもキャプチャを外すので、そのメッセージを処理し終えてから確かめる）
            if (Capture || drag == null || !IsHandleCreated) return;
            var d = drag;
            BeginInvoke(new Action(() =>
            {
                if (drag != d || Capture) return;
                if (d.Kind == "move") CancelMove();
                drag = null;
                Invalidate();
            }));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            double k = Math.Exp(e.Delta * 0.0015);
            Ox = Mx(e) - (Mx(e) - Ox) * k;
            Oy = My(e) - (My(e) - Oy) * k;
            Scale *= k;
            Fitted = false;
            Invalidate();
        }
    }
}
