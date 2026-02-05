using System;
using System.Collections.Generic;
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
    /// Dialog for selecting a contact when multiple matches are found.
    /// Shows a dropdown of matching contacts for user selection.
    /// </summary>
    public class ContactSelectionForm : Form
    {
        private readonly ComboBox _cboContact;
        private readonly List<DatevContactInfo> _contacts;
        private DatevContactInfo _selectedContact;

        // Track the current open dialog for closing on disconnect
        private static ContactSelectionForm _currentDialog;

        // Capture UI SynchronizationContext at startup
        private static SynchronizationContext _uiContext;

        /// <summary>
        /// Initialize the UI context. Call this from the main UI thread at startup.
        /// </summary>
        public static void InitializeUIContext()
        {
            _uiContext = SynchronizationContext.Current;
        }

        /// <summary>
        /// Close the current contact selection dialog if one is open.
        /// Called when a call disconnects.
        /// </summary>
        public static void CloseCurrentDialog()
        {
            try
            {
                if (_currentDialog != null && !_currentDialog.IsDisposed)
                {
                    if (_uiContext != null)
                    {
                        _uiContext.Post(_ =>
                        {
                            if (_currentDialog != null && !_currentDialog.IsDisposed)
                            {
                                _currentDialog.DialogResult = DialogResult.Cancel;
                                _currentDialog.Close();
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
                                if (_currentDialog != null && !_currentDialog.IsDisposed)
                                {
                                    _currentDialog.DialogResult = DialogResult.Cancel;
                                    _currentDialog.Close();
                                }
                            }));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Log("ContactSelection: Error closing dialog - {0}", ex.Message);
            }
        }

        /// <summary>
        /// The contact selected by the user (or first contact if auto-selected)
        /// </summary>
        public DatevContactInfo SelectedContact => _selectedContact;

        public ContactSelectionForm(
            string phoneNumber,
            List<DatevContactInfo> contacts,
            bool isIncoming = true)
        {
            _contacts = contacts;
            _selectedContact = contacts.Count > 0 ? contacts[0] : null;

            // Form settings
            UITheme.ApplyFormDefaults(this);
            Text = UIStrings.FormTitles.AppTitle;
            Size = new Size(380, 210);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            // Phone number header
            var lblPhone = new Label
            {
                Text = phoneNumber ?? UIStrings.CallerPopup.UnknownNumber,
                Location = new Point(LayoutConstants.SpaceLG, LayoutConstants.SpaceMD),
                Size = new Size(340, 22),
                ForeColor = UITheme.TextPrimary,
                Font = UITheme.FontLarge
            };

            // Description
            var lblDescription = new Label
            {
                Text = UIStrings.ContactSelection.MultipleContactsFound,
                Location = new Point(LayoutConstants.SpaceLG, 44),
                Size = new Size(340, 20),
                ForeColor = UITheme.TextSecondary,
                Font = UITheme.FontBody
            };

            // Contact label
            var lblContact = new Label
            {
                Text = UIStrings.ContactSelection.Contact,
                Location = new Point(LayoutConstants.SpaceLG, 75),
                AutoSize = true,
                ForeColor = UITheme.TextSecondary,
                Font = UITheme.FontBody
            };

            // Contact dropdown
            _cboContact = new ComboBox
            {
                Location = new Point(100, 72),
                Size = new Size(240, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = UITheme.InputBackground,
                ForeColor = UITheme.TextPrimary,
                FlatStyle = FlatStyle.Flat
            };

            foreach (var contact in contacts)
            {
                _cboContact.Items.Add(contact.DatevContact?.Name ?? UIStrings.ContactSelection.UnknownName);
            }

            if (_cboContact.Items.Count > 0)
                _cboContact.SelectedIndex = 0;

            // Source label
            var lblSource = new Label
            {
                Text = string.Format(UIStrings.ContactSelection.Source, UIStrings.ContactSelection.SourceDatev),
                Location = new Point(LayoutConstants.SpaceLG, 110),
                AutoSize = true,
                ForeColor = UITheme.TextMuted,
                Font = UITheme.FontSmall
            };

            // Buttons
            var btnCancel = UITheme.CreateSecondaryButton(UIStrings.Labels.Cancel, 80);
            btnCancel.Location = new Point(LayoutConstants.SpaceLG, 130);
            btnCancel.DialogResult = DialogResult.Cancel;

            var btnOk = UITheme.CreatePrimaryButton(UIStrings.Labels.OK, 80);
            btnOk.Location = new Point(260, 130);
            btnOk.Click += (s, e) =>
            {
                if (_cboContact.SelectedIndex >= 0)
                    _selectedContact = _contacts[_cboContact.SelectedIndex];
                DialogResult = DialogResult.OK;
                Close();
            };

            Controls.AddRange(new Control[]
            {
                lblPhone, lblDescription, lblContact, _cboContact,
                lblSource, btnCancel, btnOk
            });

            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _cboContact.Focus();
        }

        /// <summary>
        /// Shows the selection dialog and returns the selected contact.
        /// Returns first contact if cancelled or UI unavailable.
        /// </summary>
        public static DatevContactInfo SelectContact(
            string phoneNumber,
            List<DatevContactInfo> contacts,
            bool isIncoming = true)
        {
            if (contacts == null || contacts.Count == 0)
                return null;

            if (contacts.Count == 1)
                return contacts[0];

            try
            {
                DatevContactInfo result = null;

                if (_uiContext != null)
                {
                    _uiContext.Send(_ =>
                    {
                        result = SelectContactInternal(phoneNumber, contacts, isIncoming);
                    }, null);
                    return result;
                }

                if (Application.OpenForms.Count > 0)
                {
                    var mainForm = Application.OpenForms[0];
                    if (mainForm.InvokeRequired)
                    {
                        mainForm.Invoke(new Action(() =>
                        {
                            result = SelectContactInternal(phoneNumber, contacts, isIncoming);
                        }));
                        return result;
                    }
                }

                if (SynchronizationContext.Current != null)
                {
                    return SelectContactInternal(phoneNumber, contacts, isIncoming);
                }

                LogManager.Log("ContactSelection: Cannot show dialog - no UI context, using first contact");
                return contacts[0];
            }
            catch (Exception ex)
            {
                LogManager.Log("ContactSelection: Error showing dialog - {0}, using first contact", ex.Message);
                return contacts[0];
            }
        }

        private static DatevContactInfo SelectContactInternal(
            string phoneNumber,
            List<DatevContactInfo> contacts,
            bool isIncoming)
        {
            LogManager.Log("Bridge: Contact selection - {0} matches for {1}",
                contacts.Count, LogManager.Mask(phoneNumber));

            using (var form = new ContactSelectionForm(phoneNumber, contacts, isIncoming))
            {
                _currentDialog = form;
                try
                {
                    var dialogResult = form.ShowDialog();

                    if (dialogResult == DialogResult.Cancel)
                    {
                        LogManager.Log("Bridge: Contact selection cancelled, using first");
                        return contacts[0];
                    }

                    string selectedName = form.SelectedContact?.DatevContact?.Name ?? "(none)";
                    LogManager.Log("Bridge: Contact selected: {0}", LogManager.MaskName(selectedName));

                    return form.SelectedContact;
                }
                finally
                {
                    _currentDialog = null;
                }
            }
        }
    }
}
