using DatevConnector.Core;
using DatevConnector.Datev.Enums;
using DatevConnector.Datev.Managers;
using System;
using System.Runtime.InteropServices;

namespace DatevConnector.Datev.COMs
{
    /// <summary>
    /// This class implements the COM-interface IDatevCtiControl
    /// Receives Dial/Drop commands from DATEV
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    [ProgId("DatevConnector.DatevAdapter")]
    [Guid("D8CA0C15-8585-494A-93DF-07D706629793")]
    internal class DatevAdapter : IDatevCtiControl
    {
        private readonly Action<IDatevCtiData, DatevEventType> _eventHandler;

        public DatevAdapter(Action<IDatevCtiData, DatevEventType> eventHandler)
        {
            _eventHandler = eventHandler;
        }

        /// <summary>
        /// Handles IDatevCtiControl.Dial COM event.
        /// DATEV expects CallID to be set on pCallData before this method returns.
        /// </summary>
        public void Dial(object pCallData)
        {
            try
            {
                IDatevCtiData ctiData = pCallData as IDatevCtiData;

                if (ctiData != null)
                {
                    // Generate and assign CallID - DATEV checks this after Dial() returns
                    string callId = CallIdGenerator.Next();
                    ctiData.CallID = callId;

                    LogManager.Log("DATEV -> Bridge: Wählen-Befehl empfangen (CallID={0} zugewiesen)", callId);
                    LogManager.Debug("  CalledNumber={0}, Adressatenname={1}, AdressatenId={2}, DataSource={3}, SyncID={4}",
                        LogManager.Mask(ctiData.CalledNumber), LogManager.MaskName(ctiData.Adressatenname), LogManager.Mask(ctiData.AdressatenId), ctiData.DataSource, ctiData.SyncID);

                    _eventHandler(ctiData, DatevEventType.Dial);
                }
                else
                {
                    LogManager.Warning("DATEV -> Bridge: Wählen empfangen mit null/ungültigen CallData");
                }
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "DATEV Ereignis 'Dial' Fehler");
            }
        }

        public void Drop(object pCallData)
        {
            ProcessEvent(pCallData, "Drop", DatevEventType.Drop);
        }

        private void ProcessEvent(object pCallData, string eventName, DatevEventType eventType)
        {
            try
            {
                IDatevCtiData ctiData = pCallData as IDatevCtiData;

                if (ctiData != null)
                {
                    LogManager.Log("DATEV -> Bridge: {0}-Befehl empfangen", eventName);
                    LogManager.Debug("  CalledNumber={0}, Adressatenname={1}, AdressatenId={2}, DataSource={3}",
                        LogManager.Mask(ctiData.CalledNumber), LogManager.MaskName(ctiData.Adressatenname), LogManager.Mask(ctiData.AdressatenId), ctiData.DataSource);

                    _eventHandler(ctiData, eventType);
                }
                else
                {
                    LogManager.Warning("DATEV -> Bridge: {0} empfangen mit null/ungültigen CallData", eventName);
                }
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "DATEV Ereignis '{0}' Fehler", eventName);
            }
        }
    }
}
