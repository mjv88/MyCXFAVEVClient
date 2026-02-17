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
        public static async Task<bool> RunTestAsync(
            Button button,
            Label progressLabel,
            Func<Action<string>, Task<bool>> operation,
            string originalText,
            params Button[] siblingButtons)
        {
            var form = button.FindForm();

            button.Enabled = false;
            foreach (var sibling in siblingButtons)
                if (sibling != null) sibling.Enabled = false;
            button.Text = Strings.UIStrings.Status.TestPending;

            if (progressLabel != null)
            {
                progressLabel.Visible = true;
                progressLabel.Text = "";
            }

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
            catch (Exception)
            {
                success = false;
            }

            if (form == null || form.IsDisposed || !form.IsHandleCreated)
                return success;

            button.Text = success ? Strings.UIStrings.Status.TestSuccess : Strings.UIStrings.Status.TestFailed;
            button.ForeColor = success ? UITheme.StatusOk : UITheme.StatusBad;

            await Task.Delay(1500);
            if (form.IsDisposed || !form.IsHandleCreated)
                return success;

            button.Text = originalText;
            button.ForeColor = UITheme.TextPrimary;
            button.Enabled = true;
            foreach (var sibling in siblingButtons)
                if (sibling != null) sibling.Enabled = true;

            if (progressLabel != null)
            {
                progressLabel.Text = "";
                progressLabel.Visible = false;
            }

            return success;
        }

        public static async Task RunAsync(
            Button button,
            Label progressLabel,
            Func<Action<string>, Task> operation,
            string originalText,
            params Button[] siblingButtons)
        {
            var form = button.FindForm();

            button.Enabled = false;
            foreach (var sibling in siblingButtons)
                if (sibling != null) sibling.Enabled = false;
            button.Text = Strings.UIStrings.Status.TestPending;

            if (progressLabel != null)
            {
                progressLabel.Visible = true;
                progressLabel.Text = "";
            }

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
            catch (Exception)
            {
            }

            if (form == null || form.IsDisposed || !form.IsHandleCreated)
                return;

            button.Text = originalText;
            button.Enabled = true;
            foreach (var sibling in siblingButtons)
                if (sibling != null) sibling.Enabled = true;

            if (progressLabel != null)
            {
                progressLabel.Text = "";
                progressLabel.Visible = false;
            }
        }
    }
}
