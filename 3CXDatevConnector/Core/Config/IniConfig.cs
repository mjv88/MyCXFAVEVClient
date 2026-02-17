using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DatevConnector.Core.Config
{
    /// <summary>
    /// INI file configuration reader using Windows API.
    /// Used primarily for 3CXDATEVConnector.ini configuration.
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
        public static void Initialize(string path)
        {
            _iniPath = path;
        }

        public static bool Exists => !string.IsNullOrEmpty(_iniPath) && File.Exists(_iniPath);

        public static string FilePath => _iniPath;

        public static string GetString(string section, string key, string defaultValue = "")
        {
            if (string.IsNullOrEmpty(_iniPath) || !File.Exists(_iniPath))
                return defaultValue;

            var result = new StringBuilder(512);
            GetPrivateProfileString(section, key, defaultValue ?? "", result, result.Capacity, _iniPath);
            return result.ToString();
        }

        public static int GetInt(string section, string key, int defaultValue = 0)
        {
            if (string.IsNullOrEmpty(_iniPath) || !File.Exists(_iniPath))
                return defaultValue;

            return GetPrivateProfileInt(section, key, defaultValue, _iniPath);
        }

        public static bool GetBool(string section, string key, bool defaultValue = false)
        {
            var value = GetString(section, key, null);
            return ConfigParser.ParseBool(value) ?? defaultValue;
        }

        public static bool SetString(string section, string key, string value)
        {
            if (string.IsNullOrEmpty(_iniPath))
                return false;

            return WritePrivateProfileString(section, key, value, _iniPath);
        }

        public static bool SetInt(string section, string key, int value)
        {
            return SetString(section, key, value.ToString());
        }

        public static bool SetBool(string section, string key, bool value)
        {
            return SetString(section, key, value ? "true" : "false");
        }

    }
}
