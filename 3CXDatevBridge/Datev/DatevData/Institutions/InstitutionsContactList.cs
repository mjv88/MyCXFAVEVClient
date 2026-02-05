using System.Xml.Serialization;

namespace DatevBridge.Datev.DatevData.Institutions
{
    [XmlRoot("INSTITUTION", Namespace = "http://xml.datev.de/inst/v01")]
    public class InstitutionsContactList
    {
        [XmlArray("InstitutionDetails")]
        [XmlArrayItem("TELEFONIE")]
        public InstitutionContactDetail[] ContactDetails;
    }
}
