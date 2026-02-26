# Code Audit: 3CX-DATEV Connector

**Date:** 2026-02-26
**Scope:** Full 360 codebase audit (92 source files, ~17,300 LOC)
**Approach:** Depth-first on critical paths, breadth sweep on remaining code
**Constraints:** YAGNI, lightweight tray app, stay under 750 KB

## Overall Assessment: 8.5/10

Well-architected .NET 9 tray application with clean layered design, provider pattern for connection modes, and thoughtful state management. Most issues are in thread safety, event lifecycle, and a handful of security gaps.

---

## Findings

### CRITICAL (4)

#### C-1: SetupWizard leaks GDI handles on step transitions
- **File:** `SetupWizardForm.cs:104`
- **Issue:** `Controls.Clear()` removes but doesn't dispose controls. Each step navigation leaks native HWNDs and GDI objects. Windows caps at ~10,000 per process.
- **Fix:** Dispose each child control before clearing:
  ```csharp
  while (_contentPanel.Controls.Count > 0)
  {
      var ctl = _contentPanel.Controls[0];
      _contentPanel.Controls.Remove(ctl);
      ctl.Dispose();
  }
  ```

#### C-2: CallerPopupForm fade-out creates re-entrant close loop
- **File:** `CallerPopupForm.cs:138-165`
- **Issue:** `OnFormClosing` cancels close and starts fade timer that calls `Close()` again. Re-entrant close between `_fadingOut = false` and `Close()` starts a second timer.
- **Fix:** Add `_closingForReal` flag that bypasses fade logic on the final close.

#### C-3: CallStateMachine + CallRecord have no thread safety
- **Files:** `CallStateMachine.cs:42-62`, `CallRecord.cs`
- **Issue:** Check-then-act on `record.TapiState` with no synchronization. Properties read/written from TAPI threads, timer threads, and UI thread simultaneously. Two concurrent events for the same call can corrupt state.
- **Fix:** Add a `lock` object to `CallRecord`; use it in `TryTransition` and property access. Keep it simple -- no `Interlocked.CompareExchange` gymnastics.

#### C-4: DPAPI decryption failure silently destroys call history
- **File:** `CallHistoryStore.cs:146-187`
- **Issue:** If `Unprotect` fails, catch block clears both lists. Next `Save()` overwrites with empty data. Permanent data loss.
- **Fix:** Rename corrupted file to `.bak` before clearing. Log warning.

---

### HIGH (18)

#### Security (3)

**H-1: WebSocket has no authentication** (`WebSocketBridgeServer.cs:85-120`)
Any local process can connect to `127.0.0.1:19800` and inject fake call events.
Fix: Validate a shared secret in the HELLO message. Generate a random token at startup and pass it to the browser extension via native messaging.

**H-2: Named pipe grants Everyone ReadWrite access** (`TapiPipeServer.cs:71-82`)
On terminal servers, any user can connect and inject fake call events.
Fix: Remove `Everyone` ACL; current user SID + AppContainer SID is sufficient.

**H-3: GAC assembly resolver loads first DLL without version/signature check** (`GacAssemblyResolver.cs:77-84`)
Fix: Compare `GetPublicKeyToken()` before loading.

#### Reliability (10)

**H-4: Event handler leak in ConnectorService retry loop** (`ConnectorService.cs:457-476`)
Each reconnect subscribes new lambdas without unsubscribing from old provider.
Fix: Extract to named methods; unsubscribe before subscribing.

**H-5: `DatevAvailable` not thread-safe** (`ConnectorService.cs:85-90`)
Fix: Back with `volatile bool` field.

**H-6: `TapiCallEvent` mutated across threads** (`TapiLineMonitor.cs:38-75,370-413`)
Fix: Make immutable -- create new instance per state change, replace atomically in dictionary.

**H-7: `TapiLineInfo.Handle` compound operations not atomic** (`TapiLineMonitor.cs`, `TapiLineManager.cs`)
Fix: Use `Interlocked.Exchange` for handle swaps; add lock around check-then-act in `ReconnectLine`.

**H-8: Pipe read loop continues after invalid message length** (`TapiPipeServer.cs:197-201`)
`continue` desynchronizes the pipe protocol permanently.
Fix: Replace `continue` with `break`.

**H-9: ROT RegisterActiveObject HRESULT not checked** (`AdapterManager.cs:23`)
Fix: Check return value; log error on failure.

**H-10: ROT GetActiveObject HRESULT not checked** (`NotificationManager.cs:94`)
Fix: Capture and check HRESULT.

**H-11: FormNavigator double-dispose** (`FormNavigator.cs:116-141`)
`Close()` on non-modal form already disposes it. Explicit `Dispose()` after is double-dispose.
Fix: Remove explicit `Dispose()` call.

**H-12: `_cts` read-then-use race** (`ConnectorService.cs:553-558`)
Fix: Capture to local variable; add try-catch for `ObjectDisposedException`.

**H-13: Cross-thread UI access in SetupWizard** (`SetupWizardForm.cs:523-549`)
Fix: Wrap post-await UI accesses in `SafeInvoke()`.

#### Performance (2)

**H-14: RetryHelper.Thread.Sleep blocks calling thread** (`RetryHelper.cs:63`)
Up to ~8 minutes of blocking. Can freeze UI.
Fix: Add `ExecuteWithRetryAsync` variant using `Task.Delay`. Keep sync version for callers that need it but cap max delay.

**H-18: CallHistoryStore.Save() on every AddEntry** (`CallHistoryStore.cs:86-87`)
Full serialize + encrypt + write on every call event.
Fix: Mark dirty + save on a simple timer (5s). Keep immediate save for `MarkJournalSent`.

#### Quality (3)

**H-16: Hardcoded "Version 1.0"** (`AboutForm.cs:131`)
Fix: Use `Assembly.GetExecutingAssembly().GetName().Version`.

**H-17: SettingsForm reconnect button stuck on exception** (`SettingsForm.cs:661-698`)
Fix: Wrap in try-finally.

**H-15 (simplified): `Init()` called in every `GetContactByNumber`** (`DatevContactRepository.cs:88`)
Reads config values on every lookup inside the lock.
Fix: Move `Init()` call out of the lookup method. Call at startup and explicit reload only.

---

### MEDIUM (43) — Grouped by Theme

#### Thread Safety (10)
- M-1: `_extension` in ConnectorService not `volatile`
- M-2: CallEventProcessor `_extension` captured at construction, never updated
- M-3: CallEventProcessor `_minCallerIdLength` also stale
- M-4: `Communication.EffectiveNormalizedNumber` lazy init not thread-safe
- M-5: `CallTracker.AddCall` race between TryAdd and indexer fallback -- use `GetOrAdd`
- M-6: `TapiPipeServer` lacks write lock for concurrent sends
- M-7: `ContactReshowAfterDelayAsync` no CancellationToken
- M-8: Fire-and-forget `Task.Run` in `StartDatevAutoDetect` -- store and observe in Dispose
- M-9: `ContactSelectionForm._currentDialog` not thread-safe
- M-10: `DebugConfigWatcher._instance` access not fully thread-safe

#### Security & PII (8)
- M-11: WebSocket frame length can be negative (add `len < 0` check)
- M-12: WebSocket single-client vulnerable to connection hijacking
- M-13: Contact dump writes unmasked PII to disk
- M-14: PII unmasked in CallDataManager debug logs
- M-15: PII unmasked in DatevAdapter debug logs
- M-16: PII unmasked in DatevContactDiagnostics
- M-17: Outbound call logs unmasked contact name
- M-18: `AutoDual` COM interface on DatevAdapter -- change to `ClassInterfaceType.None`

#### Reliability & Protocol (8)
- M-19: Double `Disconnected` event in WebclientConnectionMethod
- M-20: Double `Disconnected` in WebSocketBridgeServer
- M-21: PipeConnectionMethod events not via SafeInvoke
- M-22: WebclientConnectionMethod events not via SafeInvoke
- M-23: `GetHashCode()` used as numeric call ID -- use counter instead
- M-25: `ReadStringFromBuffer` no bounds check on TAPI struct offsets
- M-27: Suffix match returns first match, not best (longest) match
- M-28: `Union` without equality override is effectively `Concat`

#### Performance & Quality (7)
- M-29: `TapiPipeServer.TrySend` synchronous I/O on async pipe
- M-31: `TapiOperations.TestLine` uses `Thread.Sleep`
- M-32: `TapiLineMonitor.Dispose` doesn't signal cancellation to message loop
- M-37: Circuit breaker triggered by normal "unavailable" state
- M-38: `IsTransientError` relies on English message strings
- M-39: `EventHelper` silently swallows `InvalidOperationException`
- M-43: INI config uses `//` comments (non-standard for Windows INI)

#### UI Quality (5)
- M-26: `TapiMessage` parser doesn't handle commas in values
- M-36: SDD check only tests assembly load, not actual connectivity
- M-41: CallHistoryForm auto-refresh flickers every 5s
- M-42: `GetDirectionColor` ignores parameter, always returns `AccentIncoming`
- M-14b: `CreateStatusCard` ignores `accentColor` parameter

---

### LOW (41) — Not Listed Individually

Includes: unused dead code (`TapiCallState` enum), `GC.SuppressFinalize` without finalizers, missing `volatile` on low-risk fields, hardcoded German strings outside `UIStrings`, GDI handle leaks in `RoundedPanel.OnPaint`, `CallData.ToString()` string concat in StringBuilder, `Rot` class should be static, `CallData.Begin/End` default to year 0001, minor code duplication in AboutForm, `LogManager.MaskValue` reads config per call, `WebSocket NoDelay` not set, `SimpleJsonParser` doesn't handle `\uXXXX` escapes or arrays, and similar minor items.

---

## YAGNI Filter Applied

The following recommendations from the raw audit were **dropped or simplified** to keep the app lightweight:

| Dropped/Simplified | Reason |
|---|---|
| `ReaderWriterLockSlim` for contact repository | Over-engineered. Moving `Init()` out of hot path is sufficient. |
| Reverse-string trie for suffix matching | Overkill. Simple "iterate all, pick longest" is enough. |
| WebSocket continuation frame assembly | Browser extension sends single frames. Document constraint. |
| Keep `StreamWriter` open for log writing | Adds state management. Current volume doesn't justify it. |
| Cache flattened contact lists | Premature optimization for typical DATEV sizes. |
| Async retry helper with `SemaphoreSlim` | Only add `ExecuteWithRetryAsync` with `Task.Delay`. No semaphores. |
| Unit test project | Valuable but out of scope for this audit's fix plan. |
| Telemetry infrastructure | Not needed for a tray app. |

---

## Priority Order for Implementation

1. **CRITICAL fixes** (C-1 through C-4) -- data loss prevention and crash fixes
2. **HIGH security** (H-1 through H-3) -- authentication and ACL hardening
3. **HIGH reliability** (H-4 through H-13) -- race conditions and event leaks
4. **HIGH performance/quality** (H-14 through H-18) -- blocking calls and UI bugs
5. **MEDIUM thread safety** (M-1 through M-10) -- volatile, locks, stale values
6. **MEDIUM PII masking** (M-14 through M-17) -- consistent log masking
7. **MEDIUM protocol/reliability** (M-19 through M-28) -- event dedup, SafeInvoke
8. **MEDIUM quality** (remaining) -- UI fixes, config corrections
9. **LOW** -- address opportunistically during related changes
