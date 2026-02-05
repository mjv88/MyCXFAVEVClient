using DatevBridge.Datev.DatevData.Enums;
using DatevBridge.Extensions;
using System.Xml.Serialization;

namespace DatevBridge.Datev.DatevData
{
    public class Communication
    {
        [XmlElement("Medium")]
        public Medium Medium { get; set; }

        [XmlElement("Nummer")]
        public string Number { get; set; }

        /// <summary>
        /// Normalized number from DATEV (NormierteNummer field).
        /// May be null or empty for some contacts (especially Institutions).
        /// </summary>
        [XmlElement("NormierteNummer")]
        public string NormalizedNumber { get; set; }

        private string _effectiveNormalizedNumber;
        private bool _effectiveNormalizedNumberComputed;

        /// <summary>
        /// Effective normalized number for phone number matching.
        /// Uses DATEV's NormalizedNumber if available, otherwise normalizes the raw Number.
        /// This ensures ALL contacts (Recipients and Institutions) can be matched.
        /// Cached on first access to avoid repeated Regex allocations during cache build.
        /// </summary>
        [XmlIgnore]
        public string EffectiveNormalizedNumber
        {
            get
            {
                if (!_effectiveNormalizedNumberComputed)
                {
                    _effectiveNormalizedNumber = !string.IsNullOrWhiteSpace(NormalizedNumber)
                        ? PhoneNumberNormalizer.Normalize(NormalizedNumber)
                        : PhoneNumberNormalizer.Normalize(Number);
                    _effectiveNormalizedNumberComputed = true;
                }
                return _effectiveNormalizedNumber;
            }
        }
    }
}
