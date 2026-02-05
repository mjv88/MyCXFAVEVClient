using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DatevBridge.Core.Config
{
    /// <summary>
    /// INI file configuration reader using Windows API.
    /// Used primarily for 3CXDATEVBridge.ini configuration.
    /// </summary>
    public static class IniConfig
    {
        private static string _iniPath;

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(
            string section,
            string key,
            string defaultValue,
            StringBuilder result,
            int size,
            string filePath);

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileInt(
            string section,
            string key,
            int defaultValue,
            string filePath);

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern bool WritePrivateProfileString(
            string section,
            string key,
            string value,
            string filePath);

        /// <summary>
        /// Initialize the INI config with a file path.
        /// Call this at application startup.
        /// </summary>
        /// <param name="path">Full path to the INI file</param>
        public static void Initialize(string path)
        {
            _iniPath = path;
        }

        /// <summary>
        /// Check if the INI file exists
        /// </summary>
        public static bool Exists => !string.IsNullOrEmpty(_iniPath) && File.Exists(_iniPath);

        /// <summary>
        /// Get the current INI file path
        /// </summary>
        public static string FilePath => _iniPath;

        /// <summary>
        /// Get a string value from the INI file
        /// </summary>
        /// <param name="section">INI section name</param>
        /// <param name="key">Key within the section</param>
        /// <param name="defaultValue">Default if not found</param>
        /// <returns>Configuration value or default</returns>
        public static string GetString(string section, string key, string defaultValue = "")
        {
            if (string.IsNullOrEmpty(_iniPath) || !File.Exists(_iniPath))
                return defaultValue;

            var result = new StringBuilder(512);
            GetPrivateProfileString(section, key, defaultValue ?? "", result, result.Capacity, _iniPath);
            return result.ToString();
        }

        /// <summary>
        /// Get an integer value from the INI file
        /// </summary>
        /// <param name="section">INI section name</param>
        /// <param name="key">Key within the section</param>
        /// <param name="defaultValue">Default if not found</param>
        /// <returns>Configuration value or default</returns>
        public static int GetInt(string section, string key, int defaultValue = 0)
        {
            if (string.IsNullOrEmpty(_iniPath) || !File.Exists(_iniPath))
                return defaultValue;

            return GetPrivateProfileInt(section, key, defaultValue, _iniPath);
        }

        /// <summary>
        /// Get a boolean value from the INI file
        /// </summary>
        /// <param name="section">INI section name</param>
        /// <param name="key">Key within the section</param>
        /// <param name="defaultValue">Default if not found</param>
        /// <returns>Configuration value or default</returns>
        public static bool GetBool(string section, string key, bool defaultValue = false)
        {
            var value = GetString(section, key, null);
            if (string.IsNullOrEmpty(value))
                return defaultValue;

            var lower = value.ToLowerInvariant();
            if (lower == "true" || lower == "1" || lower == "yes")
                return true;
            if (lower == "false" || lower == "0" || lower == "no")
                return false;

            return defaultValue;
        }

        /// <summary>
        /// Set a string value in the INI file
        /// </summary>
        /// <param name="section">INI section name</param>
        /// <param name="key">Key within the section</param>
        /// <param name="value">Value to write</param>
        /// <returns>True if successful</returns>
        public static bool SetString(string section, string key, string value)
        {
            if (string.IsNullOrEmpty(_iniPath))
                return false;

            return WritePrivateProfileString(section, key, value, _iniPath);
        }

        /// <summary>
        /// Set an integer value in the INI file
        /// </summary>
        /// <param name="section">INI section name</param>
        /// <param name="key">Key within the section</param>
        /// <param name="value">Value to write</param>
        /// <returns>True if successful</returns>
        public static bool SetInt(string section, string key, int value)
        {
            return SetString(section, key, value.ToString());
        }

        /// <summary>
        /// Set a boolean value in the INI file
        /// </summary>
        /// <param name="section">INI section name</param>
        /// <param name="key">Key within the section</param>
        /// <param name="value">Value to write</param>
        /// <returns>True if successful</returns>
        public static bool SetBool(string section, string key, bool value)
        {
            return SetString(section, key, value ? "true" : "false");
        }

    }
}
