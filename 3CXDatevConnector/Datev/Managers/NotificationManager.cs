using DatevConnector.Core;
using DatevConnector.Core.Config;
using DatevConnector.Datev.COMs;
using DatevConnector.Interop;
using System;
using System.Runtime.InteropServices;

namespace DatevConnector.Datev.Managers
{
    internal class NotificationManager
    {
        private Guid _clsIdDatev;
        private readonly CircuitBreaker _circuitBreaker;

        internal NotificationManager(Guid clsIdDatev)
        {
            _clsIdDatev = clsIdDatev;

            int failureThreshold = AppConfig.GetIntClamped(ConfigKeys.DatevCircuitBreakerThreshold, 3, 1, 10);
            int openTimeout = AppConfig.GetIntClamped(ConfigKeys.DatevCircuitBreakerTimeoutSeconds, 30, 10, 300);

            _circuitBreaker = new CircuitBreaker("DATEV", failureThreshold, openTimeout);
        }

        internal bool NewCall(CallData objCallData)
        {
            LogManager.Log("DATEV: NewCall (CallId={0}, Direction={1}, Number={2}, Contact={3}, DataSource={4})",
                objCallData.CallID, objCallData.Direction, LogManager.Mask(objCallData.CalledNumber),
                string.IsNullOrEmpty(objCallData.Adressatenname) ? "(none)" : LogManager.MaskName(objCallData.Adressatenname),
                string.IsNullOrEmpty(objCallData.DataSource) ? "(empty)" : objCallData.DataSource);

            Action<IDatevCtiNotification> action = s =>
            {
                // Note: SyncID is NOT set by the bridge - only DATEV Telefonie sets this
                s.NewCall(objCallData);
            };

            return NotificationWrapper(action, "NewCall");
        }

        internal bool CallStateChanged(CallData objCallData)
        {
            LogManager.Log("DATEV: CallStateChanged (CallId={0}, State={1})",
                objCallData.CallID, objCallData.CallState);

            return NotificationWrapper(s => s.CallStateChanged(objCallData), "CallStateChanged");
        }

        internal bool CallAdressatChanged(CallData objCallData)
        {
            LogManager.Log("DATEV: CallAdressatChanged (CallId={0}, Contact={1}, DataSource={2})",
                objCallData.CallID,
                string.IsNullOrEmpty(objCallData.Adressatenname) ? "(none)" : LogManager.MaskName(objCallData.Adressatenname),
                objCallData.DataSource);

            return NotificationWrapper(s => s.CallAdressatChanged(objCallData), "CallAdressatChanged");
        }

        internal bool NewJournal(CallData objCallData)
        {
            TimeSpan duration = objCallData.End - objCallData.Begin;
            LogManager.Log("DATEV: NewJournal (CallId={0}, Duration={1:hh\\:mm\\:ss}, Contact={2}, DataSource={3}, Number={4})",
                objCallData.CallID, duration,
                string.IsNullOrEmpty(objCallData.Adressatenname) ? "(none)" : LogManager.MaskName(objCallData.Adressatenname),
                string.IsNullOrEmpty(objCallData.DataSource) ? "(empty)" : objCallData.DataSource,
                LogManager.Mask(objCallData.CalledNumber) ?? "(none)");

            return NotificationWrapper(s => s.NewJournal(objCallData), "NewJournal");
        }

        private bool NotificationWrapper(Action<IDatevCtiNotification> action, string actionName)
        {
            if (!_circuitBreaker.IsOperationAllowed())
            {
                LogManager.Log("Benachrichtigung '{0}' übersprungen - DATEV Circuit-Breaker offen", actionName);
                return false;
            }

            if (!DatevConnectionChecker.IsDatevAvailable())
            {
                // Don't record as circuit breaker failure - DATEV not running is an expected state
                LogManager.Log("Benachrichtigung '{0}' übersprungen - DATEV nicht verfügbar", actionName);
                return false;
            }

            bool result = false;
            object datevObj = null;
            IDatevCtiNotification objNotification = null;

            try
            {
                uint i = 0;

                uint hr = Rot.GetActiveObject(ref _clsIdDatev, ref i, out datevObj);
                if (hr != 0) datevObj = null;

                if (datevObj == null)
                {
                    _circuitBreaker.RecordFailure();
                    LogManager.Log("Benachrichtigung '{0}' Fehler - DATEV nicht in ROT gefunden", actionName);
                    return false;
                }

                objNotification = datevObj as IDatevCtiNotification;

                if (objNotification != null)
                {
                    action(objNotification);
                    _circuitBreaker.RecordSuccess();
                    LogManager.Debug("DATEV: {0} erfolgreich gesendet", actionName);
                    result = true;
                }
                else
                {
                    _circuitBreaker.RecordFailure();
                    LogManager.Log("Benachrichtigung '{0}' Fehler - DATEV-Objekt implementiert nicht IDatevCtiNotification", actionName);
                }
            }
            catch (COMException comEx)
            {
                _circuitBreaker.RecordFailure();
                LogManager.Log("Benachrichtigung '{0}' COM-Fehler: HRESULT=0x{1:X8}, {2}", actionName, comEx.HResult, comEx.Message);
            }
            catch (InvalidCastException castEx)
            {
                _circuitBreaker.RecordFailure();
                LogManager.Log("Benachrichtigung '{0}' Cast-Fehler: {1}", actionName, castEx.Message);
            }
            catch (Exception ex)
            {
                _circuitBreaker.RecordFailure();
                LogManager.Log("Benachrichtigung '{0}' Fehler: {1}", actionName, ex.Message);
            }
            finally
            {
                // Release COM objects to prevent reference count leaks
                if (objNotification != null && Marshal.IsComObject(objNotification))
                    Marshal.ReleaseComObject(objNotification);
                if (datevObj != null && Marshal.IsComObject(datevObj))
                    Marshal.ReleaseComObject(datevObj);
            }

            return result;
        }
    }
}
