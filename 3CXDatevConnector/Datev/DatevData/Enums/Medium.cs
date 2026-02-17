using System.Xml.Serialization;

namespace DatevConnector.Datev.DatevData.Enums
{
    public enum Medium
    {
        [XmlEnum("1")]
        Phone = 1,

        [XmlEnum("2")]
        EMail = 2,

        [XmlEnum("3")]
        Internet = 3,

        [XmlEnum("4")]
        NewValue = 4,

        [XmlEnum("5")]
        Fax = 5,

        [XmlEnum("6")]
        Other = 6
    }
}
