using DatevBridge.Datev.DatevData;

namespace DatevBridge.Datev.PluginData
{
    public class DatevContact
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public bool IsPrivatePerson { get; set; }

        public bool IsRecipient { get; set; }

        public Communication[] Communications { get; set; }
    }
}
