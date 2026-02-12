using System.Xml.Serialization;

namespace DatevConnector.Datev.DatevData.Enums
{
    public enum ContactType
    {
        [XmlEnum("0")]
        SelfEmployed = 0,

        [XmlEnum("1")]
        Person = 1,

        [XmlEnum("2")]
        Company = 2
    }
}
