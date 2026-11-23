// ダイアログ: Web 版の <dialog> と同じ見た目（白い角の丸い箱、うしろを暗くする）。
// 確認、保存、開く、プログラム番号と金型、更新の確認、更新のダウンロード。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SetPressCam
{
    /// <summary>うしろのウィンドウを暗くする幕（dialog::backdrop）。</summary>
    class Backdrop : Form
    {
        public Backdrop(Form owner)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            Opacity = 0.35;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = owner.RectangleToScreen(owner.ClientRectangle);
        }

        protected override bool ShowWithoutActivation { get { return true; } }
    }

    /// <summary>Web 版の dialog: 白い箱、上に見出し（h2）、下に右寄せのボタン。Esc で閉じる。箱の空いた所をつかむと動かせる。</summary>
    public class WebDialog : Form
    {
        protected readonly TableLayoutPanel Body;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public WebDialog(string title, int minWidth = 360)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.White;
            AutoScaleMode = AutoScaleMode.None;
            Font = Ui.Font;
            KeyPreview = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.P(20);
            Body = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty, BackColor = Color.White };
            Body.MinimumSize = new Size(Ui.S(minWidth), 0);
            Body.Location = new System.Drawing.Point(Ui.S(20), Ui.S(20));  // 自動の大きさは右と下に Padding を足すので、左と上はここで空ける
            Controls.Add(Body);
            if (title != null) Add(Ui.Label(title, Ui.F(20, true)), 14);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;  // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2;  // Windows 11: DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND
            try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch (Exception) { }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            const int WM_NCHITTEST = 0x84, HTCLIENT = 1, HTCAPTION = 2;
            if (m.Msg == WM_NCHITTEST && (int)m.Result == HTCLIENT) m.Result = (IntPtr)HTCAPTION;
        }

        /// <summary>中身を 1 行足す。gap は下の間隔（CSS px）。</summary>
        protected Control Add(Control c, int gap = 0)
        {
            c.Margin = new Padding(0, 0, 0, Ui.S(gap));
            Body.Controls.Add(c);
            return c;
        }

        /// <summary>下のボタン（.dlg-buttons: 右寄せ、間隔 8、上に 18 空ける）。left は左に寄せるボタン。</summary>
        protected void Buttons(FlatButton left, params FlatButton[] list)
        {
            var row = new Panel { BackColor = Color.White, Margin = new Padding(0, Ui.S(18), 0, 0), Height = Ui.S(44), Anchor = AnchorStyles.Left | AnchorStyles.Right };
            if (left != null) row.Controls.Add(left);
            foreach (var b in list) row.Controls.Add(b);
            int need = list.Sum(b => b.Width) + Ui.S(8) * (list.Length - 1) + (left != null ? left.Width + Ui.S(16) : 0);
            row.MinimumSize = new Size(need, Ui.S(44));
            row.Width = need;
            row.Layout += (s, e) =>
            {
                int x = row.Width;
                for (int i = list.Length - 1; i >= 0; i--) { x -= list[i].Width; list[i].Location = new System.Drawing.Point(x, 0); x -= Ui.S(8); }
                if (left != null) left.Location = new System.Drawing.Point(0, 0);
            };
            Body.Controls.Add(row);
        }

        public static FlatButton Btn(string text, Color? fill = null)
        {
            return new FlatButton(text, fill ?? Ui.DIALOG_BTN);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
        }

        /// <summary>うしろを暗くして、持ち主の真ん中に出す。</summary>
        public DialogResult ShowModal(Form owner)
        {
            if (owner == null || !owner.Visible || owner.WindowState == FormWindowState.Minimized)
            {
                StartPosition = FormStartPosition.CenterScreen;
                return ShowDialog();
            }
            using (var back = new Backdrop(owner))
            {
                back.Show(owner);
                var r = owner.RectangleToScreen(owner.ClientRectangle);
                var sz = GetPreferredSize(Size.Empty);
                Location = new System.Drawing.Point(r.X + Math.Max(0, (r.Width - sz.Width) / 2), r.Y + Math.Max(0, (r.Height - sz.Height) / 2));
                try { return ShowDialog(back); }
                finally { back.Close(); owner.Activate(); }
            }
        }
    }

    /// <summary>確認・お知らせ（#dlg-confirm）: 文と、キャンセル・OK などのボタン。押したボタンの文字を返す（閉じたら null）。</summary>
    public class AskDialog : WebDialog
    {
        public string Result;

        public AskDialog(string title, string text, string[] buttons, int primary) : base(title)
        {
            Add(Ui.Para(text, 16, Color.Black, 460), 0);
            var list = new List<FlatButton>();
            for (int i = 0; i < buttons.Length; i++)
            {
                var b = Btn(buttons[i], i == primary ? Ui.PRIMARY : (Color?)null);
                string v = buttons[i];
                b.Pressed += () => { Result = v; DialogResult = DialogResult.OK; Close(); };
                list.Add(b);
            }
            Buttons(null, list.ToArray());
            if (primary >= 0) KeyDown += (s, e) => { if (e.KeyCode == Keys.Return) { e.Handled = true; list[primary].PerformClick(); } };
        }

        /// <summary>確かめる（Web 版の confirmBox）。OK なら true。</summary>
        public static bool Confirm(Form owner, string text, string okLabel = "OK")
        {
            return Ask(owner, null, text, new[] { "キャンセル", okLabel }, 1) == okLabel;
        }

        /// <summary>知らせる（OK だけ）。</summary>
        public static void Tell(Form owner, string text, string title = null)
        {
            Ask(owner, title, text, new[] { "OK" }, 0);
        }

        public static string Ask(Form owner, string title, string text, string[] buttons, int primary)
        {
            using (var d = new AskDialog(title, text, buttons, primary))
            {
                d.ShowModal(owner);
                return d.Result;
            }
        }
    }

    /// <summary>保存（#dlg-save）: 名前を入れる。</summary>
    public class SaveDialog : WebDialog
    {
        readonly InputBox name;
        public string Value;

        public SaveDialog(string current) : base("保存")
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.White };
            var lab = Ui.Label("名前");
            name = new InputBox(320, number: false);
            name.Box.MaxLength = 60;
            name.Text = current ?? "";
            lab.Margin = new Padding(0, (name.Height - lab.PreferredHeight) / 2, Ui.S(8), 0);
            row.Controls.Add(lab);
            row.Controls.Add(name);
            Add(row);
            var cancel = Btn("キャンセル");
            var ok = Btn("保存", Ui.PRIMARY);
            cancel.Pressed += () => { DialogResult = DialogResult.Cancel; Close(); };
            ok.Pressed += Submit;
            Buttons(null, cancel, ok);
            name.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Return) { e.SuppressKeyPress = true; Submit(); } };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            name.Box.Focus();
            name.Box.SelectAll();
        }

        void Submit()
        {
            string v = name.Text.Trim();
            if (v == "") { name.Box.Focus(); return; }
            Value = v;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    /// <summary>開く（#dlg-open）: 保存した図面の一覧（開く・削除）、ファイルから読み込む・ファイルに書き出す。
    /// Web 版の「Windows 版をダウンロード」の所は「更新の確認」。</summary>
    public class OpenDialog : WebDialog
    {
        const int LIST_W = 560;
        readonly Panel list;
        readonly Card frame;
        readonly Func<bool> dirty;
        public string OpenName;
        public event Action Import, Export, CheckUpdate;

        public OpenDialog(Func<bool> isDirty) : base("開く", LIST_W)
        {
            dirty = isDirty;
            frame = new Card { BackColor = Color.White, Padding = new Padding(Math.Max(1, Ui.S(1))) };
            list = new Panel { BackColor = Color.White, AutoScroll = true, Dock = DockStyle.Fill };
            frame.Controls.Add(list);
            Add(frame);
            var upd = new FlatButton("更新の確認", Ui.Hex("#3d6e9e"));
            upd.Pressed += () => CheckUpdate?.Invoke();
            var imp = Btn("ファイルから読み込む");
            imp.Pressed += () => Import?.Invoke();
            var exp = Btn("ファイルに書き出す");
            exp.Pressed += () => Export?.Invoke();
            var close = Btn("閉じる");
            close.Pressed += () => { DialogResult = DialogResult.Cancel; Close(); };
            Buttons(upd, imp, exp, close);
            Render();
        }

        /// <summary>一覧を作り直す（新しく保存した順）。</summary>
        public void Render()
        {
            var entries = Store.LoadStore().OrderByDescending(e => e.SavedAt, StringComparer.Ordinal).ToList();
            list.SuspendLayout();
            foreach (Control c in list.Controls.Cast<Control>().ToList()) c.Dispose();
            list.Controls.Clear();
            int y = 0, w = Ui.S(LIST_W) - 2 * frame.Padding.Left - (entries.Count > 6 ? SystemInformation.VerticalScrollBarWidth : 0);
            if (entries.Count == 0)
            {
                var empty = new Label { Text = "保存した図面はありません", AutoSize = false, Width = w, Height = Ui.S(64), TextAlign = ContentAlignment.MiddleCenter, ForeColor = Ui.NOTE, Font = Ui.Font };
                list.Controls.Add(empty);
                y = empty.Height;
            }
            for (int i = 0; i < entries.Count; i++)
            {
                var en = entries[i];
                var row = new Panel { BackColor = Color.White, Width = w, Height = Ui.S(62), Location = new System.Drawing.Point(0, y) };
                var name = Ui.Label(en.Name);
                var info = Ui.Label(Info(en), Ui.F(13), Ui.NOTE);
                var open = new FlatButton("開く", Ui.PRIMARY);
                var del = new FlatButton("削除", Ui.DANGER, minWidth: 56);
                row.Controls.AddRange(new Control[] { name, info, open, del });
                del.Location = new System.Drawing.Point(row.Width - Ui.S(10) - del.Width, (row.Height - del.Height) / 2);
                open.Location = new System.Drawing.Point(del.Left - Ui.S(8) - open.Width, del.Top);
                int mw = open.Left - Ui.S(18);
                name.AutoEllipsis = true; info.AutoEllipsis = true;
                name.MaximumSize = new Size(mw, 0); info.MaximumSize = new Size(mw, 0);
                int th = name.PreferredHeight + info.PreferredHeight;
                name.Location = new System.Drawing.Point(Ui.S(10), (row.Height - th) / 2);
                info.Location = new System.Drawing.Point(Ui.S(10), name.Top + name.PreferredHeight);
                if (i < entries.Count - 1) row.Paint += (s, e) => { using (var p = new Pen(Ui.LINE)) e.Graphics.DrawLine(p, 0, row.Height - 1, row.Width, row.Height - 1); };
                string nm = en.Name;
                open.Pressed += () =>
                {
                    if (dirty() && !AskDialog.Confirm(this, "いまの図面は保存されていません。破棄して開きますか？", "開く")) return;
                    OpenName = nm;
                    DialogResult = DialogResult.OK;
                    Close();
                };
                del.Pressed += () =>
                {
                    if (!AskDialog.Confirm(this, "「" + nm + "」を削除しますか？元に戻せません。", "削除")) return;
                    var st = Store.LoadStore();
                    st.RemoveAll(x => x.Name == nm);
                    Store.WriteStore(st);
                    Render();
                };
                list.Controls.Add(row);
                y += row.Height;
            }
            // 高さは中身に合わせ、画面の半分まで（max-height: 50vh）
            int max = Math.Max(Ui.S(120), Screen.PrimaryScreen.WorkingArea.Height / 2);
            frame.Size = new Size(Ui.S(LIST_W), Math.Min(y, max) + 2 * frame.Padding.Top);
            list.ResumeLayout();
        }

        static string Info(Store.Entry en)
        {
            var d = en.Doc as System.Text.Json.Nodes.JsonObject;
            string prog = "";
            try { var pv = (double?)d["program"]; if (pv != null) prog = Model.ProgramText((int)pv.Value) + "・"; } catch (Exception) { }
            int lines = d != null && d["lines"] is System.Text.Json.Nodes.JsonArray la ? la.Count : 0;
            string size = "";
            try { size = "・" + Js.Num((double)d["sheet"]["w"]) + "×" + Js.Num((double)d["sheet"]["h"]) + " mm"; } catch (Exception) { }
            DateTime when;
            string at = DateTime.TryParse(en.SavedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out when)
                ? when.ToLocalTime().ToString("yyyy/M/d H:mm:ss") : en.SavedAt;
            return prog + at + "・線 " + lines + " 本" + size;
        }
    }

    /// <summary>プログラム番号と金型 T1〜T3（#dlg-project）。新規作成のときと、「金型」のとき。</summary>
    public class ProjectDialog : WebDialog
    {
        readonly InputBox program;
        readonly List<ToolRow> rows = new List<ToolRow>();
        readonly Label err;
        public int Program;
        public List<Tool> Tools;
        readonly bool isNew;

        class ToolRow
        {
            public SelectBox Shape;
            public InputBox W, H, D, Note, W2, H2;
            public Control RectPart, RoundPart, SpecialPart;
        }

        public ProjectDialog(bool isNew, Doc doc) : base(isNew ? "新規作成" : "プログラム番号と金型", 640)
        {
            this.isNew = isNew;
            var prog = Flow();
            var pl = Ui.Label("プログラム番号 O", Ui.Bold);
            program = new InputBox(120);
            program.Text = isNew || doc.Program == null ? "" : doc.Program.Value.ToString(CultureInfo.InvariantCulture);
            Mid(prog, pl, program, 8);
            prog.Controls.Add(program);
            Mid(prog, Ui.Label("1〜9999", Ui.F(14), Ui.NOTE), program, 0, 8);
            Add(prog, 16);
            Add(Ui.Label("使用する金型", Ui.Bold), 6);
            for (int i = 0; i < Model.TOOL_COUNT; i++)
            {
                var tool = i < doc.Tools.Count ? doc.Tools[i] : Model.EmptyTool();
                var r = new ToolRow();
                var box = new Card { BackColor = Ui.TOOL_ROW, Padding = Ui.P(10), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(Ui.S(640), 0) };
                var line = Flow(Ui.TOOL_ROW);
                line.WrapContents = true;  // 入りきらなければ折り返す（flex-wrap）
                line.MaximumSize = new Size(Ui.S(620), 0);
                line.Location = new System.Drawing.Point(Ui.S(10), Ui.S(10));
                var no = Ui.Label("T" + (i + 1), Ui.F(18, true));
                no.AutoSize = false; no.Size = new Size(Ui.S(36), Ui.S(40)); no.TextAlign = ContentAlignment.MiddleLeft;
                no.Margin = new Padding(0, 0, Ui.S(12), 0);
                line.Controls.Add(no);
                r.Shape = new SelectBox(120, Model.SHAPE_KEYS.Select(k => Model.SHAPES[k]));
                int si = Array.IndexOf(Model.SHAPE_KEYS, tool.Shape);
                r.Shape.SelectedIndex = si < 0 ? Array.IndexOf(Model.SHAPE_KEYS, "none") : si;
                r.Shape.Margin = new Padding(0, 0, Ui.S(12), 0);
                line.Controls.Add(r.Shape);
                // 形ごとの欄を全部作っておき、選んだ形の欄だけ見せる（形を切り替えても入れた値が残る）
                var rect = Flow(Ui.TOOL_ROW);
                r.W = Field(rect, "幅", 90, N(tool.W)); r.H = Field(rect, "高さ", 90, N(tool.H)); Unit(rect, r.H);
                var round = Flow(Ui.TOOL_ROW);
                r.D = Field(round, "直径 φ", 90, N(tool.D)); Unit(round, r.D);
                var special = Flow(Ui.TOOL_ROW);
                r.W2 = Field(special, "幅", 90, N(tool.W)); r.H2 = Field(special, "高さ", 90, N(tool.H)); Unit(special, r.H2);
                r.Note = Field(special, "メモ", 130, tool.Note ?? "", false);
                r.Note.Box.MaxLength = 40;
                line.Controls.Add(rect); line.Controls.Add(round); line.Controls.Add(special);
                r.RectPart = rect; r.RoundPart = round; r.SpecialPart = special;
                var rr = r;
                r.Shape.Changed += () => ShowDims(rr);
                ShowDims(r);
                box.Controls.Add(line);
                rows.Add(r);
                Add(box, 8);
            }
            err = new Label { AutoSize = true, ForeColor = Ui.DANGER, Font = Ui.Bold, MinimumSize = new Size(0, Ui.S(24)), MaximumSize = new Size(Ui.S(640), 0), UseMnemonic = false };
            Add(err, 0);
            err.Margin = new Padding(0, Ui.S(2), 0, 0);
            var cancel = Btn("キャンセル");
            cancel.Pressed += () => { DialogResult = DialogResult.Cancel; Close(); };
            var ok = Btn(isNew ? "作成" : "決定", Ui.PRIMARY);
            ok.Pressed += Submit;
            Buttons(null, cancel, ok);
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Return) { e.SuppressKeyPress = true; Submit(); } };
        }

        static FlowLayoutPanel Flow(Color? back = null)
        {
            return new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = back ?? Color.White, Margin = Padding.Empty, Padding = Padding.Empty };
        }

        // 文字を入力欄の高さの真ん中にそろえて足す
        static void Mid(FlowLayoutPanel p, Label l, Control beside, int right, int left = 0)
        {
            l.Margin = new Padding(Ui.S(left), (beside.Height - l.PreferredHeight) / 2, Ui.S(right), 0);
            p.Controls.Add(l);
        }

        static InputBox Field(FlowLayoutPanel p, string label, int width, string value, bool number = true)
        {
            var box = new InputBox(width, number: number) { Margin = new Padding(0, 0, Ui.S(12), 0) };
            box.Text = value;
            Mid(p, Ui.Label(label), box, 6);
            p.Controls.Add(box);
            return box;
        }

        static void Unit(FlowLayoutPanel p, Control beside)
        {
            beside.Margin = new Padding(0, 0, Ui.S(6), 0);
            Mid(p, Ui.Label("mm"), beside, 12);
        }

        static string N(double? v) { return v == null ? "" : Js.Num(v.Value); }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (isNew) { program.Box.Focus(); program.Box.SelectAll(); }
        }

        static void ShowDims(ToolRow r)
        {
            string shape = Model.SHAPE_KEYS[r.Shape.SelectedIndex];
            r.RectPart.Visible = shape == "rect";
            r.RoundPart.Visible = shape == "round";
            r.SpecialPart.Visible = shape == "special";
        }

        void Submit()
        {
            int p;
            if (!int.TryParse(program.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out p) || p < Model.PROGRAM_MIN || p > Model.PROGRAM_MAX)
            {
                Fail("プログラム番号は " + Model.PROGRAM_MIN + "〜" + Model.PROGRAM_MAX + " の整数で入れてください。", program);
                return;
            }
            var tools = new List<Tool>();
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                string no = "T" + (i + 1);
                var t = Model.EmptyTool();
                t.Shape = Model.SHAPE_KEYS[r.Shape.SelectedIndex];
                if (t.Shape == "rect" || t.Shape == "special")
                {
                    var wb = t.Shape == "rect" ? r.W : r.W2;
                    var hb = t.Shape == "rect" ? r.H : r.H2;
                    t.W = Model.PosNum(wb.Text); t.H = Model.PosNum(hb.Text);
                    if (t.W == null || t.H == null) { Fail(no + " の幅と高さを入れてください（0 より大きい数）。", t.W == null ? wb : hb); return; }
                    if (t.Shape == "special") t.Note = r.Note.Text.Trim();
                }
                else if (t.Shape == "round")
                {
                    t.D = Model.PosNum(r.D.Text);
                    if (t.D == null) { Fail(no + " の直径を入れてください（0 より大きい数）。", r.D); return; }
                }
                tools.Add(t);
            }
            if (tools.All(t => t.Shape == "none")) { Fail("金型を 1 つ以上選んでください。", null); return; }
            Program = p;
            Tools = tools;
            DialogResult = DialogResult.OK;
            Close();
        }

        void Fail(string msg, InputBox focus)
        {
            err.Text = msg;
            if (focus != null) focus.Box.Focus();
        }
    }

    /// <summary>更新の確認: いまの版、起動時に確かめるか、いま確かめる。</summary>
    public class UpdateDialog : WebDialog
    {
        public bool CheckNow;
        public readonly CheckBox AtStart;

        public UpdateDialog(bool checkAtStart) : base("更新の確認")
        {
            Add(Ui.Para("セットプレス CAM（Windows 版）v" + Program.VERSION + "\r\n新しい版は GitHub Releases から入ります。", 16, Color.Black, 460), 12);
            AtStart = new CheckBox { Text = "起動したときに新しい版を確かめる", Checked = checkAtStart, AutoSize = true, Font = Ui.Font, BackColor = Color.White, UseMnemonic = false };
            Add(AtStart, 0);
            var close = Btn("閉じる");
            close.Pressed += () => { DialogResult = DialogResult.Cancel; Close(); };
            var now = Btn("いま確かめる", Ui.PRIMARY);
            now.Pressed += () => { CheckNow = true; DialogResult = DialogResult.OK; Close(); };
            Buttons(null, close, now);
        }
    }

    /// <summary>ダウンロードの進み具合。</summary>
    public class DownloadDialog : WebDialog
    {
        readonly Panel bar;
        double progress;
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        public string Path;
        public Exception Error;
        readonly Release release;

        public DownloadDialog(Release r) : base("更新のダウンロード", 420)
        {
            release = r;
            Add(Ui.Label(r.Name + " をダウンロードしています…"), 10);
            bar = new Panel { Size = new Size(Ui.S(420), Ui.S(12)), BackColor = Color.White };
            bar.Paint += (s, e) =>
            {
                Ui.FillRound(e.Graphics, new RectangleF(0, 0, bar.Width - 1, bar.Height - 1), bar.Height / 2f, Ui.HEAD);
                float w = (float)((bar.Width - 1) * progress);
                if (w >= bar.Height) Ui.FillRound(e.Graphics, new RectangleF(0, 0, w, bar.Height - 1), bar.Height / 2f, Ui.PRIMARY);
            };
            Add(bar);
            var cancel = Btn("キャンセル");
            cancel.Pressed += () => cts.Cancel();
            Buttons(null, cancel);
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                var p = new Progress<double>(v => { progress = Math.Max(0, Math.Min(1, v)); bar.Invalidate(); });
                Path = await Updater.Download(release, p, cts.Token);
                DialogResult = DialogResult.OK;
            }
            catch (OperationCanceledException)
            {
                DialogResult = DialogResult.Cancel;
            }
            catch (Exception ex)
            {
                Error = ex;
                DialogResult = DialogResult.Abort;
            }
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            cts.Cancel();
            base.OnFormClosing(e);
        }
    }
}
