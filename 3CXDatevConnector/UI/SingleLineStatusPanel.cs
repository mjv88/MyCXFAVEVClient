using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using DatevConnector.Core;
using DatevConnector.Datev.Managers;
using DatevConnector.UI.Strings;
using DatevConnector.UI.Theme;

namespace DatevConnector.UI
{
    /// <summary>
    /// Manages the single-line TAPI section controls and button handlers within the StatusForm.
    /// </summary>
    internal class SingleLineStatusPanel
    {
        private readonly ConnectorService _service;
        private readonly Func<bool> _isAlive;
        private readonly Action _updateConnectorStatus;

        private Label _lblStatus;
        private Label _lblExtBold;
        private Label _lblMode;
        private Label _lblProgress;
        private Button _btnTest;
        private Button _btnReconnect;

        public SingleLineStatusPanel(
            ConnectorService service,
            Panel card,
            int cardWidth,
            Func<bool> isAlive,
            Action updateConnectorStatus)
        {
            _service = service;
            _isAlive = isAlive;
            _updateConnectorStatus = updateConnectorStatus;

            InitializeControls(card, cardWidth);
        }

        private void InitializeControls(Panel card, int cardWidth)
        {
            bool tapiOk = _service?.TapiConnected ?? false;
            string ext = _service?.Extension ?? "—";
            int btnWidth = 75;
            int btnSpacing = 8;

            string activeModeName = (_service != null && tapiOk)
                ? TelephonyProviderSelector.GetModeShortName(_service.SelectedTelephonyMode)
                : "";

            _lblStatus = new Label
            {
                Text = tapiOk ? UIStrings.Status.Connected : UIStrings.Status.Disconnected,
                ForeColor = tapiOk ? UITheme.StatusOk : UITheme.StatusBad,
                Font = UITheme.FontBody,
                Location = new Point(12, 28),
                AutoSize = true
            };
            card.Controls.Add(_lblStatus);

            _lblExtBold = new Label
            {
                Text = (tapiOk && !string.IsNullOrEmpty(_service?.Extension)) ? ext : "",
                ForeColor = UITheme.TextPrimary,
                Font = UITheme.FontLabel,
                Location = new Point(90, 28),
                AutoSize = true
            };
            card.Controls.Add(_lblExtBold);

            _lblMode = new Label
            {
                Text = activeModeName,
                ForeColor = UITheme.TextMuted,
                Font = UITheme.FontSmall,
                Location = new Point(12, 48),
                AutoSize = true
            };
            card.Controls.Add(_lblMode);

            _btnReconnect = UITheme.CreateSecondaryButton(UIStrings.Labels.Connect, btnWidth);
            _btnReconnect.Location = new Point(cardWidth - 12 - btnWidth, 58);
            _btnReconnect.Click += BtnReconnect_Click;
            _btnReconnect.Enabled = !tapiOk;
            card.Controls.Add(_btnReconnect);

            _btnTest = UITheme.CreateSecondaryButton(UIStrings.Labels.Test, btnWidth);
            _btnTest.Location = new Point(cardWidth - 12 - btnWidth - btnSpacing - btnWidth, 58);
            _btnTest.Click += BtnTest_Click;
            card.Controls.Add(_btnTest);

            _lblProgress = UITheme.CreateProgressLabel(cardWidth - 24);
            _lblProgress.Location = new Point(12, 96);
            _lblProgress.Visible = false;
            card.Controls.Add(_lblProgress);
        }

        public void UpdateDisplay(bool connected, string ext)
        {
            _lblStatus.Text = connected ? UIStrings.Status.Connected : UIStrings.Status.Disconnected;
            _lblStatus.ForeColor = connected ? UITheme.StatusOk : UITheme.StatusBad;
            _lblExtBold.Text = (connected && !string.IsNullOrEmpty(ext)) ? ext : "";
        }

        public void UpdateMode(string modeName)
        {
            _lblMode.Text = modeName;
        }

        /// <summary>
        /// Called by StatusForm when ConnectorService.StatusChanged fires.
        /// Updates display and resets button states.
        /// </summary>
        public void HandleStatusChanged(bool tapiOk, string ext, string modeName)
        {
            UpdateDisplay(tapiOk, ext);
            _btnReconnect.Enabled = !tapiOk;
            UpdateMode(tapiOk ? modeName : "");

            if (_btnReconnect.Text == UIStrings.Status.TestPending)
                _btnReconnect.Text = UIStrings.Labels.Connect;
            if (_btnTest.Text == UIStrings.Status.TestPending)
            {
                _btnTest.Text = UIStrings.Labels.Test;
                _btnTest.Enabled = true;
            }
        }

        /// <summary>
        /// Disable all buttons (used by Test All)
        /// </summary>
        public void DisableButtons()
        {
            _btnTest.Enabled = false;
            _btnReconnect.Enabled = false;
        }

        /// <summary>
        /// Restore buttons after Test All completes
        /// </summary>
        public void OnTestAllComplete(bool tapiOk, string ext)
        {
            UpdateDisplay(tapiOk, ext);
            _btnTest.Enabled = true;
            _btnReconnect.Enabled = !tapiOk;
        }

        public void ShowProgress(string text)
        {
            _lblProgress.Text = text;
            if (!string.IsNullOrEmpty(text) && !_lblProgress.Visible)
                _lblProgress.Visible = true;
        }

        public void HideProgress()
        {
            _lblProgress.Text = "";
            _lblProgress.Visible = false;
        }

        /// <summary>
        /// Thread-safe progress update for use from background tasks
        /// </summary>
        public void UpdateProgressSafe(string text)
        {
            if (_lblProgress.IsDisposed) return;
            if (_lblProgress.InvokeRequired)
            {
                try { _lblProgress.BeginInvoke(new Action(() => ShowProgress(text))); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                return;
            }
            ShowProgress(text);
        }

        private async void BtnTest_Click(object sender, EventArgs e)
        {
            _btnTest.Enabled = false;
            _btnReconnect.Enabled = false;
            _btnTest.Text = UIStrings.Status.TestPending;
            _lblProgress.Visible = true;
            _lblProgress.Text = "";

            string extension = _service?.Extension;
            bool isConnected = await Task.Run(() =>
                _service?.TestTapiLine(extension, msg => UpdateProgressSafe(msg)) ?? false);

            if (!_isAlive()) return;

            UpdateDisplay(isConnected, extension);

            // Show visual feedback
            _btnTest.Text = isConnected ? UIStrings.Status.TestSuccess : UIStrings.Status.TestFailed;
            _btnTest.ForeColor = isConnected ? UITheme.StatusOk : UITheme.StatusBad;

            // Reset button after short delay
            await Task.Delay(1500);
            if (!_isAlive()) return;

            _btnTest.Text = UIStrings.Labels.Test;
            _btnTest.ForeColor = UITheme.TextPrimary;
            _btnTest.Enabled = true;
            _btnReconnect.Enabled = !isConnected;
            HideProgress();

            _updateConnectorStatus();
        }

        private async void BtnReconnect_Click(object sender, EventArgs e)
        {
            _btnReconnect.Enabled = false;
            _btnReconnect.Text = UIStrings.Status.TestPending;
            _lblProgress.Visible = true;
            _lblProgress.Text = "";

            await _service.ReconnectTapiAsync(msg => UpdateProgressSafe(msg));

            // StatusChanged event will update UI when connection establishes
            // But ensure button is restored if no status change occurs within timeout
            await Task.Delay(6000);
            if (!_isAlive()) return;

            if (_btnReconnect.Text == UIStrings.Status.TestPending)
            {
                bool tapiOk = _service?.TapiConnected ?? false;
                string ext = _service?.Extension;
                UpdateDisplay(tapiOk, ext);
                _btnReconnect.Text = UIStrings.Labels.Connect;
                _btnReconnect.Enabled = !tapiOk;
                _updateConnectorStatus();
            }
            HideProgress();
        }
    }
}
