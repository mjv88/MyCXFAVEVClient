using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using DatevConnector.Core;
using DatevConnector.Datev.Managers;
using DatevConnector.UI.Strings;
using DatevConnector.UI.Theme;

namespace DatevConnector.UI
{
    /// <summary>
    /// Displays recent call history with ability to re-send journal entries.
    /// Modern dark theme with auto-refresh.
    /// </summary>
    public class CallHistoryForm : Form
    {
        /// <summary>
        /// Which action the user requested before closing.
        /// </summary>
        public enum Action { None, Back }

        private ListView _lstInbound;
        private ListView _lstOutbound;
        private Button _btnJournal;
        private Button _btnRefresh;
        private Button _btnBack;
        private Label _lblInbound;
        private Label _lblOutbound;
        private Timer _autoRefreshTimer;

        private readonly CallHistoryStore _store;
        private readonly Action<CallHistoryEntry, string> _onJournalSubmit;

        /// <summary>
        /// The action requested by the user (check after ShowDialog returns).
        /// </summary>
        public Action RequestedAction { get; private set; }

        public CallHistoryForm(CallHistoryStore store, Action<CallHistoryEntry, string> onJournalSubmit)
        {
            _store = store;
            _onJournalSubmit = onJournalSubmit;
            RequestedAction = Action.None;
            InitializeComponent();
            LoadHistory();
            StartAutoRefresh();
        }

        private void StartAutoRefresh()
        {
            _autoRefreshTimer = new Timer { Interval = 5000 }; // 5 seconds
            _autoRefreshTimer.Tick += (s, e) => LoadHistory();
            _autoRefreshTimer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _autoRefreshTimer?.Stop();
            _autoRefreshTimer?.Dispose();
            base.OnFormClosed(e);
        }

        private void InitializeComponent()
        {
            Text = UIStrings.FormTitles.AppTitle;
            BackColor = UITheme.FormBackground;
            ForeColor = UITheme.TextPrimary;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = UITheme.FontBody;
            var appIcon = UITheme.GetFormIcon();
            if (appIcon != null) { Icon = appIcon; ShowIcon = true; }
            else ShowIcon = false;

            // Layout dimensions
            int formPadding = LayoutConstants.SpaceLG;
            int listWidth = 436;
            int listHeight = 135;
            int labelHeight = 22;
            int sectionSpacing = LayoutConstants.SpaceMD;
            int btnWidth = 95;
            int btnSpacing = LayoutConstants.SpaceSM;

            bool showInbound = _store.TrackInbound;
            bool showOutbound = _store.TrackOutbound;

            int y = formPadding;

            // Inbound section — only shown when tracking is enabled
            if (showInbound)
            {
                _lblInbound = new Label
                {
                    Text = UIStrings.CallHistory.Inbound,
                    Font = UITheme.FontLabel,
                    ForeColor = UITheme.AccentIncoming,
                    AutoSize = true,
                    Location = new Point(formPadding, y)
                };
                Controls.Add(_lblInbound);
                y += labelHeight;

                _lstInbound = CreateListView();
                _lstInbound.Location = new Point(formPadding, y);
                _lstInbound.Size = new Size(listWidth, listHeight);
                Controls.Add(_lstInbound);
                y += listHeight;

                if (showOutbound)
                    y += sectionSpacing;
            }

            // Outbound section — only shown when tracking is enabled
            if (showOutbound)
            {
                _lblOutbound = new Label
                {
                    Text = UIStrings.CallHistory.Outbound,
                    Font = UITheme.FontLabel,
                    ForeColor = UITheme.AccentOutgoing,
                    AutoSize = true,
                    Location = new Point(formPadding, y)
                };
                Controls.Add(_lblOutbound);
                y += labelHeight;

                _lstOutbound = CreateListView();
                _lstOutbound.Location = new Point(formPadding, y);
                _lstOutbound.Size = new Size(listWidth, listHeight);
                Controls.Add(_lstOutbound);
                y += listHeight;
            }

            y += formPadding;

            // Buttons row: [Aktualisieren] [Zurück]        [Journal senden]
            _btnRefresh = UITheme.CreateSecondaryButton(UIStrings.Labels.Refresh, btnWidth);
            _btnRefresh.Location = new Point(formPadding, y);
            _btnRefresh.Click += (s, e) => LoadHistory();
            Controls.Add(_btnRefresh);

            _btnBack = UITheme.CreateSecondaryButton(UIStrings.Labels.Status, btnWidth);
            _btnBack.Location = new Point(formPadding + btnWidth + btnSpacing, y);
            _btnBack.Click += (s, e) =>
            {
                RequestedAction = Action.Back;
                Close();
            };
            Controls.Add(_btnBack);

            _btnJournal = UITheme.CreatePrimaryButton(UIStrings.CallHistory.Journal, btnWidth + 25);
            _btnJournal.Location = new Point(formPadding + listWidth - (btnWidth + 25), y);
            _btnJournal.Enabled = false;
            _btnJournal.Click += BtnJournal_Click;
            Controls.Add(_btnJournal);

            // Selection events — only wire up for existing controls
            if (_lstInbound != null)
            {
                _lstInbound.SelectedIndexChanged += OnSelectionChanged;
                _lstInbound.DoubleClick += OnEntryDoubleClick;
            }
            if (_lstOutbound != null)
            {
                _lstOutbound.SelectedIndexChanged += OnSelectionChanged;
                _lstOutbound.DoubleClick += OnEntryDoubleClick;
            }
            if (_lstInbound != null && _lstOutbound != null)
            {
                _lstInbound.Click += (s, e) => _lstOutbound.SelectedItems.Clear();
                _lstOutbound.Click += (s, e) => _lstInbound.SelectedItems.Clear();
            }

            // Size the form to fit the visible content
            ClientSize = new Size(468, y + LayoutConstants.ButtonHeight + 6);
        }

        private ListView CreateListView()
        {
            var lv = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BackColor = UITheme.CardBackground,
                ForeColor = UITheme.TextPrimary,
                Font = UITheme.FontBody,
                GridLines = false,
                BorderStyle = BorderStyle.None
            };

            lv.Columns.Add(UIStrings.CallHistory.Time, 70);
            lv.Columns.Add(UIStrings.CallHistory.Number, 130);
            lv.Columns.Add(UIStrings.CallHistory.Contact, 150);
            lv.Columns.Add(UIStrings.CallHistory.Duration, 55);
            lv.Columns.Add(UIStrings.CallHistory.Journal, 55); // Journal status column with header

            return lv;
        }

        private void LoadHistory()
        {
            if (_lstInbound != null)
            {
                _lstInbound.Items.Clear();
                foreach (var entry in _store.GetInbound())
                    _lstInbound.Items.Add(CreateListItem(entry));
            }

            if (_lstOutbound != null)
            {
                _lstOutbound.Items.Clear();
                foreach (var entry in _store.GetOutbound())
                    _lstOutbound.Items.Add(CreateListItem(entry));
            }
        }

        private ListViewItem CreateListItem(CallHistoryEntry entry)
        {
            var item = new ListViewItem(entry.CallStart.ToString("HH:mm:ss"));
            item.SubItems.Add(entry.RemoteNumber ?? UIStrings.CallHistory.JournalNone);
            item.SubItems.Add(string.IsNullOrEmpty(entry.ContactName) ? UIStrings.CallHistory.Unknown : entry.ContactName);
            item.SubItems.Add(entry.Duration.ToString(@"mm\:ss"));

            // Journal status with clearer text
            if (entry.JournalSent)
            {
                item.SubItems.Add(UIStrings.CallHistory.JournalSent);
                item.ForeColor = UITheme.TextMuted;
            }
            else if (!string.IsNullOrEmpty(entry.AdressatenId))
            {
                item.SubItems.Add(UIStrings.CallHistory.JournalPending);
            }
            else
            {
                item.SubItems.Add(UIStrings.CallHistory.JournalNone);
                item.ForeColor = UITheme.TextMuted;
            }

            item.Tag = entry;
            return item;
        }

        private void OnSelectionChanged(object sender, EventArgs e)
        {
            var selected = GetSelectedEntry();
            _btnJournal.Enabled = selected != null
                && !string.IsNullOrEmpty(selected.AdressatenId)
                && !selected.JournalSent;
        }

        private CallHistoryEntry GetSelectedEntry()
        {
            if (_lstInbound != null && _lstInbound.SelectedItems.Count > 0)
                return _lstInbound.SelectedItems[0].Tag as CallHistoryEntry;
            if (_lstOutbound != null && _lstOutbound.SelectedItems.Count > 0)
                return _lstOutbound.SelectedItems[0].Tag as CallHistoryEntry;
            return null;
        }

        private void OnEntryDoubleClick(object sender, EventArgs e)
        {
            var entry = GetSelectedEntry();
            if (entry != null && entry.JournalSent)
                return; // Already sent, ignore double-click

            OpenJournalForSelected();
        }

        private void BtnJournal_Click(object sender, EventArgs e)
        {
            OpenJournalForSelected();
        }

        private void OpenJournalForSelected()
        {
            var entry = GetSelectedEntry();
            if (entry == null || string.IsNullOrEmpty(entry.AdressatenId)) return;
            if (entry.JournalSent) return;

            // Use connected start time (CallEnd - Duration) so journal shows correct connected duration
            DateTime journalStart = entry.CallEnd - entry.Duration;

            // Pause auto-refresh while modal dialog is open
            _autoRefreshTimer?.Stop();

            try
            {
                JournalForm.ShowJournal(
                    entry.ContactName ?? "Unknown",
                    entry.RemoteNumber ?? "",
                    entry.DataSource ?? "",
                    journalStart,
                    entry.CallEnd,
                    note =>
                    {
                        if (!string.IsNullOrWhiteSpace(note))
                        {
                            _onJournalSubmit?.Invoke(entry, note);
                            _store.MarkJournalSent(entry);
                            LoadHistory(); // Refresh display
                            LogManager.Log("CallHistory: Journal re-sent for {0}", LogManager.Mask(entry.RemoteNumber));
                        }
                    });
            }
            finally
            {
                // Resume auto-refresh after modal closes
                _autoRefreshTimer?.Start();
            }
        }
    }
}
