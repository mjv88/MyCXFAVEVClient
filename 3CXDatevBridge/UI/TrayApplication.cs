using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DatevBridge.Core;
using DatevBridge.Core.Config;
using DatevBridge.Datev;
using DatevBridge.Datev.Managers;
using DatevBridge.UI.Strings;
using DatevBridge.UI.Theme;

namespace DatevBridge.UI
{
    /// <summary>
    /// System tray application - hidden form with NotifyIcon
    /// </summary>
    public class TrayApplication : Form
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly ContextMenuStrip _contextMenu;
        private readonly BridgeService _bridgeService;
        private readonly CancellationTokenSource _cts;
        private readonly Icon _iconConnected;
        private readonly Icon _iconDisconnected;
        private readonly Icon _iconConnecting;

        // Track last notified status to avoid duplicate balloons
        private BridgeStatus _lastNotifiedStatus = BridgeStatus.Disconnected;

        // Mute status balloons (separate from caller popup muting)
        private readonly bool _muteStatusBalloons;

        // Single window management - only one main window at a time
        private Form _currentMainForm;
        private Point? _lastFormLocation;

        public TrayApplication(string extension)
        {
            // Initialize UI context for popup forms (must be done on UI thread)
            CallerPopupForm.InitializeUIContext();
            ContactSelectionForm.InitializeUIContext();
            JournalForm.InitializeUIContext();

            // Create tray icons with status ring colors
            _iconConnected = UITheme.CreateTrayIcon(UITheme.StatusOk);
            _iconDisconnected = UITheme.CreateTrayIcon(UITheme.StatusBad);
            _iconConnecting = UITheme.CreateTrayIcon(UITheme.StatusWarn);

            // Create context menu
            _contextMenu = CreateContextMenu();

            // Create notify icon
            _notifyIcon = new NotifyIcon
            {
                Icon = _iconDisconnected,
                Text = "3CX - DATEV Bridge - Getrennt",
                ContextMenuStrip = _contextMenu,
                Visible = true
            };

            _notifyIcon.DoubleClick += OnNotifyIconDoubleClick;

            // Pass NotifyIcon to CallerPopupForm for balloon notifications
            CallerPopupForm.SetNotifyIcon(_notifyIcon);

            // Create bridge service
            _bridgeService = new BridgeService(extension);
            _bridgeService.StatusChanged += OnStatusChanged;
            _bridgeService.DatevUnavailableNotified += OnDatevUnavailable;
            _bridgeService.DatevBecameAvailable += OnDatevBecameAvailable;

            // Status balloons muted by default (caller popups still work via their own settings)
            _muteStatusBalloons = AppConfig.GetBool(ConfigKeys.MuteNotifications, true);

            // Create cancellation token
            _cts = new CancellationTokenSource();

            // Hide the form
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            FormBorderStyle = FormBorderStyle.None;
            Opacity = 0;

            // Start the bridge service (fire-and-forget with proper exception handling)
            _ = StartBridgeServiceAsync();
        }

        /// <summary>
        /// Create context menu with dark theme:
        /// Title | --- | Status | Anrufliste | Kontakte neu laden | ---
        /// Einstellungen | Hilfe > | --- | Autostart | Stummschalten | ---
        /// Neustart | Info | Beenden
        /// </summary>
        private ContextMenuStrip CreateContextMenu()
        {
            var menu = new ContextMenuStrip();

            // Apply dark theme renderer
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
            statusItem.Click += (s, e) => ShowStatus();
            menu.Items.Add(statusItem);

            // Call History
            var historyItem = new ToolStripMenuItem(UIStrings.MenuItems.CallHistory, null, OnCallHistory);
            menu.Items.Add(historyItem);

            // Reload contacts
            var reloadItem = new ToolStripMenuItem(UIStrings.MenuItems.ReloadContacts, null, OnReloadContacts);
            menu.Items.Add(reloadItem);

            menu.Items.Add(new ToolStripSeparator());

            // Settings
            var settingsItem = new ToolStripMenuItem(UIStrings.MenuItems.Settings, null, OnSettings);
            menu.Items.Add(settingsItem);

            // Help submenu
            var helpMenu = new ToolStripMenuItem(UIStrings.MenuItems.Help);
            helpMenu.DropDownItems.Add(new ToolStripMenuItem(UIStrings.MenuItems.Troubleshooting, null, OnHelp));
            helpMenu.DropDownItems.Add(new ToolStripMenuItem(UIStrings.MenuItems.OpenLog, null, OnOpenLogFile));
            helpMenu.DropDownItems.Add(new ToolStripMenuItem(UIStrings.MenuItems.RunSetupWizard, null, OnSetupWizard));
            // Apply dark renderer to help submenu dropdown
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
            autostartItem.CheckedChanged += OnAutoStartToggle;
            menu.Items.Add(autostartItem);

            // Mute toggle (silent mode) - controls caller/journal popups only
            // Status balloons are separately controlled by MuteNotifications config
            var muteItem = new ToolStripMenuItem(UIStrings.MenuItems.Mute)
            {
                Name = "muteItem",
                CheckOnClick = true,
                Checked = false
            };
            muteItem.CheckedChanged += OnMuteToggle;
            menu.Items.Add(muteItem);

            menu.Items.Add(new ToolStripSeparator());

            // Restart bridge service
            var restartItem = new ToolStripMenuItem(UIStrings.MenuItems.Restart, null, OnRestart);
            menu.Items.Add(restartItem);

            // About/Info
            var aboutItem = new ToolStripMenuItem(UIStrings.MenuItems.Info, null, OnAbout);
            menu.Items.Add(aboutItem);

            // Exit
            var exitItem = new ToolStripMenuItem(UIStrings.MenuItems.Exit, null, OnExit);
            menu.Items.Add(exitItem);

            return menu;
        }

        /// <summary>
        /// Create a small colored dot image for status menu items.
        /// </summary>
        private static Image CreateStatusDot(Color color)
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

        /// <summary>
        /// Start bridge service async with proper exception handling
        /// </summary>
        private async Task StartBridgeServiceAsync()
        {
            try
            {
                await _bridgeService.StartAsync(_cts.Token);

                // On first run, ask user if they want to launch the setup wizard
                if (AppConfig.IsFirstRun)
                {
                    LogManager.Log("First run detected - prompting for setup wizard");
                    BeginInvoke(new Action(PromptFirstRunWizard));
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                LogManager.Log("Bridge service error: {0}", ex);
                ShowBalloon("Error", $"Bridge service error: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private void PromptFirstRunWizard()
        {
            var result = MessageBox.Show(
                UIStrings.Wizard.FirstRunPrompt,
                UIStrings.Wizard.FirstRunTitle,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                SetupWizardForm.ShowWizard(_bridgeService);
            }
        }

        /// <summary>
        /// Handle DATEV unavailable notification - show balloon to user
        /// </summary>
        private void OnDatevUnavailable(object sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<object, EventArgs>(OnDatevUnavailable), sender, e);
                return;
            }

            ShowBalloon("3CX-DATEV Bridge",
                UIStrings.Notifications.DatevLost,
                ToolTipIcon.Warning);
            UpdateTrayStatus();
        }

        /// <summary>
        /// Handle DATEV became available - show confirmation balloon
        /// </summary>
        private void OnDatevBecameAvailable(object sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<object, EventArgs>(OnDatevBecameAvailable), sender, e);
                return;
            }

            ShowBalloon("3CX-DATEV Bridge",
                UIStrings.Notifications.DatevFound,
                ToolTipIcon.Info);
            UpdateTrayStatus();
        }

        /// <summary>
        /// Handle status changes from bridge service
        /// </summary>
        private void OnStatusChanged(BridgeStatus status)
        {
            // Ensure we're on the UI thread
            if (InvokeRequired)
            {
                BeginInvoke(new Action<BridgeStatus>(OnStatusChanged), status);
                return;
            }

            // Only show balloon on actual status transitions (prevents duplicates
            // when LineDisconnected fires StatusChanged with unchanged status)
            bool statusChanged = status != _lastNotifiedStatus;

            switch (status)
            {
                case BridgeStatus.Connected:
                    if (statusChanged)
                    {
                        string connMsg = SessionManager.IsTerminalSession
                            ? string.Format(UIStrings.Notifications.PipeConnected, _bridgeService.Extension)
                            : string.Format(UIStrings.Notifications.TapiConnected, _bridgeService.Extension);
                        ShowBalloon(UIStrings.Status.Connected, connMsg, ToolTipIcon.Info);
                    }
                    break;

                case BridgeStatus.Connecting:
                    _notifyIcon.Icon = _iconConnecting;
                    _notifyIcon.Text = "3CX - DATEV Bridge - Verbinde...";
                    UpdateStatusMenuItem("Verbinde...");
                    _lastNotifiedStatus = status;
                    return; // Don't run combined status update

                case BridgeStatus.Disconnected:
                    if (statusChanged)
                    {
                        string discMsg = SessionManager.IsTerminalSession
                            ? UIStrings.Notifications.PipeDisconnected
                            : UIStrings.Notifications.TapiDisconnected;
                        ShowBalloon(UIStrings.Status.Disconnected, discMsg, ToolTipIcon.Warning);
                    }
                    break;
            }

            _lastNotifiedStatus = status;
            UpdateTrayStatus();
        }

        /// <summary>
        /// Updates tray icon and tooltip based on combined TAPI + DATEV state
        /// </summary>
        private void UpdateTrayStatus()
        {
            bool tapiOk = _bridgeService.TapiConnected;
            bool datevOk = _bridgeService.DatevAvailable;

            if (tapiOk && datevOk)
            {
                _notifyIcon.Icon = _iconConnected;
                _notifyIcon.Text = $"3CX - DATEV Bridge - Betriebsbereit (Nst: {_bridgeService.Extension})";
                UpdateStatusMenuItem("Betriebsbereit");
            }
            else if (tapiOk || datevOk)
            {
                _notifyIcon.Icon = _iconConnecting;
                string missing = !tapiOk ? "TAPI getrennt" : "DATEV nicht verfügbar";
                _notifyIcon.Text = $"3CX - DATEV Bridge - Teilweise ({missing})";
                UpdateStatusMenuItem($"Teilweise - {missing}");
            }
            else
            {
                _notifyIcon.Icon = _iconDisconnected;
                _notifyIcon.Text = "3CX - DATEV Bridge - Getrennt";
                UpdateStatusMenuItem("Getrennt");
            }
        }

        /// <summary>
        /// Update status menu item with colored dot image and text.
        /// Green = all OK, Orange = partial, Red = disconnected
        /// </summary>
        private void UpdateStatusMenuItem(string text)
        {
            var statusItem = _contextMenu.Items["statusItem"] as ToolStripMenuItem;
            if (statusItem != null)
            {
                bool tapiOk = _bridgeService.TapiConnected;
                bool datevOk = _bridgeService.DatevAvailable;

                // Update colored dot image
                Color dotColor = (tapiOk && datevOk) ? UITheme.StatusOk
                    : (tapiOk || datevOk) ? UITheme.StatusWarn
                    : UITheme.StatusBad;

                var oldImage = statusItem.Image;
                statusItem.Image = CreateStatusDot(dotColor);
                oldImage?.Dispose();

                statusItem.Text = $"{UIStrings.MenuItems.Status}: {text}";
            }
        }

        /// <summary>
        /// Show status balloon notification (suppressed when MuteNotifications=true)
        /// </summary>
        private void ShowBalloon(string title, string text, ToolTipIcon icon)
        {
            if (_muteStatusBalloons) return;
            _notifyIcon.ShowBalloonTip(3000, title, text, icon);
        }

        /// <summary>
        /// Close current main form if open, preserving location for next form
        /// </summary>
        private void CloseCurrentMainForm()
        {
            if (_currentMainForm != null && !_currentMainForm.IsDisposed)
            {
                _lastFormLocation = _currentMainForm.Location;
                _currentMainForm.Close();
                _currentMainForm.Dispose();
            }
            _currentMainForm = null;
        }

        /// <summary>
        /// Show a form as the main window, closing any existing main form first
        /// </summary>
        private void ShowMainForm(Form form)
        {
            CloseCurrentMainForm();
            _currentMainForm = form;

            // Restore previous location if available
            if (_lastFormLocation.HasValue)
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = _lastFormLocation.Value;
            }

            form.Show();
            form.Activate();
            form.BringToFront();
        }

        /// <summary>
        /// Handle double-click on tray icon - show status or call history based on setting
        /// </summary>
        private void OnNotifyIconDoubleClick(object sender, EventArgs e)
        {
            bool showCallHistory = AppConfig.GetBool("TrayDoubleClickCallHistory", true);
            if (showCallHistory)
                ShowCallHistory();
            else
                ShowStatus();
        }

        /// <summary>
        /// Show status form
        /// </summary>
        private void ShowStatus()
        {
            // If status form is already open, just bring it to front
            if (_currentMainForm is StatusForm existingStatus && !existingStatus.IsDisposed)
            {
                existingStatus.Activate();
                existingStatus.BringToFront();
                return;
            }

            var statusForm = new StatusForm(_bridgeService);
            statusForm.FormClosed += (s, args) =>
            {
                var form = s as StatusForm;
                if (form != null && !form.IsDisposed)
                {
                    _lastFormLocation = form.Location;
                }
                if (_currentMainForm == s)
                    _currentMainForm = null;

                // Handle navigation request
                if (form?.RequestedAction == StatusForm.Action.CallHistory)
                    BeginInvoke(new System.Action(ShowCallHistory));
                else if (form?.RequestedAction == StatusForm.Action.Settings)
                    BeginInvoke(new System.Action(ShowSettings));
            };

            ShowMainForm(statusForm);
        }

        /// <summary>
        /// Handle reload contacts menu item (event handler wrapper)
        /// </summary>
        private void OnReloadContacts(object sender, EventArgs e)
        {
            // Fire-and-forget with proper exception handling
            _ = ReloadContactsAsync();
        }

        /// <summary>
        /// Reload contacts async with proper exception handling
        /// </summary>
        private async Task ReloadContactsAsync()
        {
            try
            {
                ShowBalloon("Neu laden", "Kontakte werden aus DATEV geladen...", ToolTipIcon.Info);
                await _bridgeService.ReloadContactsAsync();
                ShowBalloon("Kontakte geladen", $"{_bridgeService.ContactCount} Kontakte geladen", ToolTipIcon.Info);
                UpdateStatusMenuItem("Betriebsbereit");
            }
            catch (Exception ex)
            {
                LogManager.Log("Error reloading contacts: {0}", ex);
                ShowBalloon("Fehler", $"Kontakte konnten nicht geladen werden: {ex.Message}", ToolTipIcon.Error);
            }
        }

        /// <summary>
        /// Handle open log file menu item
        /// </summary>
        private void OnOpenLogFile(object sender, EventArgs e)
        {
            try
            {
                string logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "3CXDATEVBridge",
                    "3CXDatevBridge.log");

                if (File.Exists(logPath))
                {
                    // Use default system handler for .log files
                    Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
                }
                else
                {
                    MessageBox.Show(
                        $"Protokolldatei nicht gefunden:\n{logPath}",
                        "Protokolldatei",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Fehler beim Öffnen der Protokolldatei: {ex.Message}",
                    "Fehler",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Handle settings menu item
        /// </summary>
        private void OnSettings(object sender, EventArgs e)
        {
            ShowSettings();
        }

        /// <summary>
        /// Show settings form
        /// </summary>
        private void ShowSettings()
        {
            // If settings form is already open, just bring it to front
            if (_currentMainForm is SettingsForm existingSettings && !existingSettings.IsDisposed)
            {
                existingSettings.Activate();
                existingSettings.BringToFront();
                return;
            }

            var settingsForm = new SettingsForm(_bridgeService);
            settingsForm.FormClosed += (s, args) =>
            {
                var form = s as SettingsForm;
                if (form != null && !form.IsDisposed)
                {
                    _lastFormLocation = form.Location;
                }
                if (_currentMainForm == s)
                    _currentMainForm = null;

                // Handle navigation request
                if (form?.RequestedAction == SettingsForm.Action.Status)
                    BeginInvoke(new System.Action(ShowStatus));
            };

            ShowMainForm(settingsForm);
        }

        /// <summary>
        /// Handle call history menu item
        /// </summary>
        private void OnCallHistory(object sender, EventArgs e)
        {
            ShowCallHistory();
        }

        /// <summary>
        /// Show call history form
        /// </summary>
        private void ShowCallHistory()
        {
            // If call history form is already open, just bring it to front
            if (_currentMainForm is CallHistoryForm existingHistory && !existingHistory.IsDisposed)
            {
                existingHistory.Activate();
                existingHistory.BringToFront();
                return;
            }

            var historyForm = new CallHistoryForm(
                _bridgeService.CallHistory,
                (entry, note) => _bridgeService.SendHistoryJournal(entry, note));
            historyForm.FormClosed += (s, args) =>
            {
                var form = s as CallHistoryForm;
                if (form != null && !form.IsDisposed)
                {
                    _lastFormLocation = form.Location;
                }
                if (_currentMainForm == s)
                    _currentMainForm = null;

                // Handle navigation request
                if (form?.RequestedAction == CallHistoryForm.Action.Back)
                    BeginInvoke(new System.Action(ShowStatus));
            };

            ShowMainForm(historyForm);
        }

        /// <summary>
        /// Handle help menu item - show troubleshooting form
        /// </summary>
        private void OnHelp(object sender, EventArgs e)
        {
            TroubleshootingForm.ShowHelp();
        }

        /// <summary>
        /// Handle setup wizard menu item
        /// </summary>
        private void OnSetupWizard(object sender, EventArgs e)
        {
            SetupWizardForm.ShowWizard(_bridgeService);
        }

        /// <summary>
        /// Handle about menu item
        /// </summary>
        private void OnAbout(object sender, EventArgs e)
        {
            using (var aboutForm = new AboutForm())
            {
                aboutForm.ShowDialog();
            }
        }

        /// <summary>
        /// Handle autostart toggle
        /// </summary>
        private void OnAutoStartToggle(object sender, EventArgs e)
        {
            var item = sender as ToolStripMenuItem;
            if (item != null)
                AutoStartManager.SetEnabled(item.Checked);
        }

        /// <summary>
        /// Handle mute toggle (silent mode)
        /// </summary>
        private void OnMuteToggle(object sender, EventArgs e)
        {
            var item = sender as ToolStripMenuItem;
            if (item != null)
                _bridgeService.IsMuted = item.Checked;
        }

        /// <summary>
        /// Handle restart - reconnect TAPI and reload contacts
        /// </summary>
        private void OnRestart(object sender, EventArgs e)
        {
            _ = RestartBridgeAsync();
        }

        private async Task RestartBridgeAsync()
        {
            try
            {
                LogManager.Log("Manual bridge restart requested");
                UpdateStatusMenuItem("Neustart...");
                _notifyIcon.Icon = _iconConnecting;
                _notifyIcon.Text = "3CX - DATEV Bridge - Neustart...";

                await _bridgeService.ReconnectTapiAsync();
                await _bridgeService.ReloadContactsAsync();

                UpdateTrayStatus();
            }
            catch (Exception ex)
            {
                LogManager.Log("Bridge restart error: {0}", ex.Message);
                ShowBalloon("Fehler", $"Neustart fehlgeschlagen: {ex.Message}", ToolTipIcon.Error);
            }
        }

        /// <summary>
        /// Handle exit menu item
        /// </summary>
        private void OnExit(object sender, EventArgs e)
        {
            // Cancel the bridge service
            _cts.Cancel();
            
            // Close the application
            Application.Exit();
        }

        /// <summary>
        /// Clean up resources
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cts?.Cancel();

                // Dispose any open main form (StatusForm, SettingsForm, CallHistoryForm)
                if (_currentMainForm != null && !_currentMainForm.IsDisposed)
                {
                    _currentMainForm.Close();
                    _currentMainForm.Dispose();
                }
                _currentMainForm = null;

                _bridgeService?.Dispose();
                _contextMenu?.Dispose();
                _notifyIcon?.Dispose();
                _iconConnected?.Dispose();
                _iconDisconnected?.Dispose();
                _iconConnecting?.Dispose();
                UITheme.Cleanup();
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// Prevent form from showing, but ensure handle is created for BeginInvoke
        /// </summary>
        protected override void SetVisibleCore(bool value)
        {
            if (!IsHandleCreated)
                CreateHandle();
            base.SetVisibleCore(false);
        }
    }
}
