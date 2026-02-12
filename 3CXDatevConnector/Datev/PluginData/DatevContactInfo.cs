using DatevConnector.Datev.DatevData;

namespace DatevConnector.Datev.PluginData
{
    public class DatevContactInfo
    {
        public DatevContactInfo(Communication communication, DatevContact datevContact)
        {
            Communication = communication;
            DatevContact = datevContact;
        }

        public Communication Communication { get; private set; }

        public DatevContact DatevContact { get; private set; }
    }
}
