namespace DatevConnector.UI.Strings
{
    /// <summary>
    /// Centralized German UI strings for all forms and dialogs.
    /// All user-visible text should be defined here for consistency.
    /// </summary>
    public static class UIStrings
    {
        // ===== BUTTON LABELS =====
        public static class Labels
        {
            public const string Test = "Testen";
            public const string Load = "Laden";
            public const string Save = "Speichern";
            public const string Cancel = "Abbrechen";
            public const string Close = "Schließen";
            public const string Status = "Status";
            public const string Refresh = "Aktualisieren";
            public const string Settings = "Einstellungen";
            public const string SendJournal = "Gesprächsnotiz senden";
            public const string ConnectAll = "Alle verbinden";
            public const string OK = "OK";
            public const string Reconnect = "Neu verbinden";
            public const string ReconnectShort = "Neuverbinden";
            public const string Connect = "Verbinden";
            public const string ConnectShort = "Verb.";
            public const string Retry = "Erneut versuchen";
            public const string ReconnectAll = "Alles neu";
            public const string Help = "Hilfe";
            public const string Details = "Details";
            public const string Send = "Senden";
            public const string Select = "Auswählen";
            public const string Search = "Suchen";
            public const string Contacts = "Kontakte";
            public const string CallHistory = "Anrufliste";

            // Shared labels used across multiple forms
            public const string Unknown = "Unbekannt";
            public const string Inbound = "Eingehend";
            public const string Outbound = "Ausgehend";
            public const string Contact = "Kontakt";
            public const string SourceFormat = "Quelle: {0}";
        }

        // ===== STATUS TEXT =====
        public static class Status
        {
            public const string Connected = "Verbunden";
            public const string Disconnected = "Getrennt";
            public const string NotConnected = "Nicht verbunden";
            public const string Connecting = "Verbinde...";
            public const string Partial = "Teilweise";
            public const string PartialConnected = "Teilweise verbunden";
            public const string TestSuccess = "\u2713";
            public const string TestFailed = "\u2717";
            public const string TestPending = "...";
            public const string Available = "Verfügbar";
            public const string Unavailable = "Nicht verfügbar";
            public const string Ready = "Betriebsbereit";
            public const string Checking = "Prüfe...";
            public const string Saved = "Gespeichert";
            public const string AllSystemsReady = "Alle Systeme bereit";
            public const string PartiallyReady = "Teilweise bereit";
            public const string ConnectedExt = "Verbunden ({0})";
            public const string LinesConnected = "{0}/{1} Leitungen verbunden";
            public const string LineStatus = "{0}: {1}";

            // Tray icon tooltip formats
            public const string TrayConnecting = "3CX - DATEV Connector - Verbinde...";
            public const string TrayReadyFormat = "3CX - DATEV Connector - Betriebsbereit ({0})";
            public const string TrayPartialFormat = "3CX - DATEV Connector - Teilweise ({0})";
            public const string TrayDisconnected = "3CX - DATEV Connector - Getrennt";
            public const string TrayRestarting = "3CX - DATEV Connector - Neustart...";
            public const string TapiDisconnectedShort = "TAPI getrennt";
            public const string DatevUnavailableShort = "DATEV nicht verfügbar";
            public const string LastSyncFormat = "Letzte Synchronisierung: {0:HH:mm:ss}";
            public const string LastSyncNone = "Letzte Synchronisierung: \u2014";
        }

        // ===== USER MESSAGES =====
        public static class Messages
        {
            public const string DatevUnavailable = "DATEV ist nicht verfügbar";
            public const string TapiUnavailable = "TAPI-Verbindung nicht verfügbar";
            public const string ContactsLoaded = "{0} Kontakte geladen";
            public const string SettingsSaved = "Einstellungen gespeichert";
            public const string JournalSent = "Gesprächsnotiz an DATEV gesendet";
            public const string NoCallsFound = "Keine Anrufe vorhanden";
            public const string LoadingContacts = "Kontakte werden geladen...";
            public const string RestartRequired = "Bitte Anwendung neu starten.";
            public const string SavedWithCheck = "\u2713 Gespeichert";
            public const string CheckingDatev = "Prüfe DATEV...";
            public const string ConnectingTapi = "Verbinde 3CX TAPI...";
            public const string ContactsFormat = "Kontakte: {0}";
            public const string AppDescription = "Integration zwischen 3CX Windows Softphone App (V20) und DATEV.";
            public const string BalloonNotificationHint = "Für Balloon-Benachrichtigungen müssen Windows-Benachrichtigungen aktiviert sein.\n\nMöchten Sie die Windows-Benachrichtigungseinstellungen öffnen, um dies zu überprüfen?";
            public const string BalloonNotificationTitle = "Benachrichtigungseinstellungen";
            public const string WindowsSettingsOpenFailed = "Windows-Einstellungen konnten nicht geöffnet werden.\n\nBitte öffnen Sie manuell: Einstellungen \u2192 System \u2192 Benachrichtigungen";
        }

        // ===== ERROR MESSAGES =====
        public static class Errors
        {
            public const string DatevDisconnected = "DATEV nicht erreichbar";
            public const string TapiDisconnected = "TAPI-Verbindung getrennt";
            public const string CircuitBreakerOpen = "DATEV-Verbindung pausiert (zu viele Fehler)";
            public const string NoTapiLines = "Keine TAPI-Leitungen gefunden";
            public const string CheckTapi = "3CX TAPI prüfen";
            public const string SaveFailed = "Fehler beim Speichern: {0}";
            public const string LoadFailed = "Neuladen fehlgeschlagen: {0}";
            public const string GenericError = "Fehler";
            public const string LogFileNotFound = "Log-Datei nicht gefunden.";
        }

        // ===== TOOLTIPS =====
        public static class Tooltips
        {
            public const string TestDatev = "DATEV-Verbindung testen";
            public const string TestTapi = "TAPI-Leitung testen";
            public const string LoadContacts = "Kontakte aus DATEV neu laden";
            public const string OpenLog = "Log-Datei im Editor öffnen";
            // With shortcuts (populated dynamically)
            public const string TestAllWithShortcut = "Alle Verbindungen testen (Strg+T)";
            public const string ReloadContactsWithShortcut = "Kontakte neu laden (Strg+R)";
            public const string OpenLogWithShortcut = "Log-Datei öffnen (Strg+L)";
            public const string CallHistoryWithShortcut = "Anrufliste (Strg+H)";
        }

        // ===== MENU ITEMS =====
        public static class MenuItems
        {
            public const string AppTitle = FormTitles.AppTitle;
            public const string Status = Labels.Status;
            public const string ReloadContacts = "\U0001F504 Kontakte neu laden";
            public const string CallHistory = "\U0001F4DE Anrufliste";
            public const string OpenLog = "\U0001F4DD Log-Datei öffnen";
            public const string Settings = "\u2699 Einstellungen";
            public const string Troubleshooting = "\U0001F527 Fehlerbehebung";
            public const string Info = "\u2139 Info";
            public const string Exit = "\u274C Beenden";
            public const string RunSetupWizard = "\U0001F527 Setup-Assistent";
            public const string Autostart = "Autostart";
            public const string Mute = "Stummschalten";
            public const string Restart = "\U0001F504 Neustart";
            public const string Help = "\U0001F4D8 Hilfe";
        }

        // ===== FORM TITLES =====
        public static class FormTitles
        {
            public const string AppTitle = "3CX - DATEV Connector";
            public const string Status = AppTitle;
            public const string Settings = AppTitle;
            public const string CallHistory = AppTitle;
            public const string Journal = AppTitle;
            public const string CallerPopupIncoming = "Eingehender Anruf";
            public const string CallerPopupOutgoing = "Ausgehender Anruf";
            public const string CallerPopupIncomingUpper = "EINGEHENDER ANRUF";
            public const string CallerPopupOutgoingUpper = "AUSGEHENDER ANRUF";
            public const string ContactSelection = AppTitle;
            public const string SetupWizard = AppTitle;
            public const string Troubleshooting = AppTitle;
            public const string About = AppTitle;
            public const string Overview = AppTitle;
        }

        // ===== CALLER POPUP =====
        public static class CallerPopup
        {
            public const string UnknownCaller = "Unbekannter Anrufer";
            public const string UnknownNumber = "Unbekannte Nummer";
            public const string NotInDatev = "Nicht in DATEV-Kontakten";
            public const string Recipient = "Adressaten";
            public const string Institution = "Institution";
            public const string ExtensionFormat = "({0})";
        }

        // ===== CONTACT SELECTION =====
        public static class ContactSelection
        {
            public const string MultipleContactsFound = "Mehrere Kontakte gefunden. Bitte wählen Sie aus.";
            public const string Contact = Labels.Contact;
            public const string Source = Labels.SourceFormat;
            public const string SourceDatev = "DATEV";
            public const string UnknownName = "(unbekannt)";
        }

        // ===== CONFIGURATION =====
        public static class Config
        {
            public const string Export = "Konfiguration exportieren";
            public const string Import = "Konfiguration importieren";
            public const string ResetDefaults = "Auf Standard zurücksetzen";
            public const string ExportSuccess = "Konfiguration wurde exportiert";
            public const string ImportSuccess = "Konfiguration wurde importiert. Bitte Anwendung neu starten.";
            public const string ImportFailed = "Konfiguration konnte nicht importiert werden";
            public const string ResetConfirm = "Alle Einstellungen auf Standard zurücksetzen?";
            public const string ConfigFileFilter = "Konfigurationsdateien (*.json)|*.json";
        }

        // ===== SETUP WIZARD =====
        public static class Wizard
        {
            public const string Title = "3CX - DATEV Connector - Einrichtung";
            public const string Welcome = "Willkommen";
            public const string WelcomeText = "Dieser Assistent hilft Ihnen bei der Einrichtung der 3CX - DATEV Connector.";
            public const string TapiConfig = "TAPI-Konfiguration";
            public const string TapiSelectLine = "Wählen Sie Ihre Telefonleitung:";
            public const string DatevConnection = "DATEV-Verbindung";
            public const string DatevTesting = "DATEV-Verbindung wird geprüft...";
            public const string Finish = "Fertig";
            public const string FinishText = "Die Einrichtung ist abgeschlossen.";
            public const string AutoStart = "3CX - DATEV Connector beim Windows-Start ausführen";
            public const string Back = "Zurück";
            public const string Next = "Weiter";
            public const string StepOf = "Schritt {0} von {1}";

            // Feature list items
            public const string FeatureTapi = "TAPI-Leitung konfigurieren";
            public const string FeaturePipe = "Terminal Server-Verbindung prüfen";
            public const string FeatureDatev = "DATEV-Verbindung prüfen";
            public const string FeatureAutostart = "Autostart einrichten";

            // Summary format strings
            public const string SummaryTapiConnected = "3CX TAPI: {0} Leitung(en) verbunden";
            public const string SummaryDatevConnected = "DATEV: {0} ({1} Kontakte)";

            // First-run prompt
            public const string FirstRunPrompt = "Willkommen bei 3CX - DATEV Connector!\n\nMöchten Sie den Einrichtungsassistenten starten, um TAPI und DATEV zu konfigurieren?";
            public const string FirstRunTitle = "3CX - DATEV Connector - Ersteinrichtung";

            // Terminal Server mode (Named Pipe)
            public const string PipeConfig = "Terminal Server";
            public const string PipeStatus = "Verbindungsstatus:";
            public const string PipeWaiting = "Warte auf 3CX Softphone-Verbindung...";
            public const string PipeConnected = "3CX Softphone verbunden";
            public const string PipeExtension = "Nebenstelle: {0}";
            public const string PipeName = "Pipe: \\\\.\\pipe\\3CX_tsp_server_{0}";
            public const string Softphone3CXRunning = "3CX Softphone läuft in dieser Sitzung";
            public const string Softphone3CXNotRunning = "3CX Softphone nicht erkannt \u2014 bitte starten";

            // Webclient mode
            public const string WebclientConfig = "Webclient (Browser-Erweiterung)";
            public const string WebclientDesc = "Sie verwenden den 3CX Webclient im Browser.\nAnrufereignisse werden über die Browser-Erweiterung empfangen.";
            public const string WebclientInstallSteps = "1. Browser-Erweiterung installieren (Chrome/Edge)\n2. Native Messaging Host registrieren\n3. 3CX Webclient im Browser öffnen\n4. Erweiterung mit Ihrer Nebenstelle verbinden";
            public const string WebclientConnected = "Browser-Erweiterung verbunden";
            public const string WebclientWaiting = "Warte auf Browser-Erweiterung...";
            public const string WebclientNotDetected = "Browser-Erweiterung nicht erkannt";

            // Connection mode selection (setup wizard)
            public const string ModeSelectionTitle = "Telefonie-Modus";
            public const string ModeSelectionDesc = "Wie verbinden Sie sich mit 3CX?";
            public const string ModeOptionTapi = "3CX Windows App (Desktop (TAPI))";
            public const string ModeOptionPipe = "3CX Windows App (Terminal Server (TAPI))";
            public const string ModeOptionWebclient = "3CX Webclient (nur Browser / WebRTC)";
            public const string ModeOptionAuto = "Automatisch erkennen";
            public const string FeatureWebclient = "Webclient / Browser-Erweiterung konfigurieren";
            public const string CopyDiagnostics = "Diagnose kopieren";
            public const string DiagnosticsCopied = "Diagnosedaten in Zwischenablage kopiert";
        }

        // ===== TRAY NOTIFICATIONS =====
        public static class Notifications
        {
            public const string TapiConnected = "3CX TAPI verbunden (Nebenstelle {0})";
            public const string TapiDisconnected = "3CX TAPI-Verbindung getrennt";
            public const string PipeConnected = "3CX Softphone verbunden (Nebenstelle {0})";
            public const string PipeDisconnected = "3CX Softphone-Verbindung getrennt";
            public const string DatevFound = "DATEV erkannt \u2014 Kontakte werden geladen";
            public const string DatevLost = "DATEV Arbeitsplatz nicht gefunden.\nBitte DATEV starten.";
            public const string WebclientConnected = "Webclient verbunden (Nebenstelle {0})";
            public const string WebclientDisconnected = "Browser-Erweiterung getrennt";
            public const string ReloadTitle = "Neu laden";
            public const string ContactsReloading = "Kontakte werden aus DATEV geladen...";
            public const string ContactsReloadedTitle = "Kontakte geladen";
            public const string ContactsReloadedFormat = "{0} Kontakte geladen";
            public const string ContactsReloadFailed = "Kontakte konnten nicht geladen werden: {0}";
            public const string LogFileNotFoundFormat = "Protokolldatei nicht gefunden:\n{0}";
            public const string LogFileTitle = "Protokolldatei";
            public const string LogFileOpenFailed = "Fehler beim Öffnen der Protokolldatei: {0}";
            public const string Restarting = "Neustart...";
            public const string RestartFailed = "Neustart fehlgeschlagen: {0}";
        }

        // ===== KEYBOARD SHORTCUTS =====
        public static class ShortcutLabels
        {
            public const string TestAll = "Alle Verbindungen testen";
            public const string ReloadContacts = "Kontakte neu laden";
            public const string CloseWindow = "Fenster schließen";
            public const string KeyboardShortcuts = "Tastenkürzel";
            public const string SaveSettings = "Einstellungen speichern";
        }

        // ===== CALL HISTORY =====
        public static class CallHistory
        {
            public const string Inbound = Labels.Inbound;
            public const string Outbound = Labels.Outbound;
            public const string InboundDisabled = "Eingehend (deaktiviert)";
            public const string OutboundDisabled = "Ausgehend (deaktiviert)";
            public const string Time = "Zeit";
            public const string Number = "Nummer";
            public const string Contact = Labels.Contact;
            public const string Duration = "Dauer";
            public const string Journal = "Notiz";
            public const string JournalSent = "\u2713 Ja";
            public const string JournalPending = "Offen";
            public const string JournalNone = "\u2014";
            public const string Unknown = Labels.Unknown;
        }

        // ===== JOURNAL FORM =====
        public static class JournalForm
        {
            public const string Title = "Anrufnotiz";
            public const string Header = "ANRUF-JOURNAL";
            public const string ContactLabel = "Kontakt:";
            public const string DurationLabel = "Dauer:";
            public const string DurationFormatHMS = "Dauer: {0}:{1:D2}:{2:D2}";
            public const string DurationFormatMS = "Dauer: {0:D2}:{1:D2}";
            public const string NotePlaceholder = "Notizen zum Gespräch...";
            public const string CharacterCount = "{0}/{1}";
            public const string Send = "An DATEV senden";
            public const string Source = Labels.SourceFormat;
            public const string UnknownContact = Labels.Unknown;
            public const string DefaultSource = "3CX";
        }

        // ===== TROUBLESHOOTING =====
        public static class Troubleshooting
        {
            // Section headers
            public const string CxProblems = "3CX";
            public const string DatevProblems = "DATEV-Probleme";
            public const string ContactProblems = "Kontakt-Probleme";
            public const string OpenLogFile = "Log-Datei öffnen";

            // TAPI problems
            public const string TapiNotConnected = "TAPI nicht verbunden";
            public const string TapiNotConnectedDesc = "Stellen Sie sicher, dass der 3CX Windows App läuft und der TAPI-Treiber installiert ist.";
            public const string TapiDriverNotFound = "3CX TAPI-Treiber nicht gefunden";
            public const string TapiDriverNotFoundDesc = "Installieren und konfigurieren Sie den 3CX Multi-Line TAPI-Treiber.";
            public const string TapiNoLines = "Keine TAPI-Leitungen gefunden";
            public const string TapiNoLinesDesc = "Prüfen Sie die TAPI-Konfiguration in der 3CX Windows App Verwaltung.";

            // DATEV problems
            public const string DatevNotReachable = "DATEV nicht erreichbar";
            public const string DatevNotReachableDesc = "Stellen Sie sicher, dass DATEV Arbeitsplatz läuft und SDD verfügbar ist.";
            public const string DatevCircuitBreaker = "DATEV-Verbindung pausiert";
            public const string DatevCircuitBreakerDesc = "Zu viele Fehler aufgetreten. Warten Sie einige Minuten oder starten Sie die Anwendung neu.";
            public const string DatevSddTimeout = "SDD-Zeitüberschreitung";
            public const string DatevSddTimeoutDesc = "Kontakt-Synchronisation ist gestört. Starten Sie die Anwendung neu oder prüfen Sie die Log-Datei.";

            // Contact problems
            public const string ContactNotFound = "Kontakt nicht gefunden";
            public const string ContactNotFoundDesc = "Passen Sie die Maximallänge (Einstellungen) an Ihr Nummernformat an.";
            public const string NoContactsLoaded = "Keine Kontakte geladen";
            public const string NoContactsLoadedDesc = "Prüfen Sie ob DATEV Arbeitsplatz läuft und Kontakte verfügbar sind.";
            public const string FewerContacts = "Weniger Kontakte als erwartet";
            public const string FewerContactsDesc = "Deaktivierte Kontakte (Status=0) werden standardmäßig gefiltert.";

            // Terminal Server problems
            public const string TsNoConnection = "3CX Softphone verbindet nicht";
            public const string TsNoConnectionDesc = "Deaktivieren und reaktivieren Sie TAPI in den 3CX Softphone-Einstellungen.";
            public const string TsRestartOrder = "Reihenfolge beim Start beachten";
            public const string TsRestartOrderDesc = "Starten Sie zuerst die Bridge, dann den 3CX Softphone (oder TAPI neu aktivieren).";

            // Webclient problems
            public const string WebclientNoExtension = "Browser-Erweiterung nicht verbunden";
            public const string WebclientNoExtensionDesc = "Installieren Sie die 3CX DATEV Connector Browser-Erweiterung und stellen Sie sicher, dass Native Messaging aktiviert ist.";
            public const string WebclientTimeout = "Verbindungs-Timeout";
            public const string WebclientTimeoutDesc = "Erhöhen Sie den Webclient Timeout in den Einstellungen oder der INI-Datei.";

            // Environment badge
            public const string DetectedEnvironmentFormat = "Erkannte Umgebung: {0}";
            public const string EnvDesktopTapi = "Desktop (TAPI)";
            public const string EnvTerminalServer = "Terminal Server (TAPI)";
            public const string EnvWebClient = "WebClient";
            public const string EnvAuto = "Automatisch";

            // General
            public const string CommonProblems = "Häufige Probleme";
            public const string Solutions = "Lösungen";
            public const string NeedMoreHelp = "Weitere Hilfe benötigt?";
            public const string CheckLogFile = "Prüfen Sie die Log-Datei für detaillierte Fehlermeldungen.";
            public const string RestartApp = "Anwendung neu starten";
            public const string RestartAppDesc = "Viele Probleme können durch einen Neustart behoben werden.";
        }

        // ===== SECTIONS =====
        public static class Sections
        {
            public const string Datev = "DATEV";
            public const string Tapi = "3CX";
            public const string Bridge = "Connector";
            public const string CallPopUp = "Contact-Pop-Up";
            public const string CallNote = "Journal-Pop-Up";
            public const string MultipleContacts = "Multiple Kontakte";
            public const string Advanced = "Erweitert";
            public const string CallerId = "Anruferkennung";
            public const string CallHistorySection = "Anrufliste";
            public const string Webclient = "WebClient";
            public const string ConnectionMode = "Verbindungsmodus";
        }

        // ===== SETTINGS LABELS =====
        public static class SettingsLabels
        {
            public const string Incoming = "Eingehend";
            public const string Outgoing = "Ausgehend";
            public const string Window = "Fenster";
            public const string Journaling = "Gesprächsnotiz";
            public const string Notification = "Benachrichtigungen";
            public const string Reselection = "Wiederauswahl";
            public const string Seconds = "Sek.";
            public const string Disabled = "0 = deaktiviert";
            public const string MinLength = "Mindestlänge:";
            public const string MaxCompare = "Maximallänge:";
            public const string InboundCalls = Labels.Inbound;
            public const string OutboundCalls = Labels.Outbound;
            public const string Count = "Anzahl:";
            public const string ActiveContacts = "Aktive Kontakte";
            public const string Contacts = "Kontakte: {0}";
            public const string Extension = "{0}";
            public const string Sync = "Sync: {0}";
            public const string TrayDoubleClickHistory = "Doppelklick: Anrufliste";
            public const string ConnectionMode = "Verbindungsmodus:";
            public const string ConnectionModeAuto = "Automatisch";
            public const string ConnectionModeTapi = "Desktop (TAPI)";
            public const string ConnectionModePipe = "Terminal Server (TAPI)";
            public const string ConnectionModeWebclient = "Webclient (Browser)";
            public const string WebclientTimeout = "Webclient Timeout:";
        }
    }
}
