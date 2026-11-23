// コードの区画の中身（#code）: 行番号と文、長い行は折り返す。コメントは緑、選んだ行は薄い橙。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SetPressCam
{
    public class CodeView : ScrollableControl
    {
        List<CodeLine> lines = new List<CodeLine>();
        HashSet<string> sel = new HashSet<string>();
        readonly List<Rectangle> rows = new List<Rectangle>();  // 中身の座標（スクロールの前）
        int layoutWidth = -1;
        public event Action<string> LineClicked;

        static readonly Color NO = Ui.Hex("#9aa5b0"), CMT = Ui.Hex("#5f7a5f"), SEL = Ui.Mix(Ui.PANEL, Ui.BTN_ACTIVE, 0.22);

        public CodeView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.PANEL;
            Font = Ui.Mono;
            AutoScroll = true;
        }

        int LineH { get { return Ui.S(15 * 1.6); } }
        int NoW { get { return Ui.S(45); } }

        public void SetLines(List<CodeLine> l, HashSet<string> selected)
        {
            lines = l;
            sel = selected;
            layoutWidth = -1;
            Relayout();
            Invalidate();
        }

        void Relayout()
        {
            int w = ClientSize.Width;
            if (w == layoutWidth) return;
            layoutWidth = w;
            rows.Clear();
            int textW = Math.Max(Ui.S(40), w - NoW - Ui.S(10));
            int y = Ui.S(6);
            using (var g = CreateGraphics())
                foreach (var l in lines)
                {
                    int h = LineH;
                    int n = 0;
                    for (string rest = l.Text; rest.Length > 0; n++) rest = rest.Substring(Fit(g, rest, textW));
                    h = LineH * Math.Max(1, n);
                    rows.Add(new Rectangle(0, y, w, h));
                    y += h;
                }
            AutoScrollMinSize = new Size(0, y + Ui.S(6));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
            Invalidate();
        }

        /// <summary>選んだ行が見えるところまで送る。</summary>
        public void ScrollToLine(int i)
        {
            if (i < 0 || i >= rows.Count) return;
            int top = -AutoScrollPosition.Y, r = rows[i].Y, b = rows[i].Bottom;
            if (r < top) AutoScrollPosition = new System.Drawing.Point(0, r);
            else if (b > top + ClientSize.Height) AutoScrollPosition = new System.Drawing.Point(0, b - ClientSize.Height);
        }

        int RowAt(System.Drawing.Point p)
        {
            int y = p.Y - AutoScrollPosition.Y;
            for (int i = 0; i < rows.Count; i++) if (y >= rows[i].Y && y < rows[i].Bottom) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = RowAt(e.Location);
            Cursor = i >= 0 && i < lines.Count && lines[i].Key != null ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int i = RowAt(e.Location);
            if (i >= 0 && i < lines.Count && lines[i].Key != null) LineClicked?.Invoke(lines[i].Key);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int dy = AutoScrollPosition.Y, fh = Font.Height;
            int pad = (LineH - fh) / 2;
            for (int i = 0; i < rows.Count && i < lines.Count; i++)
            {
                var r = rows[i];
                r.Offset(0, dy);
                if (r.Bottom < e.ClipRectangle.Top || r.Top > e.ClipRectangle.Bottom) continue;
                var l = lines[i];
                if (l.Key != null && sel.Contains(l.Key)) using (var b = new SolidBrush(SEL)) g.FillRectangle(b, r);
                Ui.DrawText(g, (i + 1).ToString(), Font, new Rectangle(0, r.Y + pad, NoW - Ui.S(10), fh), NO, TextFormatFlags.Right | TextFormatFlags.SingleLine);
                // 折り返した行も、1 行ぶん（line-height 1.6）ずつ間を空けて描く
                var tr = new Rectangle(NoW, r.Y + pad, r.Width - NoW - Ui.S(10), r.Height);
                DrawWrapped(g, l.Text, tr, l.Comment ? CMT : Color.Black);
            }
        }

        void DrawWrapped(Graphics g, string text, Rectangle r, Color c)
        {
            // 1 行に入る所で切り、行ごとに描く
            int y = r.Y;
            string rest = text;
            while (rest.Length > 0)
            {
                int n = Fit(g, rest, r.Width);
                Ui.DrawText(g, rest.Substring(0, n), Font, new Rectangle(r.X, y, r.Width + Ui.S(10), Font.Height), c, TextFormatFlags.SingleLine);
                rest = rest.Substring(n);
                y += LineH;
            }
        }

        int Fit(Graphics g, string s, int width)
        {
            const TextFormatFlags F = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            if (TextRenderer.MeasureText(g, s, Font, Size.Empty, F).Width <= width) return s.Length;
            int lo = 1, hi = s.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (TextRenderer.MeasureText(g, s.Substring(0, mid), Font, Size.Empty, F).Width <= width) lo = mid; else hi = mid - 1;
            }
            // 空白で切れるなら、そこで切る
            int sp = s.LastIndexOf(' ', Math.Min(lo, s.Length - 1));
            return sp > 0 && sp >= lo / 2 ? sp + 1 : lo;
        }
    }
}
