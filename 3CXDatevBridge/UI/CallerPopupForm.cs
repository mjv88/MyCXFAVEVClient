using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using DatevBridge.Datev.Managers;
using DatevBridge.Datev.PluginData;
using DatevBridge.UI.Strings;
using DatevBridge.UI.Theme;

namespace DatevBridge.UI
{
    /// <summary>
    /// Controls which popup types are shown for caller notifications
    /// </summary>
    public enum CallerPopupMode
    {
        /// <summary>Both balloon notification and form dialog</summary>
        Both,
        /// <summary>Only the Windows Form dialog</summary>
        Form,
        /// <summary>Only the system tray balloon notification</summary>
        Balloon
    }

    /// <summary>
    /// Popup form showing caller information for incoming/outgoing calls.
    /// Uses a proper Windows Form (dark themed, centered) plus a system tray balloon notification.
    /// Auto-closes after configured duration or when the call is answered.
    /// </summary>
    public class CallerPopupForm : Form
    {

        // Capture UI SynchronizationContext at startup
        private static SynchronizationContext _uiContext;

        // Track the current open popup for closing on connect
        private static CallerPopupForm _currentPopup;

        // System tray icon reference for balloon notifications
        private static NotifyIcon _notifyIcon;

        /// <summary>
        /// Initialize the UI context. Call this from the main UI thread at startup.
        /// </summary>
        public static void InitializeUIContext()
        {
            _uiContext = SynchronizationContext.Current;
        }

        /// <summary>
        /// Set the NotifyIcon reference for balloon tip notifications.
        /// Call this from TrayApplication after creating the NotifyIcon.
        /// </summary>
        public static void SetNotifyIcon(NotifyIcon notifyIcon)
        {
            _notifyIcon = notifyIcon;
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

            // Form settings
            UITheme.ApplyFormDefaults(this);
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
                LogManager.Log("CallerPopup: Error showing popup - {0}", ex.Message);
            }
        }

        private static void ShowFormOnUIThread(
            string callerNumber,
            string callerName,
            DatevContactInfo contactInfo,
            bool isIncoming)
        {
            if (_uiContext != null)
            {
                _uiContext.Post(_ => ShowPopupInternal(callerNumber, callerName, contactInfo, isIncoming), null);
                return;
            }

            if (Application.OpenForms.Count > 0)
            {
                var mainForm = Application.OpenForms[0];
                if (mainForm.InvokeRequired)
                {
                    mainForm.BeginInvoke(new Action(() =>
                        ShowPopupInternal(callerNumber, callerName, contactInfo, isIncoming)));
                    return;
                }
            }

            if (SynchronizationContext.Current != null)
            {
                ShowPopupInternal(callerNumber, callerName, contactInfo, isIncoming);
            }
            else
            {
                LogManager.Log("CallerPopup: Cannot show form - no UI context available");
            }
        }

        /// <summary>
        /// Close the current caller popup if one is open.
        /// Called when a call transitions to Connected.
        /// </summary>
        public static void CloseCurrentPopup()
        {
            try
            {
                if (_currentPopup != null && !_currentPopup.IsDisposed)
                {
                    if (_uiContext != null)
                    {
                        _uiContext.Post(_ =>
                        {
                            if (_currentPopup != null && !_currentPopup.IsDisposed)
                            {
                                var popup = _currentPopup;
                                _currentPopup = null;
                                popup.Close();
                                popup.Dispose();
                            }
                        }, null);
                    }
                    else if (Application.OpenForms.Count > 0)
                    {
                        var mainForm = Application.OpenForms[0];
                        if (mainForm.InvokeRequired)
                        {
                            mainForm.BeginInvoke(new Action(() =>
                            {
                                if (_currentPopup != null && !_currentPopup.IsDisposed)
                                {
                                    var popup = _currentPopup;
                                    _currentPopup = null;
                                    popup.Close();
                                    popup.Dispose();
                                }
                            }));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Log("CallerPopup: Error closing popup - {0}", ex.Message);
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
                LogManager.Debug("CallerPopup: Balloon skipped - NotifyIcon is null");
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
                    LogManager.Log("CallerPopup: Balloon notification failed - {0}", ex.Message);
                }
            };

            // ShowBalloonTip must be called on the UI thread
            if (_uiContext != null)
            {
                _uiContext.Post(_ => showBalloon(), null);
            }
            else if (Application.OpenForms.Count > 0)
            {
                var mainForm = Application.OpenForms[0];
                if (mainForm.InvokeRequired)
                    mainForm.BeginInvoke(showBalloon);
                else
                    showBalloon();
            }
            else
            {
                showBalloon();
            }
        }

        private static void ShowPopupInternal(
            string callerNumber,
            string callerName,
            DatevContactInfo contactInfo,
            bool isIncoming)
        {
            // Close and dispose any existing popup first
            if (_currentPopup != null && !_currentPopup.IsDisposed)
            {
                _currentPopup.Close();
                _currentPopup.Dispose();
            }

            string direction = isIncoming ? "Incoming" : "Outgoing";
            string contactName = contactInfo?.DatevContact?.Name ?? callerName ?? "Unknown";
            LogManager.Debug("CallerPopup: Showing {0} call popup - Number={1}, Name={2}",
                direction, LogManager.Mask(callerNumber), contactName);

            var popup = new CallerPopupForm(callerNumber, callerName, contactInfo, isIncoming);
            _currentPopup = popup;
            popup.FormClosed += (s, e) =>
            {
                if (_currentPopup == popup)
                    _currentPopup = null;
                ((Form)s).Dispose();
            };
            popup.Show();
        }
    }
}
