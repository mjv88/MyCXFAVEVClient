using System.Drawing;
using System.Windows.Forms;
using DatevConnector.Core;
using DatevConnector.UI.Strings;
using DatevConnector.UI.Theme;

namespace DatevConnector.UI
{
    /// <summary>
    /// Dark-themed About dialog showing app info, version, and keyboard shortcuts.
    /// </summary>
    internal sealed class AboutForm : Form
    {
        public AboutForm()
        {
            InitializeForm();
            BuildLayout();
        }

        private void InitializeForm()
        {
            Text = UIStrings.FormTitles.About;
            ShowInTaskbar = false;
            Size = new Size(360, 380);
            KeyPreview = true;

            UITheme.ApplyFormDefaults(this);

            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter)
                    Close();
            };
        }

        private void BuildLayout()
        {
            // Accent bar at top
            Controls.Add(UITheme.CreateAccentBar(UITheme.ButtonPrimary));

            // Main content panel
            var content = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(LayoutConstants.SpaceLG)
            };
            Controls.Add(content);

            int y = LayoutConstants.SpaceLG;

            // App icon + title row
            var bridgeIcon = UITheme.GetBaseIconPublic();
            if (bridgeIcon != null)
            {
                var iconBox = new PictureBox
                {
                    Image = bridgeIcon,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Size = new Size(36, 36),
                    Location = new Point(LayoutConstants.SpaceLG, y)
                };
                content.Controls.Add(iconBox);

                var titleLabel = new Label
                {
                    Text = "3CX - DATEV Connector",
                    Font = UITheme.FontTitle,
                    ForeColor = UITheme.TextPrimary,
                    AutoSize = true,
                    Location = new Point(LayoutConstants.SpaceLG + 36 + LayoutConstants.SpaceSM, y + 6)
                };
                content.Controls.Add(titleLabel);
            }
            else
            {
                var titleLabel = new Label
                {
                    Text = "3CX - DATEV Connector",
                    Font = UITheme.FontTitle,
                    ForeColor = UITheme.TextPrimary,
                    AutoSize = true,
                    Location = new Point(LayoutConstants.SpaceLG, y)
                };
                content.Controls.Add(titleLabel);
            }

            y += 44;

            // Description
            var descLabel = new Label
            {
                Text = "Integration zwischen 3CX Windows Softphone App (V20) und DATEV.",
                Font = UITheme.FontBody,
                ForeColor = UITheme.TextSecondary,
                AutoSize = false,
                Size = new Size(300, 36),
                Location = new Point(LayoutConstants.SpaceLG, y)
            };
            content.Controls.Add(descLabel);

            y += 40;

            // Version
            var versionLabel = new Label
            {
                Text = "Version 1.0",
                Font = UITheme.FontBody,
                ForeColor = UITheme.TextMuted,
                AutoSize = true,
                Location = new Point(LayoutConstants.SpaceLG, y)
            };
            content.Controls.Add(versionLabel);

            y += 28;

            // Separator
            var separator = new Panel
            {
                BackColor = UITheme.CardBorder,
                Size = new Size(300, 1),
                Location = new Point(LayoutConstants.SpaceLG, y)
            };
            content.Controls.Add(separator);

            y += 12;

            // Shortcuts header
            var shortcutsHeader = new Label
            {
                Text = UIStrings.ShortcutLabels.KeyboardShortcuts + ":",
                Font = UITheme.FontLabel,
                ForeColor = UITheme.TextPrimary,
                AutoSize = true,
                Location = new Point(LayoutConstants.SpaceLG, y)
            };
            content.Controls.Add(shortcutsHeader);

            y += 24;

            // Shortcut entries - use monospace font for aligned columns
            var shortcuts = new[]
            {
                new { Key = "Strg+T", Desc = Shortcuts.GetShortcutDescription(Shortcuts.TestAll) },
                new { Key = "Strg+R", Desc = Shortcuts.GetShortcutDescription(Shortcuts.ReloadContacts) },
                new { Key = "Strg+L", Desc = Shortcuts.GetShortcutDescription(Shortcuts.OpenLog) },
                new { Key = "Strg+H", Desc = Shortcuts.GetShortcutDescription(Shortcuts.CallHistory) },
                new { Key = "Strg+S", Desc = Shortcuts.GetShortcutDescription(Shortcuts.SaveSettings) },
                new { Key = "F5",     Desc = Shortcuts.GetShortcutDescription(Shortcuts.Refresh) },
                new { Key = "Esc",    Desc = Shortcuts.GetShortcutDescription(Shortcuts.CloseWindow) }
            };

            int keyColumnX = LayoutConstants.SpaceLG + LayoutConstants.SpaceMD;
            int descColumnX = keyColumnX + 68;

            foreach (var shortcut in shortcuts)
            {
                var keyLabel = new Label
                {
                    Text = shortcut.Key,
                    Font = UITheme.FontMono,
                    ForeColor = UITheme.TextAccentInfo,
                    AutoSize = true,
                    Location = new Point(keyColumnX, y)
                };
                content.Controls.Add(keyLabel);

                var descLabel2 = new Label
                {
                    Text = shortcut.Desc,
                    Font = UITheme.FontBody,
                    ForeColor = UITheme.TextSecondary,
                    AutoSize = true,
                    Location = new Point(descColumnX, y)
                };
                content.Controls.Add(descLabel2);

                y += 20;
            }

            // Footer panel with OK button
            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                BackColor = UITheme.PanelBackground,
                Padding = new Padding(LayoutConstants.SpaceMD)
            };

            var okButton = UITheme.CreatePrimaryButton(UIStrings.Labels.OK, 80);
            okButton.Click += (s, e) => Close();

            // Center the button in footer
            footer.Layout += (s, e) =>
            {
                okButton.Location = new Point(
                    (footer.ClientSize.Width - okButton.Width) / 2,
                    (footer.ClientSize.Height - okButton.Height) / 2);
            };

            footer.Controls.Add(okButton);
            Controls.Add(footer);

            AcceptButton = okButton;
        }
    }
}
