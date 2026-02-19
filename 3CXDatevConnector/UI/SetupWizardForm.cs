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
            _contentPanel.Controls.Clear();
            _lblStepIndicator.Text = string.Format(UIStrings.Wizard.StepOf, step, TOTAL_STEPS);

            // Update button states
            _btnBack.Visible = step > 1;
            _btnNext.Text = step == TOTAL_STEPS ? UIStrings.Wizard.Finish : UIStrings.Wizard.Next;

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
                    // Auto mode: pick based on environment
                    if (SessionManager.IsTerminalSession)
                        ShowPipePage();
                    else
                        ShowTapiPage();
                    break;
            }
        }

        // ========== TAPI / PIPE / WEBCLIENT CONFIG ==========

        private void ShowTapiPage()
        {
            if (SessionManager.IsTerminalSession)
            {
                ShowPipePage();
                return;
            }

            int y = LayoutConstants.SpaceLG;

            var lblTitle = new Label
            {
                Text = UIStrings.Wizard.TapiConfig,
                Font = UITheme.FontLarge,
                ForeColor = UITheme.AccentIncoming,
                Location = new Point(LayoutConstants.SpaceLG, y),
                AutoSize = true
            };
            _contentPanel.Controls.Add(lblTitle);
            y += 40;

            var lblDesc = new Label
            {
                Text = UIStrings.Wizard.TapiSelectLine,
                Font = UITheme.FontBody,
                ForeColor = UITheme.TextPrimary,
                Location = new Point(LayoutConstants.SpaceLG, y),
                AutoSize = true
            };
            _contentPanel.Controls.Add(lblDesc);
            y += 30;

            _cboTapiLine = new ComboBox
            {
                Location = new Point(LayoutConstants.SpaceLG, y),
                Size = new Size(ClientSize.Width - (LayoutConstants.SpaceLG * 2), 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = UITheme.InputBackground,
                ForeColor = UITheme.TextPrimary,
                FlatStyle = FlatStyle.Flat
            };
            _contentPanel.Controls.Add(_cboTapiLine);
            y += 40;

            _lblTapiStatus = new Label
            {
                Text = "",
                Font = UITheme.FontBody,
                ForeColor = UITheme.TextSecondary,
                Location = new Point(LayoutConstants.SpaceLG, y),
                Size = new Size(ClientSize.Width - (LayoutConstants.SpaceLG * 2), 60)
            };
            _contentPanel.Controls.Add(_lblTapiStatus);

            // Load TAPI lines
            LoadTapiLines();
        }

        private void ShowPipePage()
        {
            int y = LayoutConstants.SpaceLG;

            var lblTitle = new Label
            {
                Text = UIStrings.Wizard.PipeConfig,
                Font = UITheme.FontLarge,
                ForeColor = UITheme.AccentIncoming,
                Location = new Point(LayoutConstants.SpaceLG, y),
                AutoSize = true
            };
            _contentPanel.Controls.Add(lblTitle);
            y += 40;

            // Extension
            string ext = _bridgeService?.Extension ?? "(auto)";
            var lblExtension = new Label
            {
                Text = string.Format(UIStrings.Wizard.PipeExtension, ext),
                Font = UITheme.FontBody,
                ForeColor = UITheme.TextPrimary,
                Location = new Point(LayoutConstants.SpaceLG, y),
                AutoSize = true
            };
            _contentPanel.Controls.Add(lblExtension);
            y += 28;

            // Pipe name
            var lblPipeName = new Label
            {
                Text = string.Format(UIStrings.Wizard.PipeName, ext),
                Font = UITheme.FontSmall,
                ForeColor = UITheme.TextMuted,
                Location = new Point(LayoutConstants.SpaceLG, y),
                AutoSize = true
            };
            _contentPanel.Controls.Add(lblPipeName);
            y += 35;

            // Pipe status
            _lblTapiStatus = new Label
            {
                Text = UIStrings.Wizard.PipeStatus,
                Font = UITheme.FontBody,
                ForeColor = UITheme.TextSecondary,
                Location = new Point(LayoutConstants.SpaceLG, y),
                Size = new Size(ClientSize.Width - (LayoutConstants.SpaceLG * 2), 28)
            };
            _contentPanel.Controls.Add(_lblTapiStatus);
            y += 30;

            // Check pipe and 3CX Softphone status
            bool pipeConnected = _bridgeService?.TapiConnected ?? false;
            bool softphoneRunning = SessionManager.Is3CXProcessRunning();

            var lblPipeStatus = new Label
            {
                Font = UITheme.FontBody,
                Location = new Point(LayoutConstants.SpaceLG + 16, y),
                Size = new Size(ClientSize.Width - (LayoutConstants.SpaceLG * 2) - 16, 28)
            };

            if (pipeConnected)
            {
                lblPipeStatus.Text = UIStrings.Wizard.PipeConnected;
                lblPipeStatus.ForeColor = UITheme.StatusOk;
            }
            else
            {
                lblPipeStatus.Text = UIStrings.Wizard.PipeWaiting;
                lblPipeStatus.ForeColor = UITheme.StatusWarn;
            }
            _contentPanel.Controls.Add(lblPipeStatus);
            y += 28;

            var lblSoftphone = new Label
            {
                Font = UITheme.FontBody,
                Location = new Point(LayoutConstants.SpaceLG + 16, y),
                Size = new Size(ClientSize.Width - (LayoutConstants.SpaceLG * 2) - 16, 28)
            };

            if (softphoneRunning)
            {
                lblSoftphone.Text = UIStrings.Wizard.Softphone3CXRunning;
                lblSoftphone.ForeColor = UITheme.StatusOk;
            }
            else
            {
                lblSoftphone.Text = UIStrings.Wizard.Softphone3CXNotRunning;
                lblSoftphone.ForeColor = UITheme.StatusBad;
            }
            _contentPanel.Controls.Add(lblSoftphone);
        }

        private void ShowWebclientPage()
        {
            int y = LayoutConstants.SpaceLG;

            var lblTitle = new Label
            {
                Text = UIStrings.Wizard.WebclientConfig,
                Font = UITheme.FontLarge,
                ForeColor = UITheme.AccentIncoming,
                Location = new Point(LayoutConstants.SpaceLG, y),
                AutoSize = true
            };
            _contentPanel.Controls.Add(lblTitle);
            y += 35;

            // Description
            var lblDesc = new Label
            {
                Text = UIStrings.Wizard.WebclientDesc,
                Font = UITheme.FontBody,
                ForeColor = UITheme.TextPrimary,
                Location = new Point(LayoutConstants.SpaceLG, y),
                Size = new Size(ClientSize.Width - (LayoutConstants.SpaceLG * 2), 40)
            };
            _contentPanel.Controls.Add(lblDesc);
            y += 48;

            // Install steps
            var lblSteps = new Label
            {
                Text = UIStrings.Wizard.WebclientInstallSteps,
                Font = UITheme.FontBody,
                ForeColor = UITheme.TextSecondary,
                Location = new Point(LayoutConstants.SpaceLG, y),
                Size = new Size(ClientSize.Width - (LayoutConstants.SpaceLG * 2), 90)
            };
            _contentPanel.Controls.Add(lblSteps);
            y += 95;

            // Connection status
            bool webclientConnected = _bridgeService?.TapiConnected ?? false;
            var activeMode = _bridgeService?.SelectedConnectionMode ?? ConnectionMode.Auto;

            _lblTapiStatus = new Label
            {
                Font = UITheme.FontBody,
                Location = new Point(LayoutConstants.SpaceLG, y),
                Size = new Size(ClientSize.Width - (LayoutConstants.SpaceLG * 2), 28)
            };

            if (activeMode == ConnectionMode.WebClient && webclientConnected)
            {
                _lblTapiStatus.Text = UIStrings.Wizard.WebclientConnected;
                _lblTapiStatus.ForeColor = UITheme.StatusOk;
            }
            else
            {
                _lblTapiStatus.Text = UIStrings.Wizard.WebclientWaiting;
                _lblTapiStatus.ForeColor = UITheme.StatusWarn;
            }
            _contentPanel.Controls.Add(_lblTapiStatus);
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
            int y = LayoutConstants.SpaceLG;

            var lblTitle = new Label
            {
                Text = UIStrings.Wizard.DatevConnection,
                Font = UITheme.FontLarge,
                ForeColor = UITheme.AccentDatev,
                Location = new Point(LayoutConstants.SpaceLG, y),
                AutoSize = true
            };
            _contentPanel.Controls.Add(lblTitle);
            y += 40;

            _lblDatevStatus = new Label
            {
                Text = UIStrings.Wizard.DatevTesting,
                Font = UITheme.FontBody,
                ForeColor = UITheme.TextSecondary,
                Location = new Point(LayoutConstants.SpaceLG, y),
                Size = new Size(ClientSize.Width - (LayoutConstants.SpaceLG * 2), 80)
            };
            _contentPanel.Controls.Add(_lblDatevStatus);

            // Start DATEV test
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

                if (_datevOk)
                {
                    _lblDatevStatus.Text = $"{UIStrings.Status.Connected}\n\n{UIStrings.Status.Available}";
                    _lblDatevStatus.ForeColor = UITheme.StatusOk;

                    // Also show contact count if available
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
            }
            catch (Exception ex)
            {
                _datevOk = false;
                _lblDatevStatus.Text = $"{UIStrings.Errors.GenericError}: {ex.Message}";
                _lblDatevStatus.ForeColor = UITheme.StatusBad;
                LogManager.Log("SetupWizard: DATEV Test fehlgeschlagen - {0}", ex.Message);
            }

            _btnNext.Enabled = true;
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

        // ========== NAVIGATION ==========

        private void BtnBack_Click(object sender, EventArgs e)
        {
            if (_currentStep > 1)
                ShowStep(_currentStep - 1);
        }

        private void BtnNext_Click(object sender, EventArgs e)
        {
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
