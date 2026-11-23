// 画面の見た目: クラシックな Windows の色と文字、ドット絵の画像、Domino のように描く小さな部品
// （ツールバーのボタン、ステータスバーの欄）。HummingbirdStudio の Ui.cs から、このアプリで使うものを持ってきた。
using System;
using System.Drawing;
using System.Windows.Forms;

namespace SetPressCam
{
    public static class Ui
    {
        public static readonly Color FACE = Color.FromArgb(0xF0, 0xF0, 0xF0), WINDOW = Color.White;
        public static readonly Color SELECT = Color.FromArgb(0x31, 0x6A, 0xC5);
        public static readonly Color ERR = Color.FromArgb(0xC0, 0x00, 0x00), GREY_TEXT = Color.FromArgb(0x60, 0x60, 0x60);
        public static readonly Font Font = new Font("MS UI Gothic", 9f);
        public static readonly Font Mono = new Font("ＭＳ ゴシック", 9f);

        public static Color Rgb(int rgb) { return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF); }

        public static Color Hex(string hex)
        {
            return Color.FromArgb(Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16),
                                  Convert.ToInt32(hex.Substring(5, 2), 16));
        }

        /// <summary>ドット絵の画像。disabled は灰色の版。</summary>
        public static Bitmap Pixels(string[] rows, bool disabled = false)
        {
            var bmp = new Bitmap(rows[0].Length, rows.Length, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            for (int y = 0; y < rows.Length; y++)
                for (int x = 0; x < rows[y].Length; x++)
                {
                    int rgb;
                    if (!Icons.PALETTE.TryGetValue(rows[y][x], out rgb)) continue;
                    var c = Rgb(rgb);
                    if (disabled)
                    {
                        double lum = (c.R + c.G + c.B) / 3.0;
                        c = lum < 160 ? Hex("#a0a0a0") : Hex("#d4d0c8");
                    }
                    bmp.SetPixel(x, y, c);
                }
            return bmp;
        }

        /// <summary>アプリのアイコン（exe に埋め込んだもの）。</summary>
        public static Icon AppIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { return null; }
        }

        /// <summary>中身に合わせて大きさが決まるパネルを、小さくもなるようにする（既定では大きくなるだけ）。</summary>
        public static void Shrink(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c.AutoSize)
                {
                    if (c is Panel) ((Panel)c).AutoSizeMode = AutoSizeMode.GrowAndShrink;
                    else if (c is GroupBox) ((GroupBox)c).AutoSizeMode = AutoSizeMode.GrowAndShrink;
                }
                Shrink(c);
            }
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

        public static Label Text(string text, Padding margin = default(Padding))
        {
            return new Label { Text = text, AutoSize = true, Margin = margin == default(Padding) ? new Padding(2, 6, 1, 0) : margin };
        }

        /// <summary>数値の入力欄（へこんだ枠の TextBox）。chars 文字ぶんの幅。</summary>
        public static TextBox NumBox(int chars)
        {
            var tb = new TextBox { BorderStyle = BorderStyle.Fixed3D, TextAlign = HorizontalAlignment.Right, Margin = new Padding(2, 2, 2, 0) };
            tb.Width = TextRenderer.MeasureText(new string('0', chars), Font).Width + 8;
            return tb;
        }
    }

    /// <summary>平らなツールバーのボタン。マウスを乗せると浮き出し、押すとへこむ（XP の形）。</summary>
    public class ToolButton : Control
    {
        readonly Bitmap image, grey;
        bool inside, pressed, active, enabled = true;
        public event Action Pressed;

        public ToolButton(string icon, string text)
        {
            image = Ui.Pixels(Icons.ICONS[icon]);
            grey = Ui.Pixels(Icons.ICONS[icon], true);
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.Selectable, false);
            int w = 16 + 10 + (text.Length > 0 ? TextRenderer.MeasureText(text, Ui.Font).Width + 2 : 0);
            Size = new Size(w, 24);
            Margin = new Padding(1, 2, 1, 2);
        }

        /// <summary>選んでいる状態（モードのボタン）。白地でへこんで描く。</summary>
        public bool Active { get { return active; } set { if (active != value) { active = value; Invalidate(); } } }
        public bool On { get { return enabled; } set { if (enabled != value) { enabled = value; Invalidate(); } } }

        protected override void OnMouseEnter(EventArgs e) { inside = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { inside = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && enabled) { pressed = true; Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool was = pressed;
            pressed = false;
            Invalidate();
            if (was && enabled && ClientRectangle.Contains(e.Location) && Pressed != null) Pressed();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(active && enabled ? Color.White : Ui.FACE);
            var r = ClientRectangle;
            if ((active && enabled) || (pressed && inside)) ControlPaint.DrawBorder3D(g, r, Border3DStyle.SunkenOuter);
            else if (inside && enabled) ControlPaint.DrawBorder3D(g, r, Border3DStyle.RaisedInner);
            int off = (pressed && inside) || (active && enabled) ? 1 : 0;
            g.DrawImageUnscaled(enabled ? image : grey, 5 + off, (Height - 16) / 2 + off);
            if (Text.Length > 0)
            {
                var tr = new Rectangle(5 + 16 + 3 + off, off, Width - 24, Height);
                TextRenderer.DrawText(g, Text, Ui.Font, tr, enabled ? Color.Black : SystemColors.GrayText,
                                      TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            }
        }
    }

    /// <summary>ステータスバーのへこんだ欄。</summary>
    public class StatusPane : Control
    {
        public StatusPane()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 20;
            Margin = new Padding(0, 2, 2, 2);
        }

        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnForeColorChanged(EventArgs e) { Invalidate(); base.OnForeColorChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Ui.FACE);
            ControlPaint.DrawBorder3D(e.Graphics, ClientRectangle, Border3DStyle.SunkenOuter);
            TextRenderer.DrawText(e.Graphics, Text, Ui.Font, new Rectangle(4, 1, Width - 6, Height - 2), ForeColor,
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix |
                                  TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>ツールバーの区切り（へこんだ線）。</summary>
    public class ToolSeparator : Control
    {
        public ToolSeparator()
        {
            Size = new Size(2, 20);
            Margin = new Padding(4, 4, 4, 3);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.DrawLine(SystemPens.ControlDark, 0, 0, 0, Height);
            e.Graphics.DrawLine(SystemPens.ControlLightLight, 1, 0, 1, Height);
        }
    }

    /// <summary>ツールバー: 浮き出した帯に、左から部品を並べる。</summary>
    public class ToolStripPanel : FlowLayoutPanel
    {
        public ToolStripPanel()
        {
            Dock = DockStyle.Top;
            AutoSize = true;
            WrapContents = false;
            BackColor = Ui.FACE;
            Padding = new Padding(2, 1, 2, 1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            ControlPaint.DrawBorder3D(e.Graphics, ClientRectangle, Border3DStyle.RaisedInner);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
    }

    /// <summary>区画の見出し（割付・コード）: 浮き出した細い帯に名前と説明。</summary>
    public class PaneHeader : Control
    {
        public string Note = "";

        public PaneHeader(string title, string note)
        {
            Text = title;
            Note = note;
            Dock = DockStyle.Top;
            Height = 20;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Ui.FACE);
            ControlPaint.DrawBorder3D(g, ClientRectangle, Border3DStyle.RaisedInner);
            using (var bold = new Font(Ui.Font, FontStyle.Bold))
            {
                var sz = TextRenderer.MeasureText(Text, bold);
                TextRenderer.DrawText(g, Text, bold, new Rectangle(4, 0, sz.Width, Height), Color.Black, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, Note, Ui.Font, new Rectangle(8 + sz.Width, 0, Width - sz.Width - 10, Height), Ui.GREY_TEXT,
                                      TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
