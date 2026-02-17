using System;
using System.Windows.Forms;
using DatevConnector.Interop;

namespace DatevConnector.UI.Theme
{
    /// <summary>
    /// ContextMenuStrip subclass that applies DWM dark mode to the popup window
    /// and all submenus. On Windows 11 this gives native dark rounded corners.
    /// Falls back gracefully on older Windows (DwmInterop is a no-op).
    /// </summary>
    internal class DarkContextMenuStrip : ContextMenuStrip
    {
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DwmInterop.ApplyDarkTitleBar(Handle);
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            HookSubmenus(Items);
        }

        /// <summary>
        /// Recursively hook all submenu DropDown.Opened events so each submenu
        /// HWND gets DWM dark mode applied when it first appears.
        /// </summary>
        private static void HookSubmenus(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                if (item is ToolStripMenuItem menuItem && menuItem.HasDropDownItems)
                {
                    menuItem.DropDown.Opened -= OnSubmenuOpened;
                    menuItem.DropDown.Opened += OnSubmenuOpened;
                }
            }
        }

        private static void OnSubmenuOpened(object sender, EventArgs e)
        {
            if (sender is ToolStripDropDown dropDown)
            {
                DwmInterop.ApplyDarkTitleBar(dropDown.Handle);
                HookSubmenus(dropDown.Items);
            }
        }
    }
}
