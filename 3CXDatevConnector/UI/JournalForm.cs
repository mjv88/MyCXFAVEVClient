using System;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using DatevConnector.Datev.Managers;
using DatevConnector.UI.Strings;
using DatevConnector.UI.Theme;

namespace DatevConnector.UI
{
    /// <summary>
    /// Journal popup form shown after call ends.
    /// Allows user to add notes before sending journal to DATEV.
    /// </summary>
    public class JournalForm : Form
    {
        private const int MAX_NOTE_LENGTH = 2000;

        private readonly TextBox _txtNote;
        private readonly Label _lblCharCount;
        private readonly Button _btnSend;
        private readonly Button _btnCancel;
        private Action<string> _onSubmit;
        private Action _onClosed;

        /// <summary>
        /// The journal note text entered by the user
        /// </summary>
        public string JournalText
        {
            get
            {
                string text = _txtNote.Text;
                if (string.IsNullOrEmpty(text))
                    return string.Empty;

                // Strip control characters
                text = Regex.Replace(text, @"[\x00-\x1F\x7F]", "");
                if (text.Length > MAX_NOTE_LENGTH)
                    text = text.Substring(0, MAX_NOTE_LENGTH);

                return text.Trim();
            }
        }

        public JournalForm(
            string contactName,
            string contactNumber,
            string dataSource,
            DateTime callStart,
            DateTime callEnd,
            Action<string> onSubmit = null,
            Action onClosed = null)
        {
            _onSubmit = onSubmit;
            _onClosed = onClosed;
            // Form settings
            UITheme.ApplyFormDefaults(this);
            Text = UIStrings.FormTitles.AppTitle;
            ClientSize = new Size(464, 350);

            // Accent bar (DATEV green)
            var accentBar = UITheme.CreateAccentBar(UITheme.AccentDatev);

            // Direction label
            var lblHeader = new Label
            {
                Text = UIStrings.JournalForm.Header,
                ForeColor = UITheme.AccentDatev,
                Font = UITheme.FontLabel,
                Location = new Point(LayoutConstants.SpaceMD, LayoutConstants.SpaceMD),
                AutoSize = true
            };

            // Contact name label
            var lblContactName = new Label
            {
                Text = string.IsNullOrEmpty(contactName) ? UIStrings.JournalForm.UnknownContact : contactName,
                ForeColor = UITheme.TextPrimary,
                Font = UITheme.FontLarge,
                Location = new Point(LayoutConstants.SpaceMD, 36),
                Size = new Size(436, 30),
                AutoEllipsis = true
            };

            // Duration
            TimeSpan duration = callEnd - callStart;
            string durationText = duration.TotalHours >= 1
                ? string.Format(UIStrings.JournalForm.DurationFormatHMS, (int)duration.TotalHours, duration.Minutes, duration.Seconds)
                : string.Format(UIStrings.JournalForm.DurationFormatMS, duration.Minutes, duration.Seconds);
            var lblDuration = new Label
            {
                Text = durationText,
                ForeColor = UITheme.TextSecondary,
                Font = UITheme.FontBody,
                Location = new Point(LayoutConstants.SpaceMD, 70),
                AutoSize = true
            };

            // Number
            var lblNumber = new Label
            {
                Text = string.IsNullOrEmpty(contactNumber) ? "" : contactNumber,
                ForeColor = UITheme.TextMuted,
                Font = UITheme.FontBody,
                Location = new Point(200, 70),
                AutoSize = true
            };

            // Note text box
            _txtNote = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(LayoutConstants.SpaceMD, 96),
                Size = new Size(436, 186),
                BackColor = UITheme.InputBackground,
                ForeColor = UITheme.TextPrimary,
                Font = UITheme.FontBody,
                BorderStyle = BorderStyle.FixedSingle,
                MaxLength = MAX_NOTE_LENGTH
            };
            _txtNote.TextChanged += TxtNote_TextChanged;

            // Character count
            _lblCharCount = new Label
            {
                Text = string.Format(UIStrings.JournalForm.CharacterCount, 0, MAX_NOTE_LENGTH),
                ForeColor = UITheme.TextMuted,
                Font = UITheme.FontSmall,
                Location = new Point(400, 288),
                AutoSize = true,
                TextAlign = ContentAlignment.TopRight
            };

            // Source label
            string source = string.IsNullOrEmpty(dataSource) ? UIStrings.JournalForm.DefaultSource : dataSource;
            var lblSource = new Label
            {
                Text = string.Format(UIStrings.JournalForm.Source, source),
                ForeColor = UITheme.TextMuted,
                Font = UITheme.FontSmall,
                Location = new Point(LayoutConstants.SpaceMD, 288),
                AutoSize = true
            };

            // Cancel button
            _btnCancel = UITheme.CreateSecondaryButton(UIStrings.Labels.Cancel, 80);
            _btnCancel.Location = new Point(LayoutConstants.SpaceMD, 310);
            _btnCancel.Click += (s, e) =>
            {
                Close();
            };

            // Send button with DATEV logo
            _btnSend = UITheme.CreatePrimaryButton(UIStrings.JournalForm.Send, 180);
            _btnSend.Location = new Point(270, 310);
            var datevLogo = UITheme.GetDatevLogo();
            if (datevLogo != null)
            {
                // Scale logo to fit button height (20px) while maintaining aspect ratio
                int imgHeight = 20;
                int imgWidth = (int)((double)datevLogo.Width / datevLogo.Height * imgHeight);
                var scaledLogo = new Bitmap(datevLogo, new Size(imgWidth, imgHeight));
                _btnSend.Image = scaledLogo;
                _btnSend.TextImageRelation = TextImageRelation.ImageBeforeText;
                _btnSend.ImageAlign = ContentAlignment.MiddleLeft;
                _btnSend.TextAlign = ContentAlignment.MiddleRight;
                _btnSend.Padding = new Padding(6, 0, 6, 0);
            }
            _btnSend.Click += (s, e) =>
            {
                _onSubmit?.Invoke(JournalText);
                Close();
            };

            Controls.AddRange(new Control[]
            {
                accentBar, lblHeader, lblContactName, lblDuration, lblNumber,
                _txtNote, _lblCharCount, lblSource,
                _btnCancel, _btnSend
            });
        }

        private void TxtNote_TextChanged(object sender, EventArgs e)
        {
            int len = _txtNote.Text?.Length ?? 0;
            _lblCharCount.Text = string.Format(UIStrings.JournalForm.CharacterCount, len, MAX_NOTE_LENGTH);

            if (len >= MAX_NOTE_LENGTH)
                _lblCharCount.ForeColor = Color.Red;
            else if (len > MAX_NOTE_LENGTH * 0.9)
                _lblCharCount.ForeColor = Color.Orange;
            else
                _lblCharCount.ForeColor = UITheme.TextMuted;
        }

        private static JournalForm _current;

        /// <summary>
        /// Show the journal form on the UI thread as a non-modal window.
        /// Close-and-replace pattern: a new call replaces any open journal.
        /// </summary>
        public static void ShowJournal(
            string contactName,
            string contactNumber,
            string dataSource,
            DateTime callStart,
            DateTime callEnd,
            Action<string> onSubmit,
            Action onClosed = null)
        {
            FormDisplayHelper.PostToUIThread(() =>
            {
                if (_current != null && !_current.IsDisposed)
                    _current.Close();

                var form = new JournalForm(contactName, contactNumber, dataSource,
                    callStart, callEnd, onSubmit, onClosed);
                FormClosedEventHandler handler = null;
                handler = (s, e) =>
                {
                    ((Form)s).FormClosed -= handler;
                    if (_current == form) _current = null;
                    onClosed?.Invoke();
                    ((Form)s).Dispose();
                };
                form.FormClosed += handler;
                _current = form;
                form.Show();
            });
        }
    }
}
