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

        // Public key token of all DATEV-signed assemblies (ClientInterfaces,
        // ClientPlugIn.Base, Framework.MicroKernel, etc.). Used as a fast
        // filter so we only try to resolve DATEV DLLs.
        private static readonly byte[] DatevPublicKeyToken =
            { 0xcb, 0xc6, 0x31, 0xf1, 0xc6, 0x82, 0x33, 0x6b };

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
            // Handle any DATEV-signed assembly. Name prefix catches partial-name
            // lookups; public key token catches strong-named ones. Either match
            // is sufficient — the per-candidate token check further down still
            // verifies we're loading the right DLL.
            bool nameMatches = assemblyName.Name != null &&
                assemblyName.Name.StartsWith("Datev.", StringComparison.OrdinalIgnoreCase);
            var requestedToken = assemblyName.GetPublicKeyToken();
            bool tokenMatches = requestedToken != null && requestedToken.Length > 0 &&
                requestedToken.SequenceEqual(DatevPublicKeyToken);

            if (!nameMatches && !tokenMatches)
                return null;

            LogManager.Debug("GAC Resolver: Suche Assembly '{0}'", assemblyName.Name);

            // Priority 1: DATEV program install folder (C:\Program Files (x86)\DATEV\PROGRAMM\K*).
            // These are typically newer / Remoting-free versions; loading them first
            // avoids picking up an older, Remoting-dependent copy from the GAC that
            // fails at type-load time on Terminal Server.
            var fromDatevInstall = TryLoadFromDatevInstall(context, assemblyName);
            if (fromDatevInstall != null) return fromDatevInstall;

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

            LogManager.Debug("GAC Resolver: Assembly '{0}' nicht in Fallback-Pfaden, probiere Harvest", assemblyName.Name);

            string harvested = TryHarvestFromLocalDatev(assemblyName);
            if (harvested != null)
            {
                try { return context.LoadFromAssemblyPath(harvested); }
                catch (Exception ex)
                {
                    LogManager.Log("GAC Resolver: Harvest-Load von '{0}' fehlgeschlagen: {1}", harvested, ex.Message);
                }
            }

            LogManager.Debug("GAC Resolver: Assembly '{0}' konnte nicht aufgeloest werden", assemblyName.Name);
            return null;
        }

        // Probed after the GAC in order: (1) bundled copy next to the exe,
        // (2) user-writable copy under %AppData% so an operator can drop
        // replacement DLLs without re-running the installer.
        private static System.Collections.Generic.IEnumerable<string> FallbackDirectories()
        {
            yield return Path.Combine(AppContext.BaseDirectory, "Datev");
            yield return AppDataAssemblyDir();
        }

        private static string AppDataAssemblyDir() =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "3CXDATEVConnector", "Assembly");

        // Tracks names we've already scanned (found or not) so a repeated
        // missing-dependency resolve doesn't rescan the file system every call.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _harvestedNames
            = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Last-ditch: if a DATEV assembly wasn't found in GAC/bundle/AppData,
        // scan a local DATEV installation for it and copy the first matching
        // DLL (with verified public-key-token) into the AppData fallback
        // folder. Returns the destination path on success, null otherwise.
        private static string TryHarvestFromLocalDatev(AssemblyName requested)
        {
            if (requested?.Name == null) return null;
            if (!_harvestedNames.TryAdd(requested.Name, true))
                return null; // already attempted this session

            try
            {
                string destDir = AppDataAssemblyDir();
                string destPath = Path.Combine(destDir, requested.Name + ".dll");

                foreach (var root in DatevInstallRoots())
                {
                    if (!Directory.Exists(root)) continue;
                    LogManager.Debug("GAC Resolver: Scanne DATEV-Installation '{0}' nach '{1}'", root, requested.Name);

                    string match;
                    try
                    {
                        match = Directory.EnumerateFiles(root, requested.Name + ".dll",
                            SearchOption.AllDirectories).FirstOrDefault();
                    }
                    catch (Exception ex)
                    {
                        LogManager.Debug("GAC Resolver: Scan '{0}' abgebrochen: {1}", root, ex.Message);
                        continue;
                    }
                    if (match == null) continue;

                    // Token check — same rule as GAC/fallback loads.
                    try
                    {
                        var candidateName = AssemblyName.GetAssemblyName(match);
                        var expectedToken = requested.GetPublicKeyToken();
                        var candidateToken = candidateName.GetPublicKeyToken();
                        if (expectedToken != null && expectedToken.Length > 0 &&
                            (candidateToken == null || !expectedToken.SequenceEqual(candidateToken)))
                        {
                            LogManager.Debug("GAC Resolver: Harvest-Kandidat '{0}' Token-Mismatch, uebersprungen", match);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogManager.Debug("GAC Resolver: Harvest-Kandidat '{0}' nicht lesbar: {1}", match, ex.Message);
                        continue;
                    }

                    try
                    {
                        Directory.CreateDirectory(destDir);
                        File.Copy(match, destPath, overwrite: true);
                        LogManager.Log("GAC Resolver: Assembly '{0}' aus DATEV-Installation kopiert: {1} -> {2}",
                            requested.Name, match, destPath);
                        return destPath;
                    }
                    catch (Exception ex)
                    {
                        LogManager.Log("GAC Resolver: Kopie von '{0}' nach '{1}' fehlgeschlagen: {2}",
                            match, destPath, ex.Message);
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Debug("GAC Resolver: Harvest fuer '{0}' fehlgeschlagen: {1}", requested.Name, ex.Message);
            }
            return null;
        }

        // Direct load (no copy-to-AppData) from the DATEV program install folder.
        // Enumerates every match and picks the first one whose public-key-token
        // matches what .NET asked for — so version diffs between install roots
        // don't trap us on the first mismatch.
        private static Assembly TryLoadFromDatevInstall(AssemblyLoadContext context, AssemblyName requested)
        {
            if (requested?.Name == null) return null;
            var expectedToken = requested.GetPublicKeyToken();
            string fileName = requested.Name + ".dll";

            foreach (var root in DatevInstallRoots())
            {
                if (!Directory.Exists(root)) continue;
                System.Collections.Generic.IEnumerable<string> matches;
                try
                {
                    matches = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories);
                }
                catch (Exception ex)
                {
                    LogManager.Debug("GAC Resolver: Scan '{0}' abgebrochen: {1}", root, ex.Message);
                    continue;
                }

                foreach (var candidate in matches)
                {
                    try
                    {
                        var candidateName = AssemblyName.GetAssemblyName(candidate);
                        var candidateToken = candidateName.GetPublicKeyToken();
                        if (expectedToken != null && expectedToken.Length > 0 &&
                            (candidateToken == null || !expectedToken.SequenceEqual(candidateToken)))
                        {
                            continue; // wrong token, keep looking
                        }
                        LogManager.Log("GAC Resolver: Assembly '{0}' aus DATEV-Programm geladen: {1}",
                            requested.Name, candidate);
                        return context.LoadFromAssemblyPath(candidate);
                    }
                    catch (Exception ex)
                    {
                        LogManager.Debug("GAC Resolver: Kandidat '{0}' nicht ladbar: {1}", candidate, ex.Message);
                    }
                }
            }
            return null;
        }

        private static System.Collections.Generic.IEnumerable<string> DatevInstallRoots()
        {
            // Registry-configured install root takes precedence (both 32/64-bit views).
            foreach (var hive in new[] {
                Microsoft.Win32.RegistryView.Registry32,
                Microsoft.Win32.RegistryView.Registry64 })
            {
                string fromReg = null;
                try
                {
                    using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                        Microsoft.Win32.RegistryHive.LocalMachine, hive);
                    using var key = baseKey?.OpenSubKey(@"SOFTWARE\DATEV");
                    fromReg = key?.GetValue("Installationsverzeichnis") as string;
                }
                catch { }
                if (!string.IsNullOrWhiteSpace(fromReg)) yield return fromReg;
            }

            // Common default install paths — cover both Englisch- and Deutsch-locale
            // Windows, and the PROGRAMM\K* subfolder where DATEV puts versioned
            // runtime DLLs (e.g. PROGRAMM\K500, PROGRAMM\K510, ...).
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string pf64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (var baseDir in new[] {
                Path.Combine(pf86, "DATEV", "PROGRAMM"),
                Path.Combine(pf64, "DATEV", "PROGRAMM"),
                @"C:\Programme\DATEV\PROGRAMM",
                Path.Combine(pf86, "DATEV"),
                Path.Combine(pf64, "DATEV"),
                @"C:\Programme\DATEV" })
            {
                if (!string.IsNullOrEmpty(baseDir)) yield return baseDir;
            }
        }
    }
}
