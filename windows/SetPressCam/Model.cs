// 図面のデータ。Web 版と同じ JSON の形で保存・読み込みする（app.js の newDoc / loadDoc の移植）。
//   points: [{id, x, y}]、lines: [{id, a, b, dir:'h'|'v'}]、
//   dims: [{id, axis:'x'|'y', a, b, value, kind, refs:[{t:'point'|'line', id}], off?}]（b − a = value）
//   layouts: [{work: 外形を表す点, src: 割付したときの形, hits: [{tool, x0, y0, x1, y1, macro?, kind?, mj?}], warn: [], org: {x, y}}]
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SetPressCam
{
    public class Tool
    {
        public string Shape { get; set; } = "none";  // rect / round / special / none
        public double? W { get; set; }
        public double? H { get; set; }
        public double? D { get; set; }
        public string Note { get; set; } = "";
        public Tool Clone() { return (Tool)MemberwiseClone(); }
    }

    public class Sheet
    {
        public double W { get; set; } = 1000;
        public double H { get; set; } = 500;
        public double? T { get; set; }  // 板厚（mm、未設定は null）
    }

    public class Point
    {
        public int Id { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
    }

    public class Line
    {
        public int Id { get; set; }
        public int A { get; set; }
        public int B { get; set; }
        public string Dir { get; set; }
    }

    public class Ref
    {
        public string T { get; set; }
        public int Id { get; set; }
    }

    public class Dim
    {
        public int Id { get; set; }
        public string Axis { get; set; }
        public int A { get; set; }
        public int B { get; set; }
        public double Value { get; set; }
        public string Kind { get; set; }
        public List<Ref> Refs { get; set; } = new List<Ref>();
        public double? Off { get; set; }
    }

    public class Hit
    {
        public int? Tool { get; set; }
        public double X0 { get; set; }
        public double Y0 { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public string Macro { get; set; }  // null は F3、"F13"、"単発"
        public string Kind { get; set; }   // "notch"（切り欠き）
        public double? Mj { get; set; }    // F13 のミクロジョイント
        public Hit Clone() { return (Hit)MemberwiseClone(); }
    }

    public class Org
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    public class Layout
    {
        public int Work { get; set; }
        public string Src { get; set; } = "";
        public List<Hit> Hits { get; set; } = new List<Hit>();
        public List<string> Warn { get; set; } = new List<string>();
        public Org Org { get; set; }
        // F23（多数個取り）: 列数（無ければ 1）とピッチ（無ければ 抜く形を囲む四角 + 10 mm）
        public int? Nx { get; set; }
        public int? Ny { get; set; }
        public double? Px { get; set; }
        public double? Py { get; set; }
    }

    public class Doc
    {
        public int Version { get; set; } = 4;
        public string Name { get; set; } = "";
        public int? Program { get; set; }
        public List<Tool> Tools { get; set; }
        public Sheet Sheet { get; set; } = new Sheet();
        public double Mj { get; set; }
        public List<Layout> Layouts { get; set; } = new List<Layout>();
        public List<Point> Points { get; set; } = new List<Point>();
        public List<Line> Lines { get; set; } = new List<Line>();
        public List<Dim> Dims { get; set; } = new List<Dim>();
        public int NextId { get; set; } = 1;
    }

    /// <summary>選んだ要素 {t:'point'|'line'|'dim'|'origin', id}。</summary>
    public class Sel
    {
        public string T;
        public int Id;
        public Sel(string t, int id) { T = t; Id = id; }
        public Sel Clone() { return new Sel(T, Id); }
    }

    public static class Model
    {
        public static readonly string[] SHAPE_KEYS = { "rect", "round", "special", "none" };
        public static readonly Dictionary<string, string> SHAPES = new Dictionary<string, string>
        {
            ["rect"] = "長方形", ["round"] = "丸", ["special"] = "異形", ["none"] = "使わない",
        };
        public const int TOOL_COUNT = 3;  // T1〜T3
        public const int PROGRAM_MIN = 1, PROGRAM_MAX = 9999;

        public static readonly JsonSerializerOptions JSON = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        public static readonly JsonSerializerOptions JSON_INDENT = new JsonSerializerOptions(JSON) { WriteIndented = true };

        public static Tool EmptyTool() { return new Tool(); }

        public static Doc NewDoc(double mj)
        {
            return new Doc
            {
                Tools = Enumerable.Range(0, TOOL_COUNT).Select(i => EmptyTool()).ToList(),
                Mj = mj,
            };
        }

        public static string Serialize(Doc doc) { return JsonSerializer.Serialize(doc, JSON); }
        public static Doc Deserialize(string json) { return JsonSerializer.Deserialize<Doc>(json, JSON); }
        public static Doc Copy(Doc doc) { return Deserialize(Serialize(doc)); }

        // ---- JSON の値を、Web 版の Number() / String() と同じように読む ----
        static double Num(JsonNode n)
        {
            if (n == null) return double.NaN;
            try
            {
                var v = n.AsValue();
                if (v.TryGetValue(out double d)) return d;
                if (v.TryGetValue(out bool b)) return b ? 1 : 0;
                if (v.TryGetValue(out string s))
                {
                    s = s.Trim();
                    if (s == "") return 0;
                    return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : double.NaN;
                }
            }
            catch (Exception) { }
            return double.NaN;
        }

        static string Str(JsonNode n)
        {
            if (n == null) return "";
            try
            {
                var v = n.AsValue();
                if (v.TryGetValue(out string s)) return s;
                if (v.TryGetValue(out double d)) return Js.Num(d);
                if (v.TryGetValue(out bool b)) return b ? "true" : "false";
            }
            catch (Exception) { }
            return n.ToJsonString();
        }

        static bool Finite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }
        static bool IsInt(double v) { return Finite(v) && Math.Floor(v) == v; }

        /// <summary>F23 の列数（app.js の f23Count）: 2〜99 の整数なら その値、ほかは null（= 1）。</summary>
        public static int? F23Count(JsonNode n)
        {
            double v = n == null ? 0 : Num(n);
            return IsInt(v) && v > 1 && v <= Editor.F23_MAX ? (int)v : (int?)null;
        }

        /// <summary>F23 のピッチ（app.js の f23Pitch）: 0 より大きい数なら 5000 までに丸めた値、ほかは null（= 自動）。</summary>
        public static double? F23Pitch(JsonNode n)
        {
            if (n == null) return null;
            if (n is JsonValue jv && jv.TryGetValue(out string s) && s == "") return null;
            double v = Num(n);
            return Finite(v) && v > 0 ? Js.R3(Math.Min(v, 5000)) : (double?)null;
        }

        /// <summary>0 より大きい数なら丸めた値、そうでなければ null（app.js の posNum）。</summary>
        public static double? PosNum(JsonNode n)
        {
            if (n == null) return null;
            if (n is JsonValue jv && jv.TryGetValue(out string s) && s == "") return null;
            double v = Num(n);
            return Finite(v) && v > 0 ? Js.R3(v) : (double?)null;
        }

        public static double? PosNum(string s)
        {
            if (s == null || s.Trim() == "") return null;
            double v;
            if (!double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return null;
            return Finite(v) && v > 0 ? Js.R3(v) : (double?)null;
        }

        static JsonArray Arr(JsonNode n) { return n as JsonArray ?? new JsonArray(); }

        /// <summary>保存データの金型を整える（古い図面には金型が無い）</summary>
        public static List<Tool> NormalizeTools(JsonNode tools)
        {
            var arr = tools as JsonArray;
            return Enumerable.Range(0, TOOL_COUNT).Select(i =>
            {
                var t = arr != null && i < arr.Count ? arr[i] as JsonObject : null;
                t = t ?? new JsonObject();
                string shape = Str(t["shape"]);
                return new Tool
                {
                    Shape = SHAPES.ContainsKey(shape) ? shape : "none",
                    W = PosNum(t["w"]), H = PosNum(t["h"]), D = PosNum(t["d"]),
                    Note = new string(Str(t["note"]).Take(40).ToArray()),
                };
            }).ToList();
        }

        /// <summary>保存データを読み込む（app.js の loadDoc）。形が違えば例外。</summary>
        public static Doc LoadDoc(JsonNode node)
        {
            var d = node as JsonObject;
            if (d == null || !(d["sheet"] is JsonObject)) throw new FormatException("図面の形式が違います");
            double prog = Num(d["program"]);
            var points = Arr(d["points"]).OfType<JsonObject>()
                .Select(p => new { id = Num(p["id"]), x = Js.R3(Num(p["x"])), y = Js.R3(Num(p["y"])) })
                .Where(p => IsInt(p.id) && Finite(p.x) && Finite(p.y))
                .Select(p => new Point { Id = (int)p.id, X = p.x, Y = p.y }).ToList();
            var ids = new HashSet<int>(points.Select(p => p.Id));
            var lines = Arr(d["lines"]).OfType<JsonObject>()
                .Select(l => new { id = Num(l["id"]), a = Num(l["a"]), b = Num(l["b"]), dir = Str(l["dir"]) == "v" ? "v" : "h" })
                .Where(l => IsInt(l.a) && IsInt(l.b) && ids.Contains((int)l.a) && ids.Contains((int)l.b) && l.a != l.b && IsInt(l.id))
                .Select(l => new Line { Id = (int)l.id, A = (int)l.a, B = (int)l.b, Dir = l.dir }).ToList();
            var lineIds = new HashSet<int>(lines.Select(l => l.Id));
            Func<Ref, bool> refOk = r => r != null && (r.T == "point" ? ids.Contains(r.Id) : r.T == "line" && lineIds.Contains(r.Id));
            var dims = new List<Dim>();
            foreach (var q in Arr(d["dims"]).OfType<JsonObject>())
            {
                double id = Num(q["id"]), a = Num(q["a"]), b = Num(q["b"]), value = Js.R3(Num(q["value"]));
                var refs = Arr(q["refs"]).OfType<JsonObject>().Select(r => new { t = Str(r["t"]), id = Num(r["id"]) })
                    .Select(r => IsInt(r.id) ? new Ref { T = r.t, Id = (int)r.id } : null).ToList();
                double off = Num(q["off"]);
                if (!IsInt(id) || !IsInt(a) || !IsInt(b) || !ids.Contains((int)a) || !ids.Contains((int)b) || !Finite(value)) continue;
                if (refs.Count == 0 || !refs.All(refOk)) continue;
                string kind = Str(q["kind"]);
                dims.Add(new Dim
                {
                    Id = (int)id, Axis = Str(q["axis"]) == "y" ? "y" : "x", A = (int)a, B = (int)b, Value = value,
                    Kind = kind == "" ? "pp" : kind, Refs = refs,
                    Off = q["off"] != null && Finite(off) ? Js.R3(off) : (double?)null,
                });
            }
            // v0.6 までの図面のワーク原点（図面に 1 つ）は、線がつながっていればふつうの点として残し、つながっていなければ消す
            double oldOrigin = Num(d["originId"]);
            if (IsInt(oldOrigin) && ids.Contains((int)oldOrigin))
            {
                int oo = (int)oldOrigin;
                if (!lines.Any(l => l.A == oo || l.B == oo))
                {
                    points.RemoveAll(p => p.Id == oo);
                    ids.Remove(oo);
                    dims = dims.Where(q => q.A != oo && q.B != oo && !q.Refs.Any(r => r.T == "point" && r.Id == oo)).ToList();
                }
                foreach (var q in dims) if (q.Kind == "o") q.Kind = "pp";
            }
            int maxId = new[] { 0 }.Concat(points.Select(p => p.Id)).Concat(lines.Select(l => l.Id)).Concat(dims.Select(q => q.Id)).Max();
            var sheet = (JsonObject)d["sheet"];
            double sw = Num(sheet["w"]), sh = Num(sheet["h"]);
            var layouts = new List<Layout>();
            foreach (var q in Arr(d["layouts"]).OfType<JsonObject>())
            {
                double work = Num(q["work"]);
                if (!IsInt(work) || !ids.Contains((int)work)) continue;
                var hits = new List<Hit>();
                foreach (var h in Arr(q["hits"]).OfType<JsonObject>())
                {
                    double tool = Num(h["tool"]);
                    string macro = Str(h["macro"]);
                    var hit = new Hit
                    {
                        Tool = h["tool"] != null && IsInt(tool) && h["tool"].GetValueKind() == JsonValueKind.Number ? (int)tool : (int?)null,
                        X0 = Js.R3(Num(h["x0"])), Y0 = Js.R3(Num(h["y0"])), X1 = Js.R3(Num(h["x1"])), Y1 = Js.R3(Num(h["y1"])),
                    };
                    if (macro == "F13")
                    {
                        hit.Macro = "F13";
                        double mj = Js.R3(Num(h["mj"]));
                        hit.Mj = Finite(mj) && mj > 0 ? mj : 0;
                    }
                    if (macro == "単発") hit.Macro = "単発";
                    if (Str(h["kind"]) == "notch") hit.Kind = "notch";
                    if (new[] { hit.X0, hit.Y0, hit.X1, hit.Y1 }.All(Finite)) hits.Add(hit);
                }
                var org = q["org"] as JsonObject;
                double ox = org == null ? double.NaN : Num(org["x"]), oy = org == null ? double.NaN : Num(org["y"]);
                layouts.Add(new Layout
                {
                    Work = (int)work, Src = q["src"] == null ? "" : Str(q["src"]), Hits = hits,
                    Warn = Arr(q["warn"]).Select(Str).ToList(),
                    Org = org != null && Finite(ox) && Finite(oy) ? new Org { X = ox, Y = oy } : null,
                    Nx = F23Count(q["nx"]), Ny = F23Count(q["ny"]), Px = F23Pitch(q["px"]), Py = F23Pitch(q["py"]),
                });
            }
            double nextId = Num(d["nextId"]);
            return new Doc
            {
                Version = 4,
                Name = d["name"] == null ? "" : Str(d["name"]),
                Program = IsInt(prog) && prog >= PROGRAM_MIN && prog <= PROGRAM_MAX ? (int)prog : (int?)null,
                Tools = NormalizeTools(d["tools"]),
                Sheet = new Sheet { W = Finite(sw) && sw != 0 ? sw : 1000, H = Finite(sh) && sh != 0 ? sh : 500, T = PosNum(sheet["t"]) },
                Mj = PosNum(d["mj"]) ?? 0,
                Points = points, Lines = lines, Dims = dims, Layouts = layouts,
                NextId = Math.Max(Finite(nextId) && nextId != 0 ? (int)nextId : 1, maxId + 1),
            };
        }

        /// <summary>金型の説明（例: "長方形 20 x 10"、"丸 φ8"）</summary>
        public static string ToolText(Tool t)
        {
            if (t.Shape == "rect") return "長方形 " + N(t.W) + " x " + N(t.H);
            if (t.Shape == "round") return "丸 φ" + N(t.D);
            if (t.Shape == "special") return "異形 " + N(t.W) + " x " + N(t.H) + (t.Note != "" ? " (" + t.Note + ")" : "");
            return "使わない";
        }

        static string N(double? v) { return v == null ? "null" : Js.Num(v.Value); }

        public static string ProgramText(int? p) { return p == null ? "O----" : "O" + p.Value.ToString("D4"); }
    }
}
