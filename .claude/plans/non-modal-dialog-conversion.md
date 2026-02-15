# Convert All Modal Dialogs to Non-Modal

## Context

All five `ShowDialog()` forms (About, Troubleshooting, Journal, SetupWizard, ContactSelection) block interaction with any window behind them. The user wants to close/interact with any form independently. We convert all five to `.Show()` using the existing singleton pattern from `CallerPopupForm`.

---

## Shared pattern (from CallerPopupForm)

Every form gets: static `_current` field, singleton guard (activate-or-replace), `FormClosed` handler that nulls `_current` and calls `Dispose()`.

---

## 1. AboutForm — read-only, no return value

**File:** `UI/AboutForm.cs`

Add static field and `ShowAbout()` method. If already open, activate existing instance:

```csharp
private static AboutForm _current;

public static void ShowAbout()
{
    if (_current != null && !_current.IsDisposed)
    {
        _current.Activate();
        _current.BringToFront();
        return;
    }

    var form = new AboutForm();
    FormClosedEventHandler handler = null;
    handler = (s, e) =>
    {
        ((Form)s).FormClosed -= handler;
        if (_current == form) _current = null;
        ((Form)s).Dispose();
    };
    form.FormClosed += handler;
    _current = form;
    form.Show();
}
```

**File:** `UI/TrayApplication.cs` line 101-104 — replace inline `using/ShowDialog` with:
```csharp
_contextMenu.Items["aboutItem"].Click += (s, e) => AboutForm.ShowAbout();
```

---

## 2. TroubleshootingForm — read-only, no return value

**File:** `UI/TroubleshootingForm.cs`

Add `_current` field. Rewrite `ShowHelp()` — same activate-existing pattern as AboutForm:

```csharp
private static TroubleshootingForm _current;

public static void ShowHelp(TelephonyMode selectedMode)
{
    if (_current != null && !_current.IsDisposed)
    {
        _current.Activate();
        _current.BringToFront();
        return;
    }

    var form = new TroubleshootingForm(selectedMode);
    FormClosedEventHandler handler = null;
    handler = (s, e) =>
    {
        ((Form)s).FormClosed -= handler;
        if (_current == form) _current = null;
        ((Form)s).Dispose();
    };
    form.FormClosed += handler;
    _current = form;
    form.Show();
}
```

No caller changes needed — `TrayApplication.cs:111` already calls `TroubleshootingForm.ShowHelp(...)`.

---

## 3. SetupWizardForm — callers ignore return value

**File:** `UI/SetupWizardForm.cs`

Both callers (`TrayApplication.cs:113` and `:155`) discard the `DialogResult`. Change return type to `void`, add singleton activate pattern:

```csharp
private static SetupWizardForm _current;

public static void ShowWizard(ConnectorService bridgeService = null)
{
    if (_current != null && !_current.IsDisposed)
    {
        _current.Activate();
        _current.BringToFront();
        return;
    }

    var wizard = new SetupWizardForm(bridgeService);
    FormClosedEventHandler handler = null;
    handler = (s, e) =>
    {
        ((Form)s).FormClosed -= handler;
        if (_current == wizard) _current = null;
        ((Form)s).Dispose();
    };
    wizard.FormClosed += handler;
    _current = wizard;
    wizard.Show();
}
```

Also clean up `BtnNext_Click` finish logic — remove `DialogResult = DialogResult.OK` assignment (harmless but dead code for non-modal).

No caller changes needed — both sites already use `SetupWizardForm.ShowWizard(...)` as a statement.

---

## 4. JournalForm — has callback pattern, needs `onClosed` for timer cleanup

**File:** `UI/JournalForm.cs`

**Key changes:**

a) Add `_current` field, `_onSubmit` callback field, `_onClosed` callback field.

b) Expand constructor to accept `onSubmit` and `onClosed`:
```csharp
private Action<string> _onSubmit;
private Action _onClosed;

public JournalForm(...existing params..., Action<string> onSubmit, Action onClosed)
```
Store both callbacks.

c) Move result logic from `ShowJournal()` into button handlers:
- **Send button**: invoke `_onSubmit(JournalText)`, then `Close()`
- **Cancel button**: just `Close()` (no callback, user cancelled)

d) Rewrite `ShowJournal()` — close-and-replace pattern (a new call ending replaces an open journal):

```csharp
private static JournalForm _current;

public static void ShowJournal(
    string contactName, string contactNumber, string dataSource,
    DateTime callStart, DateTime callEnd,
    Action<string> onSubmit, Action onClosed = null)
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
```

**File:** `UI/CallHistoryForm.cs` lines 335-371 — `OpenJournalForSelected()`

Remove the `try/finally` timer pattern. Pass `onClosed` callback to resume timer:

```csharp
private void OpenJournalForSelected()
{
    var entry = GetSelectedEntry();
    if (entry == null || string.IsNullOrEmpty(entry.AdressatenId)) return;
    if (entry.JournalSent) return;

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
                LogManager.Log("CallHistory: Journal re-sent for {0}", LogManager.Mask(entry.RemoteNumber));
            }
        },
        onClosed: () => _autoRefreshTimer?.Start());
}
```

**File:** `Core/CallEventProcessor.cs` — no changes for `ShowJournalPopup` (it doesn't use `onClosed`, and the existing callback-only pattern still works).

---

## 5. ContactSelectionForm — synchronous return -> callback

**File:** `UI/ContactSelectionForm.cs`

This is the largest change. The current `SelectContact()` returns `DatevContactInfo` synchronously via `SendToUIThread` + `ShowDialog`. Must become callback-based.

**a) Add instance fields:**
```csharp
private Action<DatevContactInfo> _onSelected;
private bool _callbackInvoked;
```

**b) Expand constructor** to accept `Action<DatevContactInfo> onSelected`, store it.

**c) OK button handler** — invoke callback with selected contact:
```csharp
btnOk.Click += (s, e) =>
{
    if (_cboContact.SelectedIndex >= 0)
        _selectedContact = _contacts[_cboContact.SelectedIndex];
    _callbackInvoked = true;
    LogManager.Log("Connector: Contact selected: {0}",
        LogManager.MaskName(_selectedContact?.DatevContact?.Name ?? "(none)"));
    _onSelected?.Invoke(_selectedContact);
    Close();
};
```

**d) Cancel button** — replace `DialogResult` assignment with click handler:
```csharp
btnCancel.Click += (s, e) =>
{
    _callbackInvoked = true;
    LogManager.Log("Connector: Contact selection cancelled, using first");
    _onSelected?.Invoke(_contacts.Count > 0 ? _contacts[0] : null);
    Close();
};
```

**e) Guard against X-button / external close** — override `OnFormClosing` to invoke callback with default if not already invoked:
```csharp
protected override void OnFormClosing(FormClosingEventArgs e)
{
    base.OnFormClosing(e);
    if (!_callbackInvoked)
    {
        _callbackInvoked = true;
        _onSelected?.Invoke(_contacts.Count > 0 ? _contacts[0] : null);
    }
}
```

**f) Rewrite `SelectContact()`** — callback-based, close-and-replace pattern:
```csharp
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
        LogManager.Log("Connector: Contact selection - {0} matches for {1}",
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
```

Delete `SelectContactInternal()` entirely.

**g) Simplify `CloseCurrentDialog()`** — remove `DialogResult` assignment (OnFormClosing handles callback):
```csharp
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
        LogManager.Log("ContactSelection: Error closing dialog - {0}", ex.Message);
    }
}
```

**File:** `Core/CallEventProcessor.cs` lines 301-321 — rewrite to callback pattern with call-still-active guard:

```csharp
ContactSelectionForm.SelectContact(
    remoteNumber, contacts, isIncoming,
    selectedContact =>
    {
        // Verify call is still active (may have disconnected while dialog was open)
        if (_callTracker.GetCall(callId) == null) return;

        if (selectedContact != null && currentRecord.CallData != null)
        {
            string existingSyncId = currentRecord.CallData.SyncID;
            string previousId = currentRecord.CallData.AdressatenId;

            CallDataManager.Fill(currentRecord.CallData, remoteNumber, selectedContact);

            if (!string.IsNullOrEmpty(existingSyncId))
                currentRecord.CallData.SyncID = existingSyncId;

            if (currentRecord.CallData.AdressatenId != previousId)
            {
                LogManager.Log("Contact reshow: Contact changed for call {0} - new={1} (SyncID={2})",
                    callId, currentRecord.CallData.Adressatenname, currentRecord.CallData.SyncID);
                _notificationManager.CallAdressatChanged(currentRecord.CallData);
                ContactRoutingCache.RecordUsage(remoteNumber, currentRecord.CallData.AdressatenId);
            }
        }
    });
```

---

## Files modified (summary)

| # | File | Change |
|---|------|--------|
| 1 | `UI/AboutForm.cs` | Add `_current` + `ShowAbout()` |
| 2 | `UI/TrayApplication.cs` | Line 103: use `AboutForm.ShowAbout()` |
| 3 | `UI/TroubleshootingForm.cs` | Add `_current` + rewrite `ShowHelp()` |
| 4 | `UI/SetupWizardForm.cs` | Add `_current` + rewrite `ShowWizard()` to `void` |
| 5 | `UI/JournalForm.cs` | Add `_current` + `_onSubmit`/`_onClosed` + rewrite button handlers + `ShowJournal()` |
| 6 | `UI/CallHistoryForm.cs` | `OpenJournalForSelected()`: pass `onClosed` callback for timer |
| 7 | `UI/ContactSelectionForm.cs` | Callback-based `SelectContact()` + `OnFormClosing` guard + simplify `CloseCurrentDialog()` |
| 8 | `Core/CallEventProcessor.cs` | Rewrite `ContactReshowAfterDelayAsync` to callback pattern |

## Status: IMPLEMENTED (commit c2ab4a4)
