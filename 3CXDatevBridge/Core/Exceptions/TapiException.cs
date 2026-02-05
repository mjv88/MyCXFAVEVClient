using System;
using DatevBridge.Interop;

namespace DatevBridge.Core.Exceptions
{
    /// <summary>
    /// Exception for TAPI-related errors with categorization for appropriate handling.
    /// </summary>
    public class TapiException : Exception
    {
        /// <summary>
        /// The error category for determining retry/recovery behavior
        /// </summary>
        public TapiInterop.TapiErrorCategory Category { get; }

        /// <summary>
        /// The native TAPI error code (if available)
        /// </summary>
        public int NativeErrorCode { get; }

        /// <summary>
        /// The line extension involved (if applicable)
        /// </summary>
        public string Extension { get; }

        /// <summary>
        /// Creates a new TapiException
        /// </summary>
        /// <param name="message">Error message</param>
        /// <param name="category">Error category for handling decisions</param>
        /// <param name="nativeCode">Native TAPI error code</param>
        /// <param name="extension">Line extension (optional)</param>
        public TapiException(
            string message,
            TapiInterop.TapiErrorCategory category,
            int nativeCode = 0,
            string extension = null)
            : base(message)
        {
            Category = category;
            NativeErrorCode = nativeCode;
            Extension = extension;
        }

        /// <summary>
        /// Creates a new TapiException with an inner exception
        /// </summary>
        public TapiException(
            string message,
            TapiInterop.TapiErrorCategory category,
            Exception innerException,
            int nativeCode = 0,
            string extension = null)
            : base(message, innerException)
        {
            Category = category;
            NativeErrorCode = nativeCode;
            Extension = extension;
        }

        /// <summary>
        /// Whether this error is retryable (transient errors only)
        /// </summary>
        public bool IsRetryable => Category == TapiInterop.TapiErrorCategory.Transient;

        /// <summary>
        /// Whether this error requires line reconnection
        /// </summary>
        public bool RequiresReconnect => Category == TapiInterop.TapiErrorCategory.LineClosed;

        /// <summary>
        /// Whether this error requires full TAPI reinitialization
        /// </summary>
        public bool RequiresReinit => Category == TapiInterop.TapiErrorCategory.Shutdown;

        /// <summary>
        /// Whether this is a permanent error that should not be retried
        /// </summary>
        public bool IsPermanent => Category == TapiInterop.TapiErrorCategory.Permanent;

        /// <summary>
        /// Creates a TapiException from a native error code
        /// </summary>
        /// <param name="errorCode">Native TAPI error code</param>
        /// <param name="extension">Line extension (optional)</param>
        /// <returns>Categorized TapiException</returns>
        public static TapiException FromErrorCode(int errorCode, string extension = null)
        {
            var category = TapiInterop.CategorizeError(errorCode);
            var description = TapiInterop.GetErrorDescription(errorCode);

            return new TapiException(
                description,
                category,
                errorCode,
                extension);
        }

        /// <summary>
        /// Creates a TapiException for a transient error
        /// </summary>
        public static TapiException Transient(string message, string extension = null)
        {
            return new TapiException(message, TapiInterop.TapiErrorCategory.Transient, 0, extension);
        }

        /// <summary>
        /// Creates a TapiException for a line closed error
        /// </summary>
        public static TapiException LineClosed(string message, string extension = null)
        {
            return new TapiException(message, TapiInterop.TapiErrorCategory.LineClosed, 0, extension);
        }

        /// <summary>
        /// Creates a TapiException for a shutdown error
        /// </summary>
        public static TapiException Shutdown(string message)
        {
            return new TapiException(message, TapiInterop.TapiErrorCategory.Shutdown);
        }

        /// <summary>
        /// Creates a TapiException for a permanent error
        /// </summary>
        public static TapiException Permanent(string message, string extension = null)
        {
            return new TapiException(message, TapiInterop.TapiErrorCategory.Permanent, 0, extension);
        }

        public override string ToString()
        {
            var ext = string.IsNullOrEmpty(Extension) ? "" : $" (Ext: {Extension})";
            var code = NativeErrorCode != 0 ? $" [0x{NativeErrorCode:X8}]" : "";
            return $"TapiException: {Message}{ext}{code} Category={Category}";
        }
    }
}
