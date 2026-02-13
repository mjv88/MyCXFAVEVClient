using System;
using DatevConnector.Datev.Managers;

namespace DatevConnector.Core
{
    /// <summary>
    /// Shared helper for safe event invocation across all telephony providers.
    /// Centralizes disposal-aware exception handling that was previously duplicated
    /// in TapiLineMonitor, PipeTelephonyProvider, and WebclientTelephonyProvider.
    /// </summary>
    public static class EventHelper
    {
        /// <summary>
        /// Safely invoke a typed event handler. Catches ObjectDisposedException
        /// and InvalidOperationException (common during shutdown/disposal) silently,
        /// and logs any other exceptions.
        /// </summary>
        /// <returns>True if handler was invoked successfully.</returns>
        public static bool SafeInvoke<T>(Action<T> handler, T arg, string context = null)
        {
            if (handler == null)
                return false;
            try
            {
                handler(arg);
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (Exception ex)
            {
                LogManager.Log("Error in {0} handler: {1}",
                    context ?? handler.Method.Name, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Safely invoke a parameterless event handler.
        /// </summary>
        /// <returns>True if handler was invoked successfully.</returns>
        public static bool SafeInvoke(Action handler, string context = null)
        {
            if (handler == null)
                return false;
            try
            {
                handler();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (Exception ex)
            {
                LogManager.Log("Error in {0} handler: {1}",
                    context ?? handler.Method.Name, ex.Message);
                return false;
            }
        }
    }
}
