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
        public enum Action { None, CallHistory, Settings }

        private readonly ConnectorService _bridgeService;
        private Label _lblDatevStatus;
        private Label _lblContacts;
        private Label _lblSyncTime;
        private Label _lblDatevProgress;
        private Label _lblConnectorStatus;
        private Label _lblBridgeProgress;
        private Button _btnTestDatev;
        private Button _btnReloadContacts;
        private Button _btnReconnectAll;

        // TAPI section panels (one or the other is set)
        private SingleLineStatusPanel _singleLinePanel;
        private MultiLineStatusPanel _multiLinePanel;

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
                _bridgeService.ModeChanged += OnModeChanged;
            }

            Disposed += (s, e) =>
            {
                if (_bridgeService != null)
                {
                    _bridgeService.StatusChanged -= OnConnectorStatusChanged;
                    _bridgeService.ModeChanged -= OnModeChanged;
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
            SafeInvoke(() =>
            {
                bool tapiOk = _bridgeService?.TapiConnected ?? false;

                // Delegate TAPI section update to the active panel
                if (_singleLinePanel != null)
                {
                    string ext = _bridgeService?.Extension;
                    string modeName = ConnectionMethodSelector.GetModeShortName(_bridgeService.SelectedConnectionMode);
                    _singleLinePanel.HandleStatusChanged(tapiOk, ext, modeName);
                }
                else if (_multiLinePanel != null)
                {
                    string modeName = ConnectionMethodSelector.GetModeShortName(_bridgeService.SelectedConnectionMode);
                    _multiLinePanel.HandleStatusChanged(modeName);
                }

                // Reset bridge test button state if reconnecting
                if (_btnReconnectAll.Text == UIStrings.Status.TestPending)
                {
                    _btnReconnectAll.Text = UIStrings.Labels.Test;
                    _btnReconnectAll.Enabled = true;
                }

                UpdateConnectorStatus();
            });
        }

        private void OnModeChanged(ConnectionMode mode)
        {
            SafeInvoke(() =>
            {
                string modeName = ConnectionMethodSelector.GetModeShortName(mode);
                _singleLinePanel?.UpdateMode(modeName);
                _multiLinePanel?.UpdateMode(modeName);
            });
        }

        private void InitializeComponent()
        {
            // ThemedForm base class handles: BackColor, ForeColor, FormBorderStyle,
            // StartPosition, MaximizeBox, MinimizeBox, Font, Icon
            Text = UIStrings.FormTitles.Overview;
            TopMost = false;

            int y = LayoutConstants.SpaceMD;
            int cardWidth = 388;
            int cardHeight = 118;
            int btnWidth = 75;
            int btnSpacing = 8;

            // Calculate extra height for multi-line TAPI display
            var tapiLines = _bridgeService?.TapiLines ?? new List<TapiLineInfo>();
            int lineCount = tapiLines.Count;
            int extraTapiHeight = lineCount > 1 ? (88 + lineCount * 24 - cardHeight) : 0;
            ClientSize = new Size(420, 454 + extraTapiHeight);

            // ==================== DATEV Section ====================
            var datevCard = CreateSectionCard(UIStrings.Sections.Datev, UITheme.AccentDatev, y, cardWidth, cardHeight);
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
            var syncTs = DatevContactRepository.LastSyncTimestamp;
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

            // Buttons aligned to the bottom-right, Test rightmost
            _btnReloadContacts = UITheme.CreateSecondaryButton(UIStrings.Labels.Load, btnWidth);
            _btnReloadContacts.Location = new Point(cardWidth - 12 - btnWidth - btnSpacing - btnWidth, 74);
            _btnReloadContacts.Click += BtnReloadContacts_Click;
            datevCard.Controls.Add(_btnReloadContacts);

            _btnTestDatev = UITheme.CreateSecondaryButton(UIStrings.Labels.Test, btnWidth);
            _btnTestDatev.Location = new Point(cardWidth - 12 - btnWidth, 74);
            _btnTestDatev.Click += BtnTestDatev_Click;
            datevCard.Controls.Add(_btnTestDatev);

            y += cardHeight + LayoutConstants.CardPadding;

            // ==================== 3CX Section (delegated to panels) ====================
            int tapiCardHeight = lineCount > 1 ? 88 + (lineCount * 24) : cardHeight;

            var tapiCard = CreateSectionCard(UIStrings.Sections.Tapi, UITheme.AccentIncoming, y, cardWidth, tapiCardHeight);
            Controls.Add(tapiCard);

            if (lineCount <= 1)
            {
                _singleLinePanel = new SingleLineStatusPanel(
                    _bridgeService, tapiCard, cardWidth,
                    () => IsAlive, () => UpdateConnectorStatus());
            }
            else
            {
                _multiLinePanel = new MultiLineStatusPanel(
                    _bridgeService, tapiCard, cardWidth, tapiLines,
                    () => IsAlive, () => UpdateConnectorStatus());
            }

            y += tapiCardHeight + LayoutConstants.CardPadding;

            // ==================== Bridge Section ====================
            bool tapiOk = _bridgeService?.TapiConnected ?? false;
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

            // Button aligned to the bottom-right
            _btnReconnectAll = UITheme.CreateSecondaryButton(UIStrings.Labels.Test, btnWidth);
            _btnReconnectAll.Location = new Point(cardWidth - 12 - btnWidth, 74);
            _btnReconnectAll.Click += BtnReconnectAll_Click;
            bridgeCard.Controls.Add(_btnReconnectAll);

            y += cardHeight + LayoutConstants.CardPadding;

            // ==================== Quick Action Buttons ====================
            int actionBtnWidth = 180;
            int actionBtnSpacing = LayoutConstants.CardPadding;
            int totalActionWidth = (actionBtnWidth * 2) + actionBtnSpacing;
            int actionBtnX = LayoutConstants.SpaceMD + (cardWidth - totalActionWidth) / 2;

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
            var card = new RoundedPanel(accentColor)
            {
                Location = new Point(LayoutConstants.SpaceMD, yPos),
                Size = new Size(width, height)
            };

            var lblTitle = new Label
            {
                Text = title,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = accentColor,
                Location = new Point(12, 8),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            card.Controls.Add(lblTitle);

            return card;
        }

        private void UpdateProgressLabel(Label label, string text)
        {
            SafeInvoke(() =>
            {
                label.Text = text;
                if (!string.IsNullOrEmpty(text) && !label.Visible)
                    label.Visible = true;
            });
        }

        private void HideProgressLabel(Label label)
        {
            SafeInvoke(() =>
            {
                label.Text = "";
                label.Visible = false;
            });
        }

        private async void BtnTestDatev_Click(object sender, EventArgs e)
        {
            bool available = await AsyncButtonAction.RunTestAsync(
                _btnTestDatev, _lblDatevProgress,
                async progress => await Task.Run(() =>
                    DatevConnectionChecker.CheckAndLogDatevStatus(progress)),
                UIStrings.Labels.Test,
                _btnReloadContacts);

            if (!IsAlive) return;
            _lblDatevStatus.Text = available ? UIStrings.Status.Connected : UIStrings.Status.Unavailable;
            _lblDatevStatus.ForeColor = available ? UITheme.StatusOk : UITheme.StatusBad;

            // On success: update service state, reload contacts, and update UI
            if (available && _bridgeService != null)
            {
                _bridgeService.SetDatevAvailable(true);
                _btnTestDatev.Enabled = false;
                _btnReloadContacts.Enabled = false;
                _lblDatevProgress.Visible = true;

                await _bridgeService.ReloadContactsAsync(msg => UpdateProgressLabel(_lblDatevProgress, msg));

                if (!IsAlive) return;
                int contacts = _bridgeService.ContactCount;
                _lblContacts.Text = string.Format(UIStrings.Messages.ContactsFormat, contacts);
                UpdateSyncTime();

                _btnTestDatev.Enabled = true;
                _btnReloadContacts.Enabled = true;
                HideProgressLabel(_lblDatevProgress);
            }

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

            if (!IsAlive) return;

            int contacts = _bridgeService?.ContactCount ?? 0;
            _lblContacts.Text = string.Format(UIStrings.Messages.ContactsFormat, contacts);
            UpdateSyncTime();
            _btnReloadContacts.Text = UIStrings.Labels.Load;
            _btnReloadContacts.Enabled = true;
            _btnTestDatev.Enabled = true;
            HideProgressLabel(_lblDatevProgress);
        }

        private async void BtnReconnectAll_Click(object sender, EventArgs e)
        {
            _btnReconnectAll.Enabled = false;
            _btnTestDatev.Enabled = false;
            _btnReloadContacts.Enabled = false;
            _singleLinePanel?.DisableButtons();
            _multiLinePanel?.DisableButtons();
            _btnReconnectAll.Text = UIStrings.Status.TestPending;

            // Show all progress labels
            _lblBridgeProgress.Visible = true;
            _lblBridgeProgress.Text = "";
            _lblDatevProgress.Visible = true;
            _lblDatevProgress.Text = "";
            _singleLinePanel?.ShowProgress("");

            // Test DATEV first
            _lblBridgeProgress.Text = UIStrings.Messages.CheckingDatev;
            bool datevOk = await Task.Run(() =>
                DatevConnectionChecker.CheckAndLogDatevStatus(msg => UpdateProgressLabel(_lblDatevProgress, msg)));
            if (!IsAlive) return;

            _lblDatevStatus.Text = datevOk ? UIStrings.Status.Connected : UIStrings.Status.Unavailable;
            _lblDatevStatus.ForeColor = datevOk ? UITheme.StatusOk : UITheme.StatusBad;

            // Reload contacts if DATEV available
            if (datevOk)
            {
                _lblBridgeProgress.Text = UIStrings.Messages.LoadingContacts;
                await _bridgeService.ReloadContactsAsync(msg => UpdateProgressLabel(_lblDatevProgress, msg));
                if (!IsAlive) return;

                int contacts = _bridgeService?.ContactCount ?? 0;
                _lblContacts.Text = string.Format(UIStrings.Messages.ContactsFormat, contacts);
                UpdateSyncTime();
            }

            // Reconnect TAPI - StatusChanged event will update UI
            _lblBridgeProgress.Text = UIStrings.Messages.ConnectingTapi;
            await _bridgeService.ReconnectTapiAsync(msg => _singleLinePanel?.UpdateProgressSafe(msg));

            // Ensure button is restored after timeout if no status change
            await Task.Delay(6000);
            if (!IsAlive) return;

            if (_btnReconnectAll.Text == UIStrings.Status.TestPending)
            {
                bool tapiOk = _bridgeService?.TapiConnected ?? false;
                string ext = _bridgeService?.Extension;

                if (_singleLinePanel != null)
                    _singleLinePanel.OnTestAllComplete(tapiOk, ext);
                else
                    _multiLinePanel?.OnTestAllComplete();

                _btnReconnectAll.Text = UIStrings.Labels.Test;
                _btnReconnectAll.Enabled = true;
                _btnTestDatev.Enabled = true;
                _btnReloadContacts.Enabled = true;

                bool operational = datevOk && tapiOk;
                _lblBridgeProgress.Text = operational ? UIStrings.Status.AllSystemsReady : UIStrings.Status.PartiallyReady;
                UpdateConnectorStatus();

                // Hide progress labels after a short delay
                await Task.Delay(2000);
                if (!IsAlive) return;
                HideProgressLabel(_lblDatevProgress);
                _singleLinePanel?.HideProgress();
                HideProgressLabel(_lblBridgeProgress);
            }
        }

        private void UpdateSyncTime()
        {
            SafeInvoke(() =>
            {
                var syncTs = DatevContactRepository.LastSyncTimestamp;
                _lblSyncTime.Text = syncTs.HasValue
                    ? string.Format(UIStrings.Status.LastSyncFormat, syncTs.Value)
                    : UIStrings.Status.LastSyncNone;
            });
        }

        private void UpdateConnectorStatus()
        {
            SafeInvoke(UpdateConnectorStatusCore);
        }

        private void UpdateConnectorStatusCore()
        {
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
