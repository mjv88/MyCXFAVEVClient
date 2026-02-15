using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using DatevConnector.Datev.Managers;
using DatevConnector.Datev.PluginData;
using DatevConnector.UI.Strings;
using DatevConnector.UI.Theme;

namespace DatevConnector.UI
{
    /// <summary>
    /// Dialog for selecting a contact when multiple matches are found.
    /// Shows a dropdown of matching contacts for user selection.
    /// Uses callback pattern for non-modal operation.
    /// </summary>
    public class ContactSelectionForm : Form
    {
        private readonly ComboBox _cboContact;
        private readonly List<DatevContactInfo> _contacts;
        private DatevContactInfo _selectedContact;
        private Action<DatevContactInfo> _onSelected;
        private bool _callbackInvoked;

        // Track the current open dialog for closing on disconnect
        private static ContactSelectionForm _currentDialog;

        /// <summary>
        /// Close the current contact selection dialog if one is open.
        /// Called when a call disconnects.
        /// </summary>
        public static void CloseCurrentDialog()
        {
            try
            {
                var dialog = _currentDialog;
                if (dialog != null && !dialog.IsDisposed)
                {
                    FormDisplayHelper.PostToUIThread(() =>
                    {
                        if (dialog != null && !dialog.IsDisposed)
                            dialog.Close();
                    });
                }
            }
            catch (Exception ex)
            {
                LogManager.Log("ContactSelection: Fehler beim Schließen des Dialogs - {0}", ex.Message);
            }
        }

        /// <summary>
        /// The contact selected by the user (or first contact if auto-selected)
        /// </summary>
        public DatevContactInfo SelectedContact => _selectedContact;

        public ContactSelectionForm(
            string phoneNumber,
            List<DatevContactInfo> contacts,
            bool isIncoming,
            Action<DatevContactInfo> onSelected)
        {
            _contacts = contacts;
            _selectedContact = contacts.Count > 0 ? contacts[0] : null;
            _onSelected = onSelected;

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
            btnCancel.Click += (s, e) =>
            {
                _callbackInvoked = true;
                LogManager.Log("Connector: Kontaktauswahl abgebrochen, verwende ersten");
                _onSelected?.Invoke(_contacts.Count > 0 ? _contacts[0] : null);
                Close();
            };

            var btnOk = UITheme.CreatePrimaryButton(UIStrings.Labels.OK, 80);
            btnOk.Location = new Point(260, 130);
            btnOk.Click += (s, e) =>
            {
                if (_cboContact.SelectedIndex >= 0)
                    _selectedContact = _contacts[_cboContact.SelectedIndex];
                _callbackInvoked = true;
                LogManager.Log("Connector: Kontakt ausgewählt: {0}",
                    LogManager.MaskName(_selectedContact?.DatevContact?.Name ?? "(none)"));
                _onSelected?.Invoke(_selectedContact);
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

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (!_callbackInvoked)
            {
                _callbackInvoked = true;
                _onSelected?.Invoke(_contacts.Count > 0 ? _contacts[0] : null);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _cboContact.Focus();
        }

        /// <summary>
        /// Shows the selection dialog as a non-modal window and invokes callback with result.
        /// Close-and-replace pattern: a new call replaces any open selection dialog.
        /// </summary>
        public static void SelectContact(
            string phoneNumber,
            List<DatevContactInfo> contacts,
            bool isIncoming,
            Action<DatevContactInfo> onSelected)
        {
            if (contacts == null || contacts.Count == 0) { onSelected?.Invoke(null); return; }
            if (contacts.Count == 1) { onSelected?.Invoke(contacts[0]); return; }

            FormDisplayHelper.PostToUIThread(() =>
            {
                LogManager.Log("Connector: Kontaktauswahl - {0} Treffer für {1}",
                    contacts.Count, LogManager.Mask(phoneNumber));

                if (_currentDialog != null && !_currentDialog.IsDisposed)
                    _currentDialog.Close();

                var form = new ContactSelectionForm(phoneNumber, contacts, isIncoming, onSelected);
                FormClosedEventHandler handler = null;
                handler = (s, e) =>
                {
                    ((Form)s).FormClosed -= handler;
                    if (_currentDialog == form) _currentDialog = null;
                    ((Form)s).Dispose();
                };
                form.FormClosed += handler;
                _currentDialog = form;
                form.Show();
            });
        }
    }
}
