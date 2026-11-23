// 設定・作業中の図面・保存した図面（%APPDATA%\SetPressCam）、ファイルへの書き出し・読み込み。
// 保存した図面は drawings.json（Web 版の localStorage の保存と同じ {名前:{savedAt, doc}}）。
// 書き出すファイルは Web 版の「ファイルに書き出す」と同じ形 {app:'setpress-cam', version:1, exportedAt, drawings:{名前:{savedAt, doc}}}。
// Web 版の「ファイルから読み込む」でそのまま読め、Web 版で書き出したファイルもこのアプリで開ける。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SetPressCam
{
    public class Settings
    {
        public double Mj { get; set; }               // 最後に入れたミクロジョイント（新しい図面に使う）
        public double Snap { get; set; } = 5;        // グリッド（mm）
        public string LastDir { get; set; }          // 最後に開いた・保存したフォルダ
        public bool CheckUpdates { get; set; } = true;  // 起動時に新しい版を確かめる
        public string SkipVersion { get; set; }      // 「この版は知らせない」にした版
        public int[] Window { get; set; }            // x, y, w, h, 最大化
    }

    public class Current
    {
        public JsonNode Doc { get; set; }
        public bool Dirty { get; set; }
        public string File { get; set; }
    }

    public static class Store
    {
        public static readonly string DIR = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SetPressCam");
        static string SETTINGS => Path.Combine(DIR, "settings.json");
        static string CURRENT => Path.Combine(DIR, "current.json");
        static string UNDO => Path.Combine(DIR, "undo.json");
        static string DRAWINGS => Path.Combine(DIR, "drawings.json");
        static readonly UTF8Encoding UTF8 = new UTF8Encoding(false);

        public static Settings LoadSettings()
        {
            try
            {
                if (File.Exists(SETTINGS)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SETTINGS, UTF8), Model.JSON) ?? new Settings();
            }
            catch (Exception) { }
            return new Settings();
        }

        public static void SaveSettings(Settings s)
        {
            try { WriteAtomic(SETTINGS, JsonSerializer.Serialize(s, Model.JSON_INDENT)); } catch (Exception) { }
        }

        /// <summary>作業中の図面（自動保存）。次に起動したときに続きから始める。</summary>
        public static void SaveCurrent(Doc doc, bool dirty, string file, List<string> undo)
        {
            try
            {
                var o = new JsonObject
                {
                    ["doc"] = JsonNode.Parse(Model.Serialize(doc)),
                    ["dirty"] = dirty,
                    ["file"] = file,
                };
                WriteAtomic(CURRENT, o.ToJsonString(Model.JSON));
                WriteAtomic(UNDO, JsonSerializer.Serialize(undo, Model.JSON));
            }
            catch (Exception) { /* 保存できなくても続ける */ }
        }

        public static Current LoadCurrent()
        {
            try
            {
                if (!File.Exists(CURRENT)) return null;
                var o = JsonNode.Parse(File.ReadAllText(CURRENT, UTF8)) as JsonObject;
                if (o == null || o["doc"] == null) return null;
                return new Current
                {
                    Doc = o["doc"],
                    Dirty = o["dirty"] is JsonValue dv && dv.TryGetValue(out bool b) && b,
                    File = o["file"] is JsonValue fv && fv.TryGetValue(out string f) ? f : null,
                };
            }
            catch (Exception) { return null; }
        }

        public static List<string> LoadUndo()
        {
            try
            {
                if (File.Exists(UNDO)) return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(UNDO, UTF8)) ?? new List<string>();
            }
            catch (Exception) { }
            return new List<string>();
        }

        /// <summary>書きかけで壊れないように、別の名前に書いてから置き換える。</summary>
        public static void WriteAtomic(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, UTF8);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        public static string Now() { return DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"); }

        static JsonObject ToObject(List<Entry> entries)
        {
            var o = new JsonObject();
            foreach (var e in entries) o[e.Name] = new JsonObject { ["savedAt"] = e.SavedAt, ["doc"] = e.Doc.DeepClone() };
            return o;
        }

        /// <summary>保存した図面（「保存」で入れたもの）。読めなければ空。</summary>
        public static List<Entry> LoadStore()
        {
            try { if (File.Exists(DRAWINGS)) return ReadDrawings(DRAWINGS); } catch (Exception) { }
            return new List<Entry>();
        }

        public static void WriteStore(List<Entry> entries)
        {
            WriteAtomic(DRAWINGS, new JsonObject { ["app"] = "setpress-cam", ["version"] = 1, ["drawings"] = ToObject(entries) }.ToJsonString(Model.JSON));
        }

        /// <summary>保存した図面をすべてファイルに書き出す（Web 版の「ファイルに書き出す」と同じ形）。</summary>
        public static void Export(string path, List<Entry> entries)
        {
            var bundle = new JsonObject { ["app"] = "setpress-cam", ["version"] = 1, ["exportedAt"] = Now(), ["drawings"] = ToObject(entries) };
            WriteAtomic(path, bundle.ToJsonString(Model.JSON_INDENT));
        }

        public class Entry
        {
            public string Name, SavedAt;
            public JsonNode Doc;
        }

        /// <summary>ファイルの図面の一覧。Web 版の書き出し（複数入り）と、図面 1 つの JSON のどちらも読む。</summary>
        public static List<Entry> ReadDrawings(string path)
        {
            var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
            var o = root as JsonObject;
            if (o == null) throw new FormatException("形式が違います");
            if (o["drawings"] is JsonObject dr)
            {
                var list = new List<Entry>();
                foreach (var kv in dr)
                {
                    var item = kv.Value as JsonObject;
                    if (item == null || !(item["doc"] is JsonObject)) continue;
                    list.Add(new Entry
                    {
                        Name = kv.Key,
                        SavedAt = item["savedAt"] is JsonValue sv && sv.TryGetValue(out string s) ? s : "",
                        Doc = item["doc"],
                    });
                }
                // 新しい順
                return list.OrderByDescending(e => e.SavedAt, StringComparer.Ordinal).ToList();
            }
            if (o["sheet"] is JsonObject)
                return new List<Entry> { new Entry { Name = o["name"] is JsonValue nv && nv.TryGetValue(out string n) ? n : Path.GetFileNameWithoutExtension(path), SavedAt = "", Doc = o } };
            throw new FormatException("セットプレス CAM の図面ファイルではありません");
        }
    }
}
