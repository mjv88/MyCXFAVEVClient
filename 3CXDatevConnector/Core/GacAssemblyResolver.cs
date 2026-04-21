using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using DatevConnector.Datev.Managers;

namespace DatevConnector.Core
{
    /// <summary>
    /// Resolves DATEV SDD assemblies from the .NET Framework GAC at runtime.
    /// On .NET 9 the GAC is not used automatically, so we probe the well-known
    /// GAC paths manually. Falls back gracefully if assemblies are not found
    /// (SDD features will be disabled).
    /// </summary>
    internal static class GacAssemblyResolver
    {
        private static readonly string[] GacRoots =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Microsoft.NET", "assembly", "GAC_MSIL"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Microsoft.NET", "assembly", "GAC_32"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Microsoft.NET", "assembly", "GAC_64"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "assembly", "GAC_MSIL"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "assembly", "GAC_32"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "assembly", "GAC_64")
        };

        private static readonly string[] SddAssemblyNames =
        {
            "Datev.Sdd.Data.ClientInterfaces",
            "Datev.Sdd.Data.ClientPlugIn.Base"
        };

        private static bool _registered;

        internal static void Register()
        {
            if (_registered) return;
            _registered = true;

            AssemblyLoadContext.Default.Resolving += OnResolving;
            LogManager.Debug("GAC Assembly Resolver registriert");
        }

        private static Assembly OnResolving(AssemblyLoadContext context, AssemblyName assemblyName)
        {
            // Only handle DATEV SDD assemblies
            bool isSddAssembly = false;
            foreach (var name in SddAssemblyNames)
            {
                if (string.Equals(assemblyName.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    isSddAssembly = true;
                    break;
                }
            }

            if (!isSddAssembly)
                return null;

            LogManager.Debug("GAC Resolver: Suche Assembly '{0}'", assemblyName.Name);

            foreach (var gacRoot in GacRoots)
            {
                string assemblyDir = Path.Combine(gacRoot, assemblyName.Name);
                if (!Directory.Exists(assemblyDir))
                    continue;

                // GAC structure: GAC_MSIL/<AssemblyName>/v4.0_<version>_<culture>_<token>/<AssemblyName>.dll
                try
                {
                    foreach (var versionDir in Directory.GetDirectories(assemblyDir))
                    {
                        string dllPath = Path.Combine(versionDir, assemblyName.Name + ".dll");
                        if (File.Exists(dllPath))
                        {
                            var candidateName = AssemblyName.GetAssemblyName(dllPath);
                            var expectedToken = assemblyName.GetPublicKeyToken();
                            var candidateToken = candidateName.GetPublicKeyToken();
                            if (expectedToken != null && expectedToken.Length > 0 &&
                                (candidateToken == null || !expectedToken.SequenceEqual(candidateToken)))
                            {
                                LogManager.Debug("GAC Resolver: Public key token mismatch for '{0}', skipping", dllPath);
                                continue;
                            }

                            LogManager.Debug("GAC Resolver: Erfolgreich '{0}'", assemblyName.Name);
                            return context.LoadFromAssemblyPath(dllPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Debug("GAC Resolver: Fehler beim Durchsuchen von '{0}': {1}",
                        assemblyDir, ex.Message);
                }
            }

            LogManager.Debug("GAC Resolver: Assembly '{0}' nicht im GAC gefunden, probiere Fallbacks", assemblyName.Name);

            foreach (var fallbackDir in FallbackDirectories())
            {
                string dllPath = Path.Combine(fallbackDir, assemblyName.Name + ".dll");
                if (!File.Exists(dllPath))
                    continue;

                try
                {
                    var candidateName = AssemblyName.GetAssemblyName(dllPath);
                    var expectedToken = assemblyName.GetPublicKeyToken();
                    var candidateToken = candidateName.GetPublicKeyToken();
                    if (expectedToken != null && expectedToken.Length > 0 &&
                        (candidateToken == null || !expectedToken.SequenceEqual(candidateToken)))
                    {
                        LogManager.Debug("GAC Resolver: Public key token mismatch for '{0}', skipping", dllPath);
                        continue;
                    }

                    LogManager.Log("GAC Resolver: Assembly '{0}' aus Fallback geladen: {1}",
                        assemblyName.Name, dllPath);
                    return context.LoadFromAssemblyPath(dllPath);
                }
                catch (Exception ex)
                {
                    LogManager.Debug("GAC Resolver: Fehler beim Laden von '{0}': {1}", dllPath, ex.Message);
                }
            }

            LogManager.Debug("GAC Resolver: Assembly '{0}' auch nicht in Fallback-Pfaden gefunden", assemblyName.Name);
            return null;
        }

        // Probed after the GAC in order: (1) bundled copy next to the exe,
        // (2) user-writable copy under %AppData% so an operator can drop
        // replacement DLLs without re-running the installer.
        private static System.Collections.Generic.IEnumerable<string> FallbackDirectories()
        {
            yield return Path.Combine(AppContext.BaseDirectory, "Datev");
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "3CXDATEVConnector", "Assembly");
        }
    }
}
