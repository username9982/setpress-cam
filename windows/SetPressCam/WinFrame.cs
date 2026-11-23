// Window frame like Domino's, and a matching message box (from HummingbirdStudio's WinFrame.cs).
//
// Domino's frame is the Windows 7 "Basic" frame: Windows 10/11 still draw it for windows whose frame is not
// rendered by DWM. Turning DWM frame rendering off for a window (DWMWA_NCRENDERING_POLICY = DWMNCRP_DISABLED)
// gives the same frame, while Windows keeps handling snapping, the taskbar, minimize, maximize and resizing.
//
// The app is deliberately NOT DPI-aware (no DPI entry in its manifest, no SetProcessDpiAwareness): on high-DPI
// screens Windows draws it at 100 % and stretches it, which gives the same rounded corners and border as Domino.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SetPressCam
{
    public static class WinFrame
    {
        public static readonly Color FACE = Color.FromArgb(0xF0, 0xF0, 0xF0);

        const string COMPAT_KEY = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        const string COMPAT_LAYER = "WIN7RTM";  // "Windows 7" in the exe's Properties > Compatibility
        static readonly string[] COMPAT_LAYERS = { "WIN95", "WIN98", "NT4SP5", "WIN2000", "WINXP", "WINSRV03", "VISTA",
                                                   "WINSRV08", "WIN7RTM", "WIN8RTM", "WIN81RTM" };
        const string RELAUNCH_ENV = "SETPRESSCAM_COMPAT_RELAUNCHED";

        /// <summary>Run `exe` in Windows 7 compatibility mode, as Domino does with its XP mode. In compatibility mode
        /// Windows leaves out the 4 px padded border, so the frame, caption buttons and title bar match Domino's.
        /// The setting lives in the current user's registry and only takes effect for processes started after it
        /// is written, so the first time this writes it and starts `exe` again. A mode the user picked is left
        /// alone. Returns true if a new process was started (the caller should exit).</summary>
        public static bool EnsureCompatMode(string exe, string[] args)
        {
            string current = "";
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(COMPAT_KEY))
                    current = (k == null ? null : k.GetValue(exe) as string) ?? "";
            }
            catch (Exception) { }
            var tokens = current.ToUpperInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Any(t => COMPAT_LAYERS.Contains(t))) return false;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RELAUNCH_ENV))) return false;  // never loop
            var flags = current.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(t => t != "~").ToList();
            flags.Add(COMPAT_LAYER);
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(COMPAT_KEY))
                    k.SetValue(exe, "~ " + string.Join(" ", flags), RegistryValueKind.String);
                var psi = new ProcessStartInfo(exe, string.Join(" ", args.Select(a => "\"" + a.Replace("\"", "\\\"") + "\"")))
                {
                    UseShellExecute = false,
                };
                psi.EnvironmentVariables[RELAUNCH_ENV] = "1";
                Process.Start(psi);
            }
            catch (Exception)
            {
                return false;  // could not write or relaunch: just run as we are
            }
            return true;
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")]
        internal static extern bool IsZoomed(IntPtr hwnd);

        /// <summary>Give a window the Windows 7 Basic frame.</summary>
        public static void UseBasicFrame(IntPtr hwnd)
        {
            int policy = 1;  // DWMNCRP_DISABLED
            try { DwmSetWindowAttribute(hwnd, 2, ref policy, 4); } catch (Exception) { }
            // SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER: recalculate and redraw the frame now
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0027);
        }

        /// <summary>Modal message box with the same frame. kind: info, warning, error, question.
        /// Returns the text of the button pressed, or null if the window was closed.</summary>
        public static string Message(IWin32Window owner, string title, string text, string kind = "info", string[] buttons = null)
        {
            using (var box = new MessageForm(title, text, kind, buttons ?? new[] { "OK" }))
            {
                box.ShowDialog(owner);
                return box.Result;
            }
        }
    }

    /// <summary>A form with the Windows 7 Basic frame, as thin as Domino's (see WinFrame), and the app's font.</summary>
    public class ClassicForm : Form
    {
        public ClassicForm()
        {
            BackColor = WinFrame.FACE;
            Font = Ui.Font;
            AutoScaleMode = AutoScaleMode.None;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WinFrame.UseBasicFrame(Handle);
        }

        /// <summary>Drop the 4 px "padded border" Windows adds to the left, right and bottom edges, as Domino (an MFC
        /// app) does. In compatibility mode it is 0 already, and this does nothing.</summary>
        protected override void WndProc(ref Message m)
        {
            const int WM_COMMAND = 0x0111;
            if (m.Msg == WM_COMMAND && m.LParam == IntPtr.Zero && ((long)m.WParam >> 16 & 0xFFFF) == 0
                && MenuItem.Dispatch((int)((long)m.WParam & 0xFFFF)))
                return;  // a menu bar entry (see NativeMenu.cs)
            base.WndProc(ref m);
            const int WM_NCCALCSIZE = 0x0083, SM_CXPADDEDBORDER = 92;
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                try
                {
                    int pad = WinFrame.GetSystemMetrics(SM_CXPADDEDBORDER);
                    if (pad > 0 && !WinFrame.IsZoomed(m.HWnd))  // maximized: the frame is off-screen anyway
                    {
                        // NCCALCSIZE_PARAMS.rgrc[0]: the new client rect (left, top, right, bottom)
                        int left = Marshal.ReadInt32(m.LParam, 0), right = Marshal.ReadInt32(m.LParam, 8),
                            bottom = Marshal.ReadInt32(m.LParam, 12);
                        Marshal.WriteInt32(m.LParam, 0, left - pad);
                        Marshal.WriteInt32(m.LParam, 8, right + pad);
                        Marshal.WriteInt32(m.LParam, 12, bottom + pad);
                    }
                }
                catch (Exception) { }  // never let an error escape the window procedure
            }
        }
    }

    /// <summary>A modal dialog: fixed size, centered over its owner, Enter = OK and Esc = cancel.</summary>
    public class ClassicDialog : ClassicForm
    {
        public ClassicDialog(string title)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
        }

        protected override void OnLoad(EventArgs e)
        {
            Ui.Shrink(this);
            base.OnLoad(e);
            var owner = Owner;
            if (owner != null)  // a third of the way down, as the Python version
                Location = new System.Drawing.Point(Math.Max(0, owner.Left + (owner.Width - Width) / 2), Math.Max(0, owner.Top + (owner.Height - Height) / 3));
        }
    }

    class MessageForm : ClassicDialog
    {
        public string Result;

        public MessageForm(string title, string text, string kind, string[] buttons) : base(title)
        {
            var outer = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(12, 14, 12, 10) };
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
            var icon = new Panel { Size = new Size(34, 34), Margin = new Padding(0, 0, 12, 0) };
            icon.Paint += (s, e) => DrawIcon(e.Graphics, kind);
            row.Controls.Add(icon);
            row.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 2, 0, 0) });
            outer.Controls.Add(row);
            var btns = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.None };
            foreach (var b in buttons)
            {
                var btn = new Button { Text = b, AutoSize = true, MinimumSize = new Size(75, 23), Margin = new Padding(4, 0, 4, 0) };
                string v = b;
                btn.Click += (s, e) => { Result = v; Close(); };
                btns.Controls.Add(btn);
            }
            outer.Controls.Add(btns);
            Controls.Add(outer);
            AcceptButton = (IButtonControl)btns.Controls[0];
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    Result = buttons.Length > 1 ? buttons[buttons.Length - 1] : buttons[0];
                    Close();
                }
            };
        }

        static void DrawIcon(Graphics g, string kind)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            const int d = 32;
            if (kind == "error")
            {
                using (var b = new SolidBrush(Color.FromArgb(0xE0, 0x30, 0x2A))) g.FillEllipse(b, 1, 1, d - 1, d - 1);
                using (var p = new Pen(Color.FromArgb(0x90, 0x10, 0x10))) g.DrawEllipse(p, 1, 1, d - 1, d - 1);
                using (var p = new Pen(Color.White, 3))
                {
                    g.DrawLine(p, d * 0.32f, d * 0.32f, d * 0.7f, d * 0.7f);
                    g.DrawLine(p, d * 0.32f, d * 0.7f, d * 0.7f, d * 0.32f);
                }
            }
            else if (kind == "warning")
            {
                var pts = new[] { new PointF(d / 2f, 1), new PointF(d, d), new PointF(1, d) };
                using (var b = new SolidBrush(Color.FromArgb(0xF8, 0xD0, 0x20))) g.FillPolygon(b, pts);
                using (var p = new Pen(Color.FromArgb(0x80, 0x60, 0x00))) g.DrawPolygon(p, pts);
                using (var f = new Font("Arial", 12, FontStyle.Bold))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("!", f, Brushes.Black, new RectangleF(0, d * 0.3f, d, d * 0.66f), sf);
            }
            else
            {
                var blue = Color.FromArgb(0x2A, 0x5B, 0xD7);
                g.FillEllipse(Brushes.White, 1, 1, d - 1, d - 1);
                using (var p = new Pen(blue, 2)) g.DrawEllipse(p, 1, 1, d - 1, d - 1);
                using (var f = new Font("Times New Roman", 13, FontStyle.Bold))
                using (var b = new SolidBrush(blue))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(kind == "question" ? "?" : "i", f, b, new RectangleF(1, 2, d, d), sf);
            }
        }
    }
}
