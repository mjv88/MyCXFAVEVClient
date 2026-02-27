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
    /// Modern dark theme with auto-refresh. Resizable form with hardcoded column widths.
    /// </summary>
    public class CallHistoryForm : ThemedForm
    {
        public enum Action { None, Back, Settings }

        private DataGridView _gridInbound;
        private DataGridView _gridOutbound;
        private Button _btnJournal;
        private Button _btnRefresh;
        private Button _btnBack;
        private Button _btnSettings;
        private Label _lblInbound;
        private Label _lblOutbound;
        private Timer _autoRefreshTimer;

        private readonly CallHistoryStore _store;
        private readonly Action<CallHistoryEntry, string> _onJournalSubmit;
        private readonly bool _showInbound;
        private readonly bool _showOutbound;

        // Layout constants
        private const int FormPadding = 24; // LayoutConstants.SpaceLG
        private const int LabelHeight = 22;
        private const int SectionSpacing = 16; // LayoutConstants.SpaceMD
        private const int BtnWidth = 90;
        private const int BtnSpacing = 8; // LayoutConstants.SpaceSM

        public Action RequestedAction { get; private set; }

        protected override void ApplyTheme()
        {
            base.ApplyTheme();
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
        }

        public CallHistoryForm(CallHistoryStore store, Action<CallHistoryEntry, string> onJournalSubmit)
        {
            _store = store;
            _onJournalSubmit = onJournalSubmit;
            _showInbound = _store.TrackInbound;
            _showOutbound = _store.TrackOutbound;
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
            // ThemedForm + ApplyTheme() handles: BackColor, ForeColor, FormBorderStyle (Sizable),
            // StartPosition, MaximizeBox, MinimizeBox, Font, Icon
            Text = UIStrings.FormTitles.AppTitle;

            // Initial size and minimum
            ClientSize = new Size(468, 400);
            MinimumSize = new Size(484, 280);

            SuspendLayout();

            // Inbound section — only shown when tracking is enabled
            if (_showInbound)
            {
                _lblInbound = new Label
                {
                    Text = UIStrings.CallHistory.Inbound,
                    Font = UITheme.FontLabel,
                    ForeColor = UITheme.AccentIncoming,
                    AutoSize = true
                };
                Controls.Add(_lblInbound);

                _gridInbound = CreateGrid();
                Controls.Add(_gridInbound);
            }

            // Outbound section — only shown when tracking is enabled
            if (_showOutbound)
            {
                _lblOutbound = new Label
                {
                    Text = UIStrings.CallHistory.Outbound,
                    Font = UITheme.FontLabel,
                    ForeColor = UITheme.AccentIncoming,
                    AutoSize = true
                };
                Controls.Add(_lblOutbound);

                _gridOutbound = CreateGrid();
                Controls.Add(_gridOutbound);
            }

            // Buttons
            _btnRefresh = UITheme.CreateSecondaryButton(UIStrings.Labels.Refresh, BtnWidth);
            _btnRefresh.Click += (s, e) => LoadHistory();
            Controls.Add(_btnRefresh);

            _btnBack = UITheme.CreateSecondaryButton(UIStrings.Labels.Status, BtnWidth);
            _btnBack.Click += (s, e) =>
            {
                RequestedAction = Action.Back;
                Close();
            };
            Controls.Add(_btnBack);

            _btnSettings = UITheme.CreateSecondaryButton(UIStrings.Labels.Settings, BtnWidth);
            _btnSettings.Click += (s, e) =>
            {
                RequestedAction = Action.Settings;
                Close();
            };
            Controls.Add(_btnSettings);

            _btnJournal = UITheme.CreatePrimaryButton(UIStrings.CallHistory.Journal, BtnWidth);
            _btnJournal.Enabled = false;
            _btnJournal.Click += BtnJournal_Click;
            Controls.Add(_btnJournal);

            // Selection events — only wire up for existing controls
            if (_gridInbound != null)
            {
                _gridInbound.SelectionChanged += OnSelectionChanged;
                _gridInbound.CellDoubleClick += OnEntryDoubleClick;
            }
            if (_gridOutbound != null)
            {
                _gridOutbound.SelectionChanged += OnSelectionChanged;
                _gridOutbound.CellDoubleClick += OnEntryDoubleClick;
            }
            if (_gridInbound != null && _gridOutbound != null)
            {
                _gridInbound.CellClick += (s, e) => _gridOutbound.ClearSelection();
                _gridOutbound.CellClick += (s, e) => _gridInbound.ClearSelection();
            }

            ResumeLayout(false);
            LayoutControls();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (IsHandleCreated)
                LayoutControls();
        }

        private void LayoutControls()
        {
            int cw = ClientSize.Width;
            int ch = ClientSize.Height;
            int listWidth = cw - (FormPadding * 2);
            if (listWidth < 100) listWidth = 100;

            // Fixed vertical space consumed by labels, spacing, padding, and buttons
            int listCount = (_showInbound ? 1 : 0) + (_showOutbound ? 1 : 0);
            if (listCount == 0) return;

            int fixedHeight = FormPadding                                    // top padding
                + (listCount * LabelHeight)                                  // section labels
                + (_showInbound && _showOutbound ? SectionSpacing : 0)       // spacing between sections
                + FormPadding                                                // padding before buttons
                + LayoutConstants.ButtonHeight                               // button row
                + 6;                                                         // bottom padding

            int availableForLists = ch - fixedHeight;
            if (availableForLists < 60) availableForLists = 60;

            int listHeight = availableForLists / listCount;

            int y = FormPadding;

            // Inbound
            if (_showInbound)
            {
                _lblInbound.Location = new Point(FormPadding, y);
                y += LabelHeight;

                _gridInbound.Location = new Point(FormPadding, y);
                _gridInbound.Size = new Size(listWidth, listHeight);
                y += listHeight;

                if (_showOutbound)
                    y += SectionSpacing;
            }

            // Outbound
            if (_showOutbound)
            {
                _lblOutbound.Location = new Point(FormPadding, y);
                y += LabelHeight;

                _gridOutbound.Location = new Point(FormPadding, y);
                _gridOutbound.Size = new Size(listWidth, listHeight);
                y += listHeight;
            }

            y += FormPadding;

            // Buttons — navigation left, actions right
            _btnBack.Location = new Point(FormPadding, y);
            _btnSettings.Location = new Point(FormPadding + BtnWidth + BtnSpacing, y);
            _btnRefresh.Location = new Point(cw - FormPadding - BtnWidth - BtnSpacing - BtnWidth, y);
            _btnJournal.Location = new Point(cw - FormPadding - BtnWidth, y);
        }

        private DataGridView CreateGrid()
        {
            var grid = UITheme.CreateDataGridView();

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = UIStrings.CallHistory.Time,
                Width = 70,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = UIStrings.CallHistory.Number,
                Width = 120,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = UIStrings.CallHistory.Contact,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = UIStrings.CallHistory.Duration,
                Width = 55,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                HeaderCell = { Style = { Alignment = DataGridViewContentAlignment.MiddleCenter } },
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    ForeColor = UITheme.TextMuted
                }
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = UIStrings.CallHistory.Journal,
                Width = 65,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                HeaderCell = { Style = { Alignment = DataGridViewContentAlignment.MiddleCenter } },
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Alignment = DataGridViewContentAlignment.MiddleCenter
                }
            });

            grid.CellPainting += OnCellPainting;

            return grid;
        }

        private void OnCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            // Paint journal status column (index 4) with colored dot instead of text
            if (e.ColumnIndex != 4 || e.RowIndex < 0) return;

            var grid = sender as DataGridView;
            if (grid == null) return;

            var entry = grid.Rows[e.RowIndex].Tag as CallHistoryEntry;
            if (entry == null) return;

            e.Paint(e.CellBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground);

            Color dotColor;
            if (entry.JournalSent)
                dotColor = UITheme.StatusOk;
            else if (!string.IsNullOrEmpty(entry.AdressatenId))
                dotColor = UITheme.StatusWarn;
            else
                dotColor = UITheme.TextMuted;

            int dotSize = 8;
            int x = e.CellBounds.X + (e.CellBounds.Width - dotSize) / 2;
            int y = e.CellBounds.Y + (e.CellBounds.Height - dotSize) / 2;

            using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(x, y, dotSize, dotSize), dotColor,
                Color.FromArgb(dotColor.A, Math.Max(0, dotColor.R - 30), Math.Max(0, dotColor.G - 30), Math.Max(0, dotColor.B - 30)),
                System.Drawing.Drawing2D.LinearGradientMode.Vertical))
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.FillEllipse(brush, x, y, dotSize, dotSize);
            }

            e.Handled = true;
        }

        private void LoadHistory()
        {
            if (_gridInbound != null)
            {
                _gridInbound.Rows.Clear();
                foreach (var entry in _store.GetInbound())
                    _gridInbound.Rows.Add(CreateRow(entry));
            }

            if (_gridOutbound != null)
            {
                _gridOutbound.Rows.Clear();
                foreach (var entry in _store.GetOutbound())
                    _gridOutbound.Rows.Add(CreateRow(entry));
            }
        }

        private DataGridViewRow CreateRow(CallHistoryEntry entry)
        {
            var row = new DataGridViewRow();
            row.CreateCells(
                _gridInbound ?? _gridOutbound,
                entry.CallStart.ToString("HH:mm:ss"),
                entry.RemoteNumber ?? UIStrings.CallHistory.JournalNone,
                string.IsNullOrEmpty(entry.ContactName) ? UIStrings.CallHistory.Unknown : entry.ContactName,
                entry.Duration.ToString(@"mm\:ss"),
                "" // Journal column painted via CellPainting
            );

            // Muted text for already-sent journal entries
            if (entry.JournalSent)
                row.DefaultCellStyle.ForeColor = UITheme.TextMuted;

            row.Tag = entry;
            return row;
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
            if (_gridInbound != null && _gridInbound.SelectedRows.Count > 0)
                return _gridInbound.SelectedRows[0].Tag as CallHistoryEntry;
            if (_gridOutbound != null && _gridOutbound.SelectedRows.Count > 0)
                return _gridOutbound.SelectedRows[0].Tag as CallHistoryEntry;
            return null;
        }

        private void OnEntryDoubleClick(object sender, DataGridViewCellEventArgs e)
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

            _autoRefreshTimer?.Stop();

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
                        LoadHistory();
                        LogManager.Log("CallHistory: Journal erneut gesendet für {0}", LogManager.Mask(entry.RemoteNumber));
                    }
                },
                onClosed: () => _autoRefreshTimer?.Start());
        }
    }
}
