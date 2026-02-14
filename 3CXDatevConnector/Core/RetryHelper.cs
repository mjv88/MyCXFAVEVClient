using System;
using System.Threading;
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
        /// <summary>
        /// Default maximum retry attempts
        /// </summary>
        public static int DefaultMaxRetries => AppConfig.GetInt(ConfigKeys.SddMaxRetries, 3);

        /// <summary>
        /// Default initial delay in seconds
        /// </summary>
        public static int DefaultInitialDelaySeconds => AppConfig.GetInt(ConfigKeys.SddRetryDelaySeconds, 1);

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
                        LogManager.Log("{0} failed with non-retryable error: {1}", operationName, ex.Message);
                        break;
                    }

                    if (attempt < retries)
                    {
                        int currentDelay = delay * (int)Math.Pow(2, attempt);
                        LogManager.Log("{0} failed (attempt {1}/{2}), retrying in {3}s: {4}",
                            operationName, attempt + 1, retries + 1, currentDelay, ex.Message);

                        Thread.Sleep(currentDelay * 1000);
                    }
                    else
                    {
                        LogManager.Log("{0} failed after {1} attempts: {2}",
                            operationName, retries + 1, ex.Message);
                    }
                }
            }

            return default(T);
        }

        /// <summary>
        /// Executes an action with retry logic and exponential backoff.
        /// Delegates to the generic overload internally.
        /// </summary>
        /// <param name="action">The action to execute</param>
        /// <param name="operationName">Name for logging purposes</param>
        /// <param name="maxRetries">Maximum number of retry attempts (default from config)</param>
        /// <param name="initialDelaySeconds">Initial delay in seconds before first retry (default from config)</param>
        /// <param name="shouldRetry">Optional predicate to determine if exception is retryable</param>
        /// <returns>True if action succeeded, false if all retries failed</returns>
        public static bool ExecuteWithRetry(
            Action action,
            string operationName,
            int? maxRetries = null,
            int? initialDelaySeconds = null,
            Func<Exception, bool> shouldRetry = null)
        {
            var result = ExecuteWithRetry<bool>(
                () => { action(); return true; },
                operationName, maxRetries, initialDelaySeconds, shouldRetry);
            return result;
        }

        /// <summary>
        /// Determines if an exception is likely a transient error that can be retried.
        /// </summary>
        /// <param name="ex">The exception to check</param>
        /// <returns>True if the error is likely transient</returns>
        public static bool IsTransientError(Exception ex)
        {
            // Check exception types first (cheaper than string matching)
            if (ex is TimeoutException ||
                ex is System.Runtime.InteropServices.COMException ||
                ex is XmlException)
                return true;

            // Fall back to message-based heuristics for untyped errors
            string message = ex.Message?.ToLower() ?? "";

            return message.Contains("temporarily") ||
                   message.Contains("busy") ||
                   message.Contains("unavailable") ||
                   message.Contains("connection");
        }

    }
}
