using System.Xml.Serialization;

namespace DatevConnector.Datev.DatevData.Institutions
{
    [XmlRoot("INSTITUTION", Namespace = "http://xml.datev.de/inst/v01")]
    public class InstitutionsContactList
    {
        [XmlArray("InstitutionDetails")]
        [XmlArrayItem("TELEFONIE")]
        public InstitutionContactDetail[] ContactDetails;
    }
}
