using System.Collections.Generic;
using System.Text;
using DatevConnector.Datev.DatevData;
using DatevConnector.Datev.Managers;
using DatevConnector.Datev.PluginData;

namespace DatevConnector.Datev
{
    /// <summary>
    /// Debug logging helpers for DATEV contact diagnostics.
    /// Extracted from the cache to keep the main class focused on lookup/caching.
    /// </summary>
    internal static class DatevContactDiagnostics
    {
        public static void LogContactList(List<DatevContact> contacts)
        {
            LogManager.Log("----------- Contact list from DATEV:");

            foreach (DatevContact contact in contacts)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("ID=").Append(LogManager.Mask(contact.Id));
                sb.Append(" Name=").Append(LogManager.MaskName(contact.Name));
                sb.Append(" IsRecipient=").Append(contact.IsRecipient);
                sb.Append(" IsPrivate=").Append(contact.IsPrivatePerson);

                if (contact.Communications != null)
                {
                    foreach (Communication comm in contact.Communications)
                    {
                        sb.Append(" [").Append(comm.Medium);
                        sb.Append(": ").Append(LogManager.Mask(comm.Number));
                        if (!string.IsNullOrWhiteSpace(comm.NormalizedNumber))
                        {
                            sb.Append(" -> DATEV:").Append(LogManager.Mask(comm.NormalizedNumber));
                        }
                        sb.Append(" -> Effective:").Append(LogManager.Mask(comm.EffectiveNormalizedNumber));
                        sb.Append("]");
                    }
                }

                LogManager.Log(sb.ToString());
            }

            LogManager.Log("----------- End contact list");
        }

        public static void LogLookupDictionary(SortedDictionary<string, List<DatevContactInfo>> dictionary)
        {
            LogManager.Log("----------- Lookup dictionary:");

            foreach (var kvp in dictionary)
            {
                foreach (DatevContactInfo info in kvp.Value)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("Key=").Append(LogManager.Mask(kvp.Key));
                    sb.Append(" -> ID=").Append(LogManager.Mask(info.DatevContact.Id));
                    sb.Append(" Name=").Append(LogManager.MaskName(info.DatevContact.Name));
                    sb.Append(" Number=").Append(LogManager.Mask(info.Communication.Number));
                    sb.Append(" EffNorm=").Append(LogManager.Mask(info.Communication.EffectiveNormalizedNumber));

                    LogManager.Log(sb.ToString());
                }
            }

            LogManager.Log("----------- End lookup dictionary");
        }
    }
}
