using System;
using System.Windows.Forms;

namespace DatevConnector.UI.Theme
{
    /// <summary>
    /// Base form class that applies the dark theme automatically.
    /// Eliminates the repeated 8-10 property assignments that were duplicated
    /// across SettingsForm, StatusForm, SetupWizardForm, and others.
    ///
    /// Usage: inherit from ThemedForm instead of Form, then call
    /// base constructor or override ApplyTheme() for customization.
    /// </summary>
    public class ThemedForm : Form
    {
        protected ThemedForm()
        {
            ApplyTheme();
        }

        /// <summary>
        /// Apply the standard dark theme to this form.
        /// Override to customize (e.g., set TopMost = true for popups).
        /// </summary>
        protected virtual void ApplyTheme()
        {
            BackColor = UITheme.FormBackground;
            ForeColor = UITheme.TextPrimary;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = UITheme.FontBody;

            var icon = UITheme.GetFormIcon();
            if (icon != null)
            {
                Icon = icon;
                ShowIcon = true;
            }
            else
            {
                ShowIcon = false;
            }
        }

        /// <summary>
        /// Safely invoke an action on the UI thread. Centralizes the common pattern:
        ///   if (IsDisposed || !IsHandleCreated) return;
        ///   if (InvokeRequired) { BeginInvoke(...); return; }
        ///   action();
        /// </summary>
        protected void SafeInvoke(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(action); } catch (ObjectDisposedException) { }
                return;
            }
            action();
        }

        /// <summary>
        /// Check whether the form is still alive (not disposed, handle created).
        /// Useful as a guard after awaiting an async operation.
        /// </summary>
        protected bool IsAlive => !IsDisposed && IsHandleCreated;
    }
}
