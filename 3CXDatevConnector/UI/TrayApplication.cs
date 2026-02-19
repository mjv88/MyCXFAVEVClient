using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DatevConnector.Core;
using DatevConnector.Core.Config;
using DatevConnector.Datev.Managers;
using DatevConnector.UI.Strings;
using DatevConnector.UI.Theme;

namespace DatevConnector.UI
{
    /// <summary>
    /// System tray application - hidden form with NotifyIcon.
    /// Delegates menu construction to TrayContextMenuBuilder and
    /// form navigation to FormNavigator.
    /// </summary>
    public class TrayApplication : Form
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly ContextMenuStrip _contextMenu;
        private readonly ConnectorService _bridgeService;
        private readonly CancellationTokenSource _cts;
        private readonly Icon _iconConnected;
        private readonly Icon _iconDisconnected;
        private readonly Icon _iconConnecting;
        private readonly FormNavigator _navigator;

        // Track last notified status to avoid duplicate balloons
        private ConnectorStatus _lastNotifiedStatus = ConnectorStatus.Disconnected;

        // Mute status balloons (separate from caller popup muting)
        private readonly bool _muteStatusBalloons;

        public TrayApplication(string extension)
        {
            // Initialize UI context for popup forms (must be done on UI thread)
            FormDisplayHelper.InitializeUIContext();

            _iconConnected = UITheme.CreateTrayIcon(UITheme.StatusOk);
            _iconDisconnected = UITheme.CreateTrayIcon(UITheme.StatusBad);
            _iconConnecting = UITheme.CreateTrayIcon(UITheme.StatusWarn);

            _contextMenu = TrayContextMenuBuilder.Build();
            WireMenuEvents();

            _notifyIcon = new NotifyIcon
            {
                Icon = _iconDisconnected,
                Text = UIStrings.Status.TrayDisconnected,
                ContextMenuStrip = _contextMenu,
                Visible = true
            };

            _notifyIcon.DoubleClick += OnNotifyIconDoubleClick;

            CallerPopupForm.SetNotifyIcon(_notifyIcon);

            _bridgeService = new ConnectorService(extension);
            _bridgeService.StatusChanged += OnStatusChanged;
            _bridgeService.DatevUnavailableNotified += OnDatevUnavailable;
            _bridgeService.DatevBecameAvailable += OnDatevBecameAvailable;

            _navigator = new FormNavigator(_bridgeService, this);

            // Status balloons muted by default (caller popups still work via their own settings)
            _muteStatusBalloons = AppConfig.GetBool(ConfigKeys.MuteNotifications, true);

            _cts = new CancellationTokenSource();

            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            FormBorderStyle = FormBorderStyle.None;
            Opacity = 0;

            // Startup is triggered from SetVisibleCore after handle creation
        }

        private void WireMenuEvents()
        {
            _contextMenu.Items["statusItem"].Click += (s, e) => _navigator.ShowStatus();
            _contextMenu.Items["historyItem"].Click += (s, e) => _navigator.ShowCallHistory();
            _contextMenu.Items["reloadItem"].Click += (s, e) => _ = ReloadContactsAsync();
            _contextMenu.Items["settingsItem"].Click += (s, e) => _navigator.ShowSettings();
            _contextMenu.Items["restartItem"].Click += (s, e) => _ = RestartBridgeAsync();
            _contextMenu.Items["aboutItem"].Click += (s, e) => AboutForm.ShowAbout();
            _contextMenu.Items["exitItem"].Click += (s, e) => { _cts.Cancel(); Application.Exit(); };

            // Help submenu items
            var helpMenu = _contextMenu.Items["helpMenu"] as ToolStripMenuItem;
            if (helpMenu != null)
            {
                helpMenu.DropDownItems["helpItem"].Click += (s, e) => TroubleshootingForm.ShowHelp(_bridgeService.SelectedConnectionMode);
                helpMenu.DropDownItems["logItem"].Click += (s, e) => OnOpenLogFile();
                helpMenu.DropDownItems["wizardItem"].Click += (s, e) => SetupWizardForm.ShowWizard(_bridgeService);
            }

            // Toggle items
            var autostartItem = _contextMenu.Items["autostartItem"] as ToolStripMenuItem;
            if (autostartItem != null)
                autostartItem.CheckedChanged += (s, e) => AutoStartManager.SetEnabled(autostartItem.Checked);

            var muteItem = _contextMenu.Items["muteItem"] as ToolStripMenuItem;
            if (muteItem != null)
                muteItem.CheckedChanged += (s, e) => _bridgeService.IsMuted = muteItem.Checked;
        }

        private async Task StartConnectorServiceAsync()
        {
            try
            {
                if (AppConfig.IsFirstRun)
                {
                    LogManager.Log("Erststart erkannt - Einrichtungsassistent wird angeboten");
                    PromptFirstRunWizard();
                }

                await _bridgeService.StartAsync(_cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogManager.Log("Bridge-Service Fehler: {0}", ex);
                ShowBalloon("Error", $"Bridge service error: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private void PromptFirstRunWizard()
        {
            using (var dlg = new Form())
            {
                UITheme.ApplyFormDefaults(dlg);
                dlg.Text = UIStrings.Wizard.FirstRunTitle;
                dlg.ClientSize = new Size(420, 200);
                dlg.StartPosition = FormStartPosition.CenterScreen;
                dlg.TopMost = true;

                var logo = UITheme.GetBaseIcon();
                if (logo != null)
                {
                    var pic = new PictureBox
                    {
                        Image = logo,
                        SizeMode = PictureBoxSizeMode.Zoom,
                        Location = new Point(24, 24),
                        Size = new Size(48, 48)
                    };
                    dlg.Controls.Add(pic);
                }

                var lbl = new Label
                {
                    Text = UIStrings.Wizard.FirstRunPrompt,
                    ForeColor = UITheme.TextSecondary,
                    Font = UITheme.FontBody,
                    Location = new Point(88, 24),
                    Size = new Size(308, 100),
                    AutoEllipsis = true
                };
                dlg.Controls.Add(lbl);

                var btnYes = UITheme.CreatePrimaryButton(UIStrings.Wizard.Next, 100);
                btnYes.Location = new Point(dlg.ClientSize.Width - 24 - 100, dlg.ClientSize.Height - 24 - LayoutConstants.ButtonHeight);
                btnYes.DialogResult = DialogResult.Yes;
                dlg.Controls.Add(btnYes);

                var btnNo = UITheme.CreateSecondaryButton(UIStrings.Labels.Cancel, 100);
                btnNo.Location = new Point(btnYes.Left - 8 - 100, btnYes.Top);
                btnNo.DialogResult = DialogResult.No;
                dlg.Controls.Add(btnNo);

                dlg.AcceptButton = btnYes;
                dlg.CancelButton = btnNo;

                if (dlg.ShowDialog() == DialogResult.Yes)
                    SetupWizardForm.ShowWizard(_bridgeService);
            }
        }

        private void OnDatevUnavailable(object sender, EventArgs e)
        {
            if (InvokeRequired) { BeginInvoke(new Action<object, EventArgs>(OnDatevUnavailable), sender, e); return; }
            ShowBalloon("3CX - DATEV Connector", UIStrings.Notifications.DatevLost, ToolTipIcon.Warning);
            UpdateTrayStatus();
        }

        private void OnDatevBecameAvailable(object sender, EventArgs e)
        {
            if (InvokeRequired) { BeginInvoke(new Action<object, EventArgs>(OnDatevBecameAvailable), sender, e); return; }
            ShowBalloon("3CX - DATEV Connector", UIStrings.Notifications.DatevFound, ToolTipIcon.Info);
            UpdateTrayStatus();
        }

        private void OnStatusChanged(ConnectorStatus status)
        {
            if (InvokeRequired) { BeginInvoke(new Action<ConnectorStatus>(OnStatusChanged), status); return; }

            bool statusChanged = status != _lastNotifiedStatus;

            switch (status)
            {
                case ConnectorStatus.Connected:
                    if (statusChanged)
                    {
                        string connMsg;
                        if (_bridgeService.SelectedConnectionMode == Core.ConnectionMode.WebClient)
                            connMsg = string.Format(UIStrings.Notifications.WebclientConnected, _bridgeService.Extension);
                        else if (SessionManager.IsTerminalSession)
                            connMsg = string.Format(UIStrings.Notifications.PipeConnected, _bridgeService.Extension);
                        else
                            connMsg = string.Format(UIStrings.Notifications.TapiConnected, _bridgeService.Extension);
                        ShowBalloon(UIStrings.Status.Connected, connMsg, ToolTipIcon.Info);
                    }
                    break;

                case ConnectorStatus.Connecting:
                    _notifyIcon.Icon = _iconConnecting;
                    _notifyIcon.Text = UIStrings.Status.TrayConnecting;
                    UpdateStatusMenuItem(UIStrings.Status.Connecting);
                    _lastNotifiedStatus = status;
                    return;

                case ConnectorStatus.Disconnected:
                    if (statusChanged)
                    {
                        string discMsg;
                        if (_bridgeService.SelectedConnectionMode == Core.ConnectionMode.WebClient)
                            discMsg = UIStrings.Notifications.WebclientDisconnected;
                        else if (SessionManager.IsTerminalSession)
                            discMsg = UIStrings.Notifications.PipeDisconnected;
                        else
                            discMsg = UIStrings.Notifications.TapiDisconnected;
                        ShowBalloon(UIStrings.Status.Disconnected, discMsg, ToolTipIcon.Warning);
                    }
                    break;
            }

            _lastNotifiedStatus = status;
            UpdateTrayStatus();
        }

        private void UpdateTrayStatus()
        {
            bool tapiOk = _bridgeService.TapiConnected;
            bool datevOk = _bridgeService.DatevAvailable;

            if (tapiOk && datevOk)
            {
                _notifyIcon.Icon = _iconConnected;
                _notifyIcon.Text = string.Format(UIStrings.Status.TrayReadyFormat, _bridgeService.Extension);
                UpdateStatusMenuItem(UIStrings.Status.Ready);
            }
            else if (tapiOk || datevOk)
            {
                _notifyIcon.Icon = _iconConnecting;
                string missing = !tapiOk ? UIStrings.Status.TapiDisconnectedShort : UIStrings.Status.DatevUnavailableShort;
                _notifyIcon.Text = string.Format(UIStrings.Status.TrayPartialFormat, missing);
                UpdateStatusMenuItem($"{UIStrings.Status.Partial} - {missing}");
            }
            else
            {
                _notifyIcon.Icon = _iconDisconnected;
                _notifyIcon.Text = UIStrings.Status.TrayDisconnected;
                UpdateStatusMenuItem(UIStrings.Status.Disconnected);
            }
        }

        private void UpdateStatusMenuItem(string text)
        {
            var statusItem = _contextMenu.Items["statusItem"] as ToolStripMenuItem;
            if (statusItem != null)
            {
                bool tapiOk = _bridgeService.TapiConnected;
                bool datevOk = _bridgeService.DatevAvailable;

                Color dotColor = (tapiOk && datevOk) ? UITheme.StatusOk
                    : (tapiOk || datevOk) ? UITheme.StatusWarn
                    : UITheme.StatusBad;

                var oldImage = statusItem.Image;
                statusItem.Image = TrayContextMenuBuilder.CreateStatusDot(dotColor);
                oldImage?.Dispose();

                statusItem.Text = $"{UIStrings.MenuItems.Status}: {text}";
            }
        }

        private void ShowBalloon(string title, string text, ToolTipIcon icon)
        {
            if (_muteStatusBalloons) return;
            _notifyIcon.ShowBalloonTip(3000, title, text, icon);
        }

        private void OnNotifyIconDoubleClick(object sender, EventArgs e)
        {
            bool showCallHistory = AppConfig.GetBool(ConfigKeys.TrayDoubleClickCallHistory, true);
            if (showCallHistory)
                _navigator.ShowCallHistory();
            else
                _navigator.ShowStatus();
        }

        private async Task ReloadContactsAsync()
        {
            try
            {
                ShowBalloon(UIStrings.Notifications.ReloadTitle, UIStrings.Notifications.ContactsReloading, ToolTipIcon.Info);
                await _bridgeService.ReloadContactsAsync();
                ShowBalloon(UIStrings.Notifications.ContactsReloadedTitle, string.Format(UIStrings.Notifications.ContactsReloadedFormat, _bridgeService.ContactCount), ToolTipIcon.Info);
                UpdateStatusMenuItem(UIStrings.Status.Ready);
            }
            catch (Exception ex)
            {
                LogManager.Log("Fehler beim Neuladen der Kontakte: {0}", ex);
                ShowBalloon(UIStrings.Errors.GenericError, string.Format(UIStrings.Notifications.ContactsReloadFailed, ex.Message), ToolTipIcon.Error);
            }
        }

        private void OnOpenLogFile()
        {
            try
            {
                string logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "3CXDATEVConnector",
                    "3CXDatevConnector.log");

                if (File.Exists(logPath))
                    Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
                else
                    MessageBox.Show(
                        string.Format(UIStrings.Notifications.LogFileNotFoundFormat, logPath),
                        UIStrings.Notifications.LogFileTitle,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format(UIStrings.Notifications.LogFileOpenFailed, ex.Message),
                    UIStrings.Errors.GenericError,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task RestartBridgeAsync()
        {
            try
            {
                LogManager.Log("Manueller Bridge-Neustart angefordert");
                UpdateStatusMenuItem(UIStrings.Notifications.Restarting);
                _notifyIcon.Icon = _iconConnecting;
                _notifyIcon.Text = UIStrings.Status.TrayRestarting;

                await _bridgeService.ReconnectTapiAsync();
                await _bridgeService.ReloadContactsAsync();

                UpdateTrayStatus();
            }
            catch (Exception ex)
            {
                LogManager.Log("Bridge-Neustart Fehler: {0}", ex.Message);
                ShowBalloon(UIStrings.Errors.GenericError, string.Format(UIStrings.Notifications.RestartFailed, ex.Message), ToolTipIcon.Error);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cts?.Cancel();
                _navigator?.DisposeCurrentForm();
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

        protected override void SetVisibleCore(bool value)
        {
            if (!IsHandleCreated)
            {
                CreateHandle();
                // Post startup to run once the message loop is active
                BeginInvoke(new Action(() => _ = StartConnectorServiceAsync()));
            }
            base.SetVisibleCore(false);
        }
    }
}
