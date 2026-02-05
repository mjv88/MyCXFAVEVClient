using System.Xml.Serialization;

namespace DatevBridge.Datev.DatevData.Recipients
{
    public class RecipientContactDetail
    {
        [XmlAttribute("ID")]
        public string Id { get; set; }

        [XmlAttribute("Status")]
        public int Status { get; set; }

        [XmlElement("Kontakt")]
        public RecipientContact Contact { get; set; }
    }
}
