using System;
using System.Threading;
using System.Windows.Forms;
using DatevConnector.Datev.Managers;

namespace DatevConnector.UI
{
    /// <summary>
    /// Centralized helper for dispatching UI work to the main thread.
    /// Replaces the duplicated 3-tier fallback pattern (SynchronizationContext → OpenForms → log error)
    /// that was repeated across CallerPopupForm, JournalForm, and ContactSelectionForm.
    /// </summary>
    public static class FormDisplayHelper
    {
        private static SynchronizationContext _uiContext;

        /// <summary>
        /// Capture the UI SynchronizationContext. Call once from the main UI thread at startup.
        /// </summary>
        public static void InitializeUIContext()
        {
            _uiContext = SynchronizationContext.Current;
        }

        /// <summary>
        /// Post (fire-and-forget) an action to the UI thread.
        /// Falls back to Application.OpenForms[0].BeginInvoke if no context captured.
        /// </summary>
        public static void PostToUIThread(Action action)
        {
            if (_uiContext != null)
            {
                _uiContext.Post(_ => action(), null);
                return;
            }

            if (Application.OpenForms.Count > 0)
            {
                var mainForm = Application.OpenForms[0];
                if (mainForm.InvokeRequired)
                {
                    mainForm.BeginInvoke(action);
                    return;
                }

                // Already on UI thread
                action();
                return;
            }

            LogManager.Log("FormDisplayHelper: Kann nicht zum UI-Thread posten - kein Kontext verfügbar");
        }

    }
}
