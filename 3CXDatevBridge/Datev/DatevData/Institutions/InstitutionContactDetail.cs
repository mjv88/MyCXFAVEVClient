using System.Xml.Serialization;
using DatevBridge.Datev.DatevData;

namespace DatevBridge.Datev.DatevData.Institutions
{
    public class InstitutionContactDetail
    {
        [XmlAttribute("ID")]
        public string Id { get; set; }

        [XmlElement("Name")]
        public string Name { get; set; }

        [XmlArray("Kommunikationen")]
        [XmlArrayItem("Kommunikation")]
        public Communication[] Communications { get; set; }
    }
}
