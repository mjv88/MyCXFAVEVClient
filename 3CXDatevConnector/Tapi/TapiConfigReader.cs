using System;
using System.Collections.Generic;
using System.IO;
using DatevConnector.Datev.Managers;

namespace DatevConnector.Tapi
{
    /// <summary>
    /// Reads 3CX TAPI configuration from the default INI file.
    /// Used for automatic extension detection on both terminal servers and PCs.
    /// The 3CX MultiLine TAPI driver stores line configuration in 3CXTAPI.ini.
    /// </summary>
    internal static class TapiConfigReader
    {
        private const string DefaultIniPath = @"C:\ProgramData\3CXMultiLineTapi\3CXTAPI.ini";

        /// <summary>
        /// Represents a configured 3CX TAPI line from the INI file
        /// </summary>
        internal class TapiLineConfig
        {
            public string Extension { get; set; }
            public string Name { get; set; }
            public string Section { get; set; }
        }

        /// <summary>
        /// Check if 3CX TAPI is installed by looking for the INI file
        /// </summary>
        internal static bool IsTapiInstalled()
        {
            return File.Exists(DefaultIniPath);
        }

        /// <summary>
        /// Get the default INI path
        /// </summary>
        internal static string GetIniPath()
        {
            return DefaultIniPath;
        }

        /// <summary>
        /// Read all configured TAPI lines from the INI file.
        /// Returns empty list if file not found or unreadable.
        /// </summary>
        internal static List<TapiLineConfig> ReadLines()
        {
            return ReadLines(DefaultIniPath);
        }

        /// <summary>
        /// Read all configured TAPI lines from a specific INI file path.
        /// </summary>
        internal static List<TapiLineConfig> ReadLines(string iniPath)
        {
            var lines = new List<TapiLineConfig>();

            if (!File.Exists(iniPath))
            {
                LogManager.Debug("3CXTAPI.ini nicht gefunden unter: {0}", iniPath);
                return lines;
            }

            try
            {
                string[] fileLines = File.ReadAllLines(iniPath);
                string currentSection = null;
                var sectionData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < fileLines.Length; i++)
                {
                    string line = fileLines[i].Trim();

                    if (string.IsNullOrEmpty(line) || line.StartsWith(";") || line.StartsWith("#"))
                        continue;

                    // Section header: [SectionName]
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        // Save previous section if it was a line section
                        if (currentSection != null && sectionData.Count > 0)
                        {
                            var lineConfig = CreateLineConfig(currentSection, sectionData);
                            if (lineConfig != null)
                                lines.Add(lineConfig);
                        }

                        currentSection = line.Substring(1, line.Length - 2);
                        sectionData.Clear();
                        continue;
                    }

                    // Key=Value (INI format)
                    int eqIndex = line.IndexOf('=');
                    if (eqIndex > 0)
                    {
                        string key = line.Substring(0, eqIndex).Trim();
                        string value = line.Substring(eqIndex + 1).Trim();
                        sectionData[key] = value;
                        continue;
                    }

                    // CSV format: "extension, name" or "extension,name"
                    // 3CX writes this format: one line per TAPI device
                    var csvConfig = ParseCsvLine(line);
                    if (csvConfig != null)
                        lines.Add(csvConfig);
                }

                // Handle last INI section
                if (currentSection != null && sectionData.Count > 0)
                {
                    var lineConfig = CreateLineConfig(currentSection, sectionData);
                    if (lineConfig != null)
                        lines.Add(lineConfig);
                }

                LogManager.Log("3CXTAPI.ini: {0} konfigurierte Leitung(en) gefunden", lines.Count);
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "Fehler beim Lesen der 3CXTAPI.ini");
            }

            return lines;
        }

        /// <summary>
        /// Get the first configured line (for auto-configuration when only one line exists)
        /// </summary>
        internal static TapiLineConfig GetFirstLine()
        {
            var lines = ReadLines();
            return lines.Count > 0 ? lines[0] : null;
        }

        /// <summary>
        /// Try to auto-detect the extension number from 3CXTAPI.ini.
        /// Returns null if not found or not installed.
        /// </summary>
        internal static string DetectExtension()
        {
            if (!IsTapiInstalled())
            {
                LogManager.Debug("3CX TAPI Treiber: Nicht installiert");
                return null;
            }

            var lines = ReadLines();
            if (lines.Count == 0)
            {
                LogManager.Warning("3CXTAPI.ini gefunden, aber enthält keine konfigurierten Leitungen");
                return null;
            }

            if (lines.Count == 1)
            {
                LogManager.Log("Nebenstelle automatisch erkannt von 3CX TAPI: {0}", lines[0].Extension);
                return lines[0].Extension;
            }

            // Multiple lines - log all and use first
            LogManager.Log("3CXTAPI.ini enthält {0} Leitungen, verwende erste: {1}", lines.Count, lines[0].Extension);
            return lines[0].Extension;
        }

        /// <summary>
        /// Parse a CSV-format line: "extension, name" or "extension,name"
        /// This is the format 3CX MultiLine TAPI actually writes to 3CXTAPI.ini.
        /// </summary>
        private static TapiLineConfig ParseCsvLine(string line)
        {
            // Split on comma: "100, Max Mustermann" -> ["100", " Max Mustermann"]
            int commaIndex = line.IndexOf(',');
            if (commaIndex > 0)
            {
                string ext = line.Substring(0, commaIndex).Trim();
                string name = line.Substring(commaIndex + 1).Trim();

                // Extension must be numeric
                if (ext.Length > 0 && IsDigitsOnly(ext))
                {
                    return new TapiLineConfig
                    {
                        Section = "(csv)",
                        Extension = ext,
                        Name = !string.IsNullOrEmpty(name) ? name : ext
                    };
                }
            }

            // Also handle bare extension number (no comma, just digits)
            if (line.Length > 0 && IsDigitsOnly(line))
            {
                return new TapiLineConfig
                {
                    Section = "(csv)",
                    Extension = line,
                    Name = line
                };
            }

            return null;
        }

        private static bool IsDigitsOnly(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] < '0' || s[i] > '9')
                    return false;
            }
            return true;
        }

        private static TapiLineConfig CreateLineConfig(string section, Dictionary<string, string> data)
        {
            // Look for extension/number in common key names
            string extension = GetValue(data, "Extension", "Number", "Ext", "DN");
            string name = GetValue(data, "Name", "DisplayName", "Label", "Description");

            // Only create a config if we have at least an extension
            if (!string.IsNullOrWhiteSpace(extension))
            {
                return new TapiLineConfig
                {
                    Section = section,
                    Extension = extension,
                    Name = name ?? extension
                };
            }

            // Also check if the section name itself looks like "Extension : Name" pattern
            if (section.Contains(":"))
            {
                string[] parts = section.Split(new[] { ':' }, 2);
                return new TapiLineConfig
                {
                    Section = section,
                    Extension = parts[0].Trim(),
                    Name = parts.Length > 1 ? parts[1].Trim() : parts[0].Trim()
                };
            }

            return null;
        }

        private static string GetValue(Dictionary<string, string> data, params string[] keys)
        {
            foreach (string key in keys)
            {
                string value;
                if (data.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return null;
        }
    }
}
