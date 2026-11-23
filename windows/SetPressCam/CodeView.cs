// コードの区画の中身（#code）: 行番号と文、長い行は折り返す。コメントは緑、選んだ行は薄い橙。
// F23 のピッチ・列数は枠で囲んで描き、クリックするとその場で書き換えられる（Web 版の .code-in）。
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
        /// <summary>F23 の欄を書き換えた（ワーク, 欄の名前 nx/ny/px/py, 書いた文字）。</summary>
        public event Action<int, string, string> FieldEdited;
        /// <summary>Enter・Esc で書き換えを終えた（作業の領域へフォーカスを戻す）。</summary>
        public event Action EditDone;

        static readonly Color NO = Ui.Hex("#9aa5b0"), CMT = Ui.Hex("#5f7a5f"), SEL = Ui.Mix(Ui.PANEL, Ui.BTN_ACTIVE, 0.22);
        static readonly Color IN_BG = Ui.Hex("#fffbe8"), IN_LINE = Ui.Hex("#b9c4cf");

        // 書き換えられる欄の位置（中身の座標）
        class FieldBox { public int Line, J; public Rectangle R; public int PrefixX; public string Id; }
        readonly List<FieldBox> boxes = new List<FieldBox>();
        readonly Dictionary<int, int> noteX = new Dictionary<int, int>();  // 書き換えられる行の、コメントの左端
        EditBox editor;
        string editingId;

        /// <summary>Tab・Enter・Esc を自分で受ける入力欄。</summary>
        class EditBox : TextBox
        {
            protected override bool IsInputKey(Keys k)
            {
                var c = k & Keys.KeyCode;
                return c == Keys.Tab || c == Keys.Enter || c == Keys.Escape || base.IsInputKey(k);
            }
        }

        public CodeView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.PANEL;
            Font = Ui.Mono;
            AutoScroll = true;
        }

        int LineH { get { return Ui.S(15 * 1.6); } }
        int NoW { get { return Ui.S(45); } }
        int CharW { get { return TextRenderer.MeasureText("0000000000", Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width / 10; } }
        int BoxW { get { return CharW * 7 + Ui.S(8); } }

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
            boxes.Clear();
            noteX.Clear();
            int textW = Math.Max(Ui.S(40), w - NoW - Ui.S(10));
            int y = Ui.S(6), cw = CharW, fh = Font.Height, pad = (LineH - fh) / 2;
            using (var g = CreateGraphics())
                for (int li = 0; li < lines.Count; li++)
                {
                    var l = lines[li];
                    int n = 0;
                    if (l.Edit != null)
                    {
                        // "  X[    ] Y[    ] ; ピッチ" のように並べ、コメントは残りの幅で折り返す
                        int x = NoW + cw * 2;
                        for (int j = 0; j < l.Edit.Fields.Length; j++)
                        {
                            int px = x;
                            x += cw * l.Edit.Prefix[j].Length;
                            boxes.Add(new FieldBox
                            {
                                Line = li, J = j, PrefixX = px, Id = l.Edit.Work + ":" + l.Edit.Fields[j],
                                R = new Rectangle(x, y + pad - Ui.S(2), BoxW, fh + Ui.S(4)),
                            });
                            x += BoxW + cw;
                        }
                        noteX[li] = x;
                        int noteW = Math.Max(Ui.S(40), w - Ui.S(10) - x);
                        for (string rest = "; " + l.Edit.Note; rest.Length > 0; n++) rest = rest.Substring(Fit(g, rest, noteW));
                    }
                    else
                        for (string rest = l.Text; rest.Length > 0; n++) rest = rest.Substring(Fit(g, rest, textW));
                    int h = LineH * Math.Max(1, n);
                    rows.Add(new Rectangle(0, y, w, h));
                    y += h;
                }
            AutoScrollMinSize = new Size(0, y + Ui.S(6));
            PlaceEditor();
        }

        // ---- F23 の欄の書き換え ----
        FieldBox BoxAt(System.Drawing.Point p)
        {
            var q = new System.Drawing.Point(p.X - AutoScrollPosition.X, p.Y - AutoScrollPosition.Y);
            return boxes.Find(b => b.R.Contains(q));
        }

        void PlaceEditor()
        {
            if (editingId == null || editor == null) return;
            var b = boxes.Find(x => x.Id == editingId);
            if (b == null) { EndEdit(false, 0); return; }
            var r = b.R;
            r.Offset(AutoScrollPosition.X, AutoScrollPosition.Y);
            editor.Bounds = r;
        }

        void BeginEdit(string id)
        {
            var b = boxes.Find(x => x.Id == id);
            if (b == null) return;
            if (editor == null)
            {
                editor = new EditBox { BorderStyle = BorderStyle.FixedSingle, Font = Font, BackColor = IN_BG, Visible = false };
                editor.KeyDown += (s, e) =>
                {
                    if (e.KeyCode == Keys.Tab) { e.SuppressKeyPress = true; EndEdit(true, e.Shift ? -1 : 1); }
                    else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; EndEdit(true, 0); EditDone?.Invoke(); }
                    else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndEdit(false, 0); EditDone?.Invoke(); }
                };
                editor.Leave += (s, e) => EndEdit(true, 0);
                Controls.Add(editor);
            }
            editingId = id;
            editor.Text = lines[b.Line].Edit.Vals[b.J];
            PlaceEditor();
            editor.Visible = true;
            editor.Focus();
            editor.SelectAll();
            Invalidate();
        }

        /// <summary>書き換えを終える。commit なら値を渡す。move が ±1 なら次・前の欄を続けて書き換える。</summary>
        void EndEdit(bool commit, int move)
        {
            if (editingId == null) return;
            string id = editingId, text = editor.Text;
            int idx = boxes.FindIndex(x => x.Id == id);
            string next = move != 0 && idx >= 0 && idx + move >= 0 && idx + move < boxes.Count ? boxes[idx + move].Id : null;
            var b = idx >= 0 ? boxes[idx] : null;
            editingId = null;  // 先に外す（隠したときの Leave で二度呼ばれないように）
            editor.Visible = false;
            if (commit && b != null) FieldEdited?.Invoke(lines[b.Line].Edit.Work, lines[b.Line].Edit.Fields[b.J], text);  // ここでコードが作り直される
            if (next != null) BeginEdit(next);
            Invalidate();
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
            Cursor = BoxAt(e.Location) != null ? Cursors.IBeam : i >= 0 && i < lines.Count && lines[i].Key != null ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var b = e.Button == MouseButtons.Left ? BoxAt(e.Location) : null;
            if (b != null) BeginEdit(b.Id);
        }

        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);
            PlaceEditor();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (BoxAt(e.Location) != null) return;
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
                if (l.Edit != null)
                {
                    foreach (var b in boxes.Where(x => x.Line == i))
                    {
                        var br = b.R;
                        br.Offset(AutoScrollPosition.X, AutoScrollPosition.Y);
                        if (l.Edit.Prefix[b.J] != "")
                            Ui.DrawText(g, l.Edit.Prefix[b.J], Font, new Rectangle(b.PrefixX, r.Y + pad, br.X - b.PrefixX + Ui.S(4), fh), Color.Black, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                        if (b.Id == editingId) continue;  // 書き換え中は入力欄が上にある
                        using (var bg = new SolidBrush(IN_BG)) g.FillRectangle(bg, br);
                        using (var p = new Pen(IN_LINE)) g.DrawRectangle(p, br.X, br.Y, br.Width - 1, br.Height - 1);
                        Ui.DrawText(g, l.Edit.Vals[b.J], Font, new Rectangle(br.X + Ui.S(3), r.Y + pad, br.Width - Ui.S(4), fh), Color.Black, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                    }
                    int nx = noteX[i];
                    DrawWrapped(g, "; " + l.Edit.Note, new Rectangle(nx, r.Y + pad, r.Width - nx - Ui.S(10), r.Height), Color.Black);
                    continue;
                }
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
