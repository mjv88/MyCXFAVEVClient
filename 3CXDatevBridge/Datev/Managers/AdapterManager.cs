using DatevBridge.Core;
using DatevBridge.Datev.COMs;
using DatevBridge.Interop;
using System;

namespace DatevBridge.Datev.Managers
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

                Rot.RegisterActiveObject(adapter, ref adapterGuid, flags, out _registrationId);

                _adapter = adapter;

                LogManager.Log("DatevAdapter registered in ROT");
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
                    Rot.RevokeActiveObject(_registrationId, null);
                    LogManager.Log("DatevAdapter unregistered from ROT");
                }
                catch (Exception ex)
                {
                    LogManager.Log("Error unregistering DatevAdapter: {0}", ex.Message);
                }
                
                _registrationId = 0;
                _adapter = null;
            }
        }
    }
}
