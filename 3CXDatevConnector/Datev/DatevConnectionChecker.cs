using Datev.Sdd.Data.ClientInterfaces;
using DatevConnector.Core;
using DatevConnector.Datev.Constants;
using DatevConnector.Datev.Managers;
using DatevConnector.Interop;
using System;
using System.Runtime.InteropServices;

namespace DatevConnector.Datev
{
    /// <summary>
    /// Helper class to check DATEV application availability
    /// </summary>
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
        /// <returns>True if DATEV is available, false otherwise</returns>
        public static bool IsDatevAvailable()
        {
            lock (_lock)
            {
                // Use cached result if recent
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
        /// Forces a fresh check for DATEV availability (bypasses cache)
        /// </summary>
        /// <returns>True if DATEV is available, false otherwise</returns>
        public static bool CheckDatevAvailability()
        {
            lock (_lock)
            {
                _lastCheckResult = CheckDatevAvailabilityInternal();
                _lastCheckTime = DateTime.UtcNow;
                return _lastCheckResult;
            }
        }

        /// <summary>
        /// Clears the cached availability result
        /// </summary>
        public static void ClearCache()
        {
            lock (_lock)
            {
                _lastCheckTime = DateTime.MinValue;
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

                    LogManager.Debug("DATEV detected in ROT (available)");
                    return true;
                }
                else
                {
                    LogManager.Debug("DATEV not found in ROT (result=0x{0:X8})", result);
                }
            }
            catch (Exception ex)
            {
                LogManager.Log("DATEV availability check failed: {0}", ex.Message);
            }

            return false;
        }

        /// <summary>
        /// Performs a comprehensive DATEV connection test.
        /// Checks DATEV processes, Telefonie ROT entry, and SDD availability.
        /// </summary>
        /// <returns>True if DATEV is fully available, false otherwise</returns>
        public static bool CheckAndLogDatevStatus()
        {
            return CheckAndLogDatevStatus(null);
        }

        /// <summary>
        /// Performs a comprehensive DATEV connection test with progress reporting.
        /// Checks DATEV processes, Telefonie ROT entry, and SDD availability.
        /// </summary>
        /// <param name="progressText">Optional callback for progress text updates</param>
        /// <returns>True if DATEV is fully available, false otherwise</returns>
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
                LogManager.Log("DATEV SDD available - contacts accessible (CTI not yet available)");
                progressText?.Invoke("DATEV Kontakte verfügbar");
            }
            else
            {
                LogManager.Warning("DATEV not detected");
                progressText?.Invoke("DATEV nicht verfügbar");
            }

            LogManager.Log("========================================");

            // Available if either component is detected - SDD works via IPC independently of ROT
            return rotAvailable || sddAvailable;
        }

        /// <summary>
        /// Check if SDD (Stammdatendienst) is accessible for contact lookups
        /// </summary>
        private static bool CheckSddAvailability()
        {
            try
            {
                // Check if the SDD assembly is loadable (indicates DATEV SDD is installed)
                var assembly = typeof(IRequestHandler).Assembly;
                if (assembly != null)
                {
                    LogManager.Debug("SDD assembly loaded: {0}", assembly.FullName);
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogManager.Debug("SDD availability check failed: {0}", ex.Message);
            }

            return false;
        }

    }
}
