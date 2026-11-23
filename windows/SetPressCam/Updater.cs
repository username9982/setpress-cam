// 新しい版の確認と更新。GitHub Releases のいちばん新しいリリースを見て、いまの版より新しければ
// インストーラー（SetPressCam-<版>-setup.exe）をダウンロードし、確かめてから実行する。
// 起動時（設定で止められる）とメニューの「更新の確認」から使う。
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SetPressCam
{
    public class Release
    {
        public string Version, Url, Name, Sha256, Page;
        public long Size;
    }

    public static class Updater
    {
        public const string REPO = "username9982/setpress-cam";
        const string API = "https://api.github.com/repos/" + REPO + "/releases/latest";
        public const string RELEASES_PAGE = "https://github.com/" + REPO + "/releases";

        static HttpClient Client()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("SetPressCam-Updater");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        /// <summary>"0.17.6"・"v0.17.6" を比べられる形にする。読めなければ null。</summary>
        public static Version Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim().TrimStart('v', 'V');
            Version v;
            return System.Version.TryParse(s, out v) ? v : null;
        }

        /// <summary>いちばん新しいリリース。インストーラーが付いていなければ null。</summary>
        public static async Task<Release> Latest(CancellationToken ct)
        {
            using (var c = Client())
            {
                string text;
                using (var resp = await c.GetAsync(API, ct))
                {
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;  // リリースがまだ 1 つも無い
                    resp.EnsureSuccessStatusCode();
                    text = await resp.Content.ReadAsStringAsync(ct);
                }
                var o = JsonNode.Parse(text) as JsonObject;
                if (o == null) return null;
                string tag = (string)o["tag_name"];
                var asset = (o["assets"] as JsonArray)?.OfType<JsonObject>()
                    .FirstOrDefault(a => ((string)a["name"] ?? "").StartsWith("SetPressCam-", StringComparison.OrdinalIgnoreCase)
                                         && ((string)a["name"]).EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase));
                if (Parse(tag) == null || asset == null) return null;
                string digest = (string)asset["digest"];  // "sha256:…"（GitHub が付けるもの）
                return new Release
                {
                    Version = tag.TrimStart('v', 'V'),
                    Url = (string)asset["browser_download_url"],
                    Name = (string)asset["name"],
                    Size = (long?)asset["size"] ?? 0,
                    Sha256 = digest != null && digest.StartsWith("sha256:") ? digest.Substring(7).ToLowerInvariant() : null,
                    Page = (string)o["html_url"],
                };
            }
        }

        /// <summary>インストーラーをダウンロードして確かめる。progress は 0〜1。</summary>
        public static async Task<string> Download(Release r, IProgress<double> progress, CancellationToken ct)
        {
            if (!r.Url.StartsWith("https://github.com/" + REPO + "/releases/download/", StringComparison.Ordinal))
                throw new InvalidOperationException("ダウンロード先が想定と違います: " + r.Url);
            string dir = Path.Combine(Path.GetTempPath(), "SetPressCam-update");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, Path.GetFileName(r.Name));
            using (var c = Client())
            using (var resp = await c.GetAsync(r.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? r.Size;
                using (var src = await resp.Content.ReadAsStreamAsync(ct))
                using (var dst = File.Create(path))
                using (var sha = SHA256.Create())
                {
                    var buf = new byte[81920];
                    long done = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n, ct);
                        sha.TransformBlock(buf, 0, n, null, 0);
                        done += n;
                        if (total > 0) progress?.Report((double)done / total);
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    if (r.Size > 0 && done != r.Size) throw new IOException("ダウンロードしたファイルの大きさが違います");
                    string hex = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                    if (r.Sha256 != null && hex != r.Sha256) throw new IOException("ダウンロードしたファイルが壊れています（SHA-256 が違います）");
                }
            }
            return path;
        }

        /// <summary>インストーラーを実行する（画面を出さずに上書きし、終わったらアプリを起動し直す）。</summary>
        public static void RunInstaller(string path)
        {
            Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTAPP=1") { UseShellExecute = true });
        }
    }

    /// <summary>ダウンロードの進み具合を出す小さなダイアログ。</summary>
    public class DownloadDialog : ClassicDialog
    {
        readonly Label label;
        readonly ProgressBar bar;
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        public string Path;
        public Exception Error;
        readonly Release release;

        public DownloadDialog(Release r) : base("更新のダウンロード")
        {
            release = r;
            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(12) };
            label = new Label { Text = r.Name + " をダウンロードしています…", AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
            bar = new ProgressBar { Width = 320, Height = 16, Maximum = 1000, Margin = new Padding(0, 0, 0, 10) };
            var cancel = new Button { Text = "キャンセル", AutoSize = true, MinimumSize = new System.Drawing.Size(75, 23), Anchor = AnchorStyles.Right };
            cancel.Click += (s, e) => cts.Cancel();
            t.Controls.Add(label);
            t.Controls.Add(bar);
            t.Controls.Add(cancel);
            Controls.Add(t);
            CancelButton = cancel;
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                var p = new Progress<double>(v => bar.Value = Math.Max(0, Math.Min(1000, (int)(v * 1000))));
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
