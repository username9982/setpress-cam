// セットプレス CAM の Windows 版（C#、.NET 10、WinForms）。Web 版（app.js・geom.js）と同じ仕様で、同じ図面ファイルを使う。
//
//   SetPressCam.exe [図面.json]
//
// 設定と作業中の図面は %APPDATA%\SetPressCam に置く。
using System;
using System.IO;
using System.Windows.Forms;

namespace SetPressCam
{
    static class Program
    {
        /// <summary>版。Web 版の APP_VERSION と同じ番号にする（SetPressCam.csproj の Version、build_win.py の VERSION も）。</summary>
        public const string VERSION = "0.17.12";

        /// <summary>配った形（インストールしたもの・ポータブル版）か。exe の横にライセンス表記がある。開発中の exe は false。</summary>
        public static bool Packaged
        {
            get { return File.Exists(Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_LICENSES.txt")); }
        }

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 3 && args[0] == "--gencode") return GenCode(args[1], args[2]);
            if (args.Length == 4 && args[0] == "--applycode") return ApplyCodeTest(args[1], args[2], args[3]);
            // 画面は Web 版と同じ見た目（Ui.cs）。表示スケールに合わせてくっきり描く（大きさは Ui.K を掛ける）
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetDefaultFont(Ui.Font);
            var form = new MainForm();
            if (args.Length > 0 && File.Exists(args[0])) form.StartFile = Path.GetFullPath(args[0]);
            Application.Run(form);
            return 0;
        }

        /// <summary>開発用（Web 版と結果が同じか確かめる）: 図面ファイルの図面を 1 つずつ、すべてのワークを自動割付して、
        /// コードを書き出す。SetPressCam.exe --gencode 図面.json 出力.txt</summary>
        /// <summary>開発用（Web 版と結果が同じか確かめる）: 図面を自動割付してから、書き換えたコード（edits.json の [{name, code}]）を
        /// 反映し、誤りか反映したあとのコードを書き出す。SetPressCam.exe --applycode 図面.json edits.json 出力.txt</summary>
        static int ApplyCodeTest(string input, string edits, string output)
        {
            var docs = System.Linq.Enumerable.ToDictionary(Store.ReadDrawings(input), e => e.Name, e => e.Doc);
            var sb = new System.Text.StringBuilder();
            var list = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(edits)).AsArray();
            for (int i = 0; i < list.Count; i++)
            {
                string name = (string)list[i]["name"], code = (string)list[i]["code"];
                var ed = new Editor(0);
                ed.Load(Model.LoadDoc(docs[name]));
                for (int k = 0; k < ed.Info.Outers.Count; k++) { ed.LayoutWork = k; ed.AutoLayout(); }
                var errs = ed.ApplyCode(code);
                sb.Append("## ").Append(i).Append(' ').Append(name).Append('\n');
                if (errs == null) sb.Append("APPLIED\n").Append(ed.CodeText(VERSION)).Append(Model.Serialize(ed.Doc)).Append('\n');
                else if (errs.Count == 0) sb.Append("SAME\n");
                else foreach (var e in errs) sb.Append(e.Line).Append(": ").Append(e.Msg).Append('\n');
            }
            File.WriteAllText(output, sb.ToString(), new System.Text.UTF8Encoding(false));
            return 0;
        }

        static int GenCode(string input, string output)
        {
            var sb = new System.Text.StringBuilder();
            var docs = new System.Text.Json.Nodes.JsonObject();  // 割り付けたあとの図面（Web 版で開いて確かめる）
            foreach (var entry in Store.ReadDrawings(input))
            {
                var ed = new Editor(0);
                ed.Load(Model.LoadDoc(entry.Doc));
                for (int k = 0; k < ed.Info.Outers.Count; k++)
                {
                    ed.LayoutWork = k;
                    ed.AutoLayout();
                }
                sb.Append("## ").Append(entry.Name).Append('\n');
                foreach (var l in ed.GenCode(VERSION)) sb.Append(l.Text).Append('\n');
                docs[entry.Name] = new System.Text.Json.Nodes.JsonObject { ["savedAt"] = Store.Now(), ["doc"] = System.Text.Json.Nodes.JsonNode.Parse(Model.Serialize(ed.Doc)) };
            }
            var bundle = new System.Text.Json.Nodes.JsonObject { ["app"] = "setpress-cam", ["version"] = 1, ["drawings"] = docs };
            File.WriteAllText(output + ".json", bundle.ToJsonString(Model.JSON), new System.Text.UTF8Encoding(false));
            File.WriteAllText(output, sb.ToString(), new System.Text.UTF8Encoding(false));
            return 0;
        }
    }
}
