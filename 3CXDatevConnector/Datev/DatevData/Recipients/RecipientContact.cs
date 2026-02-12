using System.Xml.Serialization;
using DatevConnector.Datev.DatevData.Enums;

namespace DatevConnector.Datev.DatevData.Recipients
{
    public class RecipientContact
    {
        [XmlElement("Typ")]
        public ContactType Type { get; set; }

        [XmlElement("Langname")]
        public string Name { get; set; }

        [XmlArray("Kommunikationen")]
        [XmlArrayItem("Kommunikation")]
        public Communication[] Communications { get; set; }
    }
}
