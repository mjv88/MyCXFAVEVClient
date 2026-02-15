using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DatevConnector.Core;
using DatevConnector.Datev.Managers;
using DatevConnector.Tapi;
using DatevConnector.UI.Strings;
using DatevConnector.UI.Theme;

namespace DatevConnector.UI
{
    /// <summary>
    /// Manages the multi-line TAPI section controls and button handlers within the StatusForm.
    /// </summary>
    internal class MultiLineStatusPanel
    {
        private readonly ConnectorService _service;
        private readonly Func<bool> _isAlive;
        private readonly Action _updateConnectorStatus;

        private Label _lblSummaryStatus;
        private Label _lblMode;
        private Button _btnReconnectAll;

        private readonly Dictionary<string, Label> _lineStatusLabels = new Dictionary<string, Label>();
        private readonly Dictionary<string, Label> _lineProgressLabels = new Dictionary<string, Label>();
        private readonly Dictionary<string, Button> _lineTestButtons = new Dictionary<string, Button>();
        private readonly Dictionary<string, Button> _lineReconnectButtons = new Dictionary<string, Button>();

        public MultiLineStatusPanel(
            ConnectorService service,
            Panel card,
            int cardWidth,
            IReadOnlyList<TapiLineInfo> lines,
            Func<bool> isAlive,
            Action updateConnectorStatus)
        {
            _service = service;
            _isAlive = isAlive;
            _updateConnectorStatus = updateConnectorStatus;

            InitializeControls(card, cardWidth, lines);
        }

        private void InitializeControls(Panel card, int cardWidth, IReadOnlyList<TapiLineInfo> lines)
        {
            int lineCount = lines.Count;
            int connectedCount = _service?.ConnectedLineCount ?? 0;

            string activeModeName = (_service != null && connectedCount > 0)
                ? TelephonyProviderSelector.GetModeShortName(_service.SelectedTelephonyMode)
                : "";

            _lblSummaryStatus = new Label
            {
                Text = string.Format(UIStrings.Status.LinesConnected, connectedCount, lineCount),
                ForeColor = connectedCount == lineCount ? UITheme.StatusOk :
                           (connectedCount > 0 ? UITheme.StatusWarn : UITheme.StatusBad),
                Font = UITheme.FontBody,
                Location = new Point(12, 28),
                AutoSize = true
            };
            card.Controls.Add(_lblSummaryStatus);

            _lblMode = new Label
            {
                Text = activeModeName,
                ForeColor = UITheme.TextMuted,
                Font = UITheme.FontSmall,
                Location = new Point(12, 48),
                AutoSize = true
            };
            card.Controls.Add(_lblMode);

            // "Neuverbinden" button for reconnecting all lines
            _btnReconnectAll = UITheme.CreateSecondaryButton(UIStrings.Labels.ReconnectShort, 90);
            _btnReconnectAll.Location = new Point(cardWidth - 12 - 90, 24);
            _btnReconnectAll.Click += BtnReconnectAllLines_Click;
            card.Controls.Add(_btnReconnectAll);

            // Individual line rows: Status | Progress | Testen | Verb.
            int lineY = 70;
            int btnTestWidth = 50;
            int btnVerbWidth = 45;
            int btnGap = 4;

            foreach (var line in lines)
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
                card.Controls.Add(lblLine);
                _lineStatusLabels[line.Extension] = lblLine;

                // Progress label (middle) with styled background (hidden by default)
                int progressWidth = cardWidth - 145 - btnTestWidth - btnVerbWidth - btnGap - 16;
                var lblProgress = UITheme.CreateProgressLabel(progressWidth);
                lblProgress.Location = new Point(145, lineY);
                lblProgress.Visible = false;
                card.Controls.Add(lblProgress);
                _lineProgressLabels[line.Extension] = lblProgress;

                // Test button
                var btnTest = UITheme.CreateSecondaryButton(UIStrings.Labels.Test, btnTestWidth);
                btnTest.Font = UITheme.FontSmall;
                btnTest.Location = new Point(cardWidth - 12 - btnVerbWidth - btnGap - btnTestWidth, lineY - 2);
                btnTest.Tag = line.Extension;
                btnTest.Click += BtnTestSingleLine_Click;
                card.Controls.Add(btnTest);
                _lineTestButtons[line.Extension] = btnTest;

                // Reconnect button
                var btnReconnect = UITheme.CreateSecondaryButton(UIStrings.Labels.ConnectShort, btnVerbWidth);
                btnReconnect.Font = UITheme.FontSmall;
                btnReconnect.Location = new Point(cardWidth - 12 - btnVerbWidth, lineY - 2);
                btnReconnect.Tag = line.Extension;
                btnReconnect.Enabled = !line.IsConnected;
                btnReconnect.Click += BtnReconnectSingleLine_Click;
                card.Controls.Add(btnReconnect);
                _lineReconnectButtons[line.Extension] = btnReconnect;

                lineY += 24;
            }
        }

        /// <summary>
        /// Update all line status displays from current TapiLines state.
        /// </summary>
        public void UpdateLineStatuses()
        {
            var tapiLines = _service?.TapiLines;
            if (tapiLines == null) return;

            int connectedCount = tapiLines.Count(l => l.IsConnected);
            int totalCount = tapiLines.Count;

            _lblSummaryStatus.Text = string.Format(UIStrings.Status.LinesConnected, connectedCount, totalCount);
            _lblSummaryStatus.ForeColor = connectedCount == totalCount ? UITheme.StatusOk :
                                          (connectedCount > 0 ? UITheme.StatusWarn : UITheme.StatusBad);

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

        public void UpdateMode(string modeName)
        {
            _lblMode.Text = modeName;
        }

        /// <summary>
        /// Called by StatusForm when ConnectorService.StatusChanged fires.
        /// </summary>
        public void HandleStatusChanged(string modeName)
        {
            UpdateLineStatuses();
            UpdateMode(_service?.TapiConnected == true ? modeName : "");

            if (_btnReconnectAll.Text == UIStrings.Status.TestPending)
            {
                _btnReconnectAll.Text = UIStrings.Labels.ReconnectShort;
                _btnReconnectAll.Enabled = true;
            }
        }

        /// <summary>
        /// Disable all buttons (used by Test All)
        /// </summary>
        public void DisableButtons()
        {
            _btnReconnectAll.Enabled = false;
            foreach (var btn in _lineTestButtons.Values)
                btn.Enabled = false;
            foreach (var btn in _lineReconnectButtons.Values)
                btn.Enabled = false;
        }

        /// <summary>
        /// Restore buttons after Test All completes
        /// </summary>
        public void OnTestAllComplete()
        {
            _btnReconnectAll.Text = UIStrings.Labels.ReconnectShort;
            _btnReconnectAll.Enabled = true;
            UpdateLineStatuses();
        }

        /// <summary>
        /// Thread-safe progress update for a specific line
        /// </summary>
        private void UpdateProgressSafe(Label label, string text)
        {
            if (label == null || label.IsDisposed) return;
            if (label.InvokeRequired)
            {
                try
                {
                    label.BeginInvoke(new Action(() =>
                    {
                        label.Text = text;
                        if (!string.IsNullOrEmpty(text) && !label.Visible)
                            label.Visible = true;
                    }));
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                return;
            }
            label.Text = text;
            if (!string.IsNullOrEmpty(text) && !label.Visible)
                label.Visible = true;
        }

        private void HideProgress(Label label)
        {
            if (label == null) return;
            label.Text = "";
            label.Visible = false;
        }

        private async void BtnReconnectAllLines_Click(object sender, EventArgs e)
        {
            _btnReconnectAll.Enabled = false;
            _btnReconnectAll.Text = UIStrings.Status.TestPending;

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
            var tapiLines = _service?.TapiLines;
            if (tapiLines != null)
            {
                foreach (var line in tapiLines)
                {
                    string ext = line.Extension;
                    Label progressLabel = null;
                    _lineProgressLabels.TryGetValue(ext, out progressLabel);

                    await Task.Run(() => _service.ReconnectTapiLine(ext,
                        msg => UpdateProgressSafe(progressLabel, msg)));

                    if (!_isAlive()) return;
                }
            }

            await Task.Delay(1000);
            if (!_isAlive()) return;

            UpdateLineStatuses();
            _btnReconnectAll.Text = UIStrings.Labels.ReconnectShort;
            _btnReconnectAll.Enabled = true;
            _updateConnectorStatus();

            // Hide all per-line progress labels
            foreach (var lbl in _lineProgressLabels.Values)
                HideProgress(lbl);
        }

        private async void BtnTestSingleLine_Click(object sender, EventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;
            string extension = btn.Tag as string;
            if (string.IsNullOrEmpty(extension)) return;

            Label progressLabel = null;
            _lineProgressLabels.TryGetValue(extension, out progressLabel);

            bool isConnected = await AsyncButtonAction.RunTestAsync(
                btn, progressLabel,
                async progress => await Task.Run(() =>
                    _service?.TestTapiLine(extension, progress) ?? false),
                UIStrings.Labels.Test);

            if (!_isAlive()) return;
            if (_lineStatusLabels.TryGetValue(extension, out var statusLabel))
            {
                statusLabel.Text = string.Format(UIStrings.Status.LineStatus, extension,
                    isConnected ? UIStrings.Status.Connected : UIStrings.Status.Disconnected);
                statusLabel.ForeColor = isConnected ? UITheme.StatusOk : UITheme.StatusBad;
            }
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
                _service.ReconnectTapiLine(extension,
                    msg => UpdateProgressSafe(progressLabel, msg)));

            await Task.Delay(1500);
            if (!_isAlive()) return;

            UpdateLineStatuses();
            btn.Text = UIStrings.Labels.ConnectShort;
            _updateConnectorStatus();
            HideProgress(progressLabel);
        }
    }
}
