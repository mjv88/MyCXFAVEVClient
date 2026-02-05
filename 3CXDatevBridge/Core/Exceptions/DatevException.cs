using System;

namespace DatevBridge.Core.Exceptions
{
    /// <summary>
    /// Error categories for DATEV-related exceptions
    /// </summary>
    public enum DatevErrorCategory
    {
        /// <summary>DATEV application not running or not available</summary>
        Unavailable,

        /// <summary>Connection was established but has been lost</summary>
        ConnectionLost,

        /// <summary>COM/ROT interop error</summary>
        ComError,

        /// <summary>Stammdatendienst (SDD) error during contact loading</summary>
        SddError,

        /// <summary>Temporary error that may succeed on retry</summary>
        Transient,

        /// <summary>Permanent configuration or compatibility error</summary>
        Permanent
    }

    /// <summary>
    /// Exception for DATEV-related errors with categorization for appropriate handling.
    /// </summary>
    public class DatevException : Exception
    {
        /// <summary>
        /// The error category for determining retry/recovery behavior
        /// </summary>
        public DatevErrorCategory Category { get; }

        /// <summary>
        /// The COM HRESULT (if applicable)
        /// </summary>
        public new int HResult { get; }

        /// <summary>
        /// Creates a new DatevException
        /// </summary>
        /// <param name="message">Error message</param>
        /// <param name="category">Error category for handling decisions</param>
        /// <param name="hresult">COM HRESULT (optional)</param>
        public DatevException(string message, DatevErrorCategory category, int hresult = 0)
            : base(message)
        {
            Category = category;
            HResult = hresult;
        }

        /// <summary>
        /// Creates a new DatevException with an inner exception
        /// </summary>
        public DatevException(
            string message,
            DatevErrorCategory category,
            Exception innerException,
            int hresult = 0)
            : base(message, innerException)
        {
            Category = category;
            HResult = hresult;
        }

        /// <summary>
        /// Whether this error is retryable
        /// </summary>
        public bool IsRetryable => Category == DatevErrorCategory.Transient;

        /// <summary>
        /// Whether this error indicates DATEV is unavailable
        /// </summary>
        public bool IsUnavailable =>
            Category == DatevErrorCategory.Unavailable ||
            Category == DatevErrorCategory.ConnectionLost;

        /// <summary>
        /// Whether this is a permanent error that should not be retried
        /// </summary>
        public bool IsPermanent => Category == DatevErrorCategory.Permanent;

        /// <summary>
        /// Creates a DatevException for DATEV unavailable
        /// </summary>
        public static DatevException Unavailable(string message = null)
        {
            return new DatevException(
                message ?? "DATEV Arbeitsplatz nicht verfügbar",
                DatevErrorCategory.Unavailable);
        }

        /// <summary>
        /// Creates a DatevException for connection lost
        /// </summary>
        public static DatevException ConnectionLost(string message = null)
        {
            return new DatevException(
                message ?? "DATEV Verbindung verloren",
                DatevErrorCategory.ConnectionLost);
        }

        /// <summary>
        /// Creates a DatevException for COM/ROT errors
        /// </summary>
        public static DatevException ComError(string message, Exception innerException = null, int hresult = 0)
        {
            return new DatevException(message, DatevErrorCategory.ComError, innerException, hresult);
        }

        /// <summary>
        /// Creates a DatevException for SDD errors
        /// </summary>
        public static DatevException SddError(string message, Exception innerException = null)
        {
            return new DatevException(message, DatevErrorCategory.SddError, innerException);
        }

        /// <summary>
        /// Creates a DatevException for transient errors
        /// </summary>
        public static DatevException Transient(string message, Exception innerException = null)
        {
            return new DatevException(message, DatevErrorCategory.Transient, innerException);
        }

        /// <summary>
        /// Creates a DatevException for permanent errors
        /// </summary>
        public static DatevException Permanent(string message)
        {
            return new DatevException(message, DatevErrorCategory.Permanent);
        }

        public override string ToString()
        {
            var hr = HResult != 0 ? $" [HR=0x{HResult:X8}]" : "";
            return $"DatevException: {Message}{hr} Category={Category}";
        }
    }
}
