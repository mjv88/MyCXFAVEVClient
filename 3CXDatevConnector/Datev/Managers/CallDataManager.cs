using DatevConnector.Datev.COMs;
using DatevConnector.Datev.Constants;
using DatevConnector.Datev.PluginData;
using System;

namespace DatevConnector.Datev.Managers
{
    internal static class CallDataManager
    {
        internal static void Fill(CallData callData, string contactNumber, DatevContactInfo contact)
        {
            string dataSource = DatevDataSource.ThirdParty;
            string phoneNumber = contactNumber;
            string name = string.Empty;
            string id = string.Empty;

            if (contact != null)
            {
                DatevContact datevContact = contact.DatevContact;
                dataSource = datevContact.IsRecipient ? DatevDataSource.Recipient : DatevDataSource.Institution;
                phoneNumber = contact.Communication != null ? contact.Communication.Number : contactNumber;
                name = datevContact.Name;
                id = datevContact.Id;

                // Validate: DATEV datasource requires both name and id
                if (dataSource.StartsWith("DATEV_") && (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)))
                {
                    LogManager.Log("Incomplete DATEV contact data - falling back to 3CX: Name={0}, Id={1}",
                        LogManager.MaskName(name ?? "null"), LogManager.Mask(id ?? "null"));
                    dataSource = DatevDataSource.ThirdParty;
                    name = string.Empty;
                    id = string.Empty;
                }
                else
                {
                    LogManager.Debug("CallData filled from contact: Name={0}, Id={1}, DataSource={2}",
                        LogManager.MaskName(name), LogManager.Mask(id), dataSource);
                }
            }
            else
            {
                LogManager.Debug("CallData filled without contact match: Number={0}, DataSource={1}",
                    LogManager.Mask(contactNumber), dataSource);
            }

            callData.DataSource = dataSource;
            callData.CalledNumber = phoneNumber;
            callData.Adressatenname = name;
            callData.AdressatenId = id;
        }

        internal static CallData Create(IDatevCtiData ctiData)
        {
            var now = DateTime.Now;
            return new CallData
            {
                CallState = ENUM_CALLSTATE.eCSOffered,
                Begin = now,
                End = now,
                Direction = ENUM_DIRECTION.eDirOutgoing,
                DataSource = ctiData.DataSource ?? string.Empty,
                CalledNumber = ctiData.CalledNumber ?? string.Empty,
                Adressatenname = ctiData.Adressatenname ?? string.Empty,
                AdressatenId = ctiData.AdressatenId ?? string.Empty,
                CallID = ctiData.CallID ?? string.Empty,
                SyncID = ctiData.SyncID ?? string.Empty
            };
        }

        /// <summary>
        /// Create CallData from DATEV-initiated command, preserving all DATEV fields.
        /// Used when DATEV sends a Dial command with contact/SyncID info.
        /// </summary>
        internal static CallData CreateFromDatev(IDatevCtiData ctiData)
        {
            var callData = new CallData
            {
                CallState = ctiData.CallState,
                Begin = ctiData.Begin,
                End = ctiData.End,
                Direction = ctiData.Direction,
                DataSource = ctiData.DataSource ?? string.Empty,
                CalledNumber = ctiData.CalledNumber ?? string.Empty,
                Adressatenname = ctiData.Adressatenname ?? string.Empty,
                AdressatenId = ctiData.AdressatenId ?? string.Empty,
                CallID = ctiData.CallID ?? string.Empty,
                SyncID = ctiData.SyncID ?? string.Empty,
                Note = ctiData.Note ?? string.Empty
            };

            LogManager.Debug("DATEV CallData erstellt: SyncID={0}, ContactId={1}, ContactName={2}, DataSource={3}",
                callData.SyncID, LogManager.Mask(callData.AdressatenId), LogManager.MaskName(callData.Adressatenname), callData.DataSource);

            if (string.IsNullOrEmpty(callData.SyncID))
            {
                LogManager.Warning("DATEV CallData ohne SyncID");
            }

            return callData;
        }
    }
}
