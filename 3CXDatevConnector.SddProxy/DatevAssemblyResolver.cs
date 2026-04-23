using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace DatevConnector.SddProxy
{
    /// <summary>
    /// net48 counterpart to the tray's GacAssemblyResolver.
    ///
    /// The proxy compiles against small compile-time stub DLLs in ../lib/, but
    /// those stubs can't be deployed next to the exe (they reference System.Runtime
    /// v9 and would fail to load inside net48 anyway). At runtime we must find the
    /// real DATEV SDD assemblies. Net48 Fusion will go to the GAC automatically
    /// for strong-named references, but our proxy's assembly metadata has
    /// PublicKeyToken=null (inherited from the stubs), so the GAC never matches
    /// by identity. We hook AppDomain.AssemblyResolve and return the real DLL
    /// regardless of token — same trick the tray uses on .NET 9.
    ///
    /// Probes, in order:
    ///   1. DATEV program install folder (HKLM\Software\DATEV\Installationsverzeichnis,
    ///      then %ProgramFiles(x86)%\DATEV\PROGRAMM, ...).
    ///   2. Well-known GAC roots under %WINDIR%.
    /// </summary>
    internal static class DatevAssemblyResolver
    {
        private static bool _registered;

        public static void Register()
        {
            if (_registered) return;
            _registered = true;
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
        }

        private static Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            var requested = new AssemblyName(args.Name);
            if (requested.Name == null) return null;
            if (!requested.Name.StartsWith("Datev.", StringComparison.OrdinalIgnoreCase))
                return null;

            // 1. DATEV install folder — usually newer / Remoting-clean builds.
            foreach (var root in DatevInstallRoots())
            {
                if (!SafeDirExists(root)) continue;
                IEnumerable<string> hits;
                try
                {
                    hits = Directory.EnumerateFiles(root, requested.Name + ".dll",
                        SearchOption.AllDirectories);
                }
                catch { continue; }

                foreach (var candidate in hits)
                {
                    var asm = TryLoad(candidate);
                    if (asm != null) return asm;
                }
            }

            // 2. GAC roots.
            foreach (var gacRoot in GacRoots())
            {
                string asmDir = Path.Combine(gacRoot, requested.Name);
                if (!SafeDirExists(asmDir)) continue;
                string[] versionDirs;
                try { versionDirs = Directory.GetDirectories(asmDir); }
                catch { continue; }

                foreach (var vdir in versionDirs)
                {
                    string dll = Path.Combine(vdir, requested.Name + ".dll");
                    if (!File.Exists(dll)) continue;
                    var asm = TryLoad(dll);
                    if (asm != null) return asm;
                }
            }

            return null;
        }

        private static Assembly TryLoad(string path)
        {
            try { return Assembly.LoadFrom(path); }
            catch { return null; }
        }

        private static bool SafeDirExists(string p)
        {
            try { return !string.IsNullOrEmpty(p) && Directory.Exists(p); }
            catch { return false; }
        }

        private static IEnumerable<string> GacRoots()
        {
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            yield return Path.Combine(windir, "Microsoft.NET", "assembly", "GAC_MSIL");
            yield return Path.Combine(windir, "Microsoft.NET", "assembly", "GAC_32");
            yield return Path.Combine(windir, "Microsoft.NET", "assembly", "GAC_64");
            yield return Path.Combine(windir, "assembly", "GAC_MSIL");
            yield return Path.Combine(windir, "assembly", "GAC_32");
            yield return Path.Combine(windir, "assembly", "GAC_64");
        }

        private static IEnumerable<string> DatevInstallRoots()
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                string fromReg = null;
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var key = baseKey?.OpenSubKey(@"SOFTWARE\DATEV"))
                    {
                        fromReg = key?.GetValue("Installationsverzeichnis") as string;
                    }
                }
                catch { }
                if (!string.IsNullOrWhiteSpace(fromReg)) yield return fromReg;
            }

            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string pf64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Path.Combine(pf86, "DATEV", "PROGRAMM");
            yield return Path.Combine(pf64, "DATEV", "PROGRAMM");
            yield return @"C:\Programme\DATEV\PROGRAMM";
            yield return Path.Combine(pf86, "DATEV");
            yield return Path.Combine(pf64, "DATEV");
            yield return @"C:\Programme\DATEV";
        }
    }
}
