using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DatevConnector.UI.Theme
{
    /// <summary>
    /// Helper that encapsulates the repeated async button operation pattern:
    /// 1. Disable button(s)
    /// 2. Show progress label
    /// 3. Run async operation
    /// 4. Show success/failure feedback on button
    /// 5. Delay for visual feedback
    /// 6. Reset button state
    /// 7. Hide progress label
    ///
    /// Eliminates ~30 duplicated async button handlers across StatusForm and SettingsForm.
    /// </summary>
    public static class AsyncButtonAction
    {
        /// <summary>
        /// Run an async test operation with visual feedback on the button.
        /// </summary>
        /// <param name="button">The button that triggered the action.</param>
        /// <param name="progressLabel">Optional progress label to show during operation.</param>
        /// <param name="operation">The async operation to run. Returns true for success.</param>
        /// <param name="originalText">Text to restore on the button after completion.</param>
        /// <param name="siblingButtons">Other buttons to disable during the operation.</param>
        public static async Task<bool> RunTestAsync(
            Button button,
            Label progressLabel,
            Func<Action<string>, Task<bool>> operation,
            string originalText,
            params Button[] siblingButtons)
        {
            var form = button.FindForm();

            // 1. Disable buttons
            button.Enabled = false;
            foreach (var sibling in siblingButtons)
                if (sibling != null) sibling.Enabled = false;
            button.Text = Strings.UIStrings.Status.TestPending;

            // 2. Show progress
            if (progressLabel != null)
            {
                progressLabel.Visible = true;
                progressLabel.Text = "";
            }

            // 3. Run operation
            Action<string> updateProgress = msg =>
            {
                if (progressLabel != null && form != null && !form.IsDisposed && form.IsHandleCreated)
                {
                    try { form.BeginInvoke(new Action(() => { progressLabel.Text = msg; })); }
                    catch (ObjectDisposedException) { }
                }
            };

            bool success;
            try
            {
                success = await operation(updateProgress);
            }
            catch
            {
                success = false;
            }

            if (form == null || form.IsDisposed || !form.IsHandleCreated)
                return success;

            // 4. Show result feedback
            button.Text = success ? Strings.UIStrings.Status.TestSuccess : Strings.UIStrings.Status.TestFailed;
            button.ForeColor = success ? UITheme.StatusOk : UITheme.StatusBad;

            // 5. Visual feedback delay
            await Task.Delay(1500);
            if (form.IsDisposed || !form.IsHandleCreated)
                return success;

            // 6. Reset button
            button.Text = originalText;
            button.ForeColor = UITheme.TextPrimary;
            button.Enabled = true;
            foreach (var sibling in siblingButtons)
                if (sibling != null) sibling.Enabled = true;

            // 7. Hide progress
            if (progressLabel != null)
            {
                progressLabel.Text = "";
                progressLabel.Visible = false;
            }

            return success;
        }

        /// <summary>
        /// Run an async action (no boolean result) with visual feedback.
        /// </summary>
        public static async Task RunAsync(
            Button button,
            Label progressLabel,
            Func<Action<string>, Task> operation,
            string originalText,
            params Button[] siblingButtons)
        {
            var form = button.FindForm();

            // Disable buttons
            button.Enabled = false;
            foreach (var sibling in siblingButtons)
                if (sibling != null) sibling.Enabled = false;
            button.Text = Strings.UIStrings.Status.TestPending;

            // Show progress
            if (progressLabel != null)
            {
                progressLabel.Visible = true;
                progressLabel.Text = "";
            }

            // Run operation
            Action<string> updateProgress = msg =>
            {
                if (progressLabel != null && form != null && !form.IsDisposed && form.IsHandleCreated)
                {
                    try { form.BeginInvoke(new Action(() => { progressLabel.Text = msg; })); }
                    catch (ObjectDisposedException) { }
                }
            };

            try
            {
                await operation(updateProgress);
            }
            catch
            {
                // Swallow — caller handles error state
            }

            if (form == null || form.IsDisposed || !form.IsHandleCreated)
                return;

            // Reset button
            button.Text = originalText;
            button.Enabled = true;
            foreach (var sibling in siblingButtons)
                if (sibling != null) sibling.Enabled = true;

            // Hide progress
            if (progressLabel != null)
            {
                progressLabel.Text = "";
                progressLabel.Visible = false;
            }
        }
    }
}
