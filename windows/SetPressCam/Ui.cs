// 画面の見た目: Web 版（style.css）と同じ色・文字・角の丸い部品。
// 大きさは Web 版の CSS px で書き、Windows の表示スケール（K）を掛けて実際の画素にする。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace SetPressCam
{
    public static class Ui
    {
        // style.css の :root
        public static readonly Color BAR = Hex("#2b3a4a"), BAR_TEXT = Hex("#f2f4f6"), BTN = Hex("#3d5266"), BTN_ACTIVE = Hex("#e8a33d"),
            PRIMARY = Hex("#2f80d1"), BG = Hex("#e9ecef"), LINE = Hex("#c4ccd4");
        public static readonly Color PANEL = Hex("#fbfcfd"), HEAD = Hex("#dfe4e9"), STATUS = Hex("#d5dbe1"), NOTE = Hex("#667788"),
            DANGER = Hex("#b5473c"), DIALOG_BTN = Hex("#6b7c8c"), WARN = Hex("#b3261e"), OK_TEXT = Hex("#1a6b2f"), DARK = Hex("#1d1d1d"),
            TOOL_ROW = Hex("#f6f8fa"), DIM = Hex("#7a4fb3");

        /// <summary>Windows の表示スケール（100 % = 1）。起動したときの値（アプリはシステムの DPI に合わせる）。</summary>
        public static readonly float K = DpiScale();

        static float DpiScale()
        {
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; } catch (Exception) { return 1f; }
        }

        /// <summary>CSS px を画素に。</summary>
        public static int S(double px) { return (int)Math.Round(px * K); }
        public static Padding P(int all) { return new Padding(S(all)); }
        public static Padding P(int l, int t, int r, int b) { return new Padding(S(l), S(t), S(r), S(b)); }

        // 文字: Web 版の 16px を基準（pt = px × 0.75。pt で作ると Windows が表示スケールを掛ける）
        public const string FAMILY = "Yu Gothic UI";
        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        public static Font F(double px, bool bold = false, string family = FAMILY)
        {
            string key = family + px + bold;
            Font f;
            if (!fonts.TryGetValue(key, out f)) fonts[key] = f = new Font(family, (float)(px * 0.75), bold ? FontStyle.Bold : FontStyle.Regular);
            return f;
        }
        public static Font Font { get { return F(16); } }
        public static Font Bold { get { return F(16, true); } }
        public static Font Mono { get { return F(15, false, "Consolas"); } }

        public static Color Hex(string hex)
        {
            return Color.FromArgb(Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16),
                                  Convert.ToInt32(hex.Substring(5, 2), 16));
        }

        /// <summary>a に b を t の割合で混ぜる（半透明・明るさの代わり）。</summary>
        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        /// <summary>CSS の filter: brightness(k)。</summary>
        public static Color Bright(Color c, double k)
        {
            return Color.FromArgb(Math.Min(255, (int)(c.R * k)), Math.Min(255, (int)(c.G * k)), Math.Min(255, (int)(c.B * k)));
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>角の丸い四角を塗る（縁取りは border が空でなければ）。</summary>
        public static void FillRound(Graphics g, RectangleF r, float rad, Color fill, Color? border = null)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Round(r, rad))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (border != null) using (var pen = new Pen(border.Value, Math.Max(1, K))) g.DrawPath(pen, path);
            }
            g.SmoothingMode = old;
        }

        /// <summary>アプリのアイコン（exe に埋め込んだもの）。</summary>
        public static Icon AppIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { return null; }
        }

        /// <summary>Enter（テンキーの Enter も）で値を確定し、入力欄から出る（文字入力が続かず、ショートカットが効くように）。</summary>
        public static void ConfirmOnEnter(Control box, Action apply, Func<Control> focusTo)
        {
            box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Return)
                {
                    apply();
                    var to = focusTo();
                    if (to != null) to.Focus();
                    e.Handled = e.SuppressKeyPress = true;
                }
            };
        }

        public static Label Label(string text, Font font = null, Color? color = null)
        {
            return new Label
            {
                Text = text, AutoSize = true, Font = font ?? Font, ForeColor = color ?? Color.Black, BackColor = Color.Transparent,
                Margin = Padding.Empty, UseMnemonic = false,
            };
        }

        /// <summary>折り返す文。width は CSS px。</summary>
        public static Label Para(string text, double px, Color color, int width)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(S(width), 0), Font = F(px), ForeColor = color, Margin = Padding.Empty, UseMnemonic = false };
        }

        public static void DrawText(Graphics g, string text, Font font, Rectangle r, Color color, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, text, font, r, color, flags | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>Web 版のボタン: 角の丸い四角に白い太字。押すと明るく、使えないときは薄く。</summary>
    public class FlatButton : Control
    {
        public Color Fill = Ui.BTN, TextColor = Color.White;
        Color? activeFill;
        bool inside, pressed, active;
        public event Action Pressed;
        readonly int minW, padX;

        /// <summary>height・minWidth・padX は CSS px（Web 版の button は 44・64・14、ツールバーは 56・10）。</summary>
        public FlatButton(string text, Color? fill = null, int height = 44, int minWidth = 64, int padX = 14, double fontPx = 16)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Color.Transparent;
            if (fill != null) Fill = fill.Value;
            Font = Ui.F(fontPx, true);
            minW = minWidth;
            this.padX = padX;
            Height = Ui.S(height);
            Margin = Padding.Empty;
            Cursor = Cursors.Hand;
            Text = text;
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            int w = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + Ui.S(padX * 2);
            Width = Math.Max(Ui.S(minW), w);
            Invalidate();
        }

        /// <summary>選んでいる状態（モードのボタン）: 橙に黒い字。</summary>
        public bool Active
        {
            get { return active; }
            set { if (active != value) { active = value; Invalidate(); } }
        }

        public Color ActiveFill { set { activeFill = value; } }

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { inside = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { inside = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled) { pressed = true; Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool was = pressed;
            pressed = false;
            Invalidate();
            if (was && Enabled && ClientRectangle.Contains(e.Location) && Pressed != null) Pressed();
            base.OnMouseUp(e);
        }

        public void PerformClick() { if (Enabled && Pressed != null) Pressed(); }

        // BM_CLICK（支援技術や自動操作から押されたとき）も、クリックと同じにする
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x00F5) { PerformClick(); return; }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var back = Parent != null ? Parent.BackColor : Ui.BG;
            Color fill = active ? (activeFill ?? Ui.BTN_ACTIVE) : Fill;
            Color text = active ? Ui.DARK : TextColor;
            if (Enabled && pressed && inside) fill = Ui.Bright(fill, 1.2);
            else if (Enabled && inside) fill = Ui.Bright(fill, 1.08);
            if (!Enabled) { fill = Ui.Mix(back, fill, 0.4); text = Ui.Mix(back, text, 0.4); }
            Ui.FillRound(g, new RectangleF(0, 0, Width - 0.5f, Height - 0.5f), Ui.S(8), fill);
            Ui.DrawText(g, Text, Font, ClientRectangle, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>Web 版の入力欄: 白地に細い縁、角が丸い。中に縁のない TextBox を置く。</summary>
    public class InputBox : Control
    {
        public readonly TextBox Box;
        readonly int padX;

        /// <summary>width・height・padX は CSS px（Web 版は高さ 40、左右 8）。</summary>
        public InputBox(int width, int height = 40, int padX = 8, bool number = true, double fontPx = 16)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            this.padX = padX;
            Size = new Size(Ui.S(width), Ui.S(height));
            Margin = Padding.Empty;
            Box = new TextBox { BorderStyle = BorderStyle.None, Font = Ui.F(fontPx), BackColor = Color.White, TextAlign = HorizontalAlignment.Left };
            Controls.Add(Box);
            Box.GotFocus += (s, e) => Invalidate();
            Box.LostFocus += (s, e) => Invalidate();
            Cursor = Cursors.IBeam;
            Place();
        }

        void Place()
        {
            int x = Ui.S(padX);
            Box.SetBounds(x, (Height - Box.PreferredHeight) / 2 + 1, Math.Max(10, Width - 2 * x), Box.PreferredHeight);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); if (Box != null) Place(); }
        protected override void OnMouseDown(MouseEventArgs e) { Box.Focus(); base.OnMouseDown(e); }

        public override string Text { get { return Box == null ? "" : Box.Text; } set { if (Box != null) Box.Text = value; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.BG);
            Ui.FillRound(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.S(6), Color.White, Box.Focused ? Ui.PRIMARY : Ui.LINE);
        }
    }

    /// <summary>Web 版の select: 入力欄と同じ見た目で、押すと選ぶ一覧を出す。</summary>
    public class SelectBox : Control
    {
        readonly List<string> items = new List<string>();
        int index = -1;
        public event Action Changed;

        public SelectBox(int width, IEnumerable<string> options, int height = 40)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, true);
            items.AddRange(options);
            Size = new Size(Ui.S(width), Ui.S(height));
            Font = Ui.Font;
            Margin = Padding.Empty;
            Cursor = Cursors.Hand;
        }

        public int SelectedIndex
        {
            get { return index; }
            set { index = value; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            var menu = new ContextMenuStrip { Font = Font, ShowImageMargin = false, ShowCheckMargin = true };
            for (int i = 0; i < items.Count; i++)
            {
                int k = i;
                var it = new ToolStripMenuItem(items[i]) { Checked = i == index };
                it.Click += (s, a) => { if (index != k) { index = k; Invalidate(); Changed?.Invoke(); } };
                menu.Items.Add(it);
            }
            menu.Closed += (s, a) => BeginInvoke(new Action(menu.Dispose));
            menu.Show(this, new System.Drawing.Point(0, Height));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.BG);
            Ui.FillRound(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.S(6), Color.White, Ui.LINE);
            string t = index >= 0 && index < items.Count ? items[index] : "";
            Ui.DrawText(g, t, Font, new Rectangle(Ui.S(8), 0, Width - Ui.S(28), Height), Color.Black, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            // ▼
            float cx = Width - Ui.S(14), cy = Height / 2f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Color.FromArgb(80, 80, 80), Math.Max(1.5f, 1.5f * Ui.K)))
                g.DrawLines(p, new[] { new PointF(cx - Ui.S(4), cy - Ui.S(2)), new PointF(cx, cy + Ui.S(2)), new PointF(cx + Ui.S(4), cy - Ui.S(2)) });
        }
    }

    /// <summary>CSS の flex（横並び、折り返し、間隔）。groups の最後の 1 つだけ右に寄せられる（margin-left: auto）。</summary>
    public class FlexBar : Panel
    {
        readonly List<List<Control>> groups = new List<List<Control>>();
        readonly int gapX, gapY, inner;
        public bool LastRight = true;

        /// <summary>gap・inner（まとまりの中の間隔）・padding は CSS px。</summary>
        public FlexBar(Color back, int gapX = 12, int gapY = 8, int inner = 6, Padding? padding = null)
        {
            BackColor = back;
            this.gapX = gapX; this.gapY = gapY; this.inner = inner;
            Padding = padding ?? Ui.P(12, 8, 12, 8);
            Dock = DockStyle.Top;
            DoubleBuffered = true;
        }

        public void AddGroup(params Control[] controls)
        {
            groups.Add(controls.ToList());
            Controls.AddRange(controls);
            PerformLayout();
        }

        int GroupWidth(List<Control> g)
        {
            var vis = g.Where(c => c.Visible).ToList();
            return vis.Sum(c => c.Width) + Math.Max(0, vis.Count - 1) * Ui.S(inner);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            if (groups.Count == 0) return;
            int avail = ClientSize.Width - Padding.Horizontal;
            // まとまりを行に分ける
            var rows = new List<List<List<Control>>> { new List<List<Control>>() };
            int x = 0;
            foreach (var g in groups)
            {
                int w = GroupWidth(g);
                if (rows.Last().Count > 0 && x + Ui.S(gapX) + w > avail) { rows.Add(new List<List<Control>>()); x = 0; }
                x += (rows.Last().Count > 0 ? Ui.S(gapX) : 0) + w;
                rows.Last().Add(g);
            }
            int y = Padding.Top;
            foreach (var row in rows)
            {
                int h = row.SelectMany(g => g).Where(c => c.Visible).Select(c => c.Height).DefaultIfEmpty(0).Max();
                int cx = Padding.Left;
                for (int gi = 0; gi < row.Count; gi++)
                {
                    var g = row[gi];
                    if (LastRight && g == groups.Last()) cx = Math.Max(cx, ClientSize.Width - Padding.Right - GroupWidth(g));
                    foreach (var c in g.Where(c => c.Visible))
                    {
                        c.Location = new System.Drawing.Point(cx, y + (h - c.Height) / 2);
                        cx += c.Width + Ui.S(inner);
                    }
                    cx += Ui.S(gapX) - Ui.S(inner);
                }
                y += h + Ui.S(gapY);
            }
            int height = y - Ui.S(gapY) + Padding.Bottom;
            if (Height != height) Height = height;
        }

        protected override void OnResize(EventArgs eventargs) { base.OnResize(eventargs); PerformLayout(); }
    }

    /// <summary>区画の見出し（.code-head）: 太字の名前、灰色の説明、右にボタン。下に細い線。</summary>
    public class PaneHead : Panel
    {
        public PaneHead(string title, string note, Control right = null)
        {
            BackColor = Ui.HEAD;
            Dock = DockStyle.Top;
            Height = Ui.S(right != null ? 49 : 38);
            var t = Ui.Label(title, Ui.Bold);
            var n = Ui.Label(note, Ui.F(13), Ui.NOTE);
            Controls.Add(t);
            Controls.Add(n);
            if (right != null) Controls.Add(right);
            Layout += (s, e) =>
            {
                t.Location = new System.Drawing.Point(Ui.S(12), (Height - t.Height) / 2);
                n.Location = new System.Drawing.Point(t.Right + Ui.S(8), (Height - n.Height) / 2 + Ui.S(1));
                if (right != null) right.Location = new System.Drawing.Point(Width - right.Width - Ui.S(8), (Height - right.Height) / 2);
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(Ui.LINE)) e.Graphics.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
    }

    /// <summary>下の帯（#status）: 左から間隔 20 で並べ、選んだものの情報とバージョンは右に寄せる。</summary>
    public class StatusBar : Control
    {
        public string Name_ = "", Prog = "", Tools = "", Count = "", Pos = "", Msg = "", Sel = "", Ver = "";
        Rectangle verRect;
        public event Action VersionClicked;

        public StatusBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.STATUS;
            Dock = DockStyle.Bottom;
            Font = Ui.F(14);
            Height = TextRenderer.MeasureText("あ", Font).Height + Ui.S(12);
        }

        public void Set() { Invalidate(); }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); Cursor = verRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default; }
        protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); if (verRect.Contains(e.Location)) VersionClicked?.Invoke(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            var bold = Ui.F(14, true);
            const TextFormatFlags F = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            Func<string, Font, int> W = (t, f) => string.IsNullOrEmpty(t) ? 0 : TextRenderer.MeasureText(g, t, f, Size.Empty, F | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            int gap = Ui.S(20), pad = Ui.S(12);
            // 右: 選んだもの・バージョン
            int verW = W(Ver, Font), selW = W(Sel, bold);
            int right = Width - pad;
            verRect = new Rectangle(right - verW, 0, verW, Height);
            Ui.DrawText(g, Ver, Font, verRect, Ui.NOTE, F);
            right -= verW + (selW > 0 ? gap : 0);
            if (selW > 0) Ui.DrawText(g, Sel, bold, new Rectangle(right - selW, 0, selW, Height), Ui.BAR, F);
            int limit = right - selW - gap;
            // 左
            int x = pad;
            foreach (var (t, f, c) in new[] { (Name_, Font, Color.Black), (Prog, Font, Color.Black), (Tools, Font, Color.Black), (Count, Font, Color.Black),
                                               (Pos, Font, Color.Black), (Msg, bold, Ui.OK_TEXT) })
            {
                int w = W(t, f);
                if (w == 0) continue;
                if (x >= limit) break;
                Ui.DrawText(g, t, f, new Rectangle(x, 0, Math.Min(w, limit - x), Height), c, F | TextFormatFlags.EndEllipsis);
                x += w + gap;
            }
        }
    }

    /// <summary>白い札（#props）: 角が丸く、細い縁。</summary>
    public class Card : Panel
    {
        public Card()
        {
            BackColor = Color.White;
            DoubleBuffered = true;
            Padding = Ui.P(12);
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            using (var path = Ui.Round(new RectangleF(0, 0, Width, Height), Ui.S(10))) Region = new Region(path);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.S(10)))
            using (var p = new Pen(Ui.LINE, Math.Max(1, Ui.K))) e.Graphics.DrawPath(p, path);
        }
    }
}
