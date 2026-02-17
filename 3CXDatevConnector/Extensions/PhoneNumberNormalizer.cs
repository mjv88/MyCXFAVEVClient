using System.Text.RegularExpressions;

namespace DatevConnector.Extensions
{
    /// <summary>
    /// Centralized phone number normalization utility.
    /// Ensures consistent normalization across all contacts (Recipients and Institutions)
    /// and incoming caller IDs.
    ///
    /// Handles common international formats:
    /// - +49 format (GSM/SIP standard)
    /// - 0049 format (international dial prefix)
    /// - Leading zeros for national format (Germany: 0 + area code)
    /// </summary>
    public static class PhoneNumberNormalizer
    {
        private static readonly Regex NonDigitRegex = new Regex(@"[^0-9]+", RegexOptions.Compiled);

        /// <summary>
        /// Normalizes a phone number by:
        /// 1. Converting international prefixes (00XX → XX, +XX → XX)
        /// 2. Removing all non-digit characters
        /// </summary>
        /// <example>
        /// "+49 89 12345678" → "498912345678"
        /// "0049 89 12345678" → "498912345678"
        /// "089/12345-678" → "08912345678"
        /// "(089) 123 456" → "089123456"
        /// </example>
        public static string Normalize(string phoneNumber)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber))
                return string.Empty;

            string trimmed = phoneNumber.Trim();

            // Handle international prefixes before stripping non-digits
            // Convert 00XX to XX (international dial prefix used in Germany)
            if (trimmed.StartsWith("00") && trimmed.Length > 4)
            {
                trimmed = trimmed.Substring(2);
            }
            // Handle +XX format (E.164/SIP standard)
            else if (trimmed.StartsWith("+"))
            {
                trimmed = trimmed.Substring(1);
            }

            // Remove all remaining non-digit characters
            return NonDigitRegex.Replace(trimmed, "");
        }

        /// <summary>
        /// Normalizes a phone number and returns the last N digits for comparison.
        /// </summary>
        /// <example>
        /// NormalizeForComparison("+49 89 12345678", 10) → "8912345678"
        /// NormalizeForComparison("089/12345678", 10) → "8912345678"
        /// </example>
        public static string NormalizeForComparison(string phoneNumber, int maxLength)
        {
            string normalized = Normalize(phoneNumber);

            if (string.IsNullOrEmpty(normalized))
                return string.Empty;

            // Return last maxLength digits
            if (normalized.Length <= maxLength)
                return normalized;

            return normalized.Substring(normalized.Length - maxLength);
        }

    }
}
