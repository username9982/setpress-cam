// メインのウィンドウ: Web 版（index.html・style.css）と同じ並び。上にツールバー、左に割付の区画（割付のときだけ）、
// 中央に図（寸法の欄は右上に重ねる）、右にコード、下に帯。メニューは無く、ショートカットキーが使える。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace SetPressCam
{
    public class MainForm : Form
    {
        public const string APP_NAME = "セットプレス CAM";

        readonly Editor ed;
        readonly Settings settings;
        readonly DrawCanvas canvas;
        readonly Dictionary<string, FlatButton> modeButtons = new Dictionary<string, FlatButton>();
        FlatButton btnUndo, btnDelete;
        InputBox sheetW, sheetH, sheetT;
        SelectBox snap;
        // 割付の区画
        Panel layoutPanel;
        Label lpWork, lpSummary, lpWarn;
        FlatButton btnAuto, btnClear;
        Control lpMjRow;
        InputBox lpMj;
        // 寸法の欄
        Card props;
        string propsSig = "";
        // コード
        CodeView code;
        string lastCodeSel = "";
        StatusBar status;
        readonly System.Windows.Forms.Timer flashTimer = new System.Windows.Forms.Timer { Interval = 4000 };

        public MainForm()
        {
            settings = Store.LoadSettings();
            ed = new Editor(settings.Mj);
            AutoScaleMode = AutoScaleMode.None;
            Font = Ui.Font;
            BackColor = Ui.BG;
            Text = APP_NAME;
            Icon = Ui.AppIcon();
            KeyPreview = true;
            MinimumSize = new Size(Ui.S(800), Ui.S(500));
            StartPosition = FormStartPosition.Manual;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Bounds = new Rectangle(wa.X + Ui.S(40), wa.Y + Ui.S(30), Math.Min(Ui.S(1280), wa.Width - Ui.S(80)), Math.Min(Ui.S(820), wa.Height - Ui.S(60)));
            RestoreWindow();

            canvas = new DrawCanvas(ed) { Dock = DockStyle.Fill };
            canvas.SnapStep = () => settings.Snap;
            canvas.PosText += t => { status.Pos = t; status.Set(); };
            canvas.SelectionChanged += () => { UpdateUi(); canvas.Invalidate(); };

            var toolbar = BuildToolbar();
            status = new StatusBar { Ver = "v" + Program.VERSION };
            status.VersionClicked += ShowUpdateDialog;
            var codePanel = BuildCodePanel();
            layoutPanel = BuildLayoutPanel();
            var stage = new Panel { Dock = DockStyle.Fill, BackColor = Ui.BG };
            props = new Card { Visible = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            stage.Controls.Add(props);
            stage.Controls.Add(canvas);
            stage.Resize += (s, e) => PlaceProps();
            props.SizeChanged += (s, e) => PlaceProps();
            Controls.Add(stage);
            Controls.Add(layoutPanel);
            Controls.Add(codePanel);
            Controls.Add(toolbar);
            Controls.Add(status);
            stage.BringToFront();

            ed.Refreshed += () => { UpdateUi(); canvas.Invalidate(); };
            ed.SaveCurrent += () => Store.SaveCurrent(ed.Doc, ed.Dirty, null, ed.UndoStack);
            ed.Flash += Flash;
            flashTimer.Tick += (s, e) => { flashTimer.Stop(); status.Msg = ""; status.Set(); };
        }

        // ---- 起動 ----
        public string StartFile;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            bool resumed = false;
            var cur = Store.LoadCurrent();
            if (cur != null)
            {
                try
                {
                    var history = Store.LoadUndo();  // Load が履歴を空にして保存する前に読んでおく
                    var doc = Model.LoadDoc(cur.Doc);
                    SetMode("draw");
                    ed.Load(doc);
                    ed.UndoStack.AddRange(history);
                    ed.Dirty = cur.Dirty;
                    Store.SaveCurrent(ed.Doc, ed.Dirty, null, ed.UndoStack);
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
            }
            if (StartFile != null) ImportFile(StartFile, true);  // 「プログラムから開く」で渡された図面ファイル
            else if (!resumed) OpenProjectDialog(true);  // 初めて開いたときは、プログラム番号と金型から決める
            canvas.Focus();
            if (settings.CheckUpdates) CheckUpdates(false);
        }

        void RestoreWindow()
        {
            var w = settings.Window;
            if (w == null || w.Length < 5) return;
            var r = new Rectangle(w[0], w[1], Math.Max(MinimumSize.Width, w[2]), Math.Max(MinimumSize.Height, w[3]));
            if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r))) return;
            Bounds = r;
            if (w[4] != 0) WindowState = FormWindowState.Maximized;
        }

        // 作業中の図面は自動で保存してあり、次に起動したときに続きから使える（Web 版で再読み込みしたときと同じ）
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            settings.Window = new[] { r.X, r.Y, r.Width, r.Height, WindowState == FormWindowState.Maximized ? 1 : 0 };
            settings.Mj = ed.LastMj;
            Store.SaveSettings(settings);
            Store.SaveCurrent(ed.Doc, ed.Dirty, null, ed.UndoStack);
            base.OnFormClosing(e);
        }

        // ---- ツールバー（#toolbar） ----
        Control BuildToolbar()
        {
            var bar = new FlexBar(Ui.BAR);
            Func<string, Action, FlatButton> btn = (text, act) =>
            {
                var b = new FlatButton(text, null, 44, 56, 10);
                b.Pressed += act;
                return b;
            };
            Func<string, Label> lbl = t => Ui.Label(t, Ui.Font, Ui.BAR_TEXT);
            var modes = new List<Control>();
            foreach (var (key, label) in new[] { ("draw", "作図"), ("move", "移動"), ("dim", "寸法"), ("layout", "割付") })
            {
                var k = key;
                modeButtons[key] = btn(label, () => SetMode(k));
                modes.Add(modeButtons[key]);
            }
            bar.AddGroup(modes.ToArray());
            btnUndo = btn("元に戻す", () => ed.Undo());
            btnDelete = btn("削除", () => ed.DeleteSelected());
            bar.AddGroup(btnUndo, btnDelete, btn("全体表示", () => canvas.FitView()));
            sheetW = new InputBox(60);
            sheetH = new InputBox(60);
            sheetT = new InputBox(50, padX: 4);
            sheetT.Box.PlaceholderText = "厚さ";
            bar.AddGroup(lbl("板"), sheetW, lbl("×"), sheetH, lbl("×"), lbl("t"), sheetT, lbl("mm"));
            NumberInput(sheetW.Box, () => 10, () => 5000, v => { if (v == ed.Doc.Sheet.W) return; ed.Snapshot(); ed.Doc.Sheet.W = v; ed.Changed(); canvas.FitView(); });
            NumberInput(sheetH.Box, () => 10, () => 5000, v => { if (v == ed.Doc.Sheet.H) return; ed.Snapshot(); ed.Doc.Sheet.H = v; ed.Changed(); canvas.FitView(); });
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
            sheetT.Box.Leave += (s, e) => { if (sheetT.Box.Modified) { sheetT.Box.Modified = false; commitT(); } };
            Ui.ConfirmOnEnter(sheetT.Box, () => { sheetT.Box.Modified = false; commitT(); }, () => canvas);
            var steps = new[] { 0.1, 1, 5, 10 };
            snap = new SelectBox(72, steps.Select(v => Js.Num(v)));
            int si = Array.IndexOf(steps, settings.Snap);
            snap.SelectedIndex = si < 0 ? 2 : si;
            settings.Snap = steps[snap.SelectedIndex];
            snap.Changed += () => { settings.Snap = steps[snap.SelectedIndex]; Store.SaveSettings(settings); canvas.Focus(); };
            bar.AddGroup(lbl("グリッド"), snap, lbl("mm"));
            var save = new FlatButton("保存", Ui.PRIMARY, 44, 56, 10);
            save.Pressed += SaveDrawing;
            bar.AddGroup(btn("金型", () => OpenProjectDialog(false)), btn("新規", NewDrawing), btn("開く", OpenDialog), save);
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

        // ---- 割付の区画（#layout-panel） ----
        Panel BuildLayoutPanel()
        {
            var panel = new Panel { Dock = DockStyle.Left, Width = Ui.S(260), Visible = false, BackColor = Ui.PANEL };
            panel.Paint += (s, e) => { using (var p = new Pen(Ui.LINE)) e.Graphics.DrawLine(p, panel.Width - 1, 0, panel.Width - 1, panel.Height); };
            int w = 260 - 24 - 1;
            var body = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
                Padding = Ui.P(12, 10, 12, 10), BackColor = Ui.PANEL,
            };
            Func<Control, Control> add = c => { c.Margin = new Padding(0, 0, 0, Ui.S(10)); body.Controls.Add(c); return c; };
            var workRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Ui.PANEL };
            var wl = Ui.Label("ワーク", Ui.F(13), Ui.NOTE);
            lpWork = Ui.Label("", Ui.Bold);
            wl.Margin = new Padding(0, Ui.S(3), Ui.S(8), 0);
            workRow.Controls.Add(wl);
            workRow.Controls.Add(lpWork);
            add(workRow);
            btnAuto = new FlatButton("自動割付", Ui.PRIMARY) { Width = Ui.S(w) };
            btnAuto.Pressed += () =>
            {
                // コードで手直しした割付は、確かめてから割り付け直す
                var lay = ed.LayoutWork == null ? null : ed.LayoutOf(ed.LayoutWork.Value);
                if (lay != null && lay.Manual == true && !AskDialog.Confirm(this, "S" + (ed.LayoutWork + 1) + " の割付はコードで手直ししてあります。自動割付をやり直すと、手直しした所は消えます。続けますか？", "割り付け直す")) return;
                ed.AutoLayout();
                canvas.Focus();
            };
            btnClear = new FlatButton("割付を消す") { Width = Ui.S(w) };
            btnClear.Pressed += () => { ed.ClearLayout(); canvas.Focus(); };
            add(btnAuto);
            add(btnClear);
            body.ClientSizeChanged += (s, e) =>  // スクロールバーが出たら、ボタンを狭める
            {
                int bw = body.ClientSize.Width - body.Padding.Horizontal;
                if (bw > 0) { btnAuto.Width = bw; btnClear.Width = bw; }
            };
            lpSummary = Ui.Para("", 14, Color.Black, w);
            add(lpSummary);
            var mjRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Ui.PANEL };
            lpMj = new InputBox(70);
            var ml = Ui.Label("ミクロジョイント", Ui.F(13), Ui.NOTE);
            ml.Margin = new Padding(0, (lpMj.Height - ml.PreferredHeight) / 2, Ui.S(8), 0);
            var mm = Ui.Label("mm");
            mm.Margin = new Padding(Ui.S(4), (lpMj.Height - mm.PreferredHeight) / 2, 0, 0);
            mjRow.Controls.Add(ml);
            mjRow.Controls.Add(lpMj);
            mjRow.Controls.Add(mm);
            NumberInput(lpMj.Box, () => 0, () => ed.MjMax(), v => { ed.SetMj(v); settings.Mj = ed.LastMj; Store.SaveSettings(settings); });
            lpMjRow = add(mjRow);
            lpWarn = Ui.Para("", 13, Ui.WARN, w);
            add(lpWarn);
            add(Ui.Para("穴を長方形に分けて、それぞれ F3（開始点 左上 → XY 寸法 → 金型 XY 寸法）で抜きます。金型は長方形の金型のうち、穴に入るいちばん大きいものを使います。" +
                        "長方形の継ぎ目は、抜かない所にぶつからなければ金型寸法の約 1/3 重ねます。切り欠きは、外形と重なる所を単発（金型とちょうど同じ大きさ）か F3 で抜きます。" +
                        "ほかのワークにぶつからなければ外形の外へ広げ、あとから抜く F13 に金型寸法の約 1/3 かぶせます。外形が単純な長方形なら、最後に F13（開始点 左上 → 外形 XY → 金型 XY → 角 R）で輪郭を抜きます。" +
                        "値は金型寸法のぶん外側にオフセットします。ミクロジョイントを入れると、角 R を 金型寸法 + ミクロジョイント にして角を少しつなげたままにします" +
                        "（長方形の金型では、短いほうの辺の余りを F13 の前に単発で抜きます）。抜く長方形やコードの F3 をクリックすると、その 1 回で抜く範囲をハイライトします。",
                        12, Ui.NOTE, w));
            panel.Controls.Add(body);
            panel.Controls.Add(new PaneHead("割付", "穴・切り欠き・外形"));
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
            lpSummary.Visible = summary != "";
            lpMjRow.Visible = ed.LayoutWork != null;
            if (ed.LayoutWork != null && !lpMj.Box.Focused) { lpMj.Text = Js.Num(ed.MjShown()); lpMj.Box.Modified = false; }
            lpWarn.Text = string.Join("\r\n", warn.Select(x => "・" + x));
            lpWarn.Visible = warn.Count > 0;
        }

        // ---- コードの区画（#code-panel） ----
        Control BuildCodePanel()
        {
            var panel = new Panel { Dock = DockStyle.Right, Width = Ui.S(340), BackColor = Ui.PANEL, Padding = new Padding(1, 0, 0, 0) };
            panel.Paint += (s, e) => { using (var p = new Pen(Ui.LINE)) e.Graphics.DrawLine(p, 0, 0, 0, panel.Height); };
            code = new CodeView { Dock = DockStyle.Fill };
            code.FieldEdited += (work, field, text) => ed.SetF23(work, field, text);  // F23 のピッチ・列数
            code.EditDone += () => canvas.Focus();
            code.LineClicked += key =>
            {
                if (key.StartsWith("hit:"))  // F3 のまとまり: 「割付」にして抜く範囲をハイライトする
                {
                    var parts = key.Split(':');
                    if (ed.Mode != "layout") SetMode("layout");
                    ed.SelectHit(new HitRef(int.Parse(parts[1]), int.Parse(parts[2])));
                    UpdateUi();
                    canvas.Invalidate();
                    return;
                }
                var tp = key.Split(':');
                SetMode("dim");
                ed.Sel = new List<Sel> { new Sel(tp[0], int.Parse(tp[1])) };
                UpdateUi();
                canvas.Invalidate();
            };
            var copy = new FlatButton("コピー", null, 36, 0, 14);
            copy.Pressed += CopyCode;
            btnCodeEdit = new FlatButton("編集", null, 36, 0, 14);
            btnCodeEdit.Pressed += () => SetCodeEditing(true);
            var heads = new Panel { BackColor = Ui.HEAD, Height = copy.Height };
            heads.Controls.Add(btnCodeEdit);
            heads.Controls.Add(copy);
            btnCodeEdit.Location = new System.Drawing.Point(0, 0);
            copy.Location = new System.Drawing.Point(btnCodeEdit.Right + Ui.S(8), 0);
            heads.Width = copy.Right;
            panel.Controls.Add(code);
            panel.Controls.Add(BuildCodeEdit());
            panel.Controls.Add(new PaneHead("コード", "機械に入力するデータ", heads));
            return panel;
        }

        // ---- コードを文字で書き換える（「編集」→「反映」） ----
        FlatButton btnCodeEdit;
        Panel codeEdit;
        TextBox codeText;
        ListBox codeErr;
        List<Editor.CodeError> codeErrors = new List<Editor.CodeError>();
        string codeEditBase;  // 書き換えはじめたとき（反映したとき）のコード。null なら書き換えていない

        Control BuildCodeEdit()
        {
            codeEdit = new Panel { Dock = DockStyle.Fill, Visible = false, BackColor = Ui.PANEL };
            codeText = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, WordWrap = false, ScrollBars = ScrollBars.Both,
                BorderStyle = BorderStyle.None, Font = Ui.Mono, BackColor = Ui.Hex("#fffef6"),
            };
            codeText.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyCodeText(); }
                else if (e.Control && e.KeyCode == Keys.A) { e.SuppressKeyPress = true; codeText.SelectAll(); }  // 複数行の TextBox は Ctrl+A で全部を選ばない
            };
            codeErr = new ListBox
            {
                Dock = DockStyle.Bottom, Height = Ui.S(96), Visible = false, BorderStyle = BorderStyle.None, IntegralHeight = false,
                Font = Ui.F(13), ForeColor = Ui.Hex("#b3261e"), BackColor = Ui.PANEL,
            };
            codeErr.Click += (s, e) =>
            {
                // その行を選ぶ
                int i = codeErr.SelectedIndex;
                if (i < 0 || i >= codeErrors.Count) return;
                int ln = codeErrors[i].Line - 1;
                if (ln < 0 || ln >= codeText.Lines.Length) return;
                codeText.Focus();
                codeText.Select(codeText.GetFirstCharIndexFromLine(ln), codeText.Lines[ln].Length);
                codeText.ScrollToCaret();
            };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(49), BackColor = Ui.Hex("#eef1f4") };
            bar.Paint += (s, e) => { using (var p = new Pen(Ui.LINE)) e.Graphics.DrawLine(p, 0, 0, bar.Width, 0); };
            var note = Ui.Label("F3・F13・単発・T・F23 を書き換えて「反映」", Ui.F(12), Ui.NOTE);
            var cancel = new FlatButton("やめる", null, 36, 0, 14);
            cancel.Pressed += () => SetCodeEditing(false);
            var apply = new FlatButton("反映", Ui.PRIMARY, 36, 0, 14);
            apply.Pressed += ApplyCodeText;
            bar.Controls.Add(note);
            bar.Controls.Add(cancel);
            bar.Controls.Add(apply);
            bar.Layout += (s, e) =>
            {
                apply.Location = new System.Drawing.Point(bar.Width - apply.Width - Ui.S(8), (bar.Height - apply.Height) / 2);
                cancel.Location = new System.Drawing.Point(apply.Left - cancel.Width - Ui.S(8), apply.Top);
                note.Location = new System.Drawing.Point(Ui.S(8), (bar.Height - note.Height) / 2);
                note.Visible = note.Right < cancel.Left - Ui.S(4);
            };
            codeEdit.Controls.Add(codeText);
            codeEdit.Controls.Add(codeErr);
            codeEdit.Controls.Add(bar);
            return codeEdit;
        }

        static string Crlf(string s) { return s.Replace("\r\n", "\n").Replace("\n", "\r\n"); }

        void SetCodeEditing(bool on)
        {
            codeEditBase = on ? Crlf(ed.CodeText(Program.VERSION)) : null;
            codeEdit.Visible = on;
            code.Visible = !on;
            btnCodeEdit.Enabled = !on;
            if (on)
            {
                codeText.Text = codeEditBase;
                ShowCodeErrors(new List<Editor.CodeError>());
                codeText.Focus();
                codeText.Select(0, 0);
            }
            else canvas.Focus();
        }

        void ShowCodeErrors(List<Editor.CodeError> errs)
        {
            codeErrors = errs.Take(20).ToList();
            codeErr.Items.Clear();
            foreach (var e in codeErrors) codeErr.Items.Add(e.Line + " 行目: " + e.Msg);
            if (errs.Count > 20) codeErr.Items.Add("ほかに " + (errs.Count - 20) + " 件");
            codeErr.Visible = errs.Count > 0;
        }

        void ApplyCodeText()
        {
            var plan = ed.PlanCode(codeText.Text);
            if (plan.Errs != null)
            {
                ShowCodeErrors(plan.Errs);
                Flash("コードに " + plan.Errs.Count + " 件の誤りがあります");
                return;
            }
            // ワークの割付をまるごと消すときは確かめる（全部消してしまったときなど）
            var gone = plan.Plans.Where(q => q.Item2 == null).Select(q => "S" + (q.Item1 + 1)).ToList();
            if (gone.Count > 0 && !AskDialog.Confirm(this, "コードに " + string.Join("・", gone) + " の F23 がないので、" + string.Join("・", gone) + " の割付を消します。続けますか？", "消す")) return;
            bool done = ed.CommitCode(plan);
            ShowCodeErrors(new List<Editor.CodeError>());
            codeEditBase = Crlf(ed.CodeText(Program.VERSION));
            codeText.Text = codeEditBase;  // 読んだ結果を、ふだんの書き方に直して見せる
            Flash(done ? "コードを反映しました" : "変わった所はありません");
        }

        HashSet<string> SelKeys()
        {
            var keys = new HashSet<string>(ed.Sel.Select(s => s.T + ":" + s.Id));
            if (ed.LayoutHit != null) keys.Add(Editor.HitKey(ed.LayoutHit.Work, ed.LayoutHit.I));
            return keys;
        }

        void RenderCode()
        {
            var lines = ed.GenCode(Program.VERSION);
            // 文字で書き換えている途中: まだ書き換えていなければ、図面の変更に合わせて作り直す
            if (codeEditBase != null && codeText.Text == codeEditBase)
            {
                codeEditBase = Crlf(string.Join("\n", lines.Select(l => l.Text)) + "\n");
                codeText.Text = codeEditBase;
            }
            var keys = SelKeys();
            code.SetLines(lines, keys);
            // 選んだものが変わったときだけ、その行まで送る
            string sig = string.Join(",", keys);
            int first = lines.FindIndex(l => l.Key != null && keys.Contains(l.Key));
            if (sig != lastCodeSel && first >= 0) code.ScrollToLine(first);
            lastCodeSel = sig;
        }

        void CopyCode()
        {
            var text = codeEditBase != null ? codeText.Text : string.Join("\r\n", ed.GenCode(Program.VERSION).Select(l => l.Text)) + "\r\n";
            try { Clipboard.SetText(text); Flash("コピーしました"); }
            catch (Exception) { Flash("コピーできませんでした"); }
        }

        void Flash(string msg)
        {
            status.Msg = msg;
            status.Set();
            flashTimer.Stop();
            flashTimer.Start();
        }

        // ---- 寸法の欄（#props） ----
        void PlaceProps()
        {
            var stage = props.Parent;
            if (stage == null) return;
            props.Location = new System.Drawing.Point(Math.Max(0, stage.ClientSize.Width - props.Width - Ui.S(12)), Ui.S(12));
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
                var t = new TableLayoutPanel
                {
                    AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Color.White,
                    Location = new System.Drawing.Point(Ui.S(12), Ui.S(12)), Margin = Padding.Empty,
                };
                Func<Control, Control> add = c => { c.Margin = new Padding(0, 0, 0, Ui.S(8)); t.Controls.Add(c); return c; };
                var title = Ui.Para("", 16, Color.Black, 300);
                title.Font = Ui.Bold;
                title.Name = "title";
                add(title);
                for (int i = 0; i < opt.Rows.Count; i++)
                {
                    var r = opt.Rows[i];
                    var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.White };
                    var box = new InputBox(100);
                    box.Box.Name = "v" + i;
                    var pl = new Label
                    {
                        Text = r.Label, AutoSize = true, Font = Ui.Font, UseMnemonic = false,
                        MinimumSize = new Size(Ui.S(128), 0), MaximumSize = new Size(Ui.S(192), 0),
                    };
                    pl.Margin = new Padding(0, Math.Max(0, (box.Height - pl.PreferredHeight) / 2), Ui.S(6), 0);
                    row.Controls.Add(pl);
                    box.Margin = new Padding(0, 0, Ui.S(6), 0);
                    row.Controls.Add(box);
                    var unit = Ui.Label("mm", Ui.F(14), Ui.NOTE);
                    unit.Margin = new Padding(0, (box.Height - unit.PreferredHeight) / 2, Ui.S(6), 0);
                    row.Controls.Add(unit);
                    var ok = new FlatButton("決定", null, 36, 0, 10, 14);
                    ok.Margin = new Padding(0, (box.Height - ok.Height) / 2, Ui.S(6), 0);
                    row.Controls.Add(ok);
                    var badge = new Label { Name = "b" + i, AutoSize = true, Font = Ui.F(12, true), ForeColor = Ui.DIM, MinimumSize = new Size(Ui.S(30), 0), UseMnemonic = false };
                    badge.Margin = new Padding(0, (box.Height - badge.PreferredHeight) / 2, 0, 0);
                    row.Controls.Add(badge);
                    int idx = i;
                    // 値を変えて離れたときだけでなく、そのままの値でも Enter か「決定」で寸法にできる
                    Action go = () =>
                    {
                        double v = ParseFloat(box.Text);
                        box.Box.Modified = false;
                        BeginInvoke(new Action(() => { if (double.IsNaN(v)) UpdateUi(); else ed.CommitDim(idx, v); }));
                    };
                    box.Box.Leave += (s, e) => { if (box.Box.Modified) go(); };
                    Ui.ConfirmOnEnter(box.Box, go, () => canvas);
                    ok.Pressed += go;
                    add(row);
                }
                var note = Ui.Para("", 13, Ui.NOTE, 300);
                note.Name = "note";
                add(note);
                if (opt.IsDim)
                {
                    var del = new FlatButton("寸法を消す", Ui.DANGER);
                    del.Pressed += () => BeginInvoke(new Action(() => ed.DeleteSelected()));
                    add(del);
                }
                t.Controls[t.Controls.Count - 1].Margin = Padding.Empty;
                props.Controls.Add(t);
                props.Padding = Ui.P(12);
                props.ResumeLayout();
            }
            var titleL = props.Controls.Find("title", true).FirstOrDefault();
            if (titleL != null) titleL.Text = opt.Title;
            var noteL = props.Controls.Find("note", true).FirstOrDefault();
            if (noteL != null)
            {
                noteL.Text = opt.Note ?? (opt.Rows.Count > 0 ? "数字を入れて Enter か「決定」で決まります。いまの値のままでも決められます。紫の「寸法」は決めた寸法。" : "");
                noteL.Visible = noteL.Text != "";
            }
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
            btnDelete.Enabled = ed.Sel.Count > 0;
            btnUndo.Enabled = ed.UndoStack.Count > 0;
            var doc = ed.Doc;
            if (!sheetW.Box.Focused) { sheetW.Text = Js.Num(doc.Sheet.W); sheetW.Box.Modified = false; }
            if (!sheetH.Box.Focused) { sheetH.Text = Js.Num(doc.Sheet.H); sheetH.Box.Modified = false; }
            if (!sheetT.Box.Focused) { sheetT.Text = doc.Sheet.T == null ? "" : Js.Num(doc.Sheet.T.Value); sheetT.Box.Modified = false; }
            string name = doc.Name != "" ? doc.Name : "名前なし";
            Text = name + (ed.Dirty ? " *" : "") + " - " + APP_NAME;
            status.Name_ = name + (ed.Dirty ? " *" : "");
            int fixedLines = doc.Lines.Count(l => ed.Info.FixedLine(l));
            status.Count = "線 " + doc.Lines.Count + " 本（確定 " + fixedLines + "）・寸法 " + doc.Dims.Count;
            status.Prog = doc.Program == null ? "プログラム番号なし" : Model.ProgramText(doc.Program);
            var tools = doc.Tools.Select((t, i) => t.Shape == "none" ? null : "T" + (i + 1) + " " + Model.ToolText(t)).Where(x => x != null).ToList();
            status.Tools = tools.Count > 0 ? string.Join("　", tools) : "金型なし";
            status.Sel = ed.SelInfo();
            status.Set();
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
            layoutPanel.Visible = m == "layout";
            if (m != "layout") ed.LayoutHit = null;
            canvas.Cursor = m == "draw" ? Cursors.Cross : Cursors.Default;
            UpdateUi();
            canvas.Invalidate();
        }

        // ---- 新規・保存・開く（Web 版と同じく、図面はアプリの中に名前を付けて保存する） ----
        void NewDrawing()
        {
            if (ed.Dirty && !AskDialog.Confirm(this, "いまの図面は保存されていません。破棄して新しく始めますか？", "新規")) return;
            OpenProjectDialog(true);
        }

        void OpenProjectDialog(bool isNew)
        {
            using (var dlg = new ProjectDialog(isNew, ed.Doc))
            {
                if (dlg.ShowModal(this) != DialogResult.OK) { canvas.Focus(); return; }
                if (isNew)
                {
                    var doc = Model.NewDoc(ed.LastMj);
                    doc.Sheet = new Sheet { W = ed.Doc.Sheet.W, H = ed.Doc.Sheet.H, T = ed.Doc.Sheet.T };  // 板の大きさは引き継ぐ
                    doc.Program = dlg.Program;
                    doc.Tools = dlg.Tools;
                    LoadDoc(doc);
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

        void LoadDoc(Doc doc)
        {
            SetMode("draw");
            ed.Load(doc);
            canvas.FitView();
        }

        void SaveDrawing()
        {
            string name;
            using (var dlg = new SaveDialog(ed.Doc.Name))
            {
                if (dlg.ShowModal(this) != DialogResult.OK) { canvas.Focus(); return; }
                name = dlg.Value;
            }
            var store = Store.LoadStore();
            if (store.Any(x => x.Name == name) && name != ed.Doc.Name &&
                !AskDialog.Confirm(this, "「" + name + "」はすでにあります。上書きしますか？", "上書き")) return;
            ed.Doc.Name = name;
            store.RemoveAll(x => x.Name == name);
            store.Add(new Store.Entry { Name = name, SavedAt = Store.Now(), Doc = JsonNode.Parse(Model.Serialize(ed.Doc)) });
            try { Store.WriteStore(store); }
            catch (Exception ex)
            {
                AskDialog.Tell(this, "保存できませんでした: " + ex.Message);
                return;
            }
            ed.Dirty = false;
            Store.SaveCurrent(ed.Doc, ed.Dirty, null, ed.UndoStack);
            UpdateUi();
            Flash("「" + name + "」を保存しました");
            canvas.Focus();
        }

        void OpenDialog()
        {
            string open = null;
            using (var dlg = new OpenDialog(() => ed.Dirty))
            {
                dlg.Import += () => { ImportFile(null, false, dlg); };
                dlg.Export += () => ExportFile(dlg);
                dlg.CheckUpdate += () => { ShowUpdateDialog(dlg); };
                if (dlg.ShowModal(this) == DialogResult.OK) open = dlg.OpenName;
            }
            if (open != null)
            {
                var en = Store.LoadStore().FirstOrDefault(x => x.Name == open);
                if (en != null)
                {
                    try
                    {
                        var doc = Model.LoadDoc(en.Doc);
                        if (doc.Name == "") doc.Name = en.Name;
                        LoadDoc(doc);
                        Flash("「" + open + "」を開きました");
                    }
                    catch (Exception ex) { AskDialog.Tell(this, "開けませんでした: " + ex.Message); }
                }
            }
            canvas.Focus();
        }

        const string FILTER = "セットプレス CAM の図面 (*.json)|*.json|すべてのファイル (*.*)|*.*";

        /// <summary>ファイルから読み込む（Web 版で書き出したファイルも）。保存した図面に足す。
        /// openOne なら、図面が 1 つのときはそのまま開く（「プログラムから開く」で渡されたとき）。</summary>
        void ImportFile(string path, bool openOne, OpenDialog from = null)
        {
            Form owner = from ?? (Form)this;
            if (path == null)
            {
                using (var fd = new OpenFileDialog { Filter = FILTER, Title = "ファイルから読み込む", InitialDirectory = settings.LastDir ?? "" })
                {
                    if (fd.ShowDialog(owner) != DialogResult.OK) return;
                    path = fd.FileName;
                }
            }
            try
            {
                var entries = Store.ReadDrawings(path);
                if (entries.Count == 0) throw new FormatException("図面が入っていません");
                settings.LastDir = Path.GetDirectoryName(path);
                Store.SaveSettings(settings);
                var store = Store.LoadStore();
                int clash = entries.Count(e => store.Any(x => x.Name == e.Name));
                if (clash > 0 && !AskDialog.Confirm(owner, clash + " 件の図面が同じ名前で上書きされます。続けますか？", "読み込む")) return;
                foreach (var e in entries)
                {
                    store.RemoveAll(x => x.Name == e.Name);
                    store.Add(new Store.Entry { Name = e.Name, SavedAt = e.SavedAt != "" ? e.SavedAt : Store.Now(), Doc = e.Doc });
                }
                Store.WriteStore(store);
                from?.Render();
                Flash(entries.Count + " 件の図面を読み込みました");
                if (openOne && entries.Count == 1)
                {
                    if (ed.Dirty && !AskDialog.Confirm(this, "いまの図面は保存されていません。破棄して開きますか？", "開く")) return;
                    var doc = Model.LoadDoc(entries[0].Doc);
                    if (doc.Name == "") doc.Name = entries[0].Name;
                    LoadDoc(doc);
                    Flash("「" + entries[0].Name + "」を開きました");
                }
                else if (openOne) OpenDialog();
            }
            catch (Exception ex)
            {
                AskDialog.Tell(owner, "読み込めませんでした: " + ex.Message);
            }
        }

        /// <summary>ファイルに書き出す（保存した図面をすべて。Web 版の「ファイルから読み込む」で読める）。</summary>
        void ExportFile(Form owner)
        {
            var t = DateTime.Now;
            using (var fd = new SaveFileDialog
            {
                Filter = FILTER, Title = "ファイルに書き出す", DefaultExt = "json", AddExtension = true, OverwritePrompt = true,
                FileName = "setpress-cam-" + t.ToString("yyyyMMdd") + ".json",
                InitialDirectory = settings.LastDir ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            })
            {
                if (fd.ShowDialog(owner) != DialogResult.OK) return;
                try
                {
                    Store.Export(fd.FileName, Store.LoadStore());
                    settings.LastDir = Path.GetDirectoryName(fd.FileName);
                    Store.SaveSettings(settings);
                    Flash("書き出しました: " + Path.GetFileName(fd.FileName));
                }
                catch (Exception ex) { AskDialog.Tell(owner, "書き出せませんでした: " + ex.Message); }
            }
        }

        // ---- 更新の確認 ----
        void ShowUpdateDialog() { ShowUpdateDialog(this); }

        void ShowUpdateDialog(Form owner)
        {
            bool now;
            using (var dlg = new UpdateDialog(settings.CheckUpdates))
            {
                now = dlg.ShowModal(owner) == DialogResult.OK && dlg.CheckNow;
                if (dlg.AtStart.Checked != settings.CheckUpdates) { settings.CheckUpdates = dlg.AtStart.Checked; Store.SaveSettings(settings); }
            }
            if (now)
            {
                if (owner != this) owner.Close();
                CheckUpdates(true);
            }
        }

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
                    if (manual) AskDialog.Tell(this, "新しい版を確かめられませんでした。インターネットにつながっているか確かめてください。\r\n\r\n" + ex.Message, "更新の確認");
                    return;
                }
                var now = Updater.Parse(Program.VERSION);
                var latest = r == null ? null : Updater.Parse(r.Version);
                if (latest == null || latest <= now)
                {
                    if (manual) AskDialog.Tell(this, "お使いの版（v" + Program.VERSION + "）がいちばん新しい版です。", "更新の確認");
                    return;
                }
                if (!manual && settings.SkipVersion == r.Version) return;
                var buttons = manual ? new[] { "あとで", "更新する" } : new[] { "この版は知らせない", "あとで", "更新する" };
                var ans = AskDialog.Ask(this, "更新の確認",
                    "新しい版 v" + r.Version + " があります（いまは v" + Program.VERSION + "）。ダウンロードしてインストールしますか？\r\n" +
                    "インストールが終わると、アプリが起動し直します。作業中の図面は自動で保存され、続きから使えます。", buttons, buttons.Length - 1);
                if (ans == "この版は知らせない") { settings.SkipVersion = r.Version; Store.SaveSettings(settings); return; }
                if (ans != "更新する") return;
                if (!Program.Packaged)
                {
                    AskDialog.Tell(this, "開発中のアプリなので、インストーラーは実行しません。\r\n" + r.Url, "更新の確認");
                    return;
                }
                using (var dlg = new DownloadDialog(r))
                {
                    var res = dlg.ShowModal(this);
                    if (res == DialogResult.Abort && dlg.Error != null)
                    {
                        AskDialog.Tell(this, "ダウンロードできませんでした: " + dlg.Error.Message + "\r\n\r\n" + Updater.RELEASES_PAGE + " から入れることもできます。", "更新の確認");
                        return;
                    }
                    if (res != DialogResult.OK || dlg.Path == null) return;
                    Store.SaveCurrent(ed.Doc, ed.Dirty, null, ed.UndoStack);
                    try { Updater.RunInstaller(dlg.Path); }
                    catch (Exception ex)
                    {
                        AskDialog.Tell(this, "インストーラーを起動できませんでした: " + ex.Message, "更新の確認");
                        return;
                    }
                    Close();  // 作業中の図面は保存済み（保存していない印も残る）
                }
            }
            finally { checking = false; }
        }

        // ---- キー ----
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool inText = ActiveControl is TextBox || (ActiveControl is ContainerControl cc && cc.ActiveControl is TextBox) || ActiveControl is InputBox;
            switch (keyData)
            {
                case Keys.Control | Keys.N: NewDrawing(); return true;
                case Keys.Control | Keys.O: OpenDialog(); return true;
                case Keys.Control | Keys.S: SaveDrawing(); return true;
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
