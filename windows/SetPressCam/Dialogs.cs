// ダイアログ: プログラム番号と金型（新規作成・「金型」）、ファイルの中の図面を選ぶ。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace SetPressCam
{
    /// <summary>プログラム番号と金型 T1〜T3。新規作成のときと、「金型」のとき。</summary>
    public class ProjectDialog : ClassicDialog
    {
        readonly TextBox program;
        readonly List<ToolRow> rows = new List<ToolRow>();
        readonly Label err;
        public int Program;
        public List<Tool> Tools;

        class ToolRow
        {
            public ComboBox Shape;
            public TextBox W, H, D, Note;
            public Control RectPart, RoundPart, NotePart;
        }

        public ProjectDialog(bool isNew, Doc doc) : base(isNew ? "新規作成" : "プログラム番号と金型")
        {
            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(12, 10, 12, 10) };
            var prog = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
            prog.Controls.Add(Ui.Text("プログラム番号 O"));
            program = Ui.NumBox(5);
            program.Text = isNew || doc.Program == null ? "" : doc.Program.Value.ToString(CultureInfo.InvariantCulture);
            prog.Controls.Add(program);
            prog.Controls.Add(Ui.Text("（1〜9999）"));
            t.Controls.Add(prog);
            var group = new GroupBox { Text = "使用する金型", AutoSize = true, Padding = new Padding(6, 4, 6, 4), Margin = new Padding(0, 0, 0, 6) };
            var rowsPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill };
            for (int i = 0; i < Model.TOOL_COUNT; i++)
            {
                var tool = i < doc.Tools.Count ? doc.Tools[i] : Model.EmptyTool();
                var r = new ToolRow();
                var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
                line.Controls.Add(new Label { Text = "T" + (i + 1), AutoSize = true, Margin = new Padding(0, 6, 6, 0), Font = new Font(Ui.Font, FontStyle.Bold) });
                r.Shape = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.System, Width = 80, Margin = new Padding(2, 2, 6, 0) };
                r.Shape.Items.AddRange(Model.SHAPE_KEYS.Select(k => (object)Model.SHAPES[k]).ToArray());
                r.Shape.SelectedIndex = Array.IndexOf(Model.SHAPE_KEYS, tool.Shape) < 0 ? 3 : Array.IndexOf(Model.SHAPE_KEYS, tool.Shape);
                line.Controls.Add(r.Shape);
                var rect = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
                rect.Controls.Add(Ui.Text("幅"));
                r.W = Ui.NumBox(6); r.W.Text = N(tool.W); rect.Controls.Add(r.W);
                rect.Controls.Add(Ui.Text("高さ"));
                r.H = Ui.NumBox(6); r.H.Text = N(tool.H); rect.Controls.Add(r.H);
                rect.Controls.Add(Ui.Text("mm"));
                var round = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
                round.Controls.Add(Ui.Text("直径 φ"));
                r.D = Ui.NumBox(6); r.D.Text = N(tool.D); round.Controls.Add(r.D);
                round.Controls.Add(Ui.Text("mm"));
                var note = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
                note.Controls.Add(Ui.Text("メモ"));
                r.Note = new TextBox { BorderStyle = BorderStyle.Fixed3D, Width = 140, MaxLength = 40, Text = tool.Note ?? "", Margin = new Padding(2, 2, 2, 0) };
                note.Controls.Add(r.Note);
                line.Controls.Add(rect);
                line.Controls.Add(round);
                line.Controls.Add(note);
                r.RectPart = rect; r.RoundPart = round; r.NotePart = note;
                var rr = r;
                r.Shape.SelectedIndexChanged += (s, e) => ShowDims(rr);
                ShowDims(r);
                rows.Add(r);
                rowsPanel.Controls.Add(line);
            }
            group.Controls.Add(rowsPanel);
            t.Controls.Add(group);
            err = new Label { AutoSize = true, ForeColor = Ui.ERR, Margin = new Padding(0, 0, 0, 6), MaximumSize = new Size(460, 0), Visible = false };
            t.Controls.Add(err);
            var btns = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right };
            var ok = new Button { Text = isNew ? "作成" : "決定", AutoSize = true, MinimumSize = new Size(75, 23) };
            var cancel = new Button { Text = "キャンセル", AutoSize = true, MinimumSize = new Size(75, 23), DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) => Submit();
            btns.Controls.Add(ok);
            btns.Controls.Add(cancel);
            t.Controls.Add(btns);
            Controls.Add(t);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        static string N(double? v) { return v == null ? "" : Js.Num(v.Value); }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            program.Focus();
            program.SelectAll();
        }

        static void ShowDims(ToolRow r)
        {
            string shape = Model.SHAPE_KEYS[r.Shape.SelectedIndex];
            r.RectPart.Visible = shape == "rect" || shape == "special";
            r.RoundPart.Visible = shape == "round";
            r.NotePart.Visible = shape == "special";
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
                    t.W = Model.PosNum(r.W.Text); t.H = Model.PosNum(r.H.Text);
                    if (t.W == null || t.H == null) { Fail(no + " の幅と高さを入れてください（0 より大きい数）。", t.W == null ? r.W : r.H); return; }
                    if (t.Shape == "special") t.Note = r.Note.Text.Trim();
                }
                else if (t.Shape == "round")
                {
                    t.D = Model.PosNum(r.D.Text);
                    if (t.D == null) { Fail(no + " の直径を入れてください（0 より大きい数）。", r.D); return; }
                }
                tools.Add(t);
            }
            if (tools.All(t => t.Shape == "none")) { Fail("金型を 1 つ以上選んでください。", rows[0].Shape); return; }
            Program = p;
            Tools = tools;
            DialogResult = DialogResult.OK;
            Close();
        }

        void Fail(string msg, Control focus)
        {
            err.Text = msg;
            err.Visible = true;
            focus.Focus();
        }
    }

    /// <summary>ファイルに図面がいくつも入っているとき（Web 版で書き出したファイル）、開く図面を選ぶ。</summary>
    public class PickDialog : ClassicDialog
    {
        readonly ListView list;
        public int Index = -1;

        public PickDialog(List<Store.Entry> entries) : base("開く図面を選ぶ")
        {
            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(12, 10, 12, 10) };
            t.Controls.Add(new Label { Text = "このファイルには図面が " + entries.Count + " 件入っています。開く図面を選んでください。", AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
            list = new ListView
            {
                View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Width = 460, Height = 220,
                BorderStyle = BorderStyle.Fixed3D, HeaderStyle = ColumnHeaderStyle.Nonclickable,
            };
            list.Columns.Add("名前", 170);
            list.Columns.Add("プログラム", 70);
            list.Columns.Add("保存した日時", 130);
            list.Columns.Add("線", 60);
            foreach (var e in entries)
            {
                var d = e.Doc as System.Text.Json.Nodes.JsonObject;
                string prog = "";
                try { var pv = (double?)d["program"]; if (pv != null) prog = Model.ProgramText((int)pv.Value); } catch (Exception) { }
                int lines = d["lines"] is System.Text.Json.Nodes.JsonArray la ? la.Count : 0;
                DateTime when;
                string at = DateTime.TryParse(e.SavedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out when)
                    ? when.ToLocalTime().ToString("yyyy/MM/dd HH:mm") : e.SavedAt;
                list.Items.Add(new ListViewItem(new[] { e.Name, prog, at, lines + " 本" }));
            }
            if (list.Items.Count > 0) list.Items[0].Selected = true;
            list.DoubleClick += (s, e) => Submit();
            t.Controls.Add(list);
            var btns = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 8, 0, 0) };
            var ok = new Button { Text = "開く", AutoSize = true, MinimumSize = new Size(75, 23) };
            var cancel = new Button { Text = "キャンセル", AutoSize = true, MinimumSize = new Size(75, 23), DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) => Submit();
            btns.Controls.Add(ok);
            btns.Controls.Add(cancel);
            t.Controls.Add(btns);
            Controls.Add(t);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        void Submit()
        {
            if (list.SelectedIndices.Count == 0) return;
            Index = list.SelectedIndices[0];
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
