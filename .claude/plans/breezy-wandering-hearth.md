# README.md Accuracy Fixes

## Context

Line-by-line audit of README.md against the actual codebase revealed **4 issues to fix** and **1 version history update**. The README is overall very accurate — most of its 1128 lines are correct.

---

## Issues Found

### 1. Architecture Diagram Missing 3 UI Files (lines ~507-524)

**Problem:** Three actively-used UI files are missing from the architecture diagram:
- `FormNavigator.cs` — form navigation/lifecycle management (used by TrayApplication)
- `FormDisplayHelper.cs` — UI thread marshaling for non-modal forms
- `TrayContextMenuBuilder.cs` — builds the dark-themed tray context menu (explicitly referenced in README text but missing from diagram)

**Fix:** Add these 3 files to the `UI/` section of the architecture diagram, after `TrayApplication.cs`.

---

### 2. Pipe Security Description Contradicts Implementation (lines 454 vs 736)

**Problem:** Line 454 says pipe is secured using DACL scoped to "current user's SID" only. But:
- Line 736 says it grants access to `ALL APPLICATION PACKAGES (S-1-15-2-1)`
- Actual code in `TapiPipeServer.cs` lines 69-113 grants: current user (FullControl) + ALL APPLICATION PACKAGES (ReadWrite) + Everyone/WorldSid (ReadWrite fallback)

The Security section (lines 454-480) is misleading — it implies strict current-user-only access, but the real implementation is intentionally permissive to support MSIX/AppContainer 3CX apps.

**Fix:** Update the Security section (lines 454-480) to accurately describe the three-tier DACL: current user (FullControl), ALL APPLICATION PACKAGES (ReadWrite for MSIX/AppContainer), Everyone (ReadWrite fallback). Remove or update the code sample that shows only current-user access. Keep the Hardening Recommendations as-is since they're aspirational.

---

### 3. Two Config Keys Undocumented (Configuration Options table)

**Problem:** Two config keys exist in `ConfigKeys.cs` and are used in code but are not listed in the Configuration Options table (lines 198-241):
- `ContactLoadTimeoutSeconds` (default: 120) — timeout for DATEV SDD contact loading
- `TapiLineFilter` — TAPI line name filter pattern

**Fix:** Add both keys to the Configuration Options table in their respective sections.

---

### 4. Settings Dashboard Description Incomplete (line 368)

**Problem:** The Settings Dashboard table (lines 366-371) doesn't mention the **Telephony Mode** card/section that exists in `SettingsForm.cs`. This card shows mode selection (Auto/Tapi/Pipe/WebClient) and was added with the ModeChanged event in v1.2.0.

**Fix:** Add a row for the Telephony Mode section to the Settings Dashboard table.

---

### 5. Version History Missing Modal-to-Non-Modal Conversion

**Problem:** Version 1.2.0 (line 1116) doesn't mention the conversion of all 5 modal dialogs (AboutForm, TroubleshootingForm, SetupWizardForm, JournalForm, ContactSelectionForm) from `ShowDialog()` to non-modal `Show()` with singleton/callback patterns.

**Fix:** Append to the v1.2.0 entry: description of non-modal conversion with singleton pattern for read-only forms and callback pattern for interactive forms.

---

## Files to Modify

| # | File | Changes |
|---|------|---------|
| 1 | `README.md` | All 5 fixes above |

## Implementation Order

1. Fix architecture diagram (add 3 missing files)
2. Fix pipe security description (rewrite lines 454-480)
3. Add 2 undocumented config keys to table
4. Add Telephony Mode row to Settings Dashboard table
5. Append modal-to-non-modal note to v1.2.0 version history

## Verification

- Read through each changed section to ensure accuracy
- Grep for any remaining `ShowDialog()` references (should find none except MessageBox)
- Confirm all files listed in updated architecture diagram exist via Glob
