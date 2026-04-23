using System.Xml.Serialization;

namespace DatevConnector.SddProxy
{
    // Local XML model used to deserialize SDD responses.
    // Ported verbatim (shape + XML attributes) from the tray's
    // DatevConnector.Datev.DatevData.* types so the SDD XML payloads
    // parse identically here. Kept private to the proxy to avoid the
    // tray ever needing these types.

    public enum ContactType
    {
        [XmlEnum("0")] SelfEmployed = 0,
        [XmlEnum("1")] Person = 1,
        [XmlEnum("2")] Company = 2
    }

    public enum Medium
    {
        [XmlEnum("1")] Phone = 1,
        [XmlEnum("2")] EMail = 2,
        [XmlEnum("3")] Internet = 3,
        [XmlEnum("4")] NewValue = 4,
        [XmlEnum("5")] Fax = 5,
        [XmlEnum("6")] Other = 6
    }

    public class Communication
    {
        [XmlElement("Medium")] public Medium Medium { get; set; }
        [XmlElement("Nummer")] public string Number { get; set; }
        [XmlElement("NormierteNummer")] public string NormalizedNumber { get; set; }
    }

    public class RecipientContact
    {
        [XmlElement("Typ")] public ContactType Type { get; set; }
        [XmlElement("Langname")] public string Name { get; set; }

        [XmlArray("Kommunikationen")]
        [XmlArrayItem("Kommunikation")]
        public Communication[] Communications { get; set; }
    }

    public class RecipientContactDetail
    {
        [XmlAttribute("ID")] public string Id { get; set; }
        [XmlAttribute("Status")] public int Status { get; set; }
        [XmlElement("Kontakt")] public RecipientContact Contact { get; set; }
    }

    [XmlRoot("Browse", Namespace = "http://xml.datev.de/SDD/Browse/v01_2")]
    public class RecipientsContactList
    {
        [XmlArray("KontaktDetails")]
        [XmlArrayItem("KontaktDetail")]
        public RecipientContactDetail[] ContactDetails;
    }

    public class InstitutionContactDetail
    {
        [XmlAttribute("ID")] public string Id { get; set; }
        [XmlElement("Name")] public string Name { get; set; }

        [XmlArray("Kommunikationen")]
        [XmlArrayItem("Kommunikation")]
        public Communication[] Communications { get; set; }
    }

    [XmlRoot("INSTITUTION", Namespace = "http://xml.datev.de/inst/v01")]
    public class InstitutionsContactList
    {
        [XmlArray("InstitutionDetails")]
        [XmlArrayItem("TELEFONIE")]
        public InstitutionContactDetail[] ContactDetails;
    }
}
