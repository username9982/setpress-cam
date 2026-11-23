// メインのウィンドウ: メニュー、ツールバー、割付の区画（左）、図（中央、寸法の欄は右上に重ねる）、コード（右）、ステータスバー。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SetPressCam
{
    public class MainForm : ClassicForm
    {
        public const string APP_NAME = "セットプレス CAM";

        readonly Editor ed;
        readonly Settings settings;
        string filePath;
        readonly DrawCanvas canvas;
        readonly Dictionary<string, ToolButton> modeButtons = new Dictionary<string, ToolButton>();
        readonly Dictionary<string, MenuItem> modeItems = new Dictionary<string, MenuItem>();
        ToolButton btnUndo, btnDelete;
        MenuItem miUndo, miDelete, miCheckAtStart;
        TextBox sheetW, sheetH, sheetT;
        ComboBox snap;
        // 割付の区画
        Panel layoutPanel;
        Label lpWork, lpSummary, lpWarn, lpNote;
        Button btnAuto, btnClear;
        Panel lpMjRow;
        TextBox lpMj;
        // 寸法の欄
        Panel props;
        string propsSig = "";
        // コード
        ListBox code;
        List<CodeLine> codeLines = new List<CodeLine>();
        string lastCodeSel = "";
        // ステータスバー
        StatusPane stName, stProg, stTools, stCount, stPos, stMsg, stSel, stVer;
        readonly System.Windows.Forms.Timer flashTimer = new System.Windows.Forms.Timer { Interval = 4000 };

        public MainForm()
        {
            settings = Store.LoadSettings();
            ed = new Editor(settings.Mj);
            Text = APP_NAME;
            Icon = Ui.AppIcon();
            KeyPreview = true;
            MinimumSize = new Size(800, 500);
            StartPosition = FormStartPosition.Manual;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Bounds = new Rectangle(wa.X + 40, wa.Y + 30, Math.Min(1280, wa.Width - 80), Math.Min(820, wa.Height - 60));
            RestoreWindow();

            canvas = new DrawCanvas(ed) { Dock = DockStyle.Fill };
            canvas.SnapStep = () => settings.Snap;
            canvas.PosText += t => stPos.Text = t;
            canvas.SelectionChanged += () => { UpdateUi(); canvas.Invalidate(); };

            BuildMenu();
            var toolbar = BuildToolbar();
            var status = BuildStatus();
            var codePanel = BuildCodePanel();
            layoutPanel = BuildLayoutPanel();
            var stage = new Panel { Dock = DockStyle.Fill };
            props = new Panel { Visible = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Ui.FACE, Padding = new Padding(8, 6, 8, 6) };
            props.Paint += (s, e) => ControlPaint.DrawBorder3D(e.Graphics, props.ClientRectangle, Border3DStyle.Raised);
            stage.Controls.Add(props);
            stage.Controls.Add(canvas);
            stage.Resize += (s, e) => PlaceProps();
            props.SizeChanged += (s, e) => PlaceProps();
            var split = new Splitter { Dock = DockStyle.Right, Width = 4, BackColor = Ui.FACE };
            Controls.Add(stage);
            Controls.Add(layoutPanel);
            Controls.Add(split);
            Controls.Add(codePanel);
            Controls.Add(toolbar);
            Controls.Add(status);
            stage.BringToFront();

            ed.Refreshed += () => { UpdateUi(); canvas.Invalidate(); };
            ed.SaveCurrent += () => Store.SaveCurrent(ed.Doc, ed.Dirty, filePath, ed.UndoStack);
            ed.Flash += Flash;
            flashTimer.Tick += (s, e) => { flashTimer.Stop(); stMsg.Text = ""; };
        }

        // ---- 起動 ----
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            bool resumed = false;
            var cur = Store.LoadCurrent();
            if (cur != null)
            {
                try
                {
                    var history = Store.LoadUndo();
                    filePath = cur.File;
                    var doc = Model.LoadDoc(cur.Doc);
                    SetMode("draw");
                    ed.Load(doc);
                    ed.UndoStack.AddRange(history);
                    ed.Dirty = cur.Dirty;
                    canvas.FitView();
                    UpdateUi();
                    resumed = true;
                }
                catch (Exception) { /* 壊れていたら新しく始める */ }
            }
            if (!resumed)
            {
                SetMode("draw");
                ed.Refresh();
                canvas.FitView();
                OpenProjectDialog(true);  // 初めて開いたときは、プログラム番号と金型から決める
            }
            canvas.Focus();
            if (settings.CheckUpdates) CheckUpdates(false);
        }

        void RestoreWindow()
        {
            var w = settings.Window;
            if (w == null || w.Length < 5) return;
            var r = new Rectangle(w[0], w[1], Math.Max(800, w[2]), Math.Max(500, w[3]));
            if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r))) return;
            Bounds = r;
            if (w[4] != 0) WindowState = FormWindowState.Maximized;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !closingForUpdate && ed.Dirty && !ConfirmDiscard("閉じる")) { e.Cancel = true; return; }
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            settings.Window = new[] { r.X, r.Y, r.Width, r.Height, WindowState == FormWindowState.Maximized ? 1 : 0 };
            settings.Mj = ed.LastMj;
            Store.SaveSettings(settings);
            Store.SaveCurrent(ed.Doc, ed.Dirty, filePath, ed.UndoStack);
            base.OnFormClosing(e);
        }

        /// <summary>保存していない図面を、保存するか尋ねる。続けてよければ true。</summary>
        bool ConfirmDiscard(string action)
        {
            var r = WinFrame.Message(this, APP_NAME, "いまの図面は保存されていません。保存してから" + action + "か？",
                                     "question", new[] { "保存する", "保存しない", "キャンセル" });
            if (r == "保存する") return Save(false);
            return r == "保存しない";
        }

        // ---- メニュー ----
        void BuildMenu()
        {
            var menu = new MainMenu();
            var file = menu.MenuItems.Add("ファイル(&F)");
            file.MenuItems.Add("新規作成(&N)...\tCtrl+N", (s, e) => NewDrawing());
            file.MenuItems.Add("開く(&O)...\tCtrl+O", (s, e) => OpenFile());
            file.MenuItems.Add("上書き保存(&S)\tCtrl+S", (s, e) => Save(false));
            file.MenuItems.Add("名前を付けて保存(&A)...\tCtrl+Shift+S", (s, e) => Save(true));
            file.MenuItems.Add("-");
            file.MenuItems.Add("終了(&X)", (s, e) => Close());
            var edit = menu.MenuItems.Add("編集(&E)");
            miUndo = edit.MenuItems.Add("元に戻す(&U)\tCtrl+Z", (s, e) => ed.Undo());
            miDelete = edit.MenuItems.Add("削除(&D)\tDel", (s, e) => ed.DeleteSelected());
            edit.MenuItems.Add("-");
            edit.MenuItems.Add("コードをコピー(&C)", (s, e) => CopyCode());
            var mode = menu.MenuItems.Add("モード(&M)");
            foreach (var (key, label, sc) in new[] { ("draw", "作図(&D)", "Ctrl+1"), ("move", "移動(&M)", "Ctrl+2"), ("dim", "寸法(&S)", "Ctrl+3"), ("layout", "割付(&L)", "Ctrl+4") })
            {
                var k = key;
                var mi = mode.MenuItems.Add(label + "\t" + sc, (s, e) => SetMode(k));
                mi.RadioCheck = true;
                modeItems[key] = mi;
            }
            var view = menu.MenuItems.Add("表示(&V)");
            view.MenuItems.Add("全体表示(&F)\tHome", (s, e) => canvas.FitView());
            var set = menu.MenuItems.Add("設定(&S)");
            set.MenuItems.Add("プログラム番号と金型(&T)...", (s, e) => OpenProjectDialog(false));
            var help = menu.MenuItems.Add("ヘルプ(&H)");
            help.MenuItems.Add("更新の確認(&U)...", (s, e) => CheckUpdates(true));
            miCheckAtStart = help.MenuItems.Add("起動時に更新を確認する(&S)", (s, e) =>
            {
                settings.CheckUpdates = !settings.CheckUpdates;
                miCheckAtStart.Checked = settings.CheckUpdates;
                Store.SaveSettings(settings);
            });
            miCheckAtStart.Checked = settings.CheckUpdates;
            help.MenuItems.Add("-");
            help.MenuItems.Add("操作のしかた(&H)", (s, e) => ShowHelp());
            help.MenuItems.Add("バージョン情報(&A)", (s, e) => WinFrame.Message(this, "バージョン情報",
                APP_NAME + "  v" + Program.VERSION + "\r\n\r\nセットプレスで抜くプログラムを作る CAM（Windows 版）。\r\nWeb 版と同じ図面ファイルを使えます。\r\n\r\n" + Updater.RELEASES_PAGE));
            menu.AttachTo(this);
        }

        void ShowHelp()
        {
            WinFrame.Message(this, "操作のしかた",
                "作図: 左ボタンでなぞると、水平・垂直の線になります。描き始めの近くで離すと閉じます。\r\n" +
                "移動: 点・線・寸法・原点の名前（S1 原点）をつかんで動かします。クリックで選ぶものを足し引きします。\r\n" +
                "寸法: 点や線を 1〜2 つクリックし、右上の欄に距離を入れて Enter か「決定」。\r\n" +
                "割付: 図形をクリックして「自動割付」。抜く長方形やコードの行をクリックすると、その 1 回で抜く範囲を示します。\r\n\r\n" +
                "右ボタンか中ボタンでドラッグすると画面が動き、ホイールで拡大・縮小します。Home で全体表示。");
        }

        // ---- ツールバー ----
        Control BuildToolbar()
        {
            var bar = new ToolStripPanel();
            Func<string, string, Action, ToolButton> btn = (icon, text, act) =>
            {
                var b = new ToolButton(icon, text);
                b.Pressed += act;
                bar.Controls.Add(b);
                return b;
            };
            btn("new", "新規", NewDrawing);
            btn("open", "開く", OpenFile);
            btn("save", "保存", () => Save(false));
            bar.Controls.Add(new ToolSeparator());
            foreach (var (key, icon, label) in new[] { ("draw", "draw", "作図"), ("move", "move", "移動"), ("dim", "dim", "寸法"), ("layout", "layout", "割付") })
            {
                var k = key;
                modeButtons[key] = btn(icon, label, () => SetMode(k));
            }
            bar.Controls.Add(new ToolSeparator());
            btnUndo = btn("undo", "元に戻す", () => ed.Undo());
            btnDelete = btn("delete", "削除", () => ed.DeleteSelected());
            btn("fit", "全体表示", () => canvas.FitView());
            bar.Controls.Add(new ToolSeparator());
            bar.Controls.Add(Ui.Text("板"));
            sheetW = Ui.NumBox(5); bar.Controls.Add(sheetW);
            bar.Controls.Add(Ui.Text("×"));
            sheetH = Ui.NumBox(5); bar.Controls.Add(sheetH);
            bar.Controls.Add(Ui.Text("× t"));
            sheetT = Ui.NumBox(4); bar.Controls.Add(sheetT);
            bar.Controls.Add(Ui.Text("mm"));
            NumberInput(sheetW, () => 10, () => 5000, v => { if (v == ed.Doc.Sheet.W) return; ed.Snapshot(); ed.Doc.Sheet.W = v; ed.Changed(); canvas.FitView(); });
            NumberInput(sheetH, () => 10, () => 5000, v => { if (v == ed.Doc.Sheet.H) return; ed.Snapshot(); ed.Doc.Sheet.H = v; ed.Changed(); canvas.FitView(); });
            // 板厚: 0.1〜50 mm。空にすると未設定に戻す
            Action commitT = () =>
            {
                string s = sheetT.Text.Trim();
                double? v = s == "" ? (double?)null : ParseFloat(s);
                if (v != null && double.IsNaN(v.Value)) { UpdateUi(); return; }
                if (v != null) v = Editor.Round(Math.Min(Math.Max(v.Value, 0.1), 50));
                if (v == ed.Doc.Sheet.T) { UpdateUi(); return; }
                ed.Snapshot(); ed.Doc.Sheet.T = v; ed.Changed();
            };
            sheetT.Leave += (s, e) => { if (sheetT.Modified) commitT(); };
            Ui.ConfirmOnEnter(sheetT, () => { commitT(); sheetT.Modified = false; }, () => canvas);
            bar.Controls.Add(new ToolSeparator());
            bar.Controls.Add(Ui.Text("グリッド"));
            snap = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.System, Width = 52, Margin = new Padding(2, 2, 2, 0) };
            var steps = new[] { 0.1, 1, 5, 10 };
            snap.Items.AddRange(steps.Select(v => (object)Js.Num(v)).ToArray());
            int si = Array.IndexOf(steps, settings.Snap);
            snap.SelectedIndex = si < 0 ? 2 : si;
            settings.Snap = steps[snap.SelectedIndex];
            snap.SelectedIndexChanged += (s, e) => { settings.Snap = steps[snap.SelectedIndex]; Store.SaveSettings(settings); canvas.Focus(); };
            bar.Controls.Add(snap);
            bar.Controls.Add(Ui.Text("mm"));
            bar.Controls.Add(new ToolSeparator());
            btn("tools", "金型", () => OpenProjectDialog(false));
            return bar;
        }

        /// <summary>JavaScript の parseFloat（先頭の数だけ読む。読めなければ NaN）。</summary>
        public static double ParseFloat(string s)
        {
            var m = Regex.Match(s ?? "", @"^\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?");
            double v;
            return m.Success && double.TryParse(m.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
        }

        // 数値の入力欄: Enter・フォーカスが外れたときに確定し、範囲内に直す
        void NumberInput(TextBox box, Func<double> min, Func<double> max, Action<double> apply)
        {
            Action commit = () =>
            {
                double v = ParseFloat(box.Text);
                if (double.IsNaN(v)) { UpdateUi(); return; }
                v = Editor.Round(Math.Min(Math.Max(v, min()), max()));
                apply(v);
                UpdateUi();
            };
            box.Leave += (s, e) => { if (box.Modified) { box.Modified = false; commit(); } };
            Ui.ConfirmOnEnter(box, () => { box.Modified = false; commit(); }, () => canvas);
        }

        // ---- 割付の区画 ----
        Panel BuildLayoutPanel()
        {
            var panel = new Panel { Dock = DockStyle.Left, Width = 230, Visible = false, BackColor = Ui.FACE };
            var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(6, 6, 4, 6) };
            int w = 206;
            var workRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
            workRow.Controls.Add(new Label { Text = "ワーク", AutoSize = true, ForeColor = Ui.GREY_TEXT, Margin = new Padding(0, 2, 8, 0) });
            lpWork = new Label { AutoSize = true, Font = new Font(Ui.Font, FontStyle.Bold), Margin = new Padding(0, 2, 0, 0) };
            workRow.Controls.Add(lpWork);
            body.Controls.Add(workRow);
            btnAuto = new Button { Text = "自動割付", Width = w, Height = 26, Margin = new Padding(0, 0, 0, 4) };
            btnAuto.Click += (s, e) => { ed.AutoLayout(); canvas.Focus(); };
            btnClear = new Button { Text = "割付を消す", Width = w, Height = 24, Margin = new Padding(0, 0, 0, 8) };
            btnClear.Click += (s, e) => { ed.ClearLayout(); canvas.Focus(); };
            body.Controls.Add(btnAuto);
            body.Controls.Add(btnClear);
            lpSummary = new Label { AutoSize = true, MaximumSize = new Size(w, 0), Margin = new Padding(0, 0, 0, 6) };
            body.Controls.Add(lpSummary);
            lpMjRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
            lpMjRow.Controls.Add(new Label { Text = "ミクロジョイント", AutoSize = true, ForeColor = Ui.GREY_TEXT, Margin = new Padding(0, 5, 4, 0) });
            lpMj = Ui.NumBox(5);
            lpMjRow.Controls.Add(lpMj);
            lpMjRow.Controls.Add(Ui.Text("mm"));
            NumberInput(lpMj, () => 0, () => ed.MjMax(), v => { ed.SetMj(v); settings.Mj = ed.LastMj; Store.SaveSettings(settings); });
            body.Controls.Add(lpMjRow);
            lpWarn = new Label { AutoSize = true, MaximumSize = new Size(w, 0), ForeColor = Ui.Hex("#b3261e"), Margin = new Padding(0, 0, 0, 8) };
            body.Controls.Add(lpWarn);
            lpNote = new Label
            {
                AutoSize = true, MaximumSize = new Size(w, 0), ForeColor = Ui.GREY_TEXT,
                Text = "穴を長方形に分けて、それぞれ F3（開始点 左上 → XY 寸法 → 金型 XY 寸法）で抜きます。金型は長方形の金型のうち、穴に入るいちばん大きいものを使います。" +
                       "長方形の継ぎ目は、抜かない所にぶつからなければ金型寸法の約 1/3 重ねます。切り欠きは、外形と重なる所を単発（金型とちょうど同じ大きさ）か F3 で抜きます。" +
                       "ほかのワークにぶつからなければ外形の外へ広げ、あとから抜く F13 に金型寸法の約 1/3 かぶせます。最後に F13（開始点 左上 → 外形 XY → 金型 XY → 角 R）で輪郭を抜きます。" +
                       "値は金型寸法のぶん外側にオフセットします。ミクロジョイントを入れると、角 R を 金型寸法 + ミクロジョイント にして角を少しつなげたままにします" +
                       "（長方形の金型では、短いほうの辺の余りを F13 の前に単発で抜きます）。抜く長方形やコードの行をクリックすると、その 1 回で抜く範囲を示します。",
            };
            body.Controls.Add(lpNote);
            panel.Controls.Add(body);
            panel.Controls.Add(new PaneHeader("割付", "穴・切り欠き・外形"));
            panel.Paint += (s, e) => e.Graphics.DrawLine(SystemPens.ControlDark, panel.Width - 1, 0, panel.Width - 1, panel.Height);
            return panel;
        }

        void UpdateLayoutPanel()
        {
            if (!layoutPanel.Visible) return;
            string work, summary;
            List<string> warn;
            ed.LayoutPanelInfo(out work, out summary, out warn);
            lpWork.Text = work;
            btnAuto.Enabled = ed.LayoutWork != null;
            btnClear.Enabled = ed.LayoutWork != null && ed.LayoutOf(ed.LayoutWork.Value) != null;
            lpSummary.Text = summary;
            lpMjRow.Visible = ed.LayoutWork != null;
            if (ed.LayoutWork != null && !lpMj.Focused) { lpMj.Text = Js.Num(ed.MjShown()); lpMj.Modified = false; }
            lpWarn.Text = string.Join("\r\n", warn.Select(x => "・" + x));
            lpWarn.Visible = warn.Count > 0;
        }

        // ---- コードの区画 ----
        Control BuildCodePanel()
        {
            var panel = new Panel { Dock = DockStyle.Right, Width = 380, BackColor = Ui.FACE };
            code = new ListBox
            {
                Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, Font = Ui.Mono, IntegralHeight = false,
                BorderStyle = BorderStyle.Fixed3D, HorizontalScrollbar = true, SelectionMode = SelectionMode.None,
            };
            code.ItemHeight = Ui.Mono.Height + 3;
            code.DrawItem += DrawCodeLine;
            code.MouseClick += (s, e) =>
            {
                int i = code.IndexFromPoint(e.Location);
                if (i < 0 || i >= codeLines.Count || codeLines[i].Key == null) return;
                var key = codeLines[i].Key;
                if (key.StartsWith("hit:"))
                {
                    var parts = key.Split(':');
                    if (ed.Mode != "layout") SetMode("layout");
                    ed.SelectHit(new HitRef(int.Parse(parts[1]), int.Parse(parts[2])));
                    UpdateUi();
                    canvas.Invalidate();
                }
            };
            var head = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = Ui.FACE };
            var copy = new ToolButton("copy", "コピー") { Anchor = AnchorStyles.Top | AnchorStyles.Right };
            copy.Pressed += CopyCode;
            var hdr = new PaneHeader("コード", "機械に入力するデータ") { Dock = DockStyle.Fill };
            head.Controls.Add(hdr);
            head.Controls.Add(copy);
            copy.Location = new System.Drawing.Point(head.Width - copy.Width - 2, 1);
            copy.BringToFront();
            hdr.Height = 26;
            panel.Controls.Add(code);
            panel.Controls.Add(head);
            return panel;
        }

        void DrawCodeLine(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= codeLines.Count) return;
            var l = codeLines[e.Index];
            bool on = l.Key != null && SelKeys().Contains(l.Key);
            var g = e.Graphics;
            using (var b = new SolidBrush(on ? Ui.Hex("#fde7c8") : Color.White)) g.FillRectangle(b, e.Bounds);
            string no = (e.Index + 1).ToString();
            int numW = TextRenderer.MeasureText("0000", Ui.Mono).Width;
            TextRenderer.DrawText(g, no, Ui.Mono, new Rectangle(e.Bounds.X, e.Bounds.Y, numW, e.Bounds.Height), Ui.Hex("#9aa5b1"),
                                  TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            var col = l.Comment ? Ui.Hex("#5f7a52") : Ui.Hex("#1d2b38");
            if (on) col = Ui.Hex("#8a4b00");
            TextRenderer.DrawText(g, l.Text, Ui.Mono, new Rectangle(e.Bounds.X + numW + 8, e.Bounds.Y, 4000, e.Bounds.Height), col,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (on) using (var p = new Pen(Ui.Hex("#e8890c"), 3)) g.DrawLine(p, e.Bounds.X + 1, e.Bounds.Y, e.Bounds.X + 1, e.Bounds.Bottom);
        }

        HashSet<string> SelKeys()
        {
            var keys = new HashSet<string>(ed.Sel.Select(s => s.T + ":" + s.Id));
            if (ed.LayoutHit != null) keys.Add(Editor.HitKey(ed.LayoutHit.Work, ed.LayoutHit.I));
            return keys;
        }

        void RenderCode()
        {
            codeLines = ed.GenCode(Program.VERSION);
            int top = code.TopIndex;
            code.BeginUpdate();
            if (code.Items.Count != codeLines.Count)
            {
                code.Items.Clear();
                code.Items.AddRange(codeLines.Select(l => (object)l.Text).ToArray());
            }
            else for (int i = 0; i < codeLines.Count; i++) if ((string)code.Items[i] != codeLines[i].Text) code.Items[i] = codeLines[i].Text;
            // 横スクロールの幅
            int numW = TextRenderer.MeasureText("0000", Ui.Mono).Width + 8;
            int maxW = codeLines.Count == 0 ? 0 : codeLines.Max(l => TextRenderer.MeasureText(l.Text, Ui.Mono).Width);
            code.HorizontalExtent = numW + maxW + 8;
            if (top < code.Items.Count) code.TopIndex = top;
            code.EndUpdate();
            code.Invalidate();
            // 選んだものが変わったときだけ、その行まで送る
            var keys = SelKeys();
            string sig = string.Join(",", keys);
            int first = codeLines.FindIndex(l => l.Key != null && keys.Contains(l.Key));
            if (sig != lastCodeSel && first >= 0)
            {
                int visible = Math.Max(1, code.ClientSize.Height / code.ItemHeight);
                if (first < code.TopIndex) code.TopIndex = first;
                else if (first >= code.TopIndex + visible) code.TopIndex = Math.Max(0, first - visible + 3);
            }
            lastCodeSel = sig;
        }

        void CopyCode()
        {
            var text = string.Join("\r\n", ed.GenCode(Program.VERSION).Select(l => l.Text)) + "\r\n";
            try { Clipboard.SetText(text); Flash("コピーしました"); }
            catch (Exception) { Flash("コピーできませんでした"); }
        }

        // ---- ステータスバー ----
        Control BuildStatus()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 24, ColumnCount = 8, RowCount = 1, BackColor = Ui.FACE, Padding = new Padding(2, 1, 0, 1) };
            Func<StatusPane> pane = () => new StatusPane { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 2, 0) };
            stName = pane(); stProg = pane(); stTools = pane(); stCount = pane(); stPos = pane(); stMsg = pane(); stSel = pane(); stVer = pane();
            var widths = new[] { 150, 60, 250, 170, 140, -1, 150, 64 };
            var panes = new[] { stName, stProg, stTools, stCount, stPos, stMsg, stSel, stVer };
            for (int i = 0; i < panes.Length; i++)
            {
                t.ColumnStyles.Add(widths[i] < 0 ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.Absolute, widths[i]));
                t.Controls.Add(panes[i], i, 0);
            }
            stVer.Text = "v" + Program.VERSION;
            return t;
        }

        void Flash(string msg)
        {
            stMsg.Text = msg;
            flashTimer.Stop();
            flashTimer.Start();
        }

        // ---- 寸法の欄 ----
        void PlaceProps()
        {
            var stage = props.Parent;
            if (stage == null) return;
            props.Location = new System.Drawing.Point(Math.Max(0, stage.ClientSize.Width - props.Width - 12), 12);
        }

        void UpdateProps()
        {
            var opt = ed.Mode == "dim" ? ed.GetDimOptions() : null;
            props.Visible = opt != null;
            if (opt == null) { propsSig = ""; return; }
            string sig = string.Join(";", ed.Sel.Select(s => s.T + s.Id));
            bool rebuild = sig != propsSig;
            propsSig = sig;
            if (rebuild)
            {
                props.SuspendLayout();
                foreach (Control c in props.Controls.Cast<Control>().ToList()) c.Dispose();
                props.Controls.Clear();
                var t = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Location = new System.Drawing.Point(8, 6), BackColor = Ui.FACE };
                t.Controls.Add(new Label { Name = "title", AutoSize = true, MaximumSize = new Size(330, 0), Font = new Font(Ui.Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) });
                for (int i = 0; i < opt.Rows.Count; i++)
                {
                    var r = opt.Rows[i];
                    var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
                    row.Controls.Add(new Label { Text = r.Label, AutoSize = false, Width = 110, Height = 20, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 2, 4, 0) });
                    var box = Ui.NumBox(8);
                    box.Name = "v" + i;
                    row.Controls.Add(box);
                    row.Controls.Add(Ui.Text("mm"));
                    var ok = new Button { Text = "決定", AutoSize = true, MinimumSize = new Size(48, 23), Margin = new Padding(6, 0, 4, 0) };
                    row.Controls.Add(ok);
                    var badge = new Label { Name = "b" + i, AutoSize = true, ForeColor = Ui.Hex("#7a4fb3"), Font = new Font(Ui.Font, FontStyle.Bold), Margin = new Padding(2, 6, 0, 0) };
                    row.Controls.Add(badge);
                    int idx = i;
                    // 値を変えて離れたときだけでなく、そのままの値でも Enter か「決定」で寸法にできる
                    Action go = () =>
                    {
                        double v = ParseFloat(box.Text);
                        box.Modified = false;
                        BeginInvoke(new Action(() => { if (double.IsNaN(v)) UpdateUi(); else ed.CommitDim(idx, v); }));
                    };
                    box.Leave += (s, e) => { if (box.Modified) go(); };
                    Ui.ConfirmOnEnter(box, go, () => canvas);
                    ok.Click += (s, e) => go();
                    t.Controls.Add(row);
                }
                t.Controls.Add(new Label { Name = "note", AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = Ui.GREY_TEXT, Margin = new Padding(0, 2, 0, 2) });
                if (opt.IsDim)
                {
                    var del = new Button { Text = "寸法を消す", AutoSize = true, MinimumSize = new Size(75, 23), Margin = new Padding(0, 4, 0, 0) };
                    del.Click += (s, e) => BeginInvoke(new Action(() => ed.DeleteSelected()));
                    t.Controls.Add(del);
                }
                props.Controls.Add(t);
                props.ResumeLayout();
            }
            var title = props.Controls.Find("title", true).FirstOrDefault();
            if (title != null) title.Text = opt.Title;
            var note = props.Controls.Find("note", true).FirstOrDefault();
            if (note != null) note.Text = opt.Note ?? (opt.Rows.Count > 0 ? "数字を入れて Enter か「決定」で決まります。いまの値のままでも決められます。紫の「寸法」は決めた寸法。" : "");
            for (int i = 0; i < opt.Rows.Count; i++)
            {
                var box = props.Controls.Find("v" + i, true).FirstOrDefault() as TextBox;
                var badge = props.Controls.Find("b" + i, true).FirstOrDefault();
                if (box == null) continue;
                if (!box.Focused) { box.Text = Js.Num(ed.RowValue(opt.Rows[i])); box.Modified = false; }
                if (badge != null) badge.Text = ed.RowHasDim(opt.Rows[i]) ? "寸法" : "";
            }
            PlaceProps();
            props.BringToFront();
        }

        // ---- 全体の更新 ----
        void UpdateUi()
        {
            ed.DropMissingSelection();
            btnDelete.On = ed.Sel.Count > 0;
            btnUndo.On = ed.UndoStack.Count > 0;
            miDelete.Enabled = ed.Sel.Count > 0;
            miUndo.Enabled = ed.UndoStack.Count > 0;
            var doc = ed.Doc;
            if (!sheetW.Focused) { sheetW.Text = Js.Num(doc.Sheet.W); sheetW.Modified = false; }
            if (!sheetH.Focused) { sheetH.Text = Js.Num(doc.Sheet.H); sheetH.Modified = false; }
            if (!sheetT.Focused) { sheetT.Text = doc.Sheet.T == null ? "" : Js.Num(doc.Sheet.T.Value); sheetT.Modified = false; }
            string name = filePath != null ? Path.GetFileNameWithoutExtension(filePath) : (doc.Name != "" ? doc.Name : "名前なし");
            Text = name + (ed.Dirty ? " *" : "") + " - " + APP_NAME;
            stName.Text = name + (ed.Dirty ? " *" : "");
            int fixedLines = doc.Lines.Count(l => ed.Info.FixedLine(l));
            stCount.Text = "線 " + doc.Lines.Count + " 本（確定 " + fixedLines + "）・寸法 " + doc.Dims.Count;
            stProg.Text = doc.Program == null ? "番号なし" : Model.ProgramText(doc.Program);
            var tools = doc.Tools.Select((t, i) => t.Shape == "none" ? null : "T" + (i + 1) + " " + Model.ToolText(t)).Where(x => x != null).ToList();
            stTools.Text = tools.Count > 0 ? string.Join("　", tools) : "金型なし";
            stSel.Text = ed.SelInfo();
            UpdateProps();
            UpdateLayoutPanel();
            RenderCode();
        }

        // ---- モード ----
        void SetMode(string m)
        {
            ed.Mode = m;
            if (ed.Sel.Count > 0) ed.Sel = new List<Sel>();  // 寸法と移動では選び方が違うので、切り替えたら外す
            foreach (var kv in modeButtons) kv.Value.Active = kv.Key == m;
            foreach (var kv in modeItems) kv.Value.Checked = kv.Key == m;
            layoutPanel.Visible = m == "layout";
            if (m != "layout") ed.LayoutHit = null;
            canvas.Cursor = m == "draw" ? Cursors.Cross : Cursors.Default;
            UpdateUi();
            canvas.Invalidate();
        }

        // ---- ファイル ----
        void NewDrawing()
        {
            if (ed.Dirty && !ConfirmDiscard("新しく始めます")) return;
            OpenProjectDialog(true);
        }

        void OpenProjectDialog(bool isNew)
        {
            using (var dlg = new ProjectDialog(isNew, ed.Doc))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (isNew)
                {
                    var doc = Model.NewDoc(ed.LastMj);
                    doc.Sheet = new Sheet { W = ed.Doc.Sheet.W, H = ed.Doc.Sheet.H, T = ed.Doc.Sheet.T };  // 板の大きさは引き継ぐ
                    doc.Program = dlg.Program;
                    doc.Tools = dlg.Tools;
                    filePath = null;
                    SetMode("draw");
                    ed.Load(doc);
                    canvas.FitView();
                    Flash(Model.ProgramText(dlg.Program) + " を作りました");
                }
                else
                {
                    ed.Snapshot();
                    ed.Doc.Program = dlg.Program;
                    ed.Doc.Tools = dlg.Tools;
                    ed.Changed();
                }
            }
            canvas.Focus();
        }

        const string FILTER = "セットプレス CAM の図面 (*.json)|*.json|すべてのファイル (*.*)|*.*";

        void OpenFile()
        {
            if (ed.Dirty && !ConfirmDiscard("開きます")) return;
            string path;
            using (var dlg = new OpenFileDialog { Filter = FILTER, Title = "開く", InitialDirectory = settings.LastDir ?? "" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                path = dlg.FileName;
            }
            OpenPath(path);
        }

        public void OpenPath(string path)
        {
            try
            {
                var entries = Store.ReadDrawings(path);
                if (entries.Count == 0) throw new FormatException("図面が入っていません");
                int pick = 0;
                if (entries.Count > 1)
                    using (var dlg = new PickDialog(entries))
                    {
                        if (dlg.ShowDialog(this) != DialogResult.OK) return;
                        pick = dlg.Index;
                    }
                var doc = Model.LoadDoc(entries[pick].Doc);
                if (doc.Name == "") doc.Name = entries[pick].Name;
                // 図面が 1 つのファイルは上書き保存できる。いくつも入ったファイル（Web 版の書き出し）は、保存するとき名前を聞く
                filePath = entries.Count == 1 ? path : null;
                settings.LastDir = Path.GetDirectoryName(path);
                Store.SaveSettings(settings);
                SetMode("draw");
                ed.Load(doc);
                canvas.FitView();
                Flash("「" + entries[pick].Name + "」を開きました");
            }
            catch (Exception ex)
            {
                WinFrame.Message(this, APP_NAME, "読み込めませんでした: " + ex.Message, "error");
            }
        }

        /// <summary>保存する。saveAs か、まだファイルがなければ名前を聞く。保存できたら true。</summary>
        bool Save(bool saveAs)
        {
            string path = filePath;
            if (saveAs || path == null)
            {
                using (var dlg = new SaveFileDialog { Filter = FILTER, Title = "名前を付けて保存", DefaultExt = "json", AddExtension = true, OverwritePrompt = true })
                {
                    dlg.InitialDirectory = settings.LastDir ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    string baseName = ed.Doc.Name != "" ? ed.Doc.Name : (ed.Doc.Program != null ? Model.ProgramText(ed.Doc.Program) : "図面");
                    dlg.FileName = string.Concat(baseName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                    if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                    path = dlg.FileName;
                }
            }
            try
            {
                string name = Path.GetFileNameWithoutExtension(path);
                ed.Doc.Name = name;
                Store.WriteDrawing(path, name, ed.Doc);
                filePath = path;
                settings.LastDir = Path.GetDirectoryName(path);
                Store.SaveSettings(settings);
                ed.Dirty = false;
                Store.SaveCurrent(ed.Doc, ed.Dirty, filePath, ed.UndoStack);
                UpdateUi();
                Flash("「" + name + "」を保存しました");
                return true;
            }
            catch (Exception ex)
            {
                WinFrame.Message(this, APP_NAME, "保存できませんでした: " + ex.Message, "error");
                return false;
            }
        }

        // ---- 更新の確認 ----
        bool checking;

        async void CheckUpdates(bool manual)
        {
            if (checking) return;
            checking = true;
            try
            {
                Release r;
                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                        r = await Updater.Latest(cts.Token);
                }
                catch (Exception ex)
                {
                    if (manual) WinFrame.Message(this, "更新の確認", "新しい版を確かめられませんでした。\r\nインターネットにつながっているか確かめてください。\r\n\r\n" + ex.Message, "warning");
                    return;
                }
                var now = Updater.Parse(Program.VERSION);
                var latest = r == null ? null : Updater.Parse(r.Version);
                if (latest == null || latest <= now)
                {
                    if (manual) WinFrame.Message(this, "更新の確認", "お使いの版（v" + Program.VERSION + "）がいちばん新しい版です。");
                    return;
                }
                if (!manual && settings.SkipVersion == r.Version) return;
                var buttons = manual ? new[] { "更新する", "あとで" } : new[] { "更新する", "この版は知らせない", "あとで" };
                var ans = WinFrame.Message(this, "更新の確認",
                    "新しい版 v" + r.Version + " があります（いまは v" + Program.VERSION + "）。\r\n" +
                    "ダウンロードしてインストールしますか？ インストールが終わると、アプリが起動し直します。\r\n" +
                    "作業中の図面は自動で保存され、起動し直したあとに続きから使えます。", "question", buttons);
                if (ans == "この版は知らせない") { settings.SkipVersion = r.Version; Store.SaveSettings(settings); return; }
                if (ans != "更新する") return;
                if (!Program.Packaged)
                {
                    WinFrame.Message(this, "更新の確認", "開発中のアプリなので、インストーラーは実行しません。\r\n" + r.Url, "info");
                    return;
                }
                using (var dlg = new DownloadDialog(r))
                {
                    var res = dlg.ShowDialog(this);
                    if (res == DialogResult.Abort && dlg.Error != null)
                    {
                        WinFrame.Message(this, "更新の確認", "ダウンロードできませんでした: " + dlg.Error.Message + "\r\n\r\n" + Updater.RELEASES_PAGE + " から入れることもできます。", "error");
                        return;
                    }
                    if (res != DialogResult.OK || dlg.Path == null) return;
                    Store.SaveCurrent(ed.Doc, ed.Dirty, filePath, ed.UndoStack);
                    try { Updater.RunInstaller(dlg.Path); }
                    catch (Exception ex)
                    {
                        WinFrame.Message(this, "更新の確認", "インストーラーを起動できませんでした: " + ex.Message, "error");
                        return;
                    }
                    // 作業中の図面は保存済み（保存していない印も残る）なので、尋ねずに閉じる
                    settings.Mj = ed.LastMj;
                    Store.SaveSettings(settings);
                    closingForUpdate = true;
                    Close();
                }
            }
            finally { checking = false; }
        }

        bool closingForUpdate;

        // ---- キー ----
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool inText = ActiveControl is TextBox || ActiveControl is ComboBox || (ActiveControl is ContainerControl cc && cc.ActiveControl is TextBox);
            switch (keyData)
            {
                case Keys.Control | Keys.N: NewDrawing(); return true;
                case Keys.Control | Keys.O: OpenFile(); return true;
                case Keys.Control | Keys.S: Save(false); return true;
                case Keys.Control | Keys.Shift | Keys.S: Save(true); return true;
                case Keys.Control | Keys.D1: SetMode("draw"); return true;
                case Keys.Control | Keys.D2: SetMode("move"); return true;
                case Keys.Control | Keys.D3: SetMode("dim"); return true;
                case Keys.Control | Keys.D4: SetMode("layout"); return true;
            }
            if (!inText)
            {
                switch (keyData)
                {
                    case Keys.Control | Keys.Z: ed.Undo(); return true;
                    case Keys.Delete: ed.DeleteSelected(); return true;
                    case Keys.Home: canvas.FitView(); return true;
                    case Keys.Escape:
                        if (ed.Sel.Count > 0 || ed.LayoutHit != null) { ed.Sel = new List<Sel>(); ed.LayoutHit = null; UpdateUi(); canvas.Invalidate(); return true; }
                        break;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
