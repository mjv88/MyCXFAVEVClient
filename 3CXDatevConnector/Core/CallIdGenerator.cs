using System;

namespace DatevConnector.Core
{
    /// <summary>
    /// Generates unique CallIDs in format: Extension-ddMMyyyy-HHmm-Random7
    /// Ensures uniqueness across application restarts.
    /// </summary>
    internal static class CallIdGenerator
    {
        private static string _extension = "0";
        private static readonly Random _random = new Random();

        /// <summary>
        /// Initialize with extension number (from INI config, 3CXTAPI.ini, or TAPI auto-detect)
        /// </summary>
        public static void Initialize(string extension)
        {
            if (!string.IsNullOrEmpty(extension))
                _extension = extension;
        }

        /// <summary>
        /// Generate next unique CallID: Extension-ddMMyyyy-HHmm-Random7
        /// </summary>
        public static string Next()
        {
            var now = DateTime.Now;
            int random;
            lock (_random)
            {
                random = _random.Next(1000000, 9999999);
            }

            return string.Format("{0}-{1}-{2}-{3}",
                _extension,
                now.ToString("ddMMyyyy"),
                now.ToString("HHmm"),
                random);
        }
    }
}
