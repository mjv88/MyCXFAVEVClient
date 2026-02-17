using System;
using System.IO;
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
                "assembly", "GAC_MSIL")
        };

        private static readonly string[] SddAssemblyNames =
        {
            "Datev.Sdd.Data.ClientInterfaces",
            "Datev.Sdd.Data.ClientPlugIn.Base"
        };

        private static bool _registered;

        /// <summary>
        /// Register the assembly resolver. Call once at startup before any SDD types are used.
        /// </summary>
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
                            LogManager.Log("GAC Resolver: Lade '{0}' von {1}", assemblyName.Name, dllPath);
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

            LogManager.Debug("GAC Resolver: Assembly '{0}' nicht im GAC gefunden", assemblyName.Name);
            return null;
        }
    }
}
