using System.Xml.Serialization;

namespace DatevBridge.Datev.DatevData.Recipients
{
    [XmlRoot("Browse", Namespace = "http://xml.datev.de/SDD/Browse/v01_2")]
    public class RecipientsContactList
    {
        [XmlArray("KontaktDetails")]
        [XmlArrayItem("KontaktDetail")]
        public RecipientContactDetail[] ContactDetails;
    }
}
