using System.Xml.Serialization;

namespace DatevBridge.Datev.DatevData.Enums
{
    public enum Medium
    {
        /// <summary>
        /// Phone - Telefon
        /// </summary>
        [XmlEnum("1")]
        Phone = 1,

        /// <summary>
        /// EMail
        /// </summary>
        [XmlEnum("2")]
        EMail = 2,

        /// <summary>
        /// Internet
        /// </summary>
        [XmlEnum("3")]
        Internet = 3,

        /// <summary>
        /// NewValue
        /// </summary>
        [XmlEnum("4")]
        NewValue = 4,

        /// <summary>
        /// Fax
        /// </summary>
        [XmlEnum("5")]
        Fax = 5,

        /// <summary>
        /// Other - Sonstige
        /// </summary>
        [XmlEnum("6")]
        Other = 6
    }
}
