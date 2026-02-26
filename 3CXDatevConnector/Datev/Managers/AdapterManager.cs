using DatevConnector.Core;
using DatevConnector.Datev.COMs;
using DatevConnector.Interop;
using System;

namespace DatevConnector.Datev.Managers
{
    internal static class AdapterManager
    {
        private static DatevAdapter _adapter;
        private static uint _registrationId;

        internal static void Register(DatevAdapter adapter)
        {
            try
            {
                // Use base GUID - Windows ROT is already per-session on terminal servers,
                // so no session-specific GUID modification is needed.
                Guid adapterGuid = adapter.GetType().GUID;

                const uint flags = 0; //ACTIVEOBJECT_STRONG

                uint hr = Rot.RegisterActiveObject(adapter, ref adapterGuid, flags, out _registrationId);
                if (hr != 0)
                {
                    LogManager.Warning("ROT RegisterActiveObject fehlgeschlagen: HRESULT=0x{0:X8}", hr);
                }

                _adapter = adapter;

                LogManager.Debug("DatevConnectorAdapter registriert in ROT");
            }
            catch (Exception e)
            {
                throw new Exception("Cannot register DatevAdapter.", e);
            }
        }

        internal static void Unregister()
        {
            if (_registrationId != 0)
            {
                try
                {
                    Rot.RevokeActiveObject(_registrationId, IntPtr.Zero);
                    LogManager.Log("DatevAdapter aus ROT abgemeldet");
                }
                catch (Exception ex)
                {
                    LogManager.Log("Fehler beim Abmelden des DatevAdapter: {0}", ex.Message);
                }
                
                _registrationId = 0;
                _adapter = null;
            }
        }
    }
}
