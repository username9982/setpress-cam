// Native Windows menus (the menu bar and right-click menus), with the small part of the old WinForms
// MainMenu / MenuItem / ContextMenu API this app uses.
//
// .NET (Core) WinForms no longer has those classes; its MenuStrip / ContextMenuStrip are drawn by WinForms and do not
// look like Domino's menus. These build real Win32 menus (CreateMenu / CreatePopupMenu), so Windows draws them,
// handles Alt and the underlined letters, and shows the shortcut after "\t" right-aligned.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SetPressCam
{
    /// <summary>A menu: the menu bar, a context menu or a submenu.</summary>
    public abstract class Menu : IDisposable
    {
        public readonly MenuItemCollection MenuItems;
        internal IntPtr Handle = IntPtr.Zero;

        protected Menu() { MenuItems = new MenuItemCollection(this); }

        internal abstract IntPtr Create();

        /// <summary>Make (or remake) the native menu and its submenus.</summary>
        internal IntPtr Build()
        {
            Destroy();
            Handle = Create();
            foreach (var mi in MenuItems) mi.Insert(Handle);
            return Handle;
        }

        internal void Destroy()
        {
            foreach (var mi in MenuItems) mi.Forget();
            if (Handle != IntPtr.Zero) Native.DestroyMenu(Handle);  // also destroys the submenus
            Handle = IntPtr.Zero;
        }

        public virtual void Dispose()
        {
            Destroy();
            foreach (var mi in MenuItems) mi.Unregister();
        }
    }

    public class MenuItemCollection : List<MenuItem>
    {
        readonly Menu owner;
        internal MenuItemCollection(Menu owner) { this.owner = owner; }

        public new MenuItem Add(MenuItem item)
        {
            base.Add(item);
            item.Parent = owner;
            return item;
        }

        /// <summary>"-" adds a separator.</summary>
        public MenuItem Add(string text) { return Add(new MenuItem(text)); }

        public MenuItem Add(string text, EventHandler click) { return Add(new MenuItem(text, click)); }
    }

    public class MenuItem : Menu
    {
        static readonly Dictionary<int, MenuItem> byId = new Dictionary<int, MenuItem>();
        static int nextId = 1000;

        public readonly int Id;
        public event EventHandler Click;
        internal Menu Parent;
        string text;
        bool enabled = true, check, radio;

        public MenuItem(string text, EventHandler click = null)
        {
            this.text = text;
            if (click != null) Click += click;
            Id = nextId++;
            if (nextId > 0xFFFF) nextId = 1000;  // WM_COMMAND carries 16 bits
            byId[Id] = this;
        }

        bool IsSeparator { get { return text == "-"; } }

        public string Text
        {
            get { return text; }
            set { text = value; Update(); }
        }

        public bool Enabled
        {
            get { return enabled; }
            set { if (enabled != value) { enabled = value; Update(); } }
        }

        public bool Checked
        {
            get { return check; }
            set { if (check != value) { check = value; Update(); } }
        }

        /// <summary>Show a dot instead of a check mark.</summary>
        public bool RadioCheck
        {
            get { return radio; }
            set { if (radio != value) { radio = value; Update(); } }
        }

        internal override IntPtr Create() { return Native.CreatePopupMenu(); }

        internal void Insert(IntPtr parent)
        {
            var info = Info();
            if (MenuItems.Count > 0)
            {
                info.fMask |= Native.MIIM_SUBMENU;
                info.hSubMenu = Build();
            }
            Native.InsertMenuItem(parent, 0xFFFF, true, ref info);  // at the end
        }

        Native.MENUITEMINFO Info()
        {
            var info = new Native.MENUITEMINFO { cbSize = Marshal.SizeOf(typeof(Native.MENUITEMINFO)) };
            if (IsSeparator)
            {
                info.fMask = Native.MIIM_FTYPE | Native.MIIM_ID;
                info.fType = Native.MFT_SEPARATOR;
            }
            else
            {
                info.fMask = Native.MIIM_FTYPE | Native.MIIM_STATE | Native.MIIM_ID | Native.MIIM_STRING;
                info.fType = radio ? Native.MFT_RADIOCHECK : 0;
                info.fState = (enabled ? 0 : Native.MFS_DISABLED) | (check ? Native.MFS_CHECKED : 0);
                info.dwTypeData = text;
            }
            info.wID = Id;
            return info;
        }

        /// <summary>Show a change at once if the menu exists (the bar is redrawn for its own entries).</summary>
        void Update()
        {
            if (Parent == null || Parent.Handle == IntPtr.Zero) return;
            var info = Info();
            info.fMask &= ~Native.MIIM_ID;
            Native.SetMenuItemInfo(Parent.Handle, Id, false, ref info);
            var bar = Parent as MainMenu;
            if (bar != null) bar.Redraw();
        }

        internal void Forget()
        {
            foreach (var mi in MenuItems) mi.Forget();
            Handle = IntPtr.Zero;
        }

        internal void Unregister()
        {
            foreach (var mi in MenuItems) mi.Unregister();
            byId.Remove(Id);
        }

        public void PerformClick()
        {
            var h = Click;
            if (h != null) h(this, EventArgs.Empty);
        }

        /// <summary>A WM_COMMAND from a menu: run the item's action. False if the id is not one of ours.</summary>
        internal static bool Dispatch(int id)
        {
            MenuItem mi;
            if (!byId.TryGetValue(id, out mi) || mi.MenuItems.Count > 0) return false;
            if (mi.enabled) mi.PerformClick();
            return true;
        }
    }

    /// <summary>The menu bar of a form.</summary>
    public class MainMenu : Menu
    {
        Form form;

        internal override IntPtr Create() { return Native.CreateMenu(); }

        /// <summary>Put this menu bar on `f` (now, or when its window is made), replacing the old one. The window keeps
        /// its client size, as with the old WinForms Form.Menu.</summary>
        public void AttachTo(Form f)
        {
            form = f;
            if (f.IsHandleCreated) Show();
            else f.HandleCreated += (s, e) => { if (form == f) Show(); };
        }

        void Show()
        {
            var size = form.ClientSize;
            Native.SetMenu(form.Handle, Build());
            if (form.WindowState == FormWindowState.Normal) form.ClientSize = size;
        }

        internal void Redraw()
        {
            if (form != null && form.IsHandleCreated) Native.DrawMenuBar(form.Handle);
        }

        public override void Dispose()
        {
            if (form != null && form.IsHandleCreated && Handle != IntPtr.Zero && Native.GetMenu(form.Handle) == Handle)
                Native.SetMenu(form.Handle, IntPtr.Zero);
            form = null;
            base.Dispose();
        }
    }

    /// <summary>A right-click menu. Show() returns when it closes and then runs the chosen item.</summary>
    public class ContextMenu : Menu
    {
        internal override IntPtr Create() { return Native.CreatePopupMenu(); }

        public void Show(Control control, System.Drawing.Point at)
        {
            var p = control.PointToScreen(at);
            var owner = control.FindForm() ?? control;
            int id;
            try
            {
                id = Native.TrackPopupMenuEx(Build(), Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON, p.X, p.Y, owner.Handle, IntPtr.Zero);
            }
            finally
            {
                Dispose();
            }
            if (id != 0) Run(MenuItems, id);
        }

        static bool Run(List<MenuItem> items, int id)
        {
            foreach (var mi in items)
            {
                if (mi.Id == id && mi.Enabled) { mi.PerformClick(); return true; }
                if (Run(mi.MenuItems, id)) return true;
            }
            return false;
        }
    }

    static class Native
    {
        public const int MIIM_STATE = 0x1, MIIM_ID = 0x2, MIIM_SUBMENU = 0x4, MIIM_STRING = 0x40, MIIM_FTYPE = 0x100;
        public const int MFT_SEPARATOR = 0x800, MFT_RADIOCHECK = 0x200;
        public const int MFS_DISABLED = 0x3, MFS_CHECKED = 0x8;
        public const int TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MENUITEMINFO
        {
            public int cbSize, fMask, fType, fState, wID;
            public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData;
            public string dwTypeData;
            public int cch;
            public IntPtr hbmpItem;
        }

        [DllImport("user32.dll")] public static extern IntPtr CreateMenu();
        [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "InsertMenuItemW")]
        public static extern bool InsertMenuItem(IntPtr menu, int item, bool byPosition, ref MENUITEMINFO info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetMenuItemInfoW")]
        public static extern bool SetMenuItemInfo(IntPtr menu, int item, bool byPosition, ref MENUITEMINFO info);
        [DllImport("user32.dll")] public static extern bool SetMenu(IntPtr hwnd, IntPtr menu);
        [DllImport("user32.dll")] public static extern IntPtr GetMenu(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool DrawMenuBar(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern int TrackPopupMenuEx(IntPtr menu, int flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    }
}
