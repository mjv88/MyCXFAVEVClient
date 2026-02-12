using System.Xml.Serialization;
using DatevConnector.Datev.DatevData;

namespace DatevConnector.Datev.DatevData.Institutions
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
