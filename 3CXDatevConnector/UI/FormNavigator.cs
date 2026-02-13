using System;
using System.Drawing;
using System.Windows.Forms;
using DatevConnector.Core;

namespace DatevConnector.UI
{
    /// <summary>
    /// Manages single-window navigation for the tray application.
    /// Ensures only one main form (Status, Settings, CallHistory) is open at a time,
    /// preserves window position across form switches, and handles navigation
    /// requests (e.g., StatusForm requesting switch to CallHistory).
    ///
    /// Extracted from TrayApplication to separate form lifecycle from tray icon management.
    /// </summary>
    internal class FormNavigator
    {
        private readonly ConnectorService _bridgeService;
        private readonly Control _owner; // For BeginInvoke to marshal to UI thread
        private Form _currentMainForm;
        private Point? _lastFormLocation;

        public FormNavigator(ConnectorService bridgeService, Control owner)
        {
            _bridgeService = bridgeService;
            _owner = owner;
        }

        /// <summary>
        /// Show the status form, or bring it to front if already open.
        /// </summary>
        public void ShowStatus()
        {
            if (_currentMainForm is StatusForm existing && !existing.IsDisposed)
            {
                existing.Activate();
                existing.BringToFront();
                return;
            }

            var form = new StatusForm(_bridgeService);
            FormClosedEventHandler handler = null;
            handler = (s, args) =>
            {
                ((Form)s).FormClosed -= handler;
                var f = s as StatusForm;
                SaveLocationAndClear(f);
                if (f?.RequestedAction == StatusForm.Action.CallHistory)
                    _owner.BeginInvoke(new Action(ShowCallHistory));
                else if (f?.RequestedAction == StatusForm.Action.Settings)
                    _owner.BeginInvoke(new Action(ShowSettings));
            };
            form.FormClosed += handler;
            ShowMainForm(form);
        }

        /// <summary>
        /// Show the settings form, or bring it to front if already open.
        /// </summary>
        public void ShowSettings()
        {
            if (_currentMainForm is SettingsForm existing && !existing.IsDisposed)
            {
                existing.Activate();
                existing.BringToFront();
                return;
            }

            var form = new SettingsForm(_bridgeService);
            FormClosedEventHandler handler = null;
            handler = (s, args) =>
            {
                ((Form)s).FormClosed -= handler;
                var f = s as SettingsForm;
                SaveLocationAndClear(f);
                if (f?.RequestedAction == SettingsForm.Action.Status)
                    _owner.BeginInvoke(new Action(ShowStatus));
            };
            form.FormClosed += handler;
            ShowMainForm(form);
        }

        /// <summary>
        /// Show the call history form, or bring it to front if already open.
        /// </summary>
        public void ShowCallHistory()
        {
            if (_currentMainForm is CallHistoryForm existing && !existing.IsDisposed)
            {
                existing.Activate();
                existing.BringToFront();
                return;
            }

            var form = new CallHistoryForm(
                _bridgeService.CallHistory,
                (entry, note) => _bridgeService.SendHistoryJournal(entry, note));
            FormClosedEventHandler handler = null;
            handler = (s, args) =>
            {
                ((Form)s).FormClosed -= handler;
                var f = s as CallHistoryForm;
                SaveLocationAndClear(f);
                if (f?.RequestedAction == CallHistoryForm.Action.Back)
                    _owner.BeginInvoke(new Action(ShowStatus));
            };
            form.FormClosed += handler;
            ShowMainForm(form);
        }

        /// <summary>
        /// Dispose any currently open main form.
        /// </summary>
        public void DisposeCurrentForm()
        {
            if (_currentMainForm != null && !_currentMainForm.IsDisposed)
            {
                _currentMainForm.Close();
                _currentMainForm.Dispose();
            }
            _currentMainForm = null;
        }

        private void ShowMainForm(Form form)
        {
            CloseCurrentMainForm();
            _currentMainForm = form;

            if (_lastFormLocation.HasValue)
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = _lastFormLocation.Value;
            }

            form.Show();
            form.Activate();
            form.BringToFront();
        }

        private void CloseCurrentMainForm()
        {
            if (_currentMainForm != null && !_currentMainForm.IsDisposed)
            {
                _lastFormLocation = _currentMainForm.Location;
                _currentMainForm.Close();
                _currentMainForm.Dispose();
            }
            _currentMainForm = null;
        }

        private void SaveLocationAndClear(Form form)
        {
            if (form != null && !form.IsDisposed)
                _lastFormLocation = form.Location;
            if (_currentMainForm == form)
                _currentMainForm = null;
        }
    }
}
