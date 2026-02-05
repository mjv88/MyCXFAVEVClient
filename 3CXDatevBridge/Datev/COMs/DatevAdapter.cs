using Datev.Cti.Buddylib;
using DatevBridge.Core;
using DatevBridge.Datev.Enums;
using DatevBridge.Datev.Managers;
using System;
using System.Runtime.InteropServices;

namespace DatevBridge.Datev.COMs
{
    /// <summary>
    /// This class implements the COM-interface IDatevCtiControl
    /// Receives Dial/Drop commands from DATEV
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    [ProgId("DatevBridge.DatevAdapter")]
    [Guid("D8CA0C15-8585-494A-93DF-07D706629793")]
    internal class DatevAdapter : IDatevCtiControl
    {
        private readonly Action<IDatevCtiData, DatevEventType> _eventHandler;

        /// <summary>
        /// Constructor
        /// </summary>
        public DatevAdapter(Action<IDatevCtiData, DatevEventType> eventHandler)
        {
            _eventHandler = eventHandler;
        }

        /// <summary>
        /// This method handles the COM-events for IDatevCtiControl.Dial.
        /// DATEV expects CallID to be set on pCallData before this method returns.
        /// </summary>
        /// <param name="pCallData">Struct with the necessary information about the call.</param>
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

                    LogManager.Log("DATEV -> Bridge: Dial command received (assigned CallID={0})", callId);
                    LogManager.Debug("  CalledNumber={0}, Adressatenname={1}, AdressatenId={2}, DataSource={3}, SyncID={4}",
                        LogManager.Mask(ctiData.CalledNumber), ctiData.Adressatenname, ctiData.AdressatenId, ctiData.DataSource, ctiData.SyncID);

                    _eventHandler(ctiData, DatevEventType.Dial);
                }
                else
                {
                    LogManager.Warning("DATEV -> Bridge: Dial received with null/invalid CallData");
                }
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "DATEV event 'Dial' error");
            }
        }

        /// <summary>
        /// This method handles the COM-events for IDatevCtiControl.Drop
        /// </summary>
        /// <param name="pCallData">Struct with the necessary information about the drop.</param>
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
                    LogManager.Log("DATEV -> Bridge: {0} command received", eventName);
                    LogManager.Debug("  CalledNumber={0}, Adressatenname={1}, AdressatenId={2}, DataSource={3}",
                        LogManager.Mask(ctiData.CalledNumber), ctiData.Adressatenname, ctiData.AdressatenId, ctiData.DataSource);

                    _eventHandler(ctiData, eventType);
                }
                else
                {
                    LogManager.Warning("DATEV -> Bridge: {0} received with null/invalid CallData", eventName);
                }
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "DATEV event '{0}' error", eventName);
            }
        }
    }
}
