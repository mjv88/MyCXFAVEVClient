using DatevConnector.Core;
using DatevConnector.Datev.Constants;
using DatevConnector.Datev.Managers;
using DatevConnector.Interop;
using System;
using System.Runtime.InteropServices;

namespace DatevConnector.Datev
{
    public static class DatevConnectionChecker
    {
        private static DateTime _lastCheckTime = DateTime.MinValue;
        private static bool _lastCheckResult = false;
        private static readonly TimeSpan CacheTimeout = TimeSpan.FromSeconds(5);
        private static readonly object _lock = new object();

        /// <summary>
        /// Checks if DATEV application is running and available for CTI integration.
        /// Results are cached for a short period to avoid excessive COM lookups.
        /// </summary>
        public static bool IsDatevAvailable()
        {
            lock (_lock)
            {
                if (DateTime.UtcNow - _lastCheckTime < CacheTimeout)
                {
                    return _lastCheckResult;
                }

                _lastCheckResult = CheckDatevAvailabilityInternal();
                _lastCheckTime = DateTime.UtcNow;
                return _lastCheckResult;
            }
        }

        /// <summary>
        /// Forces a fresh check, bypassing the cached result.
        /// </summary>
        public static bool CheckDatevAvailability()
        {
            lock (_lock)
            {
                _lastCheckResult = CheckDatevAvailabilityInternal();
                _lastCheckTime = DateTime.UtcNow;
                return _lastCheckResult;
            }
        }

        private static bool CheckDatevAvailabilityInternal()
        {
            try
            {
                // Use base GUID - Windows ROT is already per-session on terminal servers,
                // so no session-specific GUID modification is needed.
                Guid clsId = CommonParameters.ClsIdDatev;
                uint reserved = 0;

                uint result = Rot.GetActiveObject(ref clsId, ref reserved, out object obj);

                if (result == 0 && obj != null)
                {
                    // Release COM object to avoid reference count leaks
                    if (Marshal.IsComObject(obj))
                        Marshal.ReleaseComObject(obj);

                    LogManager.Debug("DATEV in ROT erkannt (verfügbar)");
                    return true;
                }
                else
                {
                    LogManager.Debug("DATEV nicht in ROT gefunden (Ergebnis=0x{0:X8})", result);
                }
            }
            catch (Exception ex)
            {
                LogManager.Log("DATEV Verfügbarkeitsprüfung fehlgeschlagen: {0}", ex.Message);
            }

            return false;
        }

        /// <summary>
        /// Performs a comprehensive DATEV connection test.
        /// Checks DATEV processes, Telefonie ROT entry, and SDD availability.
        /// </summary>
        public static bool CheckAndLogDatevStatus()
        {
            return CheckAndLogDatevStatus(null);
        }

        /// <summary>
        /// Performs a comprehensive DATEV connection test with progress reporting.
        /// Checks DATEV processes, Telefonie ROT entry, and SDD availability.
        /// </summary>
        public static bool CheckAndLogDatevStatus(Action<string> progressText)
        {
            LogManager.Log("========================================");
            LogManager.Log("DATEV Konnektivitätstest");
            LogManager.Log("========================================");

            if (SessionManager.IsTerminalSession)
            {
                LogManager.Log("Terminal Server Session: Id={0}", SessionManager.SessionId);
            }

            // Check DATEV Telefonie in ROT (COM integration for call notifications)
            progressText?.Invoke("Suche DATEV Arbeitsplatz...");
            bool rotAvailable = CheckDatevAvailability();
            LogManager.Log("DATEV Telefonie (ROT): {0}", rotAvailable ? "Verfügbar" : "NICHT VERFÜGBAR");

            if (rotAvailable)
                progressText?.Invoke("DATEV Arbeitsplatz gefunden");
            else
                progressText?.Invoke("DATEV Arbeitsplatz nicht gefunden");

            // Check SDD availability (contact data via IPC - independent of ROT)
            progressText?.Invoke("Suche DATEV Stammdatendienst...");
            bool sddAvailable = CheckSddAvailability();
            LogManager.Log("DATEV SDD (Kontakte): {0}", sddAvailable ? "Verfügbar" : "NICHT VERFÜGBAR");

            if (sddAvailable)
                progressText?.Invoke("DATEV Stammdatendienst gefunden");
            else
                progressText?.Invoke("DATEV Stammdatendienst nicht gefunden");

            if (rotAvailable && sddAvailable)
            {
                LogManager.Log("DATEV Alle Komponente Verfügbar");
                progressText?.Invoke("DATEV verfügbar");
            }
            else if (sddAvailable)
            {
                LogManager.Log("DATEV SDD verfügbar - Kontakte erreichbar (CTI noch nicht verfügbar)");
                progressText?.Invoke("DATEV Kontakte verfügbar");
            }
            else
            {
                LogManager.Warning("DATEV nicht erkannt");
                progressText?.Invoke("DATEV nicht verfügbar");
            }

            // Available if either component is detected - SDD works via IPC independently of ROT
            return rotAvailable || sddAvailable;
        }

        private static bool CheckSddAvailability()
        {
            try
            {
                // Try to load the SDD assembly dynamically (resolved via GacAssemblyResolver on .NET 9)
                var assembly = System.Reflection.Assembly.Load("Datev.Sdd.Data.ClientInterfaces");
                if (assembly != null)
                {
                    LogManager.Debug("SDD Assembly geladen: {0}", assembly.FullName);
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogManager.Debug("SDD Verfügbarkeitsprüfung fehlgeschlagen: {0}", ex.Message);
            }

            return false;
        }

    }
}
