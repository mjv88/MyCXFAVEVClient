using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using DatevConnector.Datev.Managers;

namespace DatevConnector.Core
{
    /// <summary>
    /// Reusable memory optimization utilities for aggressive GC and working set trimming
    /// after large batch operations.
    /// </summary>
    public static class MemoryOptimizer
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(
            IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        /// <summary>
        /// Perform a full GC collection with LOH compaction, then trim the working set.
        /// Use after large batch load/transform operations (e.g., XML deserialization).
        ///
        /// Why this is necessary:
        /// 1. XML deserialization creates many short-lived wrapper objects that land in Gen2.
        /// 2. Workstation GC rarely does full collections, leaving dead Gen2 objects for minutes.
        /// 3. Double-collect: first pass queues finalizers; second pass frees finalized objects.
        /// 4. SetProcessWorkingSetSize(-1,-1) trims freed pages so the OS can reclaim them.
        /// </summary>
        /// <returns>Megabytes freed (working set delta), or -1 on error.</returns>
        public static long CollectAndTrim()
        {
            try
            {
                long wsBefore = Environment.WorkingSet / (1024 * 1024);

                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

                TrimWorkingSet();

                long wsAfter = Environment.WorkingSet / (1024 * 1024);
                long managedMB = GC.GetTotalMemory(false) / (1024 * 1024);
                long freed = wsBefore - wsAfter;

                LogManager.Debug("MemoryOptimizer: working set {0} MB -> {1} MB (freed {2} MB), managed heap {3} MB",
                    wsBefore, wsAfter, freed, managedMB);

                return freed;
            }
            catch (Exception ex)
            {
                LogManager.Log("MemoryOptimizer: CollectAndTrim fehlgeschlagen - {0}", ex.Message);
                return -1;
            }
        }

        /// <summary>
        /// Force a Gen2 GC to free old cache data before allocating new data.
        /// Lighter than CollectAndTrim — no LOH compaction or working set trim.
        /// </summary>
        public static void CollectGen2()
        {
            GC.Collect(2, GCCollectionMode.Forced);
            GC.WaitForPendingFinalizers();
        }

        /// <summary>
        /// Ask the OS to trim the process working set.
        /// Passing (-1, -1) tells Windows to trim pages not currently in use.
        /// </summary>
        private static void TrimWorkingSet()
        {
            try
            {
                using (var proc = Process.GetCurrentProcess())
                {
                    SetProcessWorkingSetSize(proc.Handle, (IntPtr)(-1), (IntPtr)(-1));
                }
            }
            catch (Exception ex)
            {
                LogManager.Log("MemoryOptimizer: TrimWorkingSet fehlgeschlagen - {0}", ex.Message);
            }
        }
    }
}
