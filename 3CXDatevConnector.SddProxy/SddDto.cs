using System.Collections.Generic;

namespace DatevConnector.SddProxy
{
    /// <summary>
    /// Flat, self-contained contact shape sent over the pipe.
    /// Mirrors the fields the tray's DatevContact carries so the tray can
    /// round-trip proxy responses back into its existing model without
    /// needing any DATEV SDD types in the tray process.
    /// </summary>
    internal sealed class ContactDto
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        /// <summary>"Adressat" (Recipient) or "Institution".</summary>
        public string Kind { get; set; }
        public bool IsActive { get; set; }
        public bool IsPrivatePerson { get; set; }
        public List<PhoneDto> Phones { get; set; } = new List<PhoneDto>();
    }

    /// <summary>
    /// One phone row on a contact. We send both the raw and the DATEV-normalized
    /// values so the tray can apply its own PhoneNumberNormalizer consistently
    /// (it does suffix matching on locally-normalized keys).
    /// </summary>
    internal sealed class PhoneDto
    {
        public string Number { get; set; }
        public string NormalizedNumber { get; set; }
    }
}
