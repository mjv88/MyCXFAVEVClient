using System;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using DatevConnector.Core.Config;
using DatevConnector.Datev.Managers;

namespace DatevConnector.Core
{
    /// <summary>
    /// Helper class for retry operations with exponential backoff.
    /// Provides consistent retry behavior across the application.
    /// </summary>
    public static class RetryHelper
    {
        public static int DefaultMaxRetries => AppConfig.GetIntClamped(ConfigKeys.SddMaxRetries, 3, 0, 10);

        public static int DefaultInitialDelaySeconds => AppConfig.GetIntClamped(ConfigKeys.SddRetryDelaySeconds, 1, 1, 30);

        /// <summary>
        /// Executes an operation with retry logic and exponential backoff.
        /// </summary>
        /// <typeparam name="T">Return type of the operation</typeparam>
        /// <param name="operation">The operation to execute</param>
        /// <param name="operationName">Name for logging purposes</param>
        /// <param name="maxRetries">Maximum number of retry attempts (default from config)</param>
        /// <param name="initialDelaySeconds">Initial delay in seconds before first retry (default from config)</param>
        /// <param name="shouldRetry">Optional predicate to determine if exception is retryable</param>
        /// <returns>Result of the operation, or default(T) if all retries fail</returns>
        public static T ExecuteWithRetry<T>(
            Func<T> operation,
            string operationName,
            int? maxRetries = null,
            int? initialDelaySeconds = null,
            Func<Exception, bool> shouldRetry = null)
        {
            int retries = maxRetries ?? DefaultMaxRetries;
            int delay = initialDelaySeconds ?? DefaultInitialDelaySeconds;
            Exception lastException = null;

            for (int attempt = 0; attempt <= retries; attempt++)
            {
                try
                {
                    return operation();
                }
                catch (Exception ex)
                {
                    lastException = ex;

                    // Check if this exception type should be retried
                    if (shouldRetry != null && !shouldRetry(ex))
                    {
                        LogManager.Log("{0} fehlgeschlagen (nicht wiederholbar): {1}", operationName, ex.Message);
                        break;
                    }

                    if (attempt < retries)
                    {
                        int currentDelay = Math.Min(delay * (int)Math.Pow(2, attempt), 60);
                        LogManager.Log("{0} fehlgeschlagen (Versuch {1}/{2}), erneuter Versuch in {3}s: {4}",
                            operationName, attempt + 1, retries + 1, currentDelay, ex.Message);

                        Thread.Sleep(currentDelay * 1000);
                    }
                    else
                    {
                        LogManager.Log("{0} fehlgeschlagen nach {1} Versuchen: {2}",
                            operationName, retries + 1, ex.Message);
                    }
                }
            }

            return default(T);
        }

        /// <summary>
        /// Async version of ExecuteWithRetry with exponential backoff and cancellation support.
        /// </summary>
        public static async Task<T> ExecuteWithRetryAsync<T>(
            Func<T> operation,
            string operationName,
            int? maxRetries = null,
            int? initialDelaySeconds = null,
            Func<Exception, bool> shouldRetry = null,
            CancellationToken ct = default)
        {
            int retries = maxRetries ?? DefaultMaxRetries;
            int delay = initialDelaySeconds ?? DefaultInitialDelaySeconds;

            for (int attempt = 0; attempt <= retries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return operation();
                }
                catch (Exception ex)
                {
                    if (shouldRetry != null && !shouldRetry(ex))
                    {
                        LogManager.Log("{0} fehlgeschlagen (nicht wiederholbar): {1}", operationName, ex.Message);
                        break;
                    }

                    if (attempt < retries)
                    {
                        int currentDelay = Math.Min(delay * (int)Math.Pow(2, attempt), 60);
                        LogManager.Log("{0} fehlgeschlagen (Versuch {1}/{2}), erneuter Versuch in {3}s: {4}",
                            operationName, attempt + 1, retries + 1, currentDelay, ex.Message);
                        await Task.Delay(currentDelay * 1000, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        LogManager.Log("{0} fehlgeschlagen nach {1} Versuchen: {2}",
                            operationName, retries + 1, ex.Message);
                    }
                }
            }
            return default;
        }

        /// <summary>
        /// Determines if an exception is likely a transient error that can be retried.
        /// </summary>
        /// <param name="ex">The exception to check</param>
        /// <returns>True if the error is likely transient</returns>
        public static bool IsTransientError(Exception ex)
        {
            return ex is TimeoutException
                || ex is System.Runtime.InteropServices.COMException
                || ex is XmlException
                || ex is System.IO.IOException
                || ex is System.Net.Sockets.SocketException;
        }

    }
}
