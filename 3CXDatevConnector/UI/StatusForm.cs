using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DatevConnector.Core;
using DatevConnector.Datev;
using DatevConnector.Datev.Managers;
using DatevConnector.Tapi;
using DatevConnector.UI.Strings;
using DatevConnector.UI.Theme;

namespace DatevConnector.UI
{
    /// <summary>
    /// Quick status overview form shown on tray icon double-click.
    /// Shows connection status with colored indicators and quick action buttons.
    /// </summary>
    public class StatusForm : ThemedForm
    {
        /// <summary>
        /// Which action the user requested before closing.
        /// </summary>
        public enum Action { None, CallHistory, Settings }

        private readonly ConnectorService _bridgeService;
        private Label _lblDatevStatus;
        private Label _lblTapiStatus;
        private Label _lblConnectorStatus;
        private Label _lblContacts;
        private Label _lblSyncTime;
        private Label _lblDatevProgress;
        private Label _lblTapiProgress;
        private Label _lblBridgeProgress;
        private Button _btnTestDatev;
        private Button _btnReloadContacts;
        private Button _btnTestTapi;
        private Button _btnReconnectTapi;
        private Button _btnReconnectAll;

        // Multi-line TAPI support
        private Panel _tapiCard;
        private readonly Dictionary<string, Label> _lineStatusLabels = new Dictionary<string, Label>();
        private readonly Dictionary<string, Label> _lineProgressLabels = new Dictionary<string, Label>();
        private readonly Dictionary<string, Button> _lineTestButtons = new Dictionary<string, Button>();
        private readonly Dictionary<string, Button> _lineReconnectButtons = new Dictionary<string, Button>();

        /// <summary>
        /// The action requested by the user (check after ShowDialog returns).
        /// </summary>
        public Action RequestedAction { get; private set; }

        public StatusForm(ConnectorService bridgeService)
        {
            _bridgeService = bridgeService;
            RequestedAction = Action.None;
            InitializeComponent();

            // Enable keyboard shortcuts
            KeyPreview = true;
            KeyDown += OnKeyDown;

            // Subscribe to status changes for real-time updates
            if (_bridgeService != null)
            {
                _bridgeService.StatusChanged += OnConnectorStatusChanged;
            }

            Disposed += (s, e) =>
            {
                if (_bridgeService != null)
                {
                    _bridgeService.StatusChanged -= OnConnectorStatusChanged;
                }
            };
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+T - Test all connections
            if (Shortcuts.Matches(e, Shortcuts.TestAll))
            {
                BtnReconnectAll_Click(null, EventArgs.Empty);
                e.Handled = true;
            }
            // Ctrl+R - Reload contacts
            else if (Shortcuts.Matches(e, Shortcuts.ReloadContacts))
            {
                BtnReloadContacts_Click(null, EventArgs.Empty);
                e.Handled = true;
            }
            // Ctrl+H - Open call history
            else if (Shortcuts.Matches(e, Shortcuts.CallHistory))
            {
                RequestedAction = Action.CallHistory;
                Close();
                e.Handled = true;
            }
            // Escape - Close
            else if (e.KeyCode == Keys.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        private void OnConnectorStatusChanged(ConnectorStatus status)
        {
            if (IsDisposed || !IsHandleCreated) return;

            // Marshal to UI thread
            if (InvokeRequired)
            {
                BeginInvoke(new System.Action(() => OnConnectorStatusChanged(status)));
                return;
            }

            // Update TAPI status
            bool tapiOk = _bridgeService?.TapiConnected ?? false;
            string ext = _bridgeService?.Extension ?? "\u2014";
            _lblTapiStatus.Text = tapiOk ? string.Format(UIStrings.Status.ConnectedExt, ext) : UIStrings.Status.Disconnected;
            _lblTapiStatus.ForeColor = tapiOk ? UITheme.StatusOk : UITheme.StatusBad;
            _btnReconnectTapi.Enabled = !tapiOk;

            // Reset button state if reconnecting
            if (_btnReconnectTapi.Text == UIStrings.Status.TestPending)
            {
                _btnReconnectTapi.Text = UIStrings.Labels.Connect;
            }
            if (_btnTestTapi != null && _btnTestTapi.Text == UIStrings.Status.TestPending)
            {
                _btnTestTapi.Text = UIStrings.Labels.Test;
                _btnTestTapi.Enabled = true;
            }
            if (_btnReconnectAll.Text == UIStrings.Status.TestPending)
            {
                _btnReconnectAll.Text = UIStrings.Labels.Test;
                _btnReconnectAll.Enabled = true;
            }

            UpdateConnectorStatus();
        }

        private void InitializeComponent()
        {
            // ThemedForm base class handles: BackColor, ForeColor, FormBorderStyle,
            // StartPosition, MaximizeBox, MinimizeBox, Font, Icon
            Text = UIStrings.FormTitles.Overview;
            TopMost = false;

            int y = UITheme.SpacingL;
            int cardWidth = 388;
            int cardHeight = 100;
            int datevCardHeight = 116;
            int btnWidth = 75;
            int btnSpacing = 8;

            // Calculate extra height for multi-line TAPI display
            var tapiLines = _bridgeService?.TapiLines ?? new List<TapiLineInfo>();
            int lineCount = tapiLines.Count;
            int extraTapiHeight = lineCount > 1 ? (lineCount - 1) * 24 : 0;
            ClientSize = new Size(420, 434 + extraTapiHeight);

            // ==================== DATEV Section ====================
            var datevCard = CreateSectionCard(UIStrings.Sections.Datev, UITheme.AccentDatev, y, cardWidth, datevCardHeight);
            Controls.Add(datevCard);

            bool datevOk = _bridgeService?.DatevAvailable ?? false;
            int contacts = _bridgeService?.ContactCount ?? 0;

            _lblDatevStatus = new Label
            {
                Text = datevOk ? UIStrings.Status.Connected : UIStrings.Status.Unavailable,
                ForeColor = datevOk ? UITheme.StatusOk : UITheme.StatusBad,
                Font = UITheme.FontBody,
                Location = new Point(12, 28),
                AutoSize = true
            };
            datevCard.Controls.Add(_lblDatevStatus);

            _lblContacts = new Label
            {
                Text = string.Format(UIStrings.Messages.ContactsFormat, contacts),
                ForeColor = UITheme.TextSecondary,
                Font = UITheme.FontSmall,
                Location = new Point(12, 48),
                AutoSize = true
            };
            datevCard.Controls.Add(_lblContacts);

            // Contact sync time
            var syncTs = DatevContactManager.LastSyncTimestamp;
            string syncText = syncTs.HasValue
                ? string.Format(UIStrings.Status.LastSyncFormat, syncTs.Value)
                : UIStrings.Status.LastSyncNone;
            _lblSyncTime = new Label
            {
                Text = syncText,
                ForeColor = UITheme.TextSecondary,
                Font = UITheme.FontSmall,
                Location = new Point(12, 64),
                AutoSize = true
            };
            datevCard.Controls.Add(_lblSyncTime);

            // Progress label with styled background (hidden by default)
            _lblDatevProgress = UITheme.CreateProgressLabel(cardWidth - 24);
            _lblDatevProgress.Location = new Point(12, 94);
            _lblDatevProgress.Visible = false;
            datevCard.Controls.Add(_lblDatevProgress);

            // Buttons aligned to the right
            _btnReloadContacts = UITheme.CreateSecondaryButton(UIStrings.Labels.Load, btnWidth);
            _btnReloadContacts.Location = new Point(cardWidth - 12 - btnWidth, 40);
            _btnReloadContacts.Click += BtnReloadContacts_Click;
            datevCard.Controls.Add(_btnReloadContacts);

            _btnTestDatev = UITheme.CreateSecondaryButton(UIStrings.Labels.Test, btnWidth);
            _btnTestDatev.Location = new Point(cardWidth - 12 - btnWidth - btnSpacing - btnWidth, 40);
            _btnTestDatev.Click += BtnTestDatev_Click;
            datevCard.Controls.Add(_btnTestDatev);

            y += datevCardHeight + UITheme.SpacingM;

            // ==================== 3CX Section ====================
            // Calculate card height based on number of lines (reuse tapiLines/lineCount from above)
            int tapiCardHeight = lineCount > 1 ? 88 + (lineCount * 24) : cardHeight + 18;

            _tapiCard = CreateSectionCard(UIStrings.Sections.Tapi, UITheme.AccentIncoming, y, cardWidth, tapiCardHeight);
            Controls.Add(_tapiCard);

            bool tapiOk = _bridgeService?.TapiConnected ?? false;

            // Active mode label (shown in both single and multi-line)
            var activeModeName = _bridgeService != null
                ? TelephonyProviderSelector.GetModeShortName(_bridgeService.SelectedTelephonyMode)
                : "\u2014";

            if (lineCount <= 1)
            {
                // Single line mode
                string ext = _bridgeService?.Extension ?? "—";

                _lblTapiStatus = new Label
                {
                    Text = tapiOk ? string.Format(UIStrings.Status.ConnectedExt, ext) : UIStrings.Status.Disconnected,
                    ForeColor = tapiOk ? UITheme.StatusOk : UITheme.StatusBad,
                    Font = UITheme.FontBody,
                    Location = new Point(12, 28),
                    AutoSize = true
                };
                _tapiCard.Controls.Add(_lblTapiStatus);

                _tapiCard.Controls.Add(new Label
                {
                    Text = activeModeName,
                    ForeColor = UITheme.TextMuted,
                    Font = UITheme.FontSmall,
                    Location = new Point(12, 48),
                    AutoSize = true
                });

                // Buttons aligned to the right
                _btnReconnectTapi = UITheme.CreateSecondaryButton(UIStrings.Labels.Connect, btnWidth);
                _btnReconnectTapi.Location = new Point(cardWidth - 12 - btnWidth, 58);
                _btnReconnectTapi.Click += BtnReconnectTapi_Click;
                _btnReconnectTapi.Enabled = !tapiOk;
                _tapiCard.Controls.Add(_btnReconnectTapi);

                _btnTestTapi = UITheme.CreateSecondaryButton(UIStrings.Labels.Test, btnWidth);
                _btnTestTapi.Location = new Point(cardWidth - 12 - btnWidth - btnSpacing - btnWidth, 58);
                _btnTestTapi.Click += BtnTestTapi_Click;
                _tapiCard.Controls.Add(_btnTestTapi);
            }
            else
            {
                // Multi-line mode - show each line with its own status, progress, and buttons
                _lblTapiStatus = new Label
                {
                    Text = string.Format(UIStrings.Status.LinesConnected, _bridgeService.ConnectedLineCount, lineCount),
                    ForeColor = _bridgeService.ConnectedLineCount == lineCount ? UITheme.StatusOk :
                               (_bridgeService.ConnectedLineCount > 0 ? UITheme.StatusWarn : UITheme.StatusBad),
                    Font = UITheme.FontBody,
                    Location = new Point(12, 28),
                    AutoSize = true
                };
                _tapiCard.Controls.Add(_lblTapiStatus);

                _tapiCard.Controls.Add(new Label
                {
                    Text = activeModeName,
                    ForeColor = UITheme.TextMuted,
                    Font = UITheme.FontSmall,
                    Location = new Point(12, 48),
                    AutoSize = true
                });

                // "Neuverbinden" button for reconnecting all lines
                _btnReconnectTapi = UITheme.CreateSecondaryButton(UIStrings.Labels.ReconnectShort, 90);
                _btnReconnectTapi.Location = new Point(cardWidth - 12 - 90, 24);
                _btnReconnectTapi.Click += BtnReconnectAllLines_Click;
                _tapiCard.Controls.Add(_btnReconnectTapi);

                // Individual line rows: Status | Progress | Testen | Verb.
                int lineY = 70;
                int btnTestWidth = 50;
                int btnVerbWidth = 45;
                int btnGap = 4;

                foreach (var line in tapiLines)
                {
                    // Status label (left)
                    string lineStatusText = line.IsConnected ? UIStrings.Status.Connected : UIStrings.Status.Disconnected;
                    var lblLine = new Label
                    {
                        Text = string.Format(UIStrings.Status.LineStatus, line.Extension, lineStatusText),
                        ForeColor = line.IsConnected ? UITheme.StatusOk : UITheme.StatusBad,
                        Font = UITheme.FontSmall,
                        Location = new Point(20, lineY),
                        Size = new Size(120, 18)
                    };
                    _tapiCard.Controls.Add(lblLine);
                    _lineStatusLabels[line.Extension] = lblLine;

                    // Progress label (middle) with styled background (hidden by default)
                    int progressWidth = cardWidth - 145 - btnTestWidth - btnVerbWidth - btnGap - 16;
                    var lblProgress = UITheme.CreateProgressLabel(progressWidth);
                    lblProgress.Location = new Point(145, lineY);
                    lblProgress.Visible = false;
                    _tapiCard.Controls.Add(lblProgress);
                    _lineProgressLabels[line.Extension] = lblProgress;

                    // Test button
                    var btnTest = UITheme.CreateSecondaryButton(UIStrings.Labels.Test, btnTestWidth);
                    btnTest.Font = UITheme.FontSmall;
                    btnTest.Location = new Point(cardWidth - 12 - btnVerbWidth - btnGap - btnTestWidth, lineY - 2);
                    btnTest.Tag = line.Extension;
                    btnTest.Click += BtnTestSingleLine_Click;
                    _tapiCard.Controls.Add(btnTest);
                    _lineTestButtons[line.Extension] = btnTest;

                    // Reconnect button
                    var btnReconnect = UITheme.CreateSecondaryButton(UIStrings.Labels.ConnectShort, btnVerbWidth);
                    btnReconnect.Font = UITheme.FontSmall;
                    btnReconnect.Location = new Point(cardWidth - 12 - btnVerbWidth, lineY - 2);
                    btnReconnect.Tag = line.Extension;
                    btnReconnect.Enabled = !line.IsConnected;
                    btnReconnect.Click += BtnReconnectSingleLine_Click;
                    _tapiCard.Controls.Add(btnReconnect);
                    _lineReconnectButtons[line.Extension] = btnReconnect;

                    lineY += 24;
                }
            }

            // Progress label (only for single-line mode, multi-line has per-line progress)
            if (lineCount <= 1)
            {
                _lblTapiProgress = UITheme.CreateProgressLabel(cardWidth - 24);
                _lblTapiProgress.Location = new Point(12, 96);
                _lblTapiProgress.Visible = false;
                _tapiCard.Controls.Add(_lblTapiProgress);
            }

            y += tapiCardHeight + UITheme.SpacingM;

            // ==================== Bridge Section ====================
            bool operational = datevOk && tapiOk;
            bool partial = datevOk || tapiOk;
            Color statusColor = operational ? UITheme.StatusOk : (partial ? UITheme.StatusWarn : UITheme.StatusBad);

            var bridgeCard = CreateSectionCard(UIStrings.Sections.Bridge, UITheme.AccentBridge, y, cardWidth, cardHeight);
            Controls.Add(bridgeCard);

            string bridgeText = operational ? UIStrings.Status.Ready : (partial ? UIStrings.Status.Partial : UIStrings.Status.Unavailable);
            _lblConnectorStatus = new Label
            {
                Text = bridgeText,
                ForeColor = statusColor,
                Font = UITheme.FontBody,
                Location = new Point(12, 28),
                AutoSize = true
            };
            bridgeCard.Controls.Add(_lblConnectorStatus);

            // Progress label with styled background (hidden by default)
            _lblBridgeProgress = UITheme.CreateProgressLabel(cardWidth - 24);
            _lblBridgeProgress.Location = new Point(12, 78);
            _lblBridgeProgress.Visible = false;
            bridgeCard.Controls.Add(_lblBridgeProgress);

            // Button aligned to the right
            _btnReconnectAll = UITheme.CreateSecondaryButton(UIStrings.Labels.Test, btnWidth);
            _btnReconnectAll.Location = new Point(cardWidth - 12 - btnWidth, 40);
            _btnReconnectAll.Click += BtnReconnectAll_Click;
            bridgeCard.Controls.Add(_btnReconnectAll);

            y += cardHeight + UITheme.SpacingM;

            // ==================== Quick Action Buttons ====================
            int actionBtnWidth = 180;
            int actionBtnSpacing = UITheme.SpacingM;
            int totalActionWidth = (actionBtnWidth * 2) + actionBtnSpacing;
            int actionBtnX = UITheme.SpacingL + (cardWidth - totalActionWidth) / 2;

            var btnHistory = UITheme.CreateSecondaryButton(UIStrings.Labels.CallHistory, actionBtnWidth);
            btnHistory.Location = new Point(actionBtnX, y);
            btnHistory.Click += (s, e) =>
            {
                RequestedAction = Action.CallHistory;
                Close();
            };
            Controls.Add(btnHistory);

            var btnSettings = UITheme.CreateSecondaryButton(UIStrings.Labels.Settings, actionBtnWidth);
            btnSettings.Location = new Point(actionBtnX + actionBtnWidth + actionBtnSpacing, y);
            btnSettings.Click += (s, e) =>
            {
                RequestedAction = Action.Settings;
                Close();
            };
            Controls.Add(btnSettings);
        }

        private Panel CreateSectionCard(string title, Color accentColor, int yPos, int width = 288, int height = 80)
        {
            var card = new Panel
            {
                Location = new Point(UITheme.SpacingL, yPos),
                Size = new Size(width, height),
                BackColor = UITheme.CardBackground
            };

            // Paint border
            card.Paint += (s, e) =>
            {
                using (var pen = new Pen(UITheme.CardBorder))
                {
                    var r = card.ClientRectangle;
                    r.Width -= 1;
                    r.Height -= 1;
                    e.Graphics.DrawRectangle(pen, r);
                }
                // Accent line at top
                using (var brush = new SolidBrush(accentColor))
                {
                    e.Graphics.FillRectangle(brush, 0, 0, card.Width, 3);
                }
            };

            var lblTitle = new Label
            {
                Text = title,
                Font = UITheme.FontLabel,
                ForeColor = accentColor,
                Location = new Point(12, 8),
                AutoSize = true
            };
            card.Controls.Add(lblTitle);

            return card;
        }

        private void UpdateProgressLabel(Label label, string text)
        {
            if (IsDisposed || !IsHandleCreated) return;

            if (InvokeRequired)
            {
                BeginInvoke(new System.Action(() => UpdateProgressLabel(label, text)));
                return;
            }

            label.Text = text;
            // Show the label when there's text to display
            if (!string.IsNullOrEmpty(text) && !label.Visible)
                label.Visible = true;
        }

        private void HideProgressLabel(Label label)
        {
            if (IsDisposed || !IsHandleCreated) return;

            if (InvokeRequired)
            {
                BeginInvoke(new System.Action(() => HideProgressLabel(label)));
                return;
            }

            label.Text = "";
            label.Visible = false;
        }

        private async void BtnTestDatev_Click(object sender, EventArgs e)
        {
            _btnTestDatev.Enabled = false;
            _btnReloadContacts.Enabled = false;
            _btnTestDatev.Text = UIStrings.Status.TestPending;
            _lblDatevProgress.Visible = true;
            _lblDatevProgress.Text = "";

            bool available = await Task.Run(() =>
                DatevConnectionChecker.CheckAndLogDatevStatus(msg => UpdateProgressLabel(_lblDatevProgress, msg)));

            if (IsDisposed || !IsHandleCreated) return;

            _lblDatevStatus.Text = available ? UIStrings.Status.Connected : UIStrings.Status.Unavailable;
            _lblDatevStatus.ForeColor = available ? UITheme.StatusOk : UITheme.StatusBad;

            // Show visual feedback
            _btnTestDatev.Text = available ? UIStrings.Status.TestSuccess : UIStrings.Status.TestFailed;
            _btnTestDatev.ForeColor = available ? UITheme.StatusOk : UITheme.StatusBad;

            // Reset button after short delay
            await Task.Delay(1500);
            if (IsDisposed || !IsHandleCreated) return;

            _btnTestDatev.Text = UIStrings.Labels.Test;
            _btnTestDatev.ForeColor = UITheme.TextPrimary;
            _btnTestDatev.Enabled = true;
            _btnReloadContacts.Enabled = true;
            HideProgressLabel(_lblDatevProgress);

            UpdateConnectorStatus();
        }

        private async void BtnReloadContacts_Click(object sender, EventArgs e)
        {
            _btnReloadContacts.Enabled = false;
            _btnTestDatev.Enabled = false;
            _btnReloadContacts.Text = UIStrings.Status.TestPending;
            _lblDatevProgress.Visible = true;
            _lblDatevProgress.Text = "";

            await _bridgeService.ReloadContactsAsync(msg => UpdateProgressLabel(_lblDatevProgress, msg));

            if (IsDisposed || !IsHandleCreated) return;

            int contacts = _bridgeService?.ContactCount ?? 0;
            _lblContacts.Text = string.Format(UIStrings.Messages.ContactsFormat, contacts);
            UpdateSyncTime();
            _btnReloadContacts.Text = UIStrings.Labels.Load;
            _btnReloadContacts.Enabled = true;
            _btnTestDatev.Enabled = true;
            HideProgressLabel(_lblDatevProgress);
        }

        private async void BtnTestTapi_Click(object sender, EventArgs e)
        {
            _btnTestTapi.Enabled = false;
            _btnReconnectTapi.Enabled = false;
            _btnTestTapi.Text = UIStrings.Status.TestPending;
            if (_lblTapiProgress != null)
            {
                _lblTapiProgress.Visible = true;
                _lblTapiProgress.Text = "";
            }

            string extension = _bridgeService?.Extension;
            bool isConnected = await Task.Run(() =>
                _bridgeService?.TestTapiLine(extension,
                    msg => { if (_lblTapiProgress != null) UpdateProgressLabel(_lblTapiProgress, msg); }) ?? false);

            if (IsDisposed || !IsHandleCreated) return;

            _lblTapiStatus.Text = isConnected ? string.Format(UIStrings.Status.ConnectedExt, extension ?? "—") : UIStrings.Status.Disconnected;
            _lblTapiStatus.ForeColor = isConnected ? UITheme.StatusOk : UITheme.StatusBad;

            // Show visual feedback
            _btnTestTapi.Text = isConnected ? UIStrings.Status.TestSuccess : UIStrings.Status.TestFailed;
            _btnTestTapi.ForeColor = isConnected ? UITheme.StatusOk : UITheme.StatusBad;

            // Reset button after short delay
            await Task.Delay(1500);
            if (IsDisposed || !IsHandleCreated) return;

            _btnTestTapi.Text = UIStrings.Labels.Test;
            _btnTestTapi.ForeColor = UITheme.TextPrimary;
            _btnTestTapi.Enabled = true;
            _btnReconnectTapi.Enabled = !isConnected;
            if (_lblTapiProgress != null) HideProgressLabel(_lblTapiProgress);

            UpdateConnectorStatus();
        }

        private async void BtnReconnectTapi_Click(object sender, EventArgs e)
        {
            _btnReconnectTapi.Enabled = false;
            _btnReconnectTapi.Text = UIStrings.Status.TestPending;
            if (_lblTapiProgress != null)
            {
                _lblTapiProgress.Visible = true;
                _lblTapiProgress.Text = "";
            }

            await _bridgeService.ReconnectTapiAsync(msg => { if (_lblTapiProgress != null) UpdateProgressLabel(_lblTapiProgress, msg); });

            // StatusChanged event will update UI when connection establishes
            // But ensure button is restored if no status change occurs within timeout
            await Task.Delay(6000);
            if (IsDisposed || !IsHandleCreated) return;

            if (_btnReconnectTapi.Text == UIStrings.Status.TestPending)
            {
                bool tapiOk = _bridgeService?.TapiConnected ?? false;
                string ext = _bridgeService?.Extension ?? "\u2014";
                _lblTapiStatus.Text = tapiOk ? string.Format(UIStrings.Status.ConnectedExt, ext) : UIStrings.Status.Disconnected;
                _lblTapiStatus.ForeColor = tapiOk ? UITheme.StatusOk : UITheme.StatusBad;
                _btnReconnectTapi.Text = UIStrings.Labels.Connect;
                _btnReconnectTapi.Enabled = !tapiOk;
                UpdateConnectorStatus();
            }
            if (_lblTapiProgress != null) HideProgressLabel(_lblTapiProgress);
        }

        private async void BtnReconnectAllLines_Click(object sender, EventArgs e)
        {
            _btnReconnectTapi.Enabled = false;
            _btnReconnectTapi.Text = UIStrings.Status.TestPending;

            // Disable all individual line buttons and show/clear progress
            foreach (var ext in _lineReconnectButtons.Keys)
            {
                _lineReconnectButtons[ext].Enabled = false;
                if (_lineProgressLabels.TryGetValue(ext, out var lbl))
                {
                    lbl.Visible = true;
                    lbl.Text = "";
                }
            }

            // Reconnect each line with per-line progress
            var tapiLines = _bridgeService?.TapiLines;
            if (tapiLines != null)
            {
                foreach (var line in tapiLines)
                {
                    string ext = line.Extension;
                    Label progressLabel = null;
                    _lineProgressLabels.TryGetValue(ext, out progressLabel);

                    await Task.Run(() => _bridgeService.ReconnectTapiLine(ext,
                        msg => { if (progressLabel != null) UpdateProgressLabel(progressLabel, msg); }));

                    if (IsDisposed || !IsHandleCreated) return;
                }
            }

            await Task.Delay(1000);
            if (IsDisposed || !IsHandleCreated) return;

            UpdateTapiLineStatuses();
            _btnReconnectTapi.Text = UIStrings.Labels.ReconnectShort;
            _btnReconnectTapi.Enabled = true;
            UpdateConnectorStatus();

            // Hide all per-line progress labels
            foreach (var lbl in _lineProgressLabels.Values)
                HideProgressLabel(lbl);
        }

        private async void BtnTestSingleLine_Click(object sender, EventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;

            string extension = btn.Tag as string;
            if (string.IsNullOrEmpty(extension)) return;

            btn.Enabled = false;
            btn.Text = UIStrings.Status.TestPending;

            // Get the per-line progress label and show it
            Label progressLabel = null;
            _lineProgressLabels.TryGetValue(extension, out progressLabel);
            if (progressLabel != null)
            {
                progressLabel.Visible = true;
                progressLabel.Text = "";
            }

            // Perform actual TAPI line test with detailed progress
            bool isConnected = await Task.Run(() =>
                _bridgeService?.TestTapiLine(extension,
                    msg => { if (progressLabel != null) UpdateProgressLabel(progressLabel, msg); }) ?? false);

            if (IsDisposed || !IsHandleCreated) return;

            // Show visual feedback on button
            btn.Text = isConnected ? UIStrings.Status.TestSuccess : UIStrings.Status.TestFailed;
            btn.ForeColor = isConnected ? UITheme.StatusOk : UITheme.StatusBad;

            // Update line status label
            if (_lineStatusLabels.TryGetValue(extension, out var statusLabel))
            {
                string lineStatusText = isConnected ? UIStrings.Status.Connected : UIStrings.Status.Disconnected;
                statusLabel.Text = string.Format(UIStrings.Status.LineStatus, extension, lineStatusText);
                statusLabel.ForeColor = isConnected ? UITheme.StatusOk : UITheme.StatusBad;
            }

            await Task.Delay(1500);
            if (IsDisposed || !IsHandleCreated) return;

            btn.Text = UIStrings.Labels.Test;
            btn.ForeColor = UITheme.TextPrimary;
            btn.Enabled = true;
            if (progressLabel != null) HideProgressLabel(progressLabel);
        }

        private async void BtnReconnectSingleLine_Click(object sender, EventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;

            string extension = btn.Tag as string;
            if (string.IsNullOrEmpty(extension)) return;

            btn.Enabled = false;
            btn.Text = UIStrings.Status.TestPending;

            // Get the per-line progress label and show it
            Label progressLabel = null;
            _lineProgressLabels.TryGetValue(extension, out progressLabel);
            if (progressLabel != null)
            {
                progressLabel.Visible = true;
                progressLabel.Text = "";
            }

            bool success = await Task.Run(() =>
                _bridgeService.ReconnectTapiLine(extension,
                    msg => { if (progressLabel != null) UpdateProgressLabel(progressLabel, msg); }));

            await Task.Delay(1500);
            if (IsDisposed || !IsHandleCreated) return;

            UpdateTapiLineStatuses();
            btn.Text = UIStrings.Labels.ConnectShort;
            UpdateConnectorStatus();
            if (progressLabel != null) HideProgressLabel(progressLabel);
        }

        private void UpdateTapiLineStatuses()
        {
            if (IsDisposed || !IsHandleCreated) return;

            // Marshal to UI thread if needed
            if (InvokeRequired)
            {
                BeginInvoke(new System.Action(UpdateTapiLineStatuses));
                return;
            }

            var tapiLines = _bridgeService?.TapiLines;
            if (tapiLines == null) return;

            int connectedCount = tapiLines.Count(l => l.IsConnected);
            int totalCount = tapiLines.Count;

            if (totalCount > 1)
            {
                _lblTapiStatus.Text = string.Format(UIStrings.Status.LinesConnected, connectedCount, totalCount);
                _lblTapiStatus.ForeColor = connectedCount == totalCount ? UITheme.StatusOk :
                                          (connectedCount > 0 ? UITheme.StatusWarn : UITheme.StatusBad);
            }
            else
            {
                bool tapiOk = _bridgeService?.TapiConnected ?? false;
                string ext = _bridgeService?.Extension ?? "—";
                _lblTapiStatus.Text = tapiOk ? string.Format(UIStrings.Status.ConnectedExt, ext) : UIStrings.Status.Disconnected;
                _lblTapiStatus.ForeColor = tapiOk ? UITheme.StatusOk : UITheme.StatusBad;
            }

            // Update individual line statuses
            foreach (var line in tapiLines)
            {
                if (_lineStatusLabels.TryGetValue(line.Extension, out var lbl))
                {
                    string lineStatusText = line.IsConnected ? UIStrings.Status.Connected : UIStrings.Status.Disconnected;
                    lbl.Text = string.Format(UIStrings.Status.LineStatus, line.Extension, lineStatusText);
                    lbl.ForeColor = line.IsConnected ? UITheme.StatusOk : UITheme.StatusBad;
                }
                if (_lineReconnectButtons.TryGetValue(line.Extension, out var btn))
                {
                    btn.Enabled = !line.IsConnected;
                }
            }
        }

        private async void BtnReconnectAll_Click(object sender, EventArgs e)
        {
            _btnReconnectAll.Enabled = false;
            _btnTestDatev.Enabled = false;
            _btnReloadContacts.Enabled = false;
            if (_btnTestTapi != null) _btnTestTapi.Enabled = false;
            _btnReconnectTapi.Enabled = false;
            _btnReconnectAll.Text = UIStrings.Status.TestPending;

            // Show all progress labels
            _lblBridgeProgress.Visible = true;
            _lblBridgeProgress.Text = "";
            _lblDatevProgress.Visible = true;
            _lblDatevProgress.Text = "";
            if (_lblTapiProgress != null)
            {
                _lblTapiProgress.Visible = true;
                _lblTapiProgress.Text = "";
            }

            // Test DATEV first
            _lblBridgeProgress.Text = UIStrings.Messages.CheckingDatev;
            bool datevOk = await Task.Run(() =>
                DatevConnectionChecker.CheckAndLogDatevStatus(msg => UpdateProgressLabel(_lblDatevProgress, msg)));
            if (IsDisposed || !IsHandleCreated) return;

            _lblDatevStatus.Text = datevOk ? UIStrings.Status.Connected : UIStrings.Status.Unavailable;
            _lblDatevStatus.ForeColor = datevOk ? UITheme.StatusOk : UITheme.StatusBad;

            // Reload contacts if DATEV available
            if (datevOk)
            {
                _lblBridgeProgress.Text = UIStrings.Messages.LoadingContacts;
                await _bridgeService.ReloadContactsAsync(msg => UpdateProgressLabel(_lblDatevProgress, msg));
                if (IsDisposed || !IsHandleCreated) return;

                int contacts = _bridgeService?.ContactCount ?? 0;
                _lblContacts.Text = string.Format(UIStrings.Messages.ContactsFormat, contacts);
                UpdateSyncTime();
            }

            // Reconnect TAPI - StatusChanged event will update UI
            _lblBridgeProgress.Text = UIStrings.Messages.ConnectingTapi;
            await _bridgeService.ReconnectTapiAsync(msg => { if (_lblTapiProgress != null) UpdateProgressLabel(_lblTapiProgress, msg); });

            // Ensure button is restored after timeout if no status change
            await Task.Delay(6000);
            if (IsDisposed || !IsHandleCreated) return;

            if (_btnReconnectAll.Text == UIStrings.Status.TestPending)
            {
                bool tapiOk = _bridgeService?.TapiConnected ?? false;
                string ext = _bridgeService?.Extension ?? "\u2014";
                _lblTapiStatus.Text = tapiOk ? string.Format(UIStrings.Status.ConnectedExt, ext) : UIStrings.Status.Disconnected;
                _lblTapiStatus.ForeColor = tapiOk ? UITheme.StatusOk : UITheme.StatusBad;
                if (_btnTestTapi != null) _btnTestTapi.Enabled = true;
                _btnReconnectTapi.Enabled = !tapiOk;
                _btnReconnectAll.Text = UIStrings.Labels.Test;
                _btnReconnectAll.Enabled = true;
                _btnTestDatev.Enabled = true;
                _btnReloadContacts.Enabled = true;

                bool operational = datevOk && tapiOk;
                _lblBridgeProgress.Text = operational ? UIStrings.Status.AllSystemsReady : UIStrings.Status.PartiallyReady;
                UpdateConnectorStatus();

                // Hide progress labels after a short delay
                await Task.Delay(2000);
                if (IsDisposed || !IsHandleCreated) return;
                HideProgressLabel(_lblDatevProgress);
                if (_lblTapiProgress != null) HideProgressLabel(_lblTapiProgress);
                HideProgressLabel(_lblBridgeProgress);
            }
        }

        private void UpdateSyncTime()
        {
            if (IsDisposed || !IsHandleCreated) return;

            if (InvokeRequired)
            {
                BeginInvoke(new System.Action(UpdateSyncTime));
                return;
            }

            var syncTs = DatevContactManager.LastSyncTimestamp;
            _lblSyncTime.Text = syncTs.HasValue
                ? string.Format(UIStrings.Status.LastSyncFormat, syncTs.Value)
                : UIStrings.Status.LastSyncNone;
        }

        private void UpdateConnectorStatus()
        {
            if (IsDisposed || !IsHandleCreated) return;

            // Marshal to UI thread if needed
            if (InvokeRequired)
            {
                BeginInvoke(new System.Action(UpdateConnectorStatus));
                return;
            }

            bool datevOk = _bridgeService?.DatevAvailable ?? false;
            bool tapiOk = _bridgeService?.TapiConnected ?? false;
            bool operational = datevOk && tapiOk;
            bool partial = datevOk || tapiOk;

            string bridgeText = operational ? UIStrings.Status.Ready : (partial ? UIStrings.Status.Partial : UIStrings.Status.Unavailable);
            Color bridgeColor = operational ? UITheme.StatusOk : (partial ? UITheme.StatusWarn : UITheme.StatusBad);

            _lblConnectorStatus.Text = bridgeText;
            _lblConnectorStatus.ForeColor = bridgeColor;
        }
    }
}
