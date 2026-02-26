# Code Audit Fixes — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix all CRITICAL, HIGH, and MEDIUM findings from the 2026-02-26 code audit.

**Architecture:** Surgical edits to existing files. No new classes, projects, or abstractions unless strictly necessary. Each task is a logical group of related fixes committed together.

**Tech Stack:** .NET 9, C#, WinForms, TAPI 2.x, COM Interop

**Constraints:** YAGNI. Lightweight tray app. Stay under 750 KB. No test project.

---

### Task 1: CRITICAL — Fix GDI handle leak in SetupWizard

**Files:**
- Modify: `3CXDatevConnector/UI/SetupWizardForm.cs:104`

**Step 1: Replace Controls.Clear() with disposal loop**

In `ShowStep()` at line 104, replace:
```csharp
_contentPanel.Controls.Clear();
```
with:
```csharp
while (_contentPanel.Controls.Count > 0)
{
    var ctl = _contentPanel.Controls[0];
    _contentPanel.Controls.Remove(ctl);
    ctl.Dispose();
}
```

**Step 2: Build and verify**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj`
Expected: Build succeeded.

**Step 3: Commit**
```
fix: dispose controls on wizard step transitions to prevent GDI handle leak (C-1)
```

---

### Task 2: CRITICAL — Fix CallerPopupForm fade-out re-entrant close

**Files:**
- Modify: `3CXDatevConnector/UI/CallerPopupForm.cs:138-165`

**Step 1: Add _closingForReal flag and fix OnFormClosing**

Add a field near the existing `_fadingOut` field:
```csharp
private bool _closingForReal;
```

Rewrite `OnFormClosing`:
```csharp
protected override void OnFormClosing(FormClosingEventArgs e)
{
    if (_closingForReal)
    {
        _fadeTimer?.Stop();
        _fadeTimer?.Dispose();
        base.OnFormClosing(e);
        return;
    }

    if (!_fadingOut && Opacity > 0)
    {
        e.Cancel = true;
        _fadingOut = true;
        var fadeOutTimer = new Timer { Interval = 15 };
        fadeOutTimer.Tick += (s, ev) =>
        {
            Opacity -= 0.08;
            if (Opacity <= 0)
            {
                Opacity = 0;
                fadeOutTimer.Stop();
                fadeOutTimer.Dispose();
                _closingForReal = true;
                Close();
            }
        };
        fadeOutTimer.Start();
        return;
    }

    _closingForReal = true;
    _fadeTimer?.Stop();
    _fadeTimer?.Dispose();
    base.OnFormClosing(e);
}
```

**Step 2: Build and verify**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj`

**Step 3: Commit**
```
fix: prevent re-entrant close loop in CallerPopupForm fade-out (C-2)
```

---

### Task 3: CRITICAL — Add thread safety to CallRecord and CallStateMachine

**Files:**
- Modify: `3CXDatevConnector/Core/CallRecord.cs`
- Modify: `3CXDatevConnector/Core/CallStateMachine.cs:42-62`

**Step 1: Add lock object and thread-safe TapiState to CallRecord**

Add a lock object to `CallRecord`:
```csharp
public readonly object SyncLock = new object();
```

No other changes to CallRecord — the lock will be used externally by CallStateMachine and callers.

**Step 2: Use lock in CallStateMachine.TryTransition**

Wrap the check-then-act in `TryTransition` with the record's lock:
```csharp
public static bool TryTransition(CallRecord record, TapiCallState newState)
{
    lock (record.SyncLock)
    {
        var oldState = record.TapiState;

        if (!IsValidTransition(oldState, newState))
        {
            LogManager.Debug("CallStateMachine: Ungültiger Übergang {0} -> {1} für Call {2}",
                oldState, newState, record.TapiCallId);
            return false;
        }

        record.TapiState = newState;
        LogManager.Debug("CallStateMachine: {0} -> {1} für Call {2}",
            oldState, newState, record.TapiCallId);
        return true;
    }
}
```

**Step 3: Build and verify**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj`

**Step 4: Commit**
```
fix: add thread safety to CallRecord/CallStateMachine state transitions (C-3)
```

---

### Task 4: CRITICAL — Protect call history from silent data loss

**Files:**
- Modify: `3CXDatevConnector/Core/CallHistoryStore.cs:146-187`

**Step 1: Backup corrupted file before clearing in Load()**

In the `catch` block of `Load()` (around line 183), replace:
```csharp
catch (Exception ex)
{
    LogManager.Warning("CallHistory: Laden fehlgeschlagen: {0}", ex.Message);
    _inbound.Clear();
    _outbound.Clear();
}
```
with:
```csharp
catch (Exception ex)
{
    LogManager.Warning("CallHistory: Laden fehlgeschlagen: {0}", ex.Message);

    try
    {
        if (File.Exists(StorePath))
        {
            string backupPath = StorePath + ".bak";
            File.Copy(StorePath, backupPath, overwrite: true);
            LogManager.Warning("CallHistory: Beschädigte Datei gesichert als {0}", backupPath);
        }
    }
    catch (Exception backupEx)
    {
        LogManager.Warning("CallHistory: Backup fehlgeschlagen: {0}", backupEx.Message);
    }

    _inbound.Clear();
    _outbound.Clear();
}
```

**Step 2: Build and verify**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj`

**Step 3: Commit**
```
fix: backup corrupted call history file before clearing (C-4)
```

---

### Task 5: HIGH Security — Remove Everyone ACL from named pipe

**Files:**
- Modify: `3CXDatevConnector/Tapi/TapiPipeServer.cs:71-82`

**Step 1: Remove the Everyone ACL block**

In `CreatePipeSecurity()`, remove the entire block that adds the `WorldSid` ACL (lines 71-82):
```csharp
// DELETE THIS BLOCK:
try
{
    var everyoneSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
    security.AddAccessRule(new PipeAccessRule(
        everyoneSid,
        PipeAccessRights.ReadWrite,
        AccessControlType.Allow));
}
catch (Exception ex)
{
    LogManager.Debug("PipeServer: Could not add Everyone ACL: {0}", ex.Message);
}
```

**Step 2: Build and verify**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj`

**Step 3: Commit**
```
fix: remove Everyone ACL from named pipe, keep current user + AppContainer only (H-2)
```

---

### Task 6: HIGH Security — Add public key token validation to GAC resolver

**Files:**
- Modify: `3CXDatevConnector/Core/GacAssemblyResolver.cs:77-84`

**Step 1: Validate public key token before loading**

Replace the inner loop body (around lines 77-84):
```csharp
foreach (var versionDir in Directory.GetDirectories(assemblyDir))
{
    string dllPath = Path.Combine(versionDir, assemblyName.Name + ".dll");
    if (File.Exists(dllPath))
    {
        LogManager.Debug("GAC Resolver: Erfolgreich '{0}'", assemblyName.Name);
        return context.LoadFromAssemblyPath(dllPath);
    }
}
```
with:
```csharp
foreach (var versionDir in Directory.GetDirectories(assemblyDir))
{
    string dllPath = Path.Combine(versionDir, assemblyName.Name + ".dll");
    if (!File.Exists(dllPath))
        continue;

    try
    {
        var candidate = AssemblyName.GetAssemblyName(dllPath);
        var expectedToken = assemblyName.GetPublicKeyToken();
        var candidateToken = candidate.GetPublicKeyToken();

        if (expectedToken != null && expectedToken.Length > 0 &&
            (candidateToken == null || !expectedToken.SequenceEqual(candidateToken)))
        {
            LogManager.Debug("GAC Resolver: Token-Mismatch für '{0}' in '{1}'",
                assemblyName.Name, versionDir);
            continue;
        }

        LogManager.Debug("GAC Resolver: Erfolgreich '{0}'", assemblyName.Name);
        return context.LoadFromAssemblyPath(dllPath);
    }
    catch (Exception ex)
    {
        LogManager.Debug("GAC Resolver: Kann '{0}' nicht prüfen: {1}", dllPath, ex.Message);
    }
}
```

Add `using System.Linq;` at the top if not already present.

**Step 2: Build and verify**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj`

**Step 3: Commit**
```
fix: validate public key token in GAC assembly resolver (H-3)
```

---

### Task 7: HIGH Security — Add WebSocket frame length validation

**Files:**
- Modify: `3CXDatevConnector/Webclient/WebSocketBridgeServer.cs:362`

**Step 1: Add negative length check**

Change line 362 from:
```csharp
if (len > MaxFrameSize) return null;
```
to:
```csharp
if (len < 0 || len > MaxFrameSize) return null;
```

**Step 2: Build and verify**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj`

**Step 3: Commit**
```
fix: reject WebSocket frames with negative length from malicious MSB (M-11)
```

---

### Task 8: HIGH Reliability — Fix pipe read loop desync on invalid length

**Files:**
- Modify: `3CXDatevConnector/Tapi/TapiPipeServer.cs:197-201`

**Step 1: Replace continue with break**

Change:
```csharp
if (messageLength <= 0 || messageLength > MaxMessageLength)
{
    LogManager.Warning("PipeServer: Ungültige Nachrichtenlänge: {0}", messageLength);
    continue;
}
```
to:
```csharp
if (messageLength <= 0 || messageLength > MaxMessageLength)
{
    LogManager.Warning("PipeServer: Ungültige Nachrichtenlänge: {0} - Verbindung wird getrennt", messageLength);
    break;
}
```

**Step 2: Build and verify**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj`

**Step 3: Commit**
```
fix: break pipe read loop on invalid message length to prevent desync (H-8)
```

---

### Task 9: HIGH Reliability — Fix event handler leak in ConnectorService retry loop

**Files:**
- Modify: `3CXDatevConnector/Core/ConnectorService.cs:457-476`

**Step 1: Extract event handlers to named methods**

Add private methods to ConnectorService:
```csharp
private void OnProviderCallStateChanged(TapiCallEvent evt)
{
    _callEventProcessor.OnTapiCallStateChanged(evt);
}

private void OnProviderLineDisconnected(TapiLineInfo line)
{
    LogManager.Log("TAPI Leitung getrennt: {0}", line.Extension);
    StatusChanged?.Invoke(Status);
}

private void OnProviderConnected()
{
    AdoptExtensionFromProvider("connected line");
    Status = ConnectorStatus.Connected;
}

private void OnProviderDisconnected()
{
    Status = ConnectorStatus.Disconnected;
    LogManager.Log("Connector getrennt (alle Leitungen)");
}
```

**Step 2: Unsubscribe before subscribing in ConnectWithRetryAsync**

Replace lines 457-476 (the event subscription block) with:
```csharp
// Unsubscribe from old provider if reusing
if (_tapiMonitor != null && _tapiMonitor != providerToUse)
{
    _tapiMonitor.CallStateChanged -= OnProviderCallStateChanged;
    _tapiMonitor.LineDisconnected -= OnProviderLineDisconnected;
    _tapiMonitor.Connected -= OnProviderConnected;
    _tapiMonitor.Disconnected -= OnProviderDisconnected;
}

_tapiMonitor?.Dispose();
_tapiMonitor = providerToUse;

_tapiMonitor.CallStateChanged += OnProviderCallStateChanged;
_tapiMonitor.LineDisconnected += OnProviderLineDisconnected;
_tapiMonitor.Connected += OnProviderConnected;
_tapiMonitor.Disconnected += OnProviderDisconnected;
```

**Step 3: Build and verify**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj`

**Step 4: Commit**
```
fix: extract event handlers and unsubscribe before subscribing in retry loop (H-4)
```

---

### Task 10: HIGH Reliability — Fix DatevAvailable thread safety + _cts race + fire-and-forget task

**Files:**
- Modify: `3CXDatevConnector/Core/ConnectorService.cs`

**Step 1: Back DatevAvailable with volatile field**

Replace (around line 85):
```csharp
public bool DatevAvailable { get; private set; }
```
with:
```csharp
private volatile bool _datevAvailable;
public bool DatevAvailable
{
    get => _datevAvailable;
    private set => _datevAvailable = value;
}
```

**Step 2: Fix _cts race in ReconnectTapiAsync**

In `ReconnectTapiAsync` (around lines 553-558), replace:
```csharp
if (_cts != null && !_cts.Token.IsCancellationRequested &&
    (_connectRetryTask == null || _connectRetryTask.IsCompleted))
```
with:
```csharp
var cts = _cts;
try
{
    if (cts != null && !cts.Token.IsCancellationRequested &&
        (_connectRetryTask == null || _connectRetryTask.IsCompleted))
```
and close with:
```csharp
}
catch (ObjectDisposedException) { }
```

**Step 3: Store auto-detect task reference**

In `StartDatevAutoDetect` (around line 284), change:
```csharp
Task.Run(async () =>
```
to:
```csharp
_datevAutoDetectTask = Task.Run(async () =>
```

Add the field:
```csharp
private Task _datevAutoDetectTask;
```

**Step 4: Build and verify**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj`

**Step 5: Commit**
```
fix: thread-safe DatevAvailable, fix _cts race, store auto-detect task (H-5, H-12, M-8)
```

---

### Task 11: HIGH Reliability — Check ROT HRESULTs

**Files:**
- Modify: `3CXDatevConnector/Datev/Managers/AdapterManager.cs:23`
- Modify: `3CXDatevConnector/Datev/Managers/NotificationManager.cs:94`

**Step 1: Check RegisterActiveObject HRESULT in AdapterManager**

Replace (line 23):
```csharp
Rot.RegisterActiveObject(adapter, ref adapterGuid, flags, out _registrationId);
```
with:
```csharp
uint hr = Rot.RegisterActiveObject(adapter, ref adapterGuid, flags, out _registrationId);
if (hr != 0)
{
    LogManager.Warning("ROT RegisterActiveObject fehlgeschlagen: HRESULT=0x{0:X8}", hr);
}
```

**Step 2: Check GetActiveObject HRESULT in NotificationManager**

Replace (line 94):
```csharp
Rot.GetActiveObject(ref _clsIdDatev, ref i, out datevObj);
```
with:
```csharp
uint hr = Rot.GetActiveObject(ref _clsIdDatev, ref i, out datevObj);
if (hr != 0) datevObj = null;
```

**Step 3: Build and verify**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj`

**Step 4: Commit**
```
fix: check ROT RegisterActiveObject and GetActiveObject HRESULTs (H-9, H-10)
```

---

### Task 12: HIGH Reliability — Fix FormNavigator double-dispose

**Files:**
- Modify: `3CXDatevConnector/UI/FormNavigator.cs:132-141`

**Step 1: Remove explicit Dispose() after Close()**

Replace `CloseCurrentMainForm`:
```csharp
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
```
with:
```csharp
private void CloseCurrentMainForm()
{
    if (_currentMainForm != null && !_currentMainForm.IsDisposed)
    {
        _lastFormLocation = _currentMainForm.Location;
        _currentMainForm.Close();
    }
    _currentMainForm = null;
}
```

**Step 2: Build and verify**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj`

**Step 3: Commit**
```
fix: remove double-dispose in FormNavigator (H-11)
```

---

### Task 13: HIGH Reliability — Fix cross-thread UI access in SetupWizard

**Files:**
- Modify: `3CXDatevConnector/UI/SetupWizardForm.cs:523-549`

**Step 1: Wrap post-await UI accesses in SafeInvoke**

In `TestDatevConnection()`, wrap all post-await UI updates in `SafeInvoke`. Replace direct UI property access like:
```csharp
_lblDatevStatus.Text = "...";
_lblDatevStatus.ForeColor = ...;
_btnNext.Enabled = ...;
```
with:
```csharp
SafeInvoke(() =>
{
    _lblDatevStatus.Text = "...";
    _lblDatevStatus.ForeColor = ...;
    _btnNext.Enabled = ...;
});
```

Apply this pattern to every post-await UI update in the method.

**Step 2: Build and verify**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj`

**Step 3: Commit**
```
fix: wrap post-await UI access in SafeInvoke in SetupWizard (H-13)
```

---

### Task 14: HIGH Performance — Add async retry and debounce call history saves

**Files:**
- Modify: `3CXDatevConnector/Core/RetryHelper.cs`
- Modify: `3CXDatevConnector/Core/CallHistoryStore.cs`

**Step 1: Add ExecuteWithRetryAsync to RetryHelper**

Add after the existing `ExecuteWithRetry<T>` method:
```csharp
public static async Task<T> ExecuteWithRetryAsync<T>(
    Func<T> operation,
    string operationName,
    int? maxRetries = null,
    int? initialDelaySeconds = null,
    Func<Exception, bool> shouldRetry = null,
    CancellationToken ct = default)
{
    int retries = maxRetries ?? DefaultMaxRetries;
    int delay = initialDelaySeconds ?? DefaultInitialDelaySeconds;
    Exception lastException = null;

    for (int attempt = 0; attempt <= retries; attempt++)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return operation();
        }
        catch (Exception ex)
        {
            lastException = ex;

            if (shouldRetry != null && !shouldRetry(ex))
            {
                LogManager.Log("{0} fehlgeschlagen (nicht wiederholbar): {1}", operationName, ex.Message);
                break;
            }

            if (attempt < retries)
            {
                int currentDelay = Math.Min(delay * (int)Math.Pow(2, attempt), 60);
                LogManager.Log("{0} fehlgeschlagen (Versuch {1}/{2}), erneuter Versuch in {3}s: {4}",
                    operationName, attempt + 1, retries + 1, currentDelay, ex.Message);

                await Task.Delay(currentDelay * 1000, ct).ConfigureAwait(false);
            }
            else
            {
                LogManager.Log("{0} fehlgeschlagen nach {1} Versuchen: {2}",
                    operationName, retries + 1, ex.Message);
            }
        }
    }

    return default;
}
```

**Step 2: Add debounced save to CallHistoryStore**

Add fields:
```csharp
private volatile bool _dirty;
private Timer _saveTimer;
```

In the constructor, initialize the timer:
```csharp
_saveTimer = new System.Threading.Timer(_ => FlushIfDirty(), null, Timeout.Infinite, Timeout.Infinite);
```

Add a flush method:
```csharp
private void FlushIfDirty()
{
    if (!_dirty) return;
    lock (_lock)
    {
        if (!_dirty) return;
        Save();
        _dirty = false;
    }
}
```

In `AddEntry()`, replace `Save();` with:
```csharp
_dirty = true;
_saveTimer.Change(5000, Timeout.Infinite);
```

Keep `Save()` called directly in `MarkJournalSent()` (immediate save for journal state).

In `Dispose`, add:
```csharp
_saveTimer?.Dispose();
FlushIfDirty();
```

**Step 3: Also cap the sync retry max delay**

In the existing sync `ExecuteWithRetry`, change line 59:
```csharp
int currentDelay = delay * (int)Math.Pow(2, attempt);
```
to:
```csharp
int currentDelay = Math.Min(delay * (int)Math.Pow(2, attempt), 60);
```

**Step 4: Build and verify**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj`

**Step 5: Commit**
```
fix: add async retry helper, debounce call history saves, cap retry delay (H-14, H-18)
```

---

### Task 15: HIGH Quality — Fix version string + reconnect button + Init() in hot path

**Files:**
- Modify: `3CXDatevConnector/UI/AboutForm.cs:130`
- Modify: `3CXDatevConnector/UI/SettingsForm.cs:661-698`
- Modify: `3CXDatevConnector/Datev/DatevContactRepository.cs:87`

**Step 1: Fix hardcoded version**

Replace (AboutForm.cs line 130):
```csharp
Text = "Version 1.0",
```
with:
```csharp
Text = $"Version {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}",
```

**Step 2: Wrap reconnect in try-finally**

In SettingsForm.cs `BtnReconnectAll_Click`, wrap the body after the disable/text-change in try-finally:
```csharp
private async void BtnReconnectAll_Click(object sender, EventArgs e)
{
    if (_bridgeService == null) return;

    _btnReconnectAll.Enabled = false;
    _btnReconnectAll.Text = UIStrings.Status.TestPending;

    try
    {
        // ... existing reconnect logic ...
    }
    finally
    {
        _btnReconnectAll.Text = UIStrings.Labels.ReconnectAll;
        _btnReconnectAll.Enabled = true;
    }
}
```

**Step 3: Remove Init() from GetContactByNumber hot path**

In `DatevContactRepository.GetContactByNumber` (line 87), remove or guard:
```csharp
// Replace:
Init();
// With:
if (_datevContacts == null) Init();
```

This avoids re-reading config values on every phone lookup while still lazy-loading on first use.

**Step 4: Build and verify**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj`

**Step 5: Commit**
```
fix: dynamic version string, reconnect try-finally, remove Init from hot path (H-16, H-17, H-15)
```

---

### Task 16: MEDIUM Thread Safety — Batch of volatile/lock fixes

**Files:**
- Modify: `3CXDatevConnector/Core/ConnectorService.cs` (M-1: `_extension`)
- Modify: `3CXDatevConnector/Core/CallEventProcessor.cs` (M-2, M-3: stale extension/minCallerIdLength)
- Modify: `3CXDatevConnector/Datev/DatevData/Communication.cs` (M-4: lazy init)
- Modify: `3CXDatevConnector/Core/CallTracker.cs` (M-5: TryAdd fallback)
- Modify: `3CXDatevConnector/Tapi/TapiPipeServer.cs` (M-6: write lock)

**Step 1: M-1 — Make _extension volatile in ConnectorService**

Change the `_extension` field declaration to use `volatile`:
```csharp
private volatile string _extension;
```

**Step 2: M-2, M-3 — Make CallEventProcessor read extension/minCallerIdLength from shared source**

Change `_extension` and `_minCallerIdLength` from `readonly` to settable:
```csharp
private volatile string _extension;
private volatile int _minCallerIdLength;

public void UpdateExtension(string extension) { _extension = extension; }
public void UpdateMinCallerIdLength(int length) { _minCallerIdLength = length; }
```

In `ConnectorService.AdoptExtensionFromProvider`, after updating `_extension`, also call:
```csharp
_callEventProcessor.UpdateExtension(_extension);
_callEventProcessor.UpdateMinCallerIdLength(_minCallerIdLength);
```

**Step 3: M-4 — Fix Communication.EffectiveNormalizedNumber**

Replace the lazy-init pattern with eager computation. In the `EffectiveNormalizedNumber` property, use a simple `lock`:
```csharp
private readonly object _normLock = new object();

public string EffectiveNormalizedNumber
{
    get
    {
        lock (_normLock)
        {
            if (!_effectiveNormalizedNumberComputed)
            {
                _effectiveNormalizedNumber = /* existing computation */;
                _effectiveNormalizedNumberComputed = true;
            }
            return _effectiveNormalizedNumber;
        }
    }
}
```

**Step 4: M-5 — Fix CallTracker.AddCall race**

Replace the TryAdd + indexer fallback pattern:
```csharp
// Replace:
if (!_calls.TryAdd(tapiCallId, record))
    return _calls[tapiCallId];
// With:
return _calls.GetOrAdd(tapiCallId, record);
```

Apply the same pattern to `AddPendingCall`.

**Step 5: M-6 — Add write lock to TapiPipeServer**

Add a field:
```csharp
private readonly object _writeLock = new object();
```

In `TrySend`, wrap the write in the lock:
```csharp
lock (_writeLock)
{
    _pipe.Write(data, 0, data.Length);
    _pipe.Flush();
}
```

In `SendAsync`, also use the lock (or convert to sync under lock since pipe writes are fast).

**Step 6: Build and verify**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj`

**Step 7: Commit**
```
fix: batch thread safety — volatile fields, GetOrAdd, write locks (M-1 through M-6)
```

---

### Task 17: MEDIUM Thread Safety — Remaining concurrency fixes

**Files:**
- Modify: `3CXDatevConnector/Core/CallEventProcessor.cs` (M-7: CancellationToken)
- Modify: `3CXDatevConnector/UI/ContactSelectionForm.cs` (M-9: thread-safe _currentDialog)
- Modify: `3CXDatevConnector/Core/DebugConfigWatcher.cs` (M-10: volatile _instance)

**Step 1: M-7 — Pass CancellationToken to ContactReshowAfterDelayAsync**

Add `CancellationToken ct` parameter to the method signature. Replace `Task.Delay(delayMs)` with `Task.Delay(delayMs, ct)`. Pass `_cts.Token` from the calling `ConnectorService` via the `CallEventProcessor` constructor or a property.

**Step 2: M-9 — Thread-safe _currentDialog**

Add a lock around `_currentDialog` access in `ContactSelectionForm`:
```csharp
private static readonly object _dialogLock = new object();

public static void CloseCurrentDialog()
{
    lock (_dialogLock)
    {
        if (_currentDialog != null && !_currentDialog.IsDisposed)
        {
            var dialog = _currentDialog;
            FormDisplayHelper.PostToUIThread(() =>
            {
                if (!dialog.IsDisposed)
                    dialog.Close();
            });
        }
    }
}
```

**Step 3: M-10 — Make DebugConfigWatcher._instance volatile**

Change:
```csharp
private static DebugConfigWatcher _instance;
```
to:
```csharp
private static volatile DebugConfigWatcher _instance;
```

**Step 4: Build and verify**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj`

**Step 5: Commit**
```
fix: CancellationToken for reshow delay, thread-safe dialog, volatile instance (M-7, M-9, M-10)
```

---

### Task 18: MEDIUM PII — Consistent log masking

**Files:**
- Modify: `3CXDatevConnector/Datev/Managers/CallDataManager.cs` (M-14)
- Modify: `3CXDatevConnector/Datev/COMs/DatevAdapter.cs` (M-15)
- Modify: `3CXDatevConnector/Datev/DatevContactDiagnostics.cs` (M-16)
- Modify: `3CXDatevConnector/Core/CallEventProcessor.cs` (M-17)

**Step 1: Mask PII in all four files**

In each file, find unmasked `name`, `Adressatenname`, `AdressatenId`, `Number` values in `LogManager.Debug()`/`LogManager.Log()` calls and wrap with `LogManager.MaskName()` or `LogManager.Mask()`:

- `CallDataManager.cs`: Wrap `name` and `id` in debug log (lines 36-37, 91-92)
- `DatevAdapter.cs`: Wrap `ctiData.Adressatenname` and `ctiData.AdressatenId` (lines 43-44, 73-74)
- `DatevContactDiagnostics.cs`: Wrap `contact.Name`, `comm.Number`, `comm.NormalizedNumber` (lines 23-38)
- `CallEventProcessor.cs`: Wrap outbound contact name at line 221

**Step 2: Build and verify**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj`

**Step 3: Commit**
```
fix: consistent PII masking in all debug log output (M-14 through M-17)
```

---

### Task 19: MEDIUM Protocol — Event dedup + SafeInvoke + call ID fix

**Files:**
- Modify: `3CXDatevConnector/Webclient/WebclientConnectionMethod.cs` (M-19, M-22, M-23)
- Modify: `3CXDatevConnector/Webclient/WebSocketBridgeServer.cs` (M-20)
- Modify: `3CXDatevConnector/Tapi/PipeConnectionMethod.cs` (M-21, M-23)

**Step 1: M-19 — Fix double Disconnected in WebclientConnectionMethod**

Add a flag `_disconnectedFired` to prevent duplicate events:
```csharp
private volatile bool _disconnectedFired;
```

In `OnTransportDisconnected`, check and set the flag before firing:
```csharp
if (_disconnectedFired) return;
_disconnectedFired = true;
```

Reset it in `OnHelloReceived`:
```csharp
_disconnectedFired = false;
```

Remove the duplicate `Disconnected?.Invoke()` from `StartAsync`'s finally block (guard with the same flag).

**Step 2: M-20 — Fix double Disconnected in WebSocketBridgeServer**

Same pattern: the `ReadLoopAsync` finally block should set a flag that `DisconnectClient` checks. The existing `_conn.Connected = false` check should already prevent this — verify and fix if the race exists.

**Step 3: M-21, M-22 — Use EventHelper.SafeInvoke everywhere**

In `PipeConnectionMethod`, replace all `?.Invoke()` event calls with `EventHelper.SafeInvoke()`.
In `WebclientConnectionMethod`, do the same.

**Step 4: M-23 — Replace GetHashCode with counter for numeric call ID**

In both `PipeConnectionMethod` and `WebclientConnectionMethod`, replace:
```csharp
CallId = Math.Abs(pipeCallId.GetHashCode())
```
with a counter:
```csharp
private static int _numericCallIdCounter;
// ...
CallId = Interlocked.Increment(ref _numericCallIdCounter)
```

**Step 5: Build and verify**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj`

**Step 6: Commit**
```
fix: event dedup, SafeInvoke for all providers, counter-based call IDs (M-19 through M-23)
```

---

### Task 20: MEDIUM Protocol — Bounds check + suffix match + Union fix

**Files:**
- Modify: `3CXDatevConnector/Tapi/TapiLineMonitor.cs` (M-25: ReadStringFromBuffer)
- Modify: `3CXDatevConnector/Datev/DatevContactRepository.cs` (M-27: best suffix, M-28: Union)

**Step 1: M-25 — Add bounds check to ReadStringFromBuffer**

Add a `bufferSize` parameter and validate before reading:
```csharp
if (offset < 0 || size < 0 || offset + size > bufferSize)
    return null;
```

Update all callers to pass the buffer size.

**Step 2: M-27 — Select longest suffix match instead of first**

Replace the suffix matching loop (lines 107-124):
```csharp
DatevContactInfo bestMatch = null;
int bestMatchLength = 0;

foreach (var kvp in _datevContactsSDict)
{
    if (kvp.Key.Length < minSuffixMatchLength)
        continue;

    int matchLen = 0;
    if (normalizedNumber.EndsWith(kvp.Key))
        matchLen = kvp.Key.Length;
    else if (kvp.Key.EndsWith(normalizedNumber))
        matchLen = normalizedNumber.Length;

    if (matchLen > bestMatchLength)
    {
        bestMatchLength = matchLen;
        result = kvp.Value;
    }
}
```

**Step 3: M-28 — Replace Union with Concat**

In `FetchContacts` (line 327), replace:
```csharp
var allContacts = recipients.Union(institutions).ToList();
```
with:
```csharp
var allContacts = recipients.Concat(institutions).ToList();
```

**Step 4: Build and verify**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj`

**Step 5: Commit**
```
fix: bounds check TAPI buffer, best suffix match, explicit Concat (M-25, M-27, M-28)
```

---

### Task 21: MEDIUM Quality — Remaining medium fixes batch

**Files:**
- Modify: `3CXDatevConnector/Datev/COMs/DatevAdapter.cs` (M-18: ClassInterface)
- Modify: `3CXDatevConnector/Core/RetryHelper.cs` (M-38: transient error detection)
- Modify: `3CXDatevConnector/Core/EventHelper.cs` (M-39: log InvalidOperationException)
- Modify: `3CXDatevConnector/Datev/Managers/NotificationManager.cs` (M-37: circuit breaker)
- Modify: `3CXDatevConnector/Core/Config/AppConfig.cs` (M-43: INI comments)
- Modify: `3CXDatevConnector/UI/UITheme.cs` (M-42: GetDirectionColor)

**Step 1: M-18 — Change AutoDual to ClassInterfaceType.None**

```csharp
[ClassInterface(ClassInterfaceType.None)]
```

**Step 2: M-38 — Fix IsTransientError to use exception types**

Replace message-based matching with type-based:
```csharp
public static bool IsTransientError(Exception ex)
{
    return ex is TimeoutException
        || ex is System.Runtime.InteropServices.COMException
        || ex is System.Xml.XmlException
        || ex is System.IO.IOException
        || ex is System.Net.Sockets.SocketException;
}
```

Remove the message string matching entirely.

**Step 3: M-39 — Log InvalidOperationException at debug level**

In EventHelper, change the silent swallow to a debug log:
```csharp
catch (InvalidOperationException ex)
{
    LogManager.Debug("EventHelper: InvalidOperationException (shutdown?): {0}", ex.Message);
}
```

**Step 4: M-37 — Don't record circuit breaker failure for normal unavailability**

In NotificationManager, replace:
```csharp
if (!DatevConnectionChecker.IsDatevAvailable())
{
    _circuitBreaker.RecordFailure();
```
with:
```csharp
if (!DatevConnectionChecker.IsDatevAvailable())
{
    // Don't record as circuit breaker failure -- this is an expected state
```

**Step 5: M-43 — Change // to ; in INI comments**

In AppConfig.GenerateDefaultConfig, replace all `//` comment prefixes with `;`.

**Step 6: M-42 — Fix GetDirectionColor**

Replace:
```csharp
public static Color GetDirectionColor(bool isIncoming)
{
    return AccentIncoming;
}
```
with:
```csharp
public static Color GetDirectionColor(bool isIncoming)
{
    return isIncoming ? AccentIncoming : AccentBridge;
}
```

**Step 7: Build and verify**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj`

**Step 8: Commit**
```
fix: COM interface, transient error types, circuit breaker, INI comments, direction color (M-18, M-37, M-38, M-39, M-42, M-43)
```

---

### Task 22: Final verification

**Step 1: Full clean build**

Run: `dotnet build 3CXDatevConnector/3CXDatevConnector.csproj --configuration Release`
Expected: Build succeeded, 0 errors, 0 warnings.

**Step 2: Check binary size**

Run: `ls -la 3CXDatevConnector/bin/Release/net9.0-windows/`
Expected: Main executable under 750 KB.

**Step 3: Verify git log**

Run: `git log --oneline -20`
Expected: Clean commit history with descriptive messages and finding IDs.
