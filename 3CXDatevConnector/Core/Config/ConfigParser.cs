namespace DatevConnector.Core.Config
{
    /// <summary>
    /// Shared configuration value parsing utilities.
    /// Centralizes boolean parsing logic that was previously duplicated
    /// in AppConfig.ParseBool() and IniConfig.GetBool().
    /// </summary>
    public static class ConfigParser
    {
        /// <summary>
        /// Parse a string as a boolean, accepting true/1/yes and false/0/no.
        /// Returns null if the value is not a recognized boolean string.
        /// </summary>
        public static bool? ParseBool(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;

            var lower = value.ToLowerInvariant();
            if (lower == "true" || lower == "1" || lower == "yes") return true;
            if (lower == "false" || lower == "0" || lower == "no") return false;
            return null;
        }
    }
}
