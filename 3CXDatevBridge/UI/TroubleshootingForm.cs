using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using DatevBridge.Datev.Managers;
using DatevBridge.UI.Strings;
using DatevBridge.UI.Theme;

namespace DatevBridge.UI
{
    /// <summary>
    /// Help form showing common problems and solutions.
    /// Provides quick access to log files and troubleshooting guidance.
    /// </summary>
    public class TroubleshootingForm : Form
    {
        private readonly Panel _contentPanel;

        public TroubleshootingForm()
        {
            InitializeForm();
            _contentPanel = CreateContentPanel();
            Controls.Add(_contentPanel);
            CreateSections();
            CreateFooter();
        }

        private void InitializeForm()
        {
            Text = UIStrings.FormTitles.Troubleshooting;
            ClientSize = new Size(500, 620);
            BackColor = UITheme.FormBackground;
            ForeColor = UITheme.TextPrimary;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = UITheme.FontBody;

            var appIcon = UITheme.GetFormIcon();
            if (appIcon != null)
            {
                Icon = appIcon;
                ShowIcon = true;
            }
            else
            {
                ShowIcon = false;
            }

            // Blue accent bar for help/info
            var accentBar = UITheme.CreateAccentBar(UITheme.AccentIncoming);
            Controls.Add(accentBar);
        }

        private Panel CreateContentPanel()
        {
            return new Panel
            {
                Location = new Point(0, UITheme.AccentBarHeight),
                Size = new Size(ClientSize.Width, ClientSize.Height - UITheme.AccentBarHeight),
                AutoScroll = true,
                BackColor = UITheme.FormBackground
            };
        }

        private void CreateSections()
        {
            int y = LayoutConstants.SpaceMD;

            // Header
            var lblHeader = new Label
            {
                Text = UIStrings.Troubleshooting.CommonProblems,
                Font = UITheme.FontLarge,
                ForeColor = UITheme.TextPrimary,
                Location = new Point(LayoutConstants.SpaceLG, y),
                AutoSize = true
            };
            _contentPanel.Controls.Add(lblHeader);
            y += 35;

            // TAPI Problems Section
            y = CreateSection(y, UIStrings.Troubleshooting.TapiProblems, UITheme.AccentIncoming, new[]
            {
                (UIStrings.Troubleshooting.TapiNotConnected, UIStrings.Troubleshooting.TapiNotConnectedDesc),
                (UIStrings.Troubleshooting.TapiIniNotFound, UIStrings.Troubleshooting.TapiIniNotFoundDesc),
                (UIStrings.Troubleshooting.TapiNoLines, UIStrings.Troubleshooting.TapiNoLinesDesc)
            });

            // DATEV Problems Section
            y = CreateSection(y, UIStrings.Troubleshooting.DatevProblems, UITheme.AccentDatev, new[]
            {
                (UIStrings.Troubleshooting.DatevNotReachable, UIStrings.Troubleshooting.DatevNotReachableDesc),
                (UIStrings.Troubleshooting.DatevCircuitBreaker, UIStrings.Troubleshooting.DatevCircuitBreakerDesc),
                (UIStrings.Troubleshooting.DatevSddTimeout, UIStrings.Troubleshooting.DatevSddTimeoutDesc)
            });

            // Contact Problems Section
            y = CreateSection(y, UIStrings.Troubleshooting.ContactProblems, UITheme.AccentBridge, new[]
            {
                (UIStrings.Troubleshooting.ContactNotFound, UIStrings.Troubleshooting.ContactNotFoundDesc),
                (UIStrings.Troubleshooting.NoContactsLoaded, UIStrings.Troubleshooting.NoContactsLoadedDesc),
                (UIStrings.Troubleshooting.FewerContacts, UIStrings.Troubleshooting.FewerContactsDesc)
            });

            // Terminal Server Problems Section
            y = CreateSection(y, UIStrings.Troubleshooting.TerminalServerProblems, UITheme.AccentTapi, new[]
            {
                (UIStrings.Troubleshooting.TsNoConnection, UIStrings.Troubleshooting.TsNoConnectionDesc),
                (UIStrings.Troubleshooting.TsRestartOrder, UIStrings.Troubleshooting.TsRestartOrderDesc)
            });

            // General tip
            y += LayoutConstants.SpaceSM;
            var lblTip = new Label
            {
                Text = UIStrings.Troubleshooting.CheckLogFile,
                Font = UITheme.FontItalic,
                ForeColor = UITheme.TextMuted,
                Location = new Point(LayoutConstants.SpaceLG, y),
                AutoSize = true
            };
            _contentPanel.Controls.Add(lblTip);
        }

        private int CreateSection(int startY, string title, Color accentColor, (string problem, string solution)[] items)
        {
            int y = startY;
            int cardWidth = ClientSize.Width - (LayoutConstants.SpaceLG * 2);

            // Section header with accent color
            var lblTitle = new Label
            {
                Text = title,
                Font = UITheme.FontLabel,
                ForeColor = accentColor,
                Location = new Point(LayoutConstants.SpaceLG, y),
                AutoSize = true
            };
            _contentPanel.Controls.Add(lblTitle);
            y += 22;

            // Card background for items
            var card = new Panel
            {
                Location = new Point(LayoutConstants.SpaceLG, y),
                Size = new Size(cardWidth, 0), // Height calculated below
                BackColor = UITheme.CardBackground
            };

            int cardY = LayoutConstants.SpaceSM;
            foreach (var (problem, solution) in items)
            {
                // Problem title (bold)
                var lblProblem = new Label
                {
                    Text = problem,
                    Font = UITheme.FontLabel,
                    ForeColor = UITheme.TextPrimary,
                    Location = new Point(LayoutConstants.CardPadding, cardY),
                    AutoSize = true
                };
                card.Controls.Add(lblProblem);
                cardY += 18;

                // Solution text (normal, wrapped)
                var lblSolution = new Label
                {
                    Text = solution,
                    Font = UITheme.FontBody,
                    ForeColor = UITheme.TextSecondary,
                    Location = new Point(LayoutConstants.CardPadding, cardY),
                    Size = new Size(cardWidth - (LayoutConstants.CardPadding * 2), 36),
                    AutoEllipsis = false
                };
                card.Controls.Add(lblSolution);
                cardY += 40;
            }

            card.Height = cardY + LayoutConstants.SpaceXS;
            _contentPanel.Controls.Add(card);

            return y + card.Height + LayoutConstants.SpaceMD;
        }

        private void CreateFooter()
        {
            int btnY = ClientSize.Height - 50;
            int btnWidth = 130;

            // Open log button
            var btnLog = UITheme.CreateSecondaryButton(UIStrings.Troubleshooting.OpenLogFile, btnWidth);
            btnLog.Location = new Point(LayoutConstants.SpaceLG, btnY);
            btnLog.Click += BtnLog_Click;
            Controls.Add(btnLog);

            // Close button
            var btnClose = UITheme.CreatePrimaryButton(UIStrings.Labels.Close, 80);
            btnClose.Location = new Point(ClientSize.Width - 80 - LayoutConstants.SpaceLG, btnY);
            btnClose.Click += (s, e) => Close();
            Controls.Add(btnClose);

            CancelButton = btnClose;
        }

        private void BtnLog_Click(object sender, EventArgs e)
        {
            try
            {
                string logPath = LogManager.LogFilePath;
                if (File.Exists(logPath))
                {
                    Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
                }
                else
                {
                    MessageBox.Show(
                        UIStrings.Errors.LogFileNotFound,
                        UIStrings.FormTitles.AppTitle,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                LogManager.Log("Troubleshooting: Error opening log - {0}", ex.Message);
            }
        }

        /// <summary>
        /// Show the troubleshooting form as a modal dialog.
        /// </summary>
        public static void ShowHelp()
        {
            using (var form = new TroubleshootingForm())
            {
                form.ShowDialog();
            }
        }
    }
}
