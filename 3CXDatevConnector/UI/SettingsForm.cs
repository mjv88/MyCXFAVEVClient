using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using DatevConnector.Core;
using DatevConnector.Core.Config;
using DatevConnector.Datev;
using DatevConnector.Datev.Managers;
using DatevConnector.UI.Strings;

namespace DatevConnector.UI
{
    /// <summary>
    /// Settings dialog - single-page dashboard layout
    /// </summary>
    public class SettingsForm : Form
    {
        /// <summary>
        /// Which action the user requested before closing.
        /// </summary>
        public enum Action { None, Status }

        /// <summary>
        /// The action requested by the user (check after form closes).
        /// </summary>
        public Action RequestedAction { get; private set; }

        // Status badges
        private Label _lblDatevBadge;
        private Label _lblTapiBadge;
        private Label _lblBridgeBadge;
        private Label _lblContactCount;
        private Label _lblExtension;
        private Button _btnTestDatev;
        private Button _btnReloadContacts;
        private Button _btnReconnectTapi;
        private Button _btnReconnectAll;

        // Popup settings
        private CheckBox _chkEnableCallerPopup;
        private CheckBox _chkEnableCallerPopupOutbound;
        private CheckBox _chkEnableJournalPopup;
        private CheckBox _chkEnableJournalPopupOutbound;
        private CheckBox _chkPopupFormular;
        private CheckBox _chkPopupBalloon;
        private NumericUpDown _numContactReshowDelay;

        // Advanced - Caller ID
        private NumericUpDown _numMinCallerIdLength;
        private NumericUpDown _numMaxCompareLength;

        // Advanced - Call History
        private CheckBox _chkHistoryInbound;
        private CheckBox _chkHistoryOutbound;
        private NumericUpDown _numHistoryCount;

        // Advanced - DATEV
        private CheckBox _chkActiveContactsOnly;
        private Label _lblLastSync;

        // Advanced - Tray
        private CheckBox _chkTrayDoubleClickCallHistory;

        // Telephony Mode
        private ComboBox _cboTelephonyMode;
        private Label _lblActiveMode;

        // Popup - Journaling
        private CheckBox _chkJournaling;

        // Buttons
        private Button _btnStatus;
        private Button _btnSave;
        private Button _btnCancel;

        private BridgeService _bridgeService;
        private bool _isLoadingSettings;

        public SettingsForm(BridgeService bridgeService = null)
        {
            _bridgeService = bridgeService;
            RequestedAction = Action.None;
            InitializeComponent();
            LoadSettings();
            RefreshOverviewStatus();
        }

        private void InitializeComponent()
        {
            Text = UIStrings.FormTitles.Overview;
            Size = new Size(540, 554);
            BackColor = UITheme.FormBackground;
            ForeColor = UITheme.TextPrimary;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = UITheme.FontBody;
            var appIcon = UITheme.GetFormIcon();
            if (appIcon != null) { Icon = appIcon; ShowIcon = true; }
            else ShowIcon = false;

            var root = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(UITheme.SpacingL)
            };

            // === TITLE ===
            var lblTitle = new Label
            {
                Text = UIStrings.FormTitles.Overview,
                Font = UITheme.FontTitle,
                ForeColor = UITheme.TextPrimary,
                AutoSize = true,
                Location = new Point(UITheme.SpacingL, UITheme.SpacingM)
            };
            root.Controls.Add(lblTitle);

            // === STATUS ROW (3 cards side by side) ===
            var statusRow = new TableLayoutPanel
            {
                Location = new Point(UITheme.SpacingL, 38),
                Size = new Size(492, 115),
                ColumnCount = 3,
                RowCount = 1
            };
            statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
            statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
            statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.4f));

            statusRow.Controls.Add(BuildDatevStatusCard(), 0, 0);
            statusRow.Controls.Add(BuildTapiStatusCard(), 1, 0);
            statusRow.Controls.Add(BuildBridgeStatusCard(), 2, 0);
            root.Controls.Add(statusRow);

            // === POP-UP BEHAVIOR ===
            var popupCard = BuildPopupCard();
            popupCard.Location = new Point(UITheme.SpacingL, 161);
            popupCard.Size = new Size(492, 124);
            root.Controls.Add(popupCard);

            // === ADVANCED ===
            var advancedCard = BuildAdvancedCard();
            advancedCard.Location = new Point(UITheme.SpacingL, 293);
            advancedCard.Size = new Size(492, 130);
            root.Controls.Add(advancedCard);

            // === TELEPHONY MODE ===
            var telephonyCard = BuildTelephonyModeCard();
            telephonyCard.Location = new Point(UITheme.SpacingL, 430);
            telephonyCard.Size = new Size(492, 50);
            root.Controls.Add(telephonyCard);

            // === BUTTONS ===
            var buttonPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 45,
                BackColor = UITheme.PanelBackground
            };

            int btnWidth = 90;
            int btnSpacing = 8;
            int formWidth = ClientSize.Width;
            int btnMargin = 16;

            // Status button (left side)
            _btnStatus = UITheme.CreateSecondaryButton(UIStrings.Labels.Status, btnWidth);
            _btnStatus.Location = new Point(btnMargin, 8);
            _btnStatus.Click += (s, e) =>
            {
                RequestedAction = Action.Status;
                Close();
            };

            // Dialog buttons (right side, properly aligned)
            _btnSave = UITheme.CreatePrimaryButton(UIStrings.Labels.Save, btnWidth);
            _btnSave.Location = new Point(formWidth - btnMargin - btnWidth, 8);
            _btnSave.Click += BtnSave_Click;

            _btnCancel = UITheme.CreateSecondaryButton(UIStrings.Labels.Cancel, btnWidth);
            _btnCancel.Location = new Point(formWidth - btnMargin - btnWidth - btnSpacing - btnWidth, 8);
            _btnCancel.DialogResult = DialogResult.Cancel;
            _btnCancel.Click += (s, e) => Close();

            buttonPanel.Controls.AddRange(new Control[] { _btnStatus, _btnCancel, _btnSave });

            Controls.Add(root);
            Controls.Add(buttonPanel);

            AcceptButton = _btnSave;
            CancelButton = _btnCancel;
        }

        // ========== STATUS CARDS ==========

        private Panel BuildDatevStatusCard()
        {
            var card = CreateStatusCard();
            var y = UITheme.SpacingS;
            const int btnWidth = 60;
            const int btnHeight = 24;

            var lbl = new Label { Text = UIStrings.Sections.Datev, Font = UITheme.FontLabel, ForeColor = UITheme.AccentDatev, AutoSize = true, Location = new Point(UITheme.SpacingM, y) };
            card.Controls.Add(lbl);
            y += 18;

            _lblDatevBadge = CreateBadge(UIStrings.Status.Unavailable, UITheme.StatusBad);
            _lblDatevBadge.Location = new Point(UITheme.SpacingM, y);
            card.Controls.Add(_lblDatevBadge);
            y += 22;

            _lblContactCount = new Label
            {
                Text = string.Format(UIStrings.SettingsLabels.Contacts, DatevCache.ContactCount),
                AutoSize = true, ForeColor = UITheme.TextMuted, Font = UITheme.FontSmall,
                Location = new Point(UITheme.SpacingM, y)
            };
            card.Controls.Add(_lblContactCount);
            y += 14;

            // Sync timestamp (under contacts)
            string syncText = DatevContactManager.LastSyncTimestamp.HasValue
                ? DatevContactManager.LastSyncTimestamp.Value.ToString("HH:mm")
                : "\u2014";
            _lblLastSync = new Label
            {
                Text = string.Format(UIStrings.SettingsLabels.Sync, syncText),
                AutoSize = true, ForeColor = UITheme.TextMuted, Font = UITheme.FontSmall,
                Location = new Point(UITheme.SpacingM, y)
            };
            card.Controls.Add(_lblLastSync);

            // Buttons side by side - uniform size
            _btnTestDatev = UITheme.CreateSecondaryButton(UIStrings.Labels.Test, btnWidth);
            _btnTestDatev.Size = new Size(btnWidth, btnHeight);
            _btnTestDatev.Location = new Point(UITheme.SpacingM, y + 16);
            _btnTestDatev.Click += BtnTestDatev_Click;
            card.Controls.Add(_btnTestDatev);

            _btnReloadContacts = UITheme.CreateSecondaryButton(UIStrings.Labels.Load, btnWidth);
            _btnReloadContacts.Size = new Size(btnWidth, btnHeight);
            _btnReloadContacts.Location = new Point(UITheme.SpacingM + btnWidth + 4, y + 16);
            _btnReloadContacts.Click += BtnReloadContacts_Click;
            card.Controls.Add(_btnReloadContacts);

            return card;
        }

        private Panel BuildTapiStatusCard()
        {
            var card = CreateStatusCard();
            var y = UITheme.SpacingS;
            const int btnWidth = 60;
            const int btnHeight = 24;

            var lbl = new Label { Text = UIStrings.Sections.Tapi, Font = UITheme.FontLabel, ForeColor = UITheme.AccentIncoming, AutoSize = true, Location = new Point(UITheme.SpacingM, y) };
            card.Controls.Add(lbl);
            y += 18;

            _lblTapiBadge = CreateBadge(UIStrings.Status.Disconnected, UITheme.StatusBad);
            _lblTapiBadge.Location = new Point(UITheme.SpacingM, y);
            card.Controls.Add(_lblTapiBadge);
            y += 22;

            _lblExtension = new Label
            {
                Text = string.Format(UIStrings.SettingsLabels.Extension, _bridgeService?.Extension ?? "\u2014"),
                AutoSize = true, ForeColor = UITheme.TextMuted, Font = UITheme.FontSmall,
                Location = new Point(UITheme.SpacingM, y)
            };
            card.Controls.Add(_lblExtension);
            y += 30;

            _btnReconnectTapi = UITheme.CreateSecondaryButton(UIStrings.Labels.Test, btnWidth);
            _btnReconnectTapi.Size = new Size(btnWidth, btnHeight);
            _btnReconnectTapi.Location = new Point(UITheme.SpacingM, y);
            _btnReconnectTapi.Click += BtnReconnectTapi_Click;
            card.Controls.Add(_btnReconnectTapi);

            return card;
        }

        private Panel BuildBridgeStatusCard()
        {
            var card = CreateStatusCard();
            var y = UITheme.SpacingS;
            const int btnWidth = 60;
            const int btnHeight = 24;

            var lbl = new Label { Text = UIStrings.Sections.Bridge, Font = UITheme.FontLabel, ForeColor = UITheme.AccentBridge, AutoSize = true, Location = new Point(UITheme.SpacingM, y) };
            card.Controls.Add(lbl);
            y += 18;

            _lblBridgeBadge = CreateBadge(UIStrings.Status.Unavailable, UITheme.StatusBad);
            _lblBridgeBadge.Location = new Point(UITheme.SpacingM, y);
            card.Controls.Add(_lblBridgeBadge);
            y += 52;

            _btnReconnectAll = UITheme.CreateSecondaryButton(UIStrings.Labels.Test, btnWidth);
            _btnReconnectAll.Size = new Size(btnWidth, btnHeight);
            _btnReconnectAll.Location = new Point(UITheme.SpacingM, y);
            _btnReconnectAll.Click += BtnReconnectAll_Click;
            card.Controls.Add(_btnReconnectAll);

            return card;
        }

        private Panel CreateStatusCard()
        {
            return new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = UITheme.CardBackground,
                Margin = new Padding(2),
                Padding = new Padding(UITheme.SpacingS)
            };
        }

        // ========== POP-UP BEHAVIOR CARD ==========

        private Panel BuildPopupCard()
        {
            var card = new Panel { BackColor = UITheme.CardBackground };
            card.Paint += CardBorder_Paint;

            // Column positions - evenly distributed across 492px width
            int col1 = UITheme.SpacingL;       // Left: Call-Pop-Up
            int col2 = 180;                     // Middle: Gesprächsnotiz
            int col3 = 340;                     // Right: Multiple Kontakte
            int row1 = 28;

            // Column headers
            card.Controls.Add(new Label { Text = UIStrings.Sections.CallPopUp, Font = UITheme.FontLabel, ForeColor = UITheme.TextPrimary, AutoSize = true, Location = new Point(col1, UITheme.SpacingS) });
            card.Controls.Add(new Label { Text = UIStrings.Sections.CallNote, Font = UITheme.FontLabel, ForeColor = UITheme.TextPrimary, AutoSize = true, Location = new Point(col2, UITheme.SpacingS) });
            card.Controls.Add(new Label { Text = UIStrings.Sections.MultipleContacts, Font = UITheme.FontLabel, ForeColor = UITheme.TextPrimary, AutoSize = true, Location = new Point(col3, UITheme.SpacingS) });

            // Left column: Call-Pop-Up
            _chkEnableCallerPopup = new CheckBox { Text = UIStrings.SettingsLabels.Incoming, Location = new Point(col1, row1), AutoSize = true };
            card.Controls.Add(_chkEnableCallerPopup);

            _chkEnableCallerPopupOutbound = new CheckBox { Text = UIStrings.SettingsLabels.Outgoing, Location = new Point(col1, row1 + 24), AutoSize = true };
            card.Controls.Add(_chkEnableCallerPopupOutbound);

            _chkPopupFormular = new CheckBox { Text = UIStrings.SettingsLabels.Window, Location = new Point(col1, row1 + 48), AutoSize = true };
            card.Controls.Add(_chkPopupFormular);

            _chkPopupBalloon = new CheckBox { Text = UIStrings.SettingsLabels.Notification, Location = new Point(col1, row1 + 72), AutoSize = true };
            _chkPopupBalloon.CheckedChanged += OnBalloonCheckChanged;
            card.Controls.Add(_chkPopupBalloon);

            // Middle column: Gesprächsnotiz
            _chkJournaling = new CheckBox { Text = UIStrings.SettingsLabels.Journaling, Location = new Point(col2, row1), AutoSize = true };
            card.Controls.Add(_chkJournaling);

            _chkEnableJournalPopup = new CheckBox { Text = UIStrings.SettingsLabels.Incoming, Location = new Point(col2, row1 + 24), AutoSize = true };
            card.Controls.Add(_chkEnableJournalPopup);

            _chkEnableJournalPopupOutbound = new CheckBox { Text = UIStrings.SettingsLabels.Outgoing, Location = new Point(col2, row1 + 48), AutoSize = true };
            card.Controls.Add(_chkEnableJournalPopupOutbound);

            // Right column: Multiple Kontakte
            card.Controls.Add(new Label { Text = UIStrings.SettingsLabels.Reselection, Location = new Point(col3, row1), AutoSize = true, ForeColor = UITheme.TextSecondary });
            _numContactReshowDelay = CreateCompactNumeric(col3 + 5, row1 + 20, 0, 30, 3);
            card.Controls.Add(_numContactReshowDelay);
            card.Controls.Add(new Label { Text = UIStrings.SettingsLabels.Seconds, Location = new Point(col3 + 67, row1 + 22), AutoSize = true, ForeColor = UITheme.TextMuted });

            card.Controls.Add(new Label { Text = UIStrings.SettingsLabels.Disabled, Location = new Point(col3, row1 + 46), AutoSize = true, ForeColor = UITheme.TextMuted, Font = UITheme.FontSmall });

            return card;
        }

        // ========== ADVANCED CARD ==========

        private Panel BuildAdvancedCard()
        {
            var card = new Panel { BackColor = UITheme.CardBackground };
            card.Paint += CardBorder_Paint;

            // Column positions - aligned with popup card
            int col1 = UITheme.SpacingL;       // Left: Anrufer-ID
            int col2 = 180;                     // Middle: Anrufliste
            int col3 = 340;                     // Right: DATEV
            int row1 = UITheme.SpacingS;

            // Left column: Anrufer-ID
            card.Controls.Add(new Label { Text = UIStrings.Sections.CallerId, Font = UITheme.FontLabel, ForeColor = UITheme.TextPrimary, AutoSize = true, Location = new Point(col1, row1) });

            card.Controls.Add(new Label { Text = UIStrings.SettingsLabels.MinLength, Location = new Point(col1, row1 + 24), AutoSize = true, ForeColor = UITheme.TextSecondary });
            _numMinCallerIdLength = CreateCompactNumeric(col1 + 85, row1 + 22, 1, 20, 2);
            card.Controls.Add(_numMinCallerIdLength);

            card.Controls.Add(new Label { Text = UIStrings.SettingsLabels.MaxCompare, Location = new Point(col1, row1 + 50), AutoSize = true, ForeColor = UITheme.TextSecondary });
            _numMaxCompareLength = CreateCompactNumeric(col1 + 85, row1 + 48, 3, 20, 10);
            card.Controls.Add(_numMaxCompareLength);

            // Middle column: Anrufliste
            card.Controls.Add(new Label { Text = UIStrings.Sections.CallHistorySection, Font = UITheme.FontLabel, ForeColor = UITheme.TextPrimary, AutoSize = true, Location = new Point(col2, row1) });

            _chkHistoryInbound = new CheckBox { Text = UIStrings.SettingsLabels.InboundCalls, Location = new Point(col2, row1 + 24), AutoSize = true };
            card.Controls.Add(_chkHistoryInbound);

            _chkHistoryOutbound = new CheckBox { Text = UIStrings.SettingsLabels.OutboundCalls, Location = new Point(col2, row1 + 48), AutoSize = true };
            card.Controls.Add(_chkHistoryOutbound);

            card.Controls.Add(new Label { Text = UIStrings.SettingsLabels.Count, Location = new Point(col2, row1 + 76), AutoSize = true, ForeColor = UITheme.TextSecondary });
            _numHistoryCount = CreateCompactNumeric(col2 + 50, row1 + 74, 1, 20, 5);
            card.Controls.Add(_numHistoryCount);

            // Right column: DATEV
            card.Controls.Add(new Label { Text = UIStrings.Sections.Datev, Font = UITheme.FontLabel, ForeColor = UITheme.TextPrimary, AutoSize = true, Location = new Point(col3, row1) });

            _chkActiveContactsOnly = new CheckBox { Text = UIStrings.SettingsLabels.ActiveContacts, Location = new Point(col3, row1 + 24), AutoSize = true };
            card.Controls.Add(_chkActiveContactsOnly);

            _chkTrayDoubleClickCallHistory = new CheckBox { Text = UIStrings.SettingsLabels.TrayDoubleClickHistory, Location = new Point(col3, row1 + 48), AutoSize = true };
            card.Controls.Add(_chkTrayDoubleClickCallHistory);

            return card;
        }

        // ========== TELEPHONY MODE CARD ==========

        private Panel BuildTelephonyModeCard()
        {
            var card = new Panel { BackColor = UITheme.CardBackground };
            card.Paint += CardBorder_Paint;

            int col1 = UITheme.SpacingL;
            int col2 = 180;
            int row1 = UITheme.SpacingS;

            // Title
            card.Controls.Add(new Label
            {
                Text = UIStrings.Sections.TelephonyMode,
                Font = UITheme.FontLabel,
                ForeColor = UITheme.TextPrimary,
                AutoSize = true,
                Location = new Point(col1, row1)
            });

            // Mode dropdown
            card.Controls.Add(new Label
            {
                Text = UIStrings.SettingsLabels.TelephonyMode,
                AutoSize = true,
                ForeColor = UITheme.TextSecondary,
                Location = new Point(col2, row1 + 2)
            });

            _cboTelephonyMode = new ComboBox
            {
                Location = new Point(col2 + 100, row1 - 2),
                Size = new Size(130, 22),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = UITheme.InputBackground,
                ForeColor = UITheme.TextPrimary,
                Font = UITheme.FontSmall
            };
            _cboTelephonyMode.Items.AddRange(new object[]
            {
                UIStrings.SettingsLabels.TelephonyModeAuto,
                UIStrings.SettingsLabels.TelephonyModeTapi,
                UIStrings.SettingsLabels.TelephonyModePipe,
                UIStrings.SettingsLabels.TelephonyModeWebclient
            });
            card.Controls.Add(_cboTelephonyMode);

            // Active mode indicator
            string activeModeName = _bridgeService != null
                ? TelephonyProviderSelector.GetModeDescription(_bridgeService.SelectedTelephonyMode)
                : "-";
            _lblActiveMode = new Label
            {
                Text = string.Format(UIStrings.SettingsLabels.ActiveMode, activeModeName),
                AutoSize = true,
                ForeColor = UITheme.TextMuted,
                Font = UITheme.FontSmall,
                Location = new Point(col1, row1 + 24)
            };
            card.Controls.Add(_lblActiveMode);

            return card;
        }

        // ========== HELPERS ==========

        private Label CreateBadge(string text, Color backColor)
        {
            return new Label
            {
                Text = string.Format("  {0}  ", text),
                AutoSize = true,
                BackColor = backColor,
                ForeColor = Color.White,
                Font = UITheme.FontSmall,
                Padding = new Padding(4, 2, 4, 2)
            };
        }

        private void SetBadge(Label badge, string text, Color badgeColor)
        {
            badge.Text = string.Format("  {0}  ", text);
            badge.BackColor = badgeColor;
        }

        private NumericUpDown CreateCompactNumeric(int x, int y, int min, int max, int defaultValue)
        {
            return new NumericUpDown
            {
                Location = new Point(x, y),
                Size = new Size(58, 20),
                Minimum = min,
                Maximum = max,
                Value = defaultValue,
                BackColor = UITheme.InputBackground,
                ForeColor = UITheme.TextPrimary,
                Font = UITheme.FontSmall
            };
        }

        private void CardBorder_Paint(object sender, PaintEventArgs e)
        {
            var p = (Panel)sender;
            using (var pen = new Pen(UITheme.CardBorder))
            {
                var r = p.ClientRectangle;
                r.Width -= 1; r.Height -= 1;
                e.Graphics.DrawRectangle(pen, r);
            }
        }

        // ========== STATUS REFRESH ==========

        private void RefreshOverviewStatus()
        {
            if (_bridgeService == null) return;

            bool datevOk = _bridgeService.DatevAvailable;
            SetBadge(_lblDatevBadge, datevOk ? UIStrings.Status.Available : UIStrings.Status.Unavailable,
                datevOk ? UITheme.StatusOk : UITheme.StatusBad);

            bool tapiOk = _bridgeService.TapiConnected;
            SetBadge(_lblTapiBadge, tapiOk ? UIStrings.Status.Connected : UIStrings.Status.Disconnected,
                tapiOk ? UITheme.StatusOk : UITheme.StatusBad);

            if (datevOk && tapiOk)
                SetBadge(_lblBridgeBadge, UIStrings.Status.Ready, UITheme.StatusOk);
            else if (datevOk || tapiOk)
                SetBadge(_lblBridgeBadge, UIStrings.Status.Partial, UITheme.StatusWarn);
            else
                SetBadge(_lblBridgeBadge, UIStrings.Status.Unavailable, UITheme.StatusBad);

            _lblContactCount.Text = string.Format(UIStrings.SettingsLabels.Contacts, DatevCache.ContactCount);
            _lblExtension.Text = string.Format(UIStrings.SettingsLabels.Extension, _bridgeService.Extension ?? "\u2014");
        }

        private void BtnTestDatev_Click(object sender, EventArgs e)
        {
            SetBadge(_lblDatevBadge, UIStrings.Status.Checking, UITheme.StatusWarn);
            _btnTestDatev.Enabled = false;

            Task.Run(() =>
            {
                bool connected = DatevConnectionChecker.CheckAndLogDatevStatus();
                BeginInvoke(new System.Action(() =>
                {
                    SetBadge(_lblDatevBadge, connected ? UIStrings.Status.Available : UIStrings.Status.Unavailable,
                        connected ? UITheme.StatusOk : UITheme.StatusBad);
                    _btnTestDatev.Enabled = true;
                    _lblContactCount.Text = string.Format(UIStrings.SettingsLabels.Contacts, DatevCache.ContactCount);
                }));
            });
        }

        private async void BtnReloadContacts_Click(object sender, EventArgs e)
        {
            if (_bridgeService == null) return;

            _btnReloadContacts.Enabled = false;
            _btnReloadContacts.Text = UIStrings.Status.TestPending;

            try
            {
                await _bridgeService.ReloadContactsAsync();
                _lblContactCount.Text = string.Format(UIStrings.SettingsLabels.Contacts, DatevCache.ContactCount);

                // Update sync timestamp
                string syncText = DatevContactManager.LastSyncTimestamp.HasValue
                    ? DatevContactManager.LastSyncTimestamp.Value.ToString("HH:mm")
                    : "\u2014";
                _lblLastSync.Text = string.Format(UIStrings.SettingsLabels.Sync, syncText);
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(UIStrings.Errors.LoadFailed, ex.Message), UIStrings.Errors.GenericError,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _btnReloadContacts.Text = UIStrings.Labels.Load;
                _btnReloadContacts.Enabled = true;
            }
        }

        private async void BtnReconnectTapi_Click(object sender, EventArgs e)
        {
            if (_bridgeService == null) return;

            _btnReconnectTapi.Enabled = false;
            _btnReconnectTapi.Text = UIStrings.Status.TestPending;
            SetBadge(_lblTapiBadge, UIStrings.Status.Connecting, UITheme.StatusWarn);

            try
            {
                await _bridgeService.ReconnectTapiAsync();
                await Task.Delay(2000); // Give time for connection to establish

                bool tapiOk = _bridgeService.TapiConnected;
                SetBadge(_lblTapiBadge, tapiOk ? UIStrings.Status.Connected : UIStrings.Status.Disconnected,
                    tapiOk ? UITheme.StatusOk : UITheme.StatusBad);
                _lblExtension.Text = string.Format(UIStrings.SettingsLabels.Extension, _bridgeService.Extension ?? "\u2014");
                RefreshOverviewStatus();
            }
            finally
            {
                _btnReconnectTapi.Text = UIStrings.Labels.Connect;
                _btnReconnectTapi.Enabled = true;
            }
        }

        private async void BtnReconnectAll_Click(object sender, EventArgs e)
        {
            if (_bridgeService == null) return;

            _btnReconnectAll.Enabled = false;
            _btnReconnectAll.Text = UIStrings.Status.TestPending;

            // Test DATEV
            SetBadge(_lblDatevBadge, UIStrings.Status.Checking, UITheme.StatusWarn);
            bool datevOk = await Task.Run(() => DatevConnectionChecker.CheckAndLogDatevStatus());
            SetBadge(_lblDatevBadge, datevOk ? UIStrings.Status.Available : UIStrings.Status.Unavailable,
                datevOk ? UITheme.StatusOk : UITheme.StatusBad);

            // Reload contacts if DATEV available
            if (datevOk)
            {
                await _bridgeService.ReloadContactsAsync();
                _lblContactCount.Text = string.Format(UIStrings.SettingsLabels.Contacts, DatevCache.ContactCount);
                string syncText = DatevContactManager.LastSyncTimestamp.HasValue
                    ? DatevContactManager.LastSyncTimestamp.Value.ToString("HH:mm")
                    : "\u2014";
                _lblLastSync.Text = string.Format(UIStrings.SettingsLabels.Sync, syncText);
            }

            // Reconnect TAPI
            SetBadge(_lblTapiBadge, UIStrings.Status.Connecting, UITheme.StatusWarn);
            await _bridgeService.ReconnectTapiAsync();
            await Task.Delay(2000);

            bool tapiOk = _bridgeService.TapiConnected;
            SetBadge(_lblTapiBadge, tapiOk ? UIStrings.Status.Connected : UIStrings.Status.Disconnected,
                tapiOk ? UITheme.StatusOk : UITheme.StatusBad);
            _lblExtension.Text = string.Format(UIStrings.SettingsLabels.Extension, _bridgeService.Extension ?? "\u2014");

            RefreshOverviewStatus();
            _btnReconnectAll.Text = UIStrings.Labels.ReconnectAll;
            _btnReconnectAll.Enabled = true;
        }

        // ========== LOAD / SAVE ==========

        private void LoadSettings()
        {
            _isLoadingSettings = true;

            // Core
            _chkJournaling.Checked = AppConfig.GetBool(ConfigKeys.EnableJournaling, true);

            // Caller ID
            _numMinCallerIdLength.Value = AppConfig.GetInt(ConfigKeys.MinCallerIdLength, 2);
            _numMaxCompareLength.Value = AppConfig.GetInt(ConfigKeys.MaxCompareLength, 10);

            // Popup
            _chkEnableCallerPopup.Checked = AppConfig.GetBool(ConfigKeys.EnableCallerPopup, true);
            _chkEnableCallerPopupOutbound.Checked = AppConfig.GetBool(ConfigKeys.EnableCallerPopupOutbound, false);
            string popupMode = AppConfig.GetString(ConfigKeys.CallerPopupMode, "Form");
            // Map config values to checkboxes
            switch (popupMode.ToLower())
            {
                case "form":
                    _chkPopupFormular.Checked = true;
                    _chkPopupBalloon.Checked = false;
                    break;
                case "balloon":
                    _chkPopupFormular.Checked = false;
                    _chkPopupBalloon.Checked = true;
                    break;
                default: // Both
                    _chkPopupFormular.Checked = true;
                    _chkPopupBalloon.Checked = true;
                    break;
            }
            _chkEnableJournalPopup.Checked = AppConfig.GetBool(ConfigKeys.EnableJournalPopup, true);
            _chkEnableJournalPopupOutbound.Checked = AppConfig.GetBool(ConfigKeys.EnableJournalPopupOutbound, false);
            _numContactReshowDelay.Value = AppConfig.GetInt(ConfigKeys.ContactReshowDelaySeconds, 3);

            // Call History
            _chkHistoryInbound.Checked = AppConfig.GetBool(ConfigKeys.CallHistoryInbound, true);
            _chkHistoryOutbound.Checked = AppConfig.GetBool(ConfigKeys.CallHistoryOutbound, false);
            _numHistoryCount.Value = AppConfig.GetInt(ConfigKeys.CallHistoryMaxEntries, 5);

            // DATEV Filter - default to false (all contacts)
            _chkActiveContactsOnly.Checked = AppConfig.GetBool(ConfigKeys.ActiveContactsOnly, false);
            DatevContactManager.FilterActiveContactsOnly = _chkActiveContactsOnly.Checked;

            // Tray double-click - default to Call History
            _chkTrayDoubleClickCallHistory.Checked = AppConfig.GetBool("TrayDoubleClickCallHistory", true);

            // Telephony Mode
            var telephonyMode = AppConfig.GetEnum(ConfigKeys.TelephonyMode, TelephonyMode.Auto);
            switch (telephonyMode)
            {
                case TelephonyMode.Tapi: _cboTelephonyMode.SelectedIndex = 1; break;
                case TelephonyMode.Pipe: _cboTelephonyMode.SelectedIndex = 2; break;
                case TelephonyMode.Webclient: _cboTelephonyMode.SelectedIndex = 3; break;
                default: _cboTelephonyMode.SelectedIndex = 0; break;
            }

            _isLoadingSettings = false;
        }

        private int ParseInt(string value, int defaultValue)
        {
            return int.TryParse(value, out int result) ? result : defaultValue;
        }

        private async void BtnSave_Click(object sender, EventArgs e)
        {
            try
            {
                SaveSettings();

                // Show saved feedback without closing the form
                await ShowSavedFeedback();
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(UIStrings.Errors.SaveFailed, ex.Message), UIStrings.Errors.GenericError,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task ShowSavedFeedback()
        {
            var originalText = _btnSave.Text;
            var originalBackColor = _btnSave.BackColor;

            _btnSave.Text = UIStrings.Messages.SavedWithCheck;
            _btnSave.BackColor = UITheme.StatusOk;
            _btnSave.Enabled = false;

            await Task.Delay(1500);

            if (!_btnSave.IsDisposed)
            {
                _btnSave.Text = originalText;
                _btnSave.BackColor = originalBackColor;
                _btnSave.Enabled = true;
            }
        }

        private void SaveSettings()
        {
            // Detect if ActiveContactsOnly changed (for triggering reload)
            bool previousActiveOnly = AppConfig.GetBool(ConfigKeys.ActiveContactsOnly, false);

            AppConfig.SetBool(ConfigKeys.EnableJournaling, _chkJournaling.Checked);

            AppConfig.SetInt(ConfigKeys.MinCallerIdLength, (int)_numMinCallerIdLength.Value);
            AppConfig.SetInt(ConfigKeys.MaxCompareLength, (int)_numMaxCompareLength.Value);

            AppConfig.SetBool(ConfigKeys.EnableCallerPopup, _chkEnableCallerPopup.Checked);
            AppConfig.SetBool(ConfigKeys.EnableCallerPopupOutbound, _chkEnableCallerPopupOutbound.Checked);
            // Map checkbox states to config value
            string modeValue;
            if (_chkPopupFormular.Checked && _chkPopupBalloon.Checked)
                modeValue = "Both";
            else if (_chkPopupFormular.Checked)
                modeValue = "Form";
            else if (_chkPopupBalloon.Checked)
                modeValue = "Balloon";
            else
                modeValue = "Both"; // Default if neither checked
            AppConfig.Set(ConfigKeys.CallerPopupMode, modeValue);
            AppConfig.SetBool(ConfigKeys.EnableJournalPopup, _chkEnableJournalPopup.Checked);
            AppConfig.SetBool(ConfigKeys.EnableJournalPopupOutbound, _chkEnableJournalPopupOutbound.Checked);
            AppConfig.SetInt(ConfigKeys.ContactReshowDelaySeconds, (int)_numContactReshowDelay.Value);

            AppConfig.SetBool(ConfigKeys.CallHistoryInbound, _chkHistoryInbound.Checked);
            AppConfig.SetBool(ConfigKeys.CallHistoryOutbound, _chkHistoryOutbound.Checked);
            AppConfig.SetInt(ConfigKeys.CallHistoryMaxEntries, (int)_numHistoryCount.Value);

            // DATEV Filter
            AppConfig.SetBool(ConfigKeys.ActiveContactsOnly, _chkActiveContactsOnly.Checked);
            DatevContactManager.FilterActiveContactsOnly = _chkActiveContactsOnly.Checked;

            // Tray double-click behavior
            AppConfig.SetBool("TrayDoubleClickCallHistory", _chkTrayDoubleClickCallHistory.Checked);

            // Telephony Mode (requires restart to take effect)
            string[] modeValues = { "Auto", "Tapi", "Pipe", "Webclient" };
            int modeIndex = _cboTelephonyMode.SelectedIndex;
            if (modeIndex >= 0 && modeIndex < modeValues.Length)
            {
                AppConfig.Set(ConfigKeys.TelephonyMode, modeValues[modeIndex]);
            }

            // Apply settings live to running service
            _bridgeService?.ApplySettings();

            // Trigger contact reload if ActiveContactsOnly changed
            bool newActiveOnly = _chkActiveContactsOnly.Checked;
            if (newActiveOnly != previousActiveOnly && _bridgeService != null)
            {
                LogManager.Log("Settings: ActiveContactsOnly changed ({0} -> {1}), triggering contact reload",
                    previousActiveOnly, newActiveOnly);
                _ = _bridgeService.ReloadContactsAsync(null);
            }

            LogManager.Log("Settings: Einstellungen gespeichert und angewendet");
        }

        private void OnBalloonCheckChanged(object sender, EventArgs e)
        {
            // Don't show prompt during initial settings load
            if (_isLoadingSettings)
                return;

            if (_chkPopupBalloon.Checked)
            {
                // Prompt user to verify Windows notifications are enabled
                var result = MessageBox.Show(
                    UIStrings.Messages.BalloonNotificationHint,
                    UIStrings.Messages.BalloonNotificationTitle,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);

                if (result == DialogResult.Yes)
                {
                    OpenWindowsNotificationSettings();
                }
            }
        }

        private void OpenWindowsNotificationSettings()
        {
            try
            {
                // Opens Windows 10/11 notification settings directly
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ms-settings:notifications",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LogManager.Log("Settings: Failed to open Windows notification settings - {0}", ex.Message);
                MessageBox.Show(
                    UIStrings.Messages.WindowsSettingsOpenFailed,
                    UIStrings.Errors.GenericError,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

    }
}
