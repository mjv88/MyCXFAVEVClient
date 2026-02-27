using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using DatevConnector.Core;
using DatevConnector.Core.Config;
using DatevConnector.Datev;
using DatevConnector.Datev.Managers;
using DatevConnector.UI.Strings;
using DatevConnector.UI.Theme;

namespace DatevConnector.UI
{
    /// <summary>
    /// First-run setup wizard for initial configuration.
    /// Auto-detects the connection method and guides user through
    /// connection verification, DATEV connection test, and autostart setup.
    /// </summary>
    public class SetupWizardForm : ThemedForm
    {
        private const int TOTAL_STEPS = 4;

        private int _currentStep = 1;
        private readonly Panel _contentPanel;
        private readonly Label _lblStepIndicator;
        private readonly Button _btnBack;
        private readonly Button _btnNext;
        private readonly ConnectorService _bridgeService;

        // Step 2: TAPI / Pipe / Webclient config
        private ComboBox _cboTapiLine;
        private Label _lblTapiStatus;

        // Step 3: DATEV
        private Label _lblDatevStatus;
        private bool _datevOk;

        // Step 4: Finish
        private CheckBox _chkAutoStart;

        public SetupWizardForm(ConnectorService bridgeService = null)
        {
            _bridgeService = bridgeService;
            InitializeForm();

            // Step indicator
            _lblStepIndicator = new Label
            {
                Font = UITheme.FontSmall,
                ForeColor = UITheme.TextMuted,
                Location = new Point(LayoutConstants.SpaceLG, ClientSize.Height - 45),
                AutoSize = true
            };
            Controls.Add(_lblStepIndicator);

            // Content panel
            _contentPanel = new Panel
            {
                Location = new Point(0, UITheme.AccentBarHeight),
                Size = new Size(ClientSize.Width, ClientSize.Height - UITheme.AccentBarHeight - 55),
                BackColor = UITheme.FormBackground
            };
            Controls.Add(_contentPanel);

            // Navigation buttons
            _btnBack = UITheme.CreateSecondaryButton(UIStrings.Wizard.Back, 100);
            _btnBack.Location = new Point(ClientSize.Width - 220, ClientSize.Height - 45);
            _btnBack.Click += BtnBack_Click;
            Controls.Add(_btnBack);

            _btnNext = UITheme.CreatePrimaryButton(UIStrings.Wizard.Next, 100);
            _btnNext.Location = new Point(ClientSize.Width - 112, ClientSize.Height - 45);
            _btnNext.Click += BtnNext_Click;
            Controls.Add(_btnNext);

            // Subscribe to live status updates from the connector
            if (_bridgeService != null)
                _bridgeService.StatusChanged += OnConnectorStatusChanged;

            FormClosed += (s, e) =>
            {
                if (_bridgeService != null)
                    _bridgeService.StatusChanged -= OnConnectorStatusChanged;
            };

            ShowStep(_currentStep);
        }

        private void InitializeForm()
        {
            // ThemedForm handles: BackColor, ForeColor, FormBorderStyle, StartPosition,
            // MaximizeBox, MinimizeBox, Font, Icon
            Text = UIStrings.Wizard.Title;
            ClientSize = new Size(480, 360);

            // Accent bar
            var accentBar = UITheme.CreateAccentBar(UITheme.AccentDatev);
            Controls.Add(accentBar);
        }

        private void ShowStep(int step)
        {
            _currentStep = step;
            while (_contentPanel.Controls.Count > 0)
            {
                var ctl = _contentPanel.Controls[0];
                _contentPanel.Controls.Remove(ctl);
                ctl.Dispose();
            }
            _lblStepIndicator.Text = string.Format(UIStrings.Wizard.StepOf, step, TOTAL_STEPS);

            // Update button states
            _noEndpointMode = false;
            _waitingForDetection = false;
            _btnBack.Visible = step > 1;
            _btnNext.Text = step == TOTAL_STEPS ? UIStrings.Wizard.Finish : UIStrings.Wizard.Next;
            _btnNext.Enabled = true;

            switch (step)
            {
                case 1:
                    ShowWelcomePage();
                    break;
                case 2:
                    ShowProviderConfigPage();
                    break;
                case 3:
                    ShowDatevPage();
                    break;
                case 4:
                    ShowFinishPage();
                    break;
            }
        }

        // ========== STEP 1: WELCOME ==========

        private void ShowWelcomePage()
        {
            int y = LayoutConstants.SpaceLG;

            var lblTitle = new Label
            {
                Text = UIStrings.Wizard.Welcome,
                Font = UITheme.FontLarge,
                ForeColor = UITheme.AccentDatev,
                Location = new Point(LayoutConstants.SpaceLG, y),
                AutoSize = true
            };
            _contentPanel.Controls.Add(lblTitle);
            y += 40;

            var lblWelcome = new Label
            {
                Text = UIStrings.Wizard.WelcomeText,
                Font = UITheme.FontBody,
                ForeColor = UITheme.TextPrimary,
                Location = new Point(LayoutConstants.SpaceLG, y),
                Size = new Size(ClientSize.Width - (LayoutConstants.SpaceLG * 2), 60)
            };
            _contentPanel.Controls.Add(lblWelcome);
            y += 70;

            // Feature list based on detected mode
            var detectedMode = _bridgeService?.SelectedConnectionMode ?? ConnectionMode.Auto;
            string providerFeature;
            switch (detectedMode)
            {
                case ConnectionMode.WebClient:
                    providerFeature = UIStrings.Wizard.FeatureWebclient;
                    break;
                case ConnectionMode.TerminalServer:
                    providerFeature = UIStrings.Wizard.FeaturePipe;
                    break;
                default:
                    providerFeature = SessionManager.IsTerminalSession ? UIStrings.Wizard.FeaturePipe : UIStrings.Wizard.FeatureTapi;
                    break;
            }

            var features = new[]
            {
                providerFeature,
                UIStrings.Wizard.FeatureDatev,
                UIStrings.Wizard.FeatureAutostart
            };

            foreach (var feature in features)
            {
                var lblFeature = new Label
                {
                    Text = $"  {feature}",
                    Font = UITheme.FontBody,
                    ForeColor = UITheme.TextSecondary,
                    Location = new Point(LayoutConstants.SpaceLG + 16, y),
                    AutoSize = true
                };
                _contentPanel.Controls.Add(lblFeature);
                y += 24;
            }

            // Show detected environment
            y += 10;
            string envLabel = GetEnvironmentLabel(detectedMode);
            var lblEnv = new Label
            {
                Text = string.Format(UIStrings.Troubleshooting.DetectedEnvironmentFormat, envLabel),
                Font = UITheme.FontItalic,
                ForeColor = UITheme.TextMuted,
                Location = new Point(LayoutConstants.SpaceLG, y),
                AutoSize = true
            };
            _contentPanel.Controls.Add(lblEnv);
        }

        // ========== STEP 2: PROVIDER CONFIG (AUTO-DETECTED) ==========

        private void ShowProviderConfigPage()
        {
            var detectedMode = _bridgeService?.SelectedConnectionMode ?? ConnectionMode.Auto;

            switch (detectedMode)
            {
                case ConnectionMode.WebClient:
                    ShowWebclientPage();
                    break;
                case ConnectionMode.TerminalServer:
                    ShowPipePage();
                    break;
                case ConnectionMode.Desktop:
                    ShowTapiPage();
                    break;
                default:
                    // No provider detected yet — show searching page and wait for service to detect one
                    ShowSearchingPage();
                    _btnNext.Enabled = false;
                    _waitingForDetection = true;
                    break;
            }
        }

        private void ShowSearchingPage()
        {
            _contentPanel.Controls.Clear();
            AddStepTitle("3CX Verbindung", UITheme.AccentIncoming);

            _lblTapiStatus = new Label
            {
                Text = "Suche nach 3CX Endpunkt...",
                Font = UITheme.FontMedium,
                ForeColor = UITheme.TextSecondary,
                Location = new Point(LayoutConstants.SpaceLG, StepStatusY),
                Size = new Size(ContentWidth, 30)
            };
            _contentPanel.Controls.Add(_lblTapiStatus);
        }

        private bool _noEndpointMode;
        private bool _waitingForDetection;

        private void ShowNoEndpointPage()
        {
            _noEndpointMode = true;
            _btnNext.Text = UIStrings.Wizard.NoEndpointRetry;
            _btnNext.Enabled = true;

            AddStepTitle(UIStrings.Wizard.NoEndpointTitle, UITheme.StatusBad);

            var lblDesc = new Label
            {
                Text = UIStrings.Wizard.NoEndpointDesc,
                Font = UITheme.FontMedium,
                ForeColor = UITheme.TextPrimary,
                Location = new Point(LayoutConstants.SpaceLG, StepStatusY),
                Size = new Size(ContentWidth, 60)
            };
            _contentPanel.Controls.Add(lblDesc);

            int optionY = StepDetailY + 20;

            var lblOption1 = new Label
            {
                Text = "\u2022 " + UIStrings.Wizard.NoEndpointOption1,
                Font = UITheme.FontBody,
                ForeColor = UITheme.TextSecondary,
                Location = new Point(LayoutConstants.SpaceLG + 16, optionY),
                AutoSize = true
            };
            _contentPanel.Controls.Add(lblOption1);

            var lblOption2 = new Label
            {
                Text = "\u2022 " + UIStrings.Wizard.NoEndpointOption2,
                Font = UITheme.FontBody,
                ForeColor = UITheme.TextSecondary,
                Location = new Point(LayoutConstants.SpaceLG + 16, optionY + 24),
                AutoSize = true
            };
            _contentPanel.Controls.Add(lblOption2);
        }

        // ========== UNIFIED PROVIDER / DATEV PAGES ==========

        // Shared layout constants for Step 2 and Step 3
        private const int StepTitleY = 30;
        private const int StepStatusY = 85;
        private const int StepDetailY = 135;
        private int ContentWidth => ClientSize.Width - (LayoutConstants.SpaceLG * 2);

        private void ShowTapiPage()
        {
            if (SessionManager.IsTerminalSession)
            {
                ShowPipePage();
                return;
            }

            bool connected = _bridgeService?.TapiConnected ?? false;
            string ext = _bridgeService?.Extension ?? "";

            AddStepTitle(UIStrings.Wizard.TapiConfig, UITheme.AccentIncoming);

            if (connected)
            {
                string statusText = !string.IsNullOrEmpty(ext)
                    ? string.Format(UIStrings.Status.ConnectedExtension, ext)
                    : UIStrings.Status.Connected;
                AddStepStatus(statusText, UITheme.StatusOk);
                AddStepDetail(ConnectionMethodSelector.GetModeDescription(ConnectionMode.Desktop));
            }
            else
            {
                AddStepStatus(UIStrings.Wizard.TapiSelectLine, UITheme.TextSecondary);

                _cboTapiLine = new ComboBox
                {
                    Location = new Point(LayoutConstants.SpaceLG, StepDetailY),
                    Size = new Size(ContentWidth, 28),
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = UITheme.InputBackground,
                    ForeColor = UITheme.TextPrimary,
                    FlatStyle = FlatStyle.Flat
                };
                _contentPanel.Controls.Add(_cboTapiLine);

                _lblTapiStatus = new Label
                {
                    Text = "",
                    Font = UITheme.FontMedium,
                    ForeColor = UITheme.TextSecondary,
                    Location = new Point(LayoutConstants.SpaceLG, StepDetailY + 40),
                    Size = new Size(ContentWidth, 60)
                };
                _contentPanel.Controls.Add(_lblTapiStatus);

                LoadTapiLines();
            }
        }

        private void ShowPipePage()
        {
            bool connected = _bridgeService?.TapiConnected ?? false;
            string ext = _bridgeService?.Extension ?? "";

            AddStepTitle(UIStrings.Wizard.PipeConfig, UITheme.AccentIncoming);

            if (connected)
            {
                string statusText = !string.IsNullOrEmpty(ext)
                    ? string.Format(UIStrings.Status.ConnectedExtension, ext)
                    : UIStrings.Status.Connected;
                AddStepStatus(statusText, UITheme.StatusOk);
                AddStepDetail(ConnectionMethodSelector.GetModeDescription(ConnectionMode.TerminalServer));
            }
            else
            {
                AddStepStatus(UIStrings.Wizard.PipeWaiting, UITheme.StatusWarn);

                _lblTapiStatus = new Label
                {
                    Font = UITheme.FontMedium,
                    ForeColor = UITheme.StatusWarn,
                    Text = UIStrings.Wizard.PipeWaiting,
                    Location = new Point(LayoutConstants.SpaceLG, StepStatusY),
                    Size = new Size(ContentWidth, 30)
                };
                _contentPanel.Controls.Add(_lblTapiStatus);
            }
        }

        private void ShowWebclientPage()
        {
            bool connected = _bridgeService?.TapiConnected ?? false;
            var activeMode = _bridgeService?.SelectedConnectionMode ?? ConnectionMode.Auto;
            string ext = _bridgeService?.Extension ?? "";

            AddStepTitle(UIStrings.Wizard.WebclientConfig, UITheme.AccentIncoming);

            if (activeMode == ConnectionMode.WebClient && connected)
            {
                string statusText = !string.IsNullOrEmpty(ext)
                    ? string.Format(UIStrings.Status.ConnectedExtension, ext)
                    : UIStrings.Wizard.WebclientConnected;
                AddStepStatus(statusText, UITheme.StatusOk);
                AddStepDetail(ConnectionMethodSelector.GetModeDescription(ConnectionMode.WebClient));
            }
            else
            {
                AddStepStatus(UIStrings.Wizard.WebclientWaiting, UITheme.StatusWarn);

                _lblTapiStatus = new Label
                {
                    Font = UITheme.FontMedium,
                    ForeColor = UITheme.StatusWarn,
                    Text = UIStrings.Wizard.WebclientWaiting,
                    Location = new Point(LayoutConstants.SpaceLG, StepStatusY),
                    Size = new Size(ContentWidth, 30)
                };
                _contentPanel.Controls.Add(_lblTapiStatus);
            }
        }

        private void LoadTapiLines()
        {
            _cboTapiLine.Items.Clear();

            var tapiLines = _bridgeService?.TapiLines;
            if (tapiLines != null && tapiLines.Count > 0)
            {
                foreach (var line in tapiLines)
                {
                    string status = line.IsConnected ? UIStrings.Status.Connected : UIStrings.Status.Disconnected;
                    _cboTapiLine.Items.Add($"{line.Extension} - {line.LineName} ({status})");
                }
                _cboTapiLine.SelectedIndex = 0;
                _lblTapiStatus.Text = string.Format(UIStrings.Status.LinesConnected,
                    tapiLines.Count, tapiLines.Count);
                _lblTapiStatus.ForeColor = UITheme.StatusOk;
            }
            else
            {
                _cboTapiLine.Items.Add(UIStrings.Errors.NoTapiLines);
                _cboTapiLine.SelectedIndex = 0;
                _cboTapiLine.Enabled = false;
                _lblTapiStatus.Text = UIStrings.Troubleshooting.TapiNotConnectedDesc;
                _lblTapiStatus.ForeColor = UITheme.StatusBad;
            }
        }

        // ========== STEP 3: DATEV ==========

        private void ShowDatevPage()
        {
            AddStepTitle(UIStrings.Wizard.DatevConnection, UITheme.AccentDatev);

            _lblDatevStatus = new Label
            {
                Text = UIStrings.Wizard.DatevTesting,
                Font = UITheme.FontMedium,
                ForeColor = UITheme.TextSecondary,
                Location = new Point(LayoutConstants.SpaceLG, StepStatusY),
                Size = new Size(ContentWidth, 120)
            };
            _contentPanel.Controls.Add(_lblDatevStatus);

            TestDatevConnection();
        }

        private async void TestDatevConnection()
        {
            _btnNext.Enabled = false;
            _datevOk = false;

            try
            {
                await Task.Run(() =>
                {
                    _datevOk = DatevConnectionChecker.CheckDatevAvailability();
                });

                SafeInvoke(() =>
                {
                    if (_datevOk)
                    {
                        _lblDatevStatus.Text = UIStrings.Status.Connected;
                        _lblDatevStatus.ForeColor = UITheme.StatusOk;

                        int contactCount = _bridgeService?.ContactCount ?? 0;
                        if (contactCount > 0)
                        {
                            _lblDatevStatus.Text += $"\n{string.Format(UIStrings.Messages.ContactsFormat, contactCount)}";
                        }
                    }
                    else
                    {
                        _lblDatevStatus.Text = $"{UIStrings.Status.Unavailable}\n\n{UIStrings.Troubleshooting.DatevNotReachableDesc}";
                        _lblDatevStatus.ForeColor = UITheme.StatusWarn;
                    }

                    _btnNext.Enabled = true;
                });
            }
            catch (Exception ex)
            {
                _datevOk = false;
                LogManager.Log("SetupWizard: DATEV Test fehlgeschlagen - {0}", ex.Message);
                SafeInvoke(() =>
                {
                    _lblDatevStatus.Text = $"{UIStrings.Errors.GenericError}: {ex.Message}";
                    _lblDatevStatus.ForeColor = UITheme.StatusBad;
                    _btnNext.Enabled = true;
                });
            }
        }

        // ========== SHARED STEP LAYOUT HELPERS ==========

        private void AddStepTitle(string text, Color color)
        {
            var lbl = new Label
            {
                Text = text,
                Font = UITheme.FontLarge,
                ForeColor = color,
                Location = new Point(LayoutConstants.SpaceLG, StepTitleY),
                AutoSize = true
            };
            _contentPanel.Controls.Add(lbl);
        }

        private void AddStepStatus(string text, Color color)
        {
            _lblTapiStatus = new Label
            {
                Text = text,
                Font = UITheme.FontMedium,
                ForeColor = color,
                Location = new Point(LayoutConstants.SpaceLG, StepStatusY),
                Size = new Size(ContentWidth, 30)
            };
            _contentPanel.Controls.Add(_lblTapiStatus);
        }

        private void AddStepDetail(string text)
        {
            var lbl = new Label
            {
                Text = text,
                Font = UITheme.FontBody,
                ForeColor = UITheme.TextMuted,
                Location = new Point(LayoutConstants.SpaceLG, StepDetailY),
                AutoSize = true
            };
            _contentPanel.Controls.Add(lbl);
        }

        // ========== STEP 4: FINISH ==========

        private void ShowFinishPage()
        {
            int y = LayoutConstants.SpaceLG;

            var lblTitle = new Label
            {
                Text = UIStrings.Wizard.Finish,
                Font = UITheme.FontLarge,
                ForeColor = UITheme.AccentDatev,
                Location = new Point(LayoutConstants.SpaceLG, y),
                AutoSize = true
            };
            _contentPanel.Controls.Add(lblTitle);
            y += 40;

            var lblFinish = new Label
            {
                Text = UIStrings.Wizard.FinishText,
                Font = UITheme.FontBody,
                ForeColor = UITheme.TextPrimary,
                Location = new Point(LayoutConstants.SpaceLG, y),
                Size = new Size(ClientSize.Width - (LayoutConstants.SpaceLG * 2), 40)
            };
            _contentPanel.Controls.Add(lblFinish);
            y += 50;

            // Autostart checkbox
            _chkAutoStart = new CheckBox
            {
                Text = UIStrings.Wizard.AutoStart,
                Font = UITheme.FontBody,
                ForeColor = UITheme.TextPrimary,
                Location = new Point(LayoutConstants.SpaceLG, y),
                AutoSize = true,
                Checked = IsAutoStartEnabled()
            };
            _contentPanel.Controls.Add(_chkAutoStart);
            y += 40;

            // Summary
            var summaryText = BuildSummary();
            var lblSummary = new Label
            {
                Text = summaryText,
                Font = UITheme.FontSmall,
                ForeColor = UITheme.TextMuted,
                Location = new Point(LayoutConstants.SpaceLG, y),
                Size = new Size(ClientSize.Width - (LayoutConstants.SpaceLG * 2), 100)
            };
            _contentPanel.Controls.Add(lblSummary);
        }

        private string BuildSummary()
        {
            var lines = new System.Text.StringBuilder();

            // Telephony mode (auto-detected)
            var activeMode = _bridgeService?.SelectedConnectionMode ?? ConnectionMode.Auto;
            lines.AppendLine(string.Format("Modus: {0}", ConnectionMethodSelector.GetModeDescription(activeMode)));

            // 3CX connection status
            bool connected = _bridgeService?.TapiConnected ?? false;

            switch (activeMode)
            {
                case ConnectionMode.WebClient:
                    lines.AppendLine(string.Format("WebClient: {0}", connected ? UIStrings.Status.Connected : UIStrings.Status.NotConnected));
                    break;
                case ConnectionMode.TerminalServer:
                    lines.AppendLine(string.Format("3CX Pipe: {0}", connected ? UIStrings.Status.Connected : UIStrings.Status.NotConnected));
                    break;
                default:
                    var tapiLines = _bridgeService?.TapiLines;
                    if (tapiLines != null && tapiLines.Count > 0)
                        lines.AppendLine(string.Format(UIStrings.Wizard.SummaryTapiConnected, tapiLines.Count));
                    else
                        lines.AppendLine(string.Format("3CX: {0}", UIStrings.Status.NotConnected));
                    break;
            }

            // DATEV status
            if (_datevOk)
            {
                int contactCount = _bridgeService?.ContactCount ?? 0;
                lines.AppendLine(string.Format(UIStrings.Wizard.SummaryDatevConnected, UIStrings.Status.Connected, contactCount));
            }
            else
            {
                lines.AppendLine(string.Format("DATEV: {0}", UIStrings.Status.NotConnected));
            }

            return lines.ToString();
        }

        // ========== LIVE STATUS UPDATES ==========

        private void OnConnectorStatusChanged(ConnectorStatus status)
        {
            if (_currentStep != 2) return;

            BeginInvoke(new Action(() =>
            {
                if (IsDisposed) return;

                var mode = _bridgeService?.SelectedConnectionMode ?? ConnectionMode.Auto;
                bool connected = status == ConnectorStatus.Connected;

                // If we're waiting for detection (searching or no-endpoint page) and the service found a provider,
                // switch to the appropriate provider config page
                if ((_waitingForDetection || _noEndpointMode) && connected && mode != ConnectionMode.Auto)
                {
                    _waitingForDetection = false;
                    _noEndpointMode = false;
                    _contentPanel.Controls.Clear();
                    _btnNext.Text = UIStrings.Wizard.Next;
                    _btnNext.Enabled = true;

                    switch (mode)
                    {
                        case ConnectionMode.WebClient:
                            ShowWebclientPage();
                            break;
                        case ConnectionMode.TerminalServer:
                            ShowPipePage();
                            break;
                        case ConnectionMode.Desktop:
                            ShowTapiPage();
                            break;
                    }
                    return;
                }

                // If we're waiting and still no endpoint after a Disconnected status,
                // switch from searching page to no-endpoint page
                if (_waitingForDetection && status == ConnectorStatus.Disconnected)
                {
                    _waitingForDetection = false;
                    _contentPanel.Controls.Clear();
                    ShowNoEndpointPage();
                    return;
                }

                // Normal live-update of status label on an existing provider page
                if (_lblTapiStatus == null || _lblTapiStatus.IsDisposed) return;

                string ext = _bridgeService?.Extension;

                if (connected && !string.IsNullOrEmpty(ext))
                {
                    _lblTapiStatus.Text = string.Format(UIStrings.Status.ConnectedExtension, ext);
                    _lblTapiStatus.ForeColor = UITheme.StatusOk;
                }
                else if (connected)
                {
                    switch (mode)
                    {
                        case ConnectionMode.WebClient:
                            _lblTapiStatus.Text = UIStrings.Wizard.WebclientConnected;
                            break;
                        case ConnectionMode.TerminalServer:
                            _lblTapiStatus.Text = UIStrings.Wizard.PipeConnected;
                            break;
                        default:
                            _lblTapiStatus.Text = UIStrings.Status.Connected;
                            break;
                    }
                    _lblTapiStatus.ForeColor = UITheme.StatusOk;
                }
                else if (status == ConnectorStatus.Connecting)
                {
                    _lblTapiStatus.Text = UIStrings.Messages.ConnectingTapi;
                    _lblTapiStatus.ForeColor = UITheme.StatusWarn;
                }
            }));
        }

        // ========== NAVIGATION ==========

        private void BtnBack_Click(object sender, EventArgs e)
        {
            if (_currentStep > 1)
                ShowStep(_currentStep - 1);
        }

        private void BtnNext_Click(object sender, EventArgs e)
        {
            // On the no-endpoint page, show searching page and wait for service detection
            if (_noEndpointMode && _currentStep == 2)
            {
                _noEndpointMode = false;
                _contentPanel.Controls.Clear();
                ShowSearchingPage();
                _btnNext.Enabled = false;
                _waitingForDetection = true;
                return;
            }

            if (_currentStep < TOTAL_STEPS)
            {
                ShowStep(_currentStep + 1);
            }
            else
            {
                // Finish - save settings and close
                ApplySettings();
                Close();
            }
        }

        private void ApplySettings()
        {
            try
            {
                // Set autostart based on checkbox
                if (_chkAutoStart != null)
                {
                    SetAutoStart(_chkAutoStart.Checked);
                }

                // Always use Auto mode (auto-detection)
                AppConfig.Set(ConfigKeys.TelephonyMode, ConnectionMode.Auto.ToString());
                LogManager.Log("SetupWizard: ConnectionMode auf Auto gesetzt");

                LogManager.Log("SetupWizard: Konfiguration abgeschlossen");
            }
            catch (Exception ex)
            {
                LogManager.Log("SetupWizard: Fehler beim Anwenden der Einstellungen - {0}", ex.Message);
            }
        }

        // ========== HELPERS ==========

        private static string GetEnvironmentLabel(ConnectionMode mode)
        {
            switch (mode)
            {
                case ConnectionMode.Desktop: return UIStrings.Troubleshooting.EnvDesktopTapi;
                case ConnectionMode.TerminalServer: return UIStrings.Troubleshooting.EnvTerminalServer;
                case ConnectionMode.WebClient: return UIStrings.Troubleshooting.EnvWebClient;
                default: return UIStrings.Troubleshooting.EnvAuto;
            }
        }

        private bool IsAutoStartEnabled() => AutoStartManager.IsEnabled();

        private void SetAutoStart(bool enable) => AutoStartManager.SetEnabled(enable);

        private static SetupWizardForm _current;

        public static void ShowWizard(ConnectorService bridgeService = null)
        {
            if (_current != null && !_current.IsDisposed)
            {
                _current.Activate();
                _current.BringToFront();
                return;
            }

            var wizard = new SetupWizardForm(bridgeService);
            FormClosedEventHandler handler = null;
            handler = (s, e) =>
            {
                ((Form)s).FormClosed -= handler;
                if (_current == wizard) _current = null;
                ((Form)s).Dispose();
            };
            wizard.FormClosed += handler;
            _current = wizard;
            wizard.Show();
        }
    }
}
