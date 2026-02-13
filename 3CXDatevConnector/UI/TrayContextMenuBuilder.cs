using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using DatevConnector.Core;
using DatevConnector.UI.Strings;
using DatevConnector.UI.Theme;

namespace DatevConnector.UI
{
    /// <summary>
    /// Builds the system tray context menu with dark theme styling.
    /// Extracted from TrayApplication to separate menu construction from business logic.
    ///
    /// Menu layout:
    /// Title | --- | Status | Anrufliste | Kontakte neu laden | ---
    /// Einstellungen | Hilfe > | --- | Autostart | Stummschalten | ---
    /// Neustart | Info | Beenden
    /// </summary>
    internal static class TrayContextMenuBuilder
    {
        /// <summary>
        /// Build the fully styled context menu. Returns named items for later updates.
        /// </summary>
        public static ContextMenuStrip Build()
        {
            var menu = new ContextMenuStrip();
            menu.Renderer = new DarkMenuRenderer();
            menu.ShowImageMargin = true;
            menu.ShowCheckMargin = true;

            // App title (bold, non-interactive)
            var titleItem = new ToolStripMenuItem(UIStrings.MenuItems.AppTitle)
            {
                Enabled = false,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };
            menu.Items.Add(titleItem);
            menu.Items.Add(new ToolStripSeparator());

            // Status (clickable, with colored dot image)
            var statusItem = new ToolStripMenuItem(UIStrings.MenuItems.Status)
            {
                Name = "statusItem",
                Image = CreateStatusDot(UITheme.StatusBad)
            };
            menu.Items.Add(statusItem);

            // Call History
            menu.Items.Add(new ToolStripMenuItem(UIStrings.MenuItems.CallHistory) { Name = "historyItem" });

            // Reload contacts
            menu.Items.Add(new ToolStripMenuItem(UIStrings.MenuItems.ReloadContacts) { Name = "reloadItem" });
            menu.Items.Add(new ToolStripSeparator());

            // Settings
            menu.Items.Add(new ToolStripMenuItem(UIStrings.MenuItems.Settings) { Name = "settingsItem" });

            // Help submenu
            var helpMenu = new ToolStripMenuItem(UIStrings.MenuItems.Help) { Name = "helpMenu" };
            helpMenu.DropDownItems.Add(new ToolStripMenuItem(UIStrings.MenuItems.Troubleshooting) { Name = "helpItem" });
            helpMenu.DropDownItems.Add(new ToolStripMenuItem(UIStrings.MenuItems.OpenLog) { Name = "logItem" });
            helpMenu.DropDownItems.Add(new ToolStripMenuItem(UIStrings.MenuItems.RunSetupWizard) { Name = "wizardItem" });
            helpMenu.DropDown.Renderer = new DarkMenuRenderer();
            menu.Items.Add(helpMenu);
            menu.Items.Add(new ToolStripSeparator());

            // Autostart toggle
            var autostartItem = new ToolStripMenuItem(UIStrings.MenuItems.Autostart)
            {
                Name = "autostartItem",
                CheckOnClick = true,
                Checked = AutoStartManager.IsEnabled()
            };
            menu.Items.Add(autostartItem);

            // Mute toggle
            var muteItem = new ToolStripMenuItem(UIStrings.MenuItems.Mute)
            {
                Name = "muteItem",
                CheckOnClick = true,
                Checked = false
            };
            menu.Items.Add(muteItem);
            menu.Items.Add(new ToolStripSeparator());

            // Restart, About, Exit
            menu.Items.Add(new ToolStripMenuItem(UIStrings.MenuItems.Restart) { Name = "restartItem" });
            menu.Items.Add(new ToolStripMenuItem(UIStrings.MenuItems.Info) { Name = "aboutItem" });
            menu.Items.Add(new ToolStripMenuItem(UIStrings.MenuItems.Exit) { Name = "exitItem" });

            return menu;
        }

        /// <summary>
        /// Create a small colored dot image for status menu items.
        /// </summary>
        public static Image CreateStatusDot(Color color)
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var brush = new SolidBrush(color))
                {
                    g.FillEllipse(brush, 3, 3, 10, 10);
                }
            }
            return bmp;
        }
    }
}
