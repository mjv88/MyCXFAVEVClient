using System;
using System.Drawing;
using System.Windows.Forms;
using DatevConnector.Datev.Managers;
using DatevConnector.Datev.PluginData;
using DatevConnector.UI.Strings;
using DatevConnector.UI.Theme;

namespace DatevConnector.UI
{
    /// <summary>
    /// Controls which popup types are shown for caller notifications
    /// </summary>
    public enum CallerPopupMode
    {
            Both,
        Form,
        Balloon
    }

    /// <summary>
    /// Popup form showing caller information for incoming/outgoing calls.
    /// Uses a proper Windows Form (dark themed, centered) plus a system tray balloon notification.
    /// Auto-closes after configured duration or when the call is answered.
    /// </summary>
    public class CallerPopupForm : ThemedForm
    {
        // Track the current open popup for closing on connect
        private static CallerPopupForm _currentPopup;
        private static readonly object _popupLock = new object();

        // Fade animation
        private Timer _fadeTimer;
        private bool _fadingOut;

        // System tray icon reference for balloon notifications
        private static NotifyIcon _notifyIcon;

        public static void SetNotifyIcon(NotifyIcon notifyIcon)
        {
            _notifyIcon = notifyIcon;
        }

        protected override void ApplyTheme()
        {
            base.ApplyTheme();
            TopMost = true;
            ShowInTaskbar = true;
        }

        public CallerPopupForm(
            string callerNumber,
            string callerName,
            DatevContactInfo contactInfo,
            bool isIncoming)
        {
            bool isDatevContact = contactInfo?.DatevContact != null;
            string displayName = GetDisplayName(callerName, contactInfo);
            string contactDesc = GetContactDescription(contactInfo);
            Color accentColor = UITheme.GetDirectionColor(isIncoming);

            // ThemedForm handles: BackColor, ForeColor, FormBorderStyle, etc.
            Text = UIStrings.FormTitles.AppTitle;
            Size = new Size(380, 180);

            // Accent bar at top
            var accentBar = UITheme.CreateAccentBar(accentColor);

            // Direction label
            var lblDirection = new Label
            {
                Text = isIncoming ? UIStrings.FormTitles.CallerPopupIncomingUpper : UIStrings.FormTitles.CallerPopupOutgoingUpper,
                ForeColor = accentColor,
                Font = UITheme.FontLabel,
                Location = new Point(LayoutConstants.SpaceLG, 14),
                AutoSize = true
            };

            // Caller name (large)
            var lblName = new Label
            {
                Text = displayName,
                ForeColor = UITheme.TextPrimary,
                Font = UITheme.FontLarge,
                Location = new Point(LayoutConstants.SpaceLG, 38),
                Size = new Size(340, 30),
                AutoEllipsis = true
            };

            // Phone number
            var lblNumber = new Label
            {
                Text = FormatPhoneNumber(callerNumber),
                ForeColor = UITheme.TextSecondary,
                Font = UITheme.FontMedium,
                Location = new Point(LayoutConstants.SpaceLG, 72),
                Size = new Size(340, 24),
                AutoEllipsis = true
            };

            // Contact type / data source
            var lblContact = new Label
            {
                Text = contactDesc,
                ForeColor = isDatevContact ? UITheme.TextAccentInfo : UITheme.TextMuted,
                Font = UITheme.FontItalic,
                Location = new Point(LayoutConstants.SpaceLG, 100),
                Size = new Size(340, 20)
            };

            Controls.AddRange(new Control[]
            {
                accentBar, lblDirection, lblName, lblNumber,
                lblContact
            });
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Fade in: animate Opacity from 0 to 1
            Opacity = 0;
            _fadingOut = false;
            _fadeTimer = new Timer { Interval = 15 };
            _fadeTimer.Tick += (s, ev) =>
            {
                if (_fadingOut) return;
                Opacity += 0.08;
                if (Opacity >= 1.0)
                {
                    Opacity = 1.0;
                    _fadeTimer.Stop();
                }
            };
            _fadeTimer.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_fadingOut && Opacity > 0)
            {
                // Start fade out
                e.Cancel = true;
                _fadingOut = true;
                var fadeOutTimer = new Timer { Interval = 15 };
                fadeOutTimer.Tick += (s, ev) =>
                {
                    Opacity -= 0.08;
                    if (Opacity <= 0)
                    {
                        Opacity = 0;
                        fadeOutTimer.Stop();
                        fadeOutTimer.Dispose();
                        _fadingOut = false;
                        Close();
                    }
                };
                fadeOutTimer.Start();
                return;
            }

            _fadeTimer?.Stop();
            _fadeTimer?.Dispose();
            base.OnFormClosing(e);
        }

        private static string GetDisplayName(string callerName, DatevContactInfo contactInfo)
        {
            // DATEV contact name takes priority (if valid)
            if (contactInfo?.DatevContact != null && IsValidDisplayName(contactInfo.DatevContact.Name))
                return contactInfo.DatevContact.Name;

            // Fall back to TAPI/3CX caller name (if valid)
            if (IsValidDisplayName(callerName))
                return callerName;

            return UIStrings.CallerPopup.UnknownCaller;
        }

        private static bool IsValidDisplayName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            // Filter out placeholder values that TAPI/3CX may send
            string trimmed = name.Trim();
            if (trimmed == "." || trimmed == "-" || trimmed == "?" || trimmed == "*")
                return false;

            return true;
        }

        private static string GetContactDescription(DatevContactInfo contactInfo)
        {
            if (contactInfo?.DatevContact == null)
                return UIStrings.CallerPopup.NotInDatev;

            return contactInfo.DatevContact.IsRecipient ? UIStrings.CallerPopup.Recipient : UIStrings.CallerPopup.Institution;
        }

        private static string FormatPhoneNumber(string number)
        {
            return string.IsNullOrWhiteSpace(number) ? UIStrings.CallerPopup.UnknownNumber : number;
        }

        /// <summary>
        /// Shows caller notification based on the configured popup mode.
        /// Popup stays open until dismissed or closed via CloseCurrentPopup().
        /// </summary>
        public static void ShowPopup(
            string callerNumber,
            string callerName,
            DatevContactInfo contactInfo,
            bool isIncoming,
            CallerPopupMode mode = CallerPopupMode.Form,
            string extension = null)
        {
            try
            {
                // Show balloon notification if mode allows
                if (mode == CallerPopupMode.Both || mode == CallerPopupMode.Balloon)
                {
                    ShowBalloonNotification(callerNumber, callerName, contactInfo, isIncoming, extension);
                }

                // Show form if mode allows
                if (mode == CallerPopupMode.Both || mode == CallerPopupMode.Form)
                {
                    ShowFormOnUIThread(callerNumber, callerName, contactInfo, isIncoming);
                }
            }
            catch (Exception ex)
            {
                LogManager.Log("CallerPopup: Fehler beim Anzeigen des Popups - {0}", ex.Message);
            }
        }

        private static void ShowFormOnUIThread(
            string callerNumber,
            string callerName,
            DatevContactInfo contactInfo,
            bool isIncoming)
        {
            FormDisplayHelper.PostToUIThread(() =>
                ShowPopupInternal(callerNumber, callerName, contactInfo, isIncoming));
        }

        public static void CloseCurrentPopup()
        {
            try
            {
                CallerPopupForm popupToClose;
                lock (_popupLock)
                {
                    popupToClose = _currentPopup;
                    if (popupToClose == null || popupToClose.IsDisposed)
                        return;
                    _currentPopup = null;
                }

                FormDisplayHelper.PostToUIThread(() =>
                {
                    if (!popupToClose.IsDisposed)
                    {
                        popupToClose.Close();
                        popupToClose.Dispose();
                    }
                });
            }
            catch (Exception ex)
            {
                LogManager.Log("CallerPopup: Fehler beim Schließen des Popups - {0}", ex.Message);
            }
        }

        private static void ShowBalloonNotification(
            string callerNumber,
            string callerName,
            DatevContactInfo contactInfo,
            bool isIncoming,
            string extension = null)
        {
            if (_notifyIcon == null)
            {
                LogManager.Debug("CallerPopup: Benachrichtigung übersprungen - NotifyIcon ist null");
                return;
            }

            Action showBalloon = () =>
            {
                try
                {
                    string baseTitle = isIncoming ? UIStrings.FormTitles.CallerPopupIncoming : UIStrings.FormTitles.CallerPopupOutgoing;
                    string title = string.IsNullOrEmpty(extension) ? baseTitle : $"{baseTitle} {string.Format(UIStrings.CallerPopup.ExtensionFormat, extension)}";
                    string displayName = GetDisplayName(callerName, contactInfo);
                    string body = $"{displayName}\n{FormatPhoneNumber(callerNumber)}";
                    ToolTipIcon icon = isIncoming ? ToolTipIcon.Info : ToolTipIcon.None;

                    LogManager.Debug("CallerPopup: Showing balloon - Title={0}, Name={1}, Number={2}", title, displayName, LogManager.Mask(callerNumber));
                    _notifyIcon.ShowBalloonTip(3000, title, body, icon);
                }
                catch (Exception ex)
                {
                    LogManager.Log("CallerPopup: Balloon-Benachrichtigung fehlgeschlagen - {0}", ex.Message);
                }
            };

            // ShowBalloonTip must be called on the UI thread
            FormDisplayHelper.PostToUIThread(showBalloon);
        }

        private static void ShowPopupInternal(
            string callerNumber,
            string callerName,
            DatevContactInfo contactInfo,
            bool isIncoming)
        {
            // Close and dispose any existing popup first
            CallerPopupForm oldPopup;
            lock (_popupLock)
            {
                oldPopup = _currentPopup;
                _currentPopup = null;
            }
            if (oldPopup != null && !oldPopup.IsDisposed)
            {
                oldPopup.Close();
                oldPopup.Dispose();
            }

            string direction = isIncoming ? "Incoming" : "Outgoing";
            string contactName = contactInfo?.DatevContact?.Name ?? callerName ?? "Unknown";
            LogManager.Debug("CallerPopup: Showing {0} call popup - Number={1}, Name={2}",
                direction, LogManager.Mask(callerNumber), contactName);

            var popup = new CallerPopupForm(callerNumber, callerName, contactInfo, isIncoming);
            FormClosedEventHandler handler = null;
            handler = (s, e) =>
            {
                ((Form)s).FormClosed -= handler;
                lock (_popupLock)
                {
                    if (_currentPopup == popup)
                        _currentPopup = null;
                }
                ((Form)s).Dispose();
            };
            popup.FormClosed += handler;
            lock (_popupLock)
            {
                _currentPopup = popup;
            }
            popup.Show();
        }
    }
}
