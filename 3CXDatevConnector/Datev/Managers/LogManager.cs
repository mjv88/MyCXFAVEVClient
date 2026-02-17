using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DatevConnector.Core;
using DatevConnector.Core.Config;

namespace DatevConnector.Datev.Managers
{
    /// <summary>
    /// Log severity levels
    /// </summary>
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
        Critical = 4
    }

    /// <summary>
    /// Enhanced logging implementation with log levels and rotation
    /// </summary>
    public static class LogManager
    {
        private static readonly object _lockObj = new object();
        private static readonly string _logFolder;
        private static readonly string _logFileBaseName;
        private static LogLevel _minLogLevel;
        private static LogLevel _configuredLogLevel;
        private static readonly long _maxLogSizeBytes;
        private static readonly int _maxLogFiles;
        private static bool _asyncLogging;
        private static readonly ConcurrentQueue<string> _logQueue;
        private static readonly CancellationTokenSource _cts;
        private static readonly Task _logWriterTask;

        private static readonly int _logRetentionDays;
        private static string _currentLogFilePath;
        private static long _currentLogSize;

        static LogManager()
        {
            // Setup log folder
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _logFolder = Path.Combine(appDataPath, "3CXDATEVConnector");

            if (!Directory.Exists(_logFolder))
                Directory.CreateDirectory(_logFolder);

            _logFileBaseName = "3CXDatevConnector";
            _currentLogFilePath = Path.Combine(_logFolder, $"{_logFileBaseName}.log");

            // Read configuration from INI
            string levelStr = AppConfig.GetString(ConfigKeys.LogLevel, "Info");
            if (!Enum.TryParse(levelStr, true, out _minLogLevel))
                _minLogLevel = LogLevel.Info;

            // Legacy support: DebugLogging=true sets level to Debug
            if (AppConfig.GetBool(ConfigKeys.DebugLogging, false))
                _minLogLevel = LogLevel.Debug;

            // Remember configured level for restoring after debug toggle
            _configuredLogLevel = _minLogLevel;

            // Max log size in MB (default 10MB)
            int maxSizeMb = AppConfig.GetInt(ConfigKeys.LogMaxSizeMB, 10);
            _maxLogSizeBytes = maxSizeMb * 1024L * 1024L;

            // Max number of log files to keep (default 5)
            _maxLogFiles = AppConfig.GetInt(ConfigKeys.LogMaxFiles, 5);

            // Log retention in days (default 7)
            _logRetentionDays = AppConfig.GetInt(ConfigKeys.LogRetentionDays, 7);
            if (_logRetentionDays < 1) _logRetentionDays = 1;
            if (_logRetentionDays > 90) _logRetentionDays = 90;

            // Async logging (default true for better performance)
            bool asyncEnabled = AppConfig.GetBool(ConfigKeys.LogAsync, true);

            // Initialize current log size
            if (File.Exists(_currentLogFilePath))
            {
                try
                {
                    _currentLogSize = new FileInfo(_currentLogFilePath).Length;
                }
                catch
                {
                    _currentLogSize = 0;
                }
            }

            // Log startup synchronously (before async writer starts to avoid race condition)
            Log(LogLevel.Debug, "LogManager initialized");

            // Setup async logging after init message is written synchronously
            _asyncLogging = asyncEnabled;
            if (_asyncLogging)
            {
                _logQueue = new ConcurrentQueue<string>();
                _cts = new CancellationTokenSource();
                _logWriterTask = Task.Run(() => LogWriterLoop(_cts.Token));
            }

            // Purge rotated log files older than retention period
            PurgeOldLogs();
        }

        /// <summary>
        /// Log a message at Info level (backward compatible)
        /// </summary>
        public static void Log(string format, params object[] parameters)
        {
            Log(LogLevel.Info, format, parameters);
        }

        /// <summary>
        /// Log a message at specified level
        /// </summary>
        public static void Log(LogLevel level, string format, params object[] parameters)
        {
            if (level < _minLogLevel)
                return;

            try
            {
                string message = parameters.Length > 0
                    ? string.Format(format, parameters)
                    : format;

                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string levelStr = level.ToString().ToUpper().PadRight(8);
                string threadId = Thread.CurrentThread.ManagedThreadId.ToString().PadLeft(4);
                string logLine = $"[{timestamp}] [{levelStr}] [T{threadId}] {message}";

                // Always write to console
                WriteToConsole(level, logLine);

                // Write to file
                if (_asyncLogging)
                {
                    _logQueue.Enqueue(logLine);
                }
                else
                {
                    WriteToFile(logLine);
                }
            }
            catch (Exception ex)
            {
                // Logging should never throw - output to stderr as last resort
                try
                {
                    Console.Error.WriteLine($"[LOG ERROR] {ex.Message}");
                }
                catch
                {
                    // Truly nothing we can do
                }
            }
        }

        /// <summary>
        /// Log debug message
        /// </summary>
        public static void Debug(string format, params object[] parameters)
        {
            Log(LogLevel.Debug, format, parameters);
        }

        /// <summary>
        /// Log info message
        /// </summary>
        public static void Info(string format, params object[] parameters)
        {
            Log(LogLevel.Info, format, parameters);
        }

        /// <summary>
        /// Log warning message
        /// </summary>
        public static void Warning(string format, params object[] parameters)
        {
            Log(LogLevel.Warning, format, parameters);
        }

        /// <summary>
        /// Log error message
        /// </summary>
        public static void Error(string format, params object[] parameters)
        {
            Log(LogLevel.Error, format, parameters);
        }

        /// <summary>
        /// Log error with exception
        /// </summary>
        public static void Error(Exception ex, string format, params object[] parameters)
        {
            string message = parameters.Length > 0
                ? string.Format(format, parameters)
                : format;
            Log(LogLevel.Error, "{0}: {1}\nStackTrace: {2}", message, ex.Message, ex.StackTrace);
        }

        /// <summary>
        /// Log critical message
        /// </summary>
        public static void Critical(string format, params object[] parameters)
        {
            Log(LogLevel.Critical, format, parameters);
        }

        /// <summary>
        /// Write to console with color coding
        /// </summary>
        private static void WriteToConsole(LogLevel level, string logLine)
        {
            ConsoleColor originalColor = Console.ForegroundColor;
            try
            {
                switch (level)
                {
                    case LogLevel.Debug:
                        Console.ForegroundColor = ConsoleColor.Gray;
                        break;
                    case LogLevel.Info:
                        Console.ForegroundColor = ConsoleColor.White;
                        break;
                    case LogLevel.Warning:
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        break;
                    case LogLevel.Error:
                        Console.ForegroundColor = ConsoleColor.Red;
                        break;
                    case LogLevel.Critical:
                        Console.ForegroundColor = ConsoleColor.Magenta;
                        break;
                    default:
                        Console.ForegroundColor = ConsoleColor.White;
                        break;
                }
                Console.WriteLine(logLine);
            }
            finally
            {
                Console.ForegroundColor = originalColor;
            }
        }

        /// <summary>
        /// Write to log file (synchronous)
        /// </summary>
        private static void WriteToFile(string logLine)
        {
            lock (_lockObj)
            {
                try
                {
                    // Check if rotation needed
                    if (_currentLogSize >= _maxLogSizeBytes)
                    {
                        RotateLogs();
                    }

                    string lineWithNewline = logLine + Environment.NewLine;
                    File.AppendAllText(_currentLogFilePath, lineWithNewline);
                    _currentLogSize += System.Text.Encoding.UTF8.GetByteCount(lineWithNewline);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[LOG FILE ERROR] {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Async log writer loop
        /// </summary>
        private static async Task LogWriterLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Process queued log entries
                    while (_logQueue.TryDequeue(out string logLine))
                    {
                        WriteToFile(logLine);
                    }

                    // Wait before next batch
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[LOG WRITER ERROR] {ex.Message}");
                }
            }

            // Flush remaining entries on shutdown
            while (_logQueue.TryDequeue(out string logLine))
            {
                WriteToFile(logLine);
            }
        }

        /// <summary>
        /// Rotate log files
        /// </summary>
        private static void RotateLogs()
        {
            try
            {
                // Delete oldest log if at max
                string oldestLog = Path.Combine(_logFolder, $"{_logFileBaseName}.{_maxLogFiles}.log");
                if (File.Exists(oldestLog))
                {
                    File.Delete(oldestLog);
                }

                // Rotate existing logs
                for (int i = _maxLogFiles - 1; i >= 1; i--)
                {
                    string currentPath = Path.Combine(_logFolder, $"{_logFileBaseName}.{i}.log");
                    string nextPath = Path.Combine(_logFolder, $"{_logFileBaseName}.{i + 1}.log");

                    if (File.Exists(currentPath))
                    {
                        File.Move(currentPath, nextPath);
                    }
                }

                // Rename current log to .1.log
                if (File.Exists(_currentLogFilePath))
                {
                    string firstRotated = Path.Combine(_logFolder, $"{_logFileBaseName}.1.log");
                    File.Move(_currentLogFilePath, firstRotated);
                }

                // Reset size counter
                _currentLogSize = 0;

                PurgeOldLogs();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Delete rotated log files older than the configured retention period.
        /// </summary>
        private static void PurgeOldLogs()
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-_logRetentionDays);
                for (int i = 1; i <= _maxLogFiles; i++)
                {
                    string path = Path.Combine(_logFolder, $"{_logFileBaseName}.{i}.log");
                    if (File.Exists(path) && File.GetLastWriteTime(path) < cutoff)
                    {
                        File.Delete(path);
                    }
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup
            }
        }

        /// <summary>
        /// Flush pending log entries (call on shutdown)
        /// </summary>
        public static void Flush()
        {
            if (_asyncLogging && _logQueue != null)
            {
                // Wait for queue to drain
                int maxWait = 50; // 5 seconds max
                while (!_logQueue.IsEmpty && maxWait > 0)
                {
                    Thread.Sleep(100);
                    maxWait--;
                }
            }
        }

        /// <summary>
        /// Shutdown the log manager
        /// </summary>
        public static void Shutdown()
        {
            if (_asyncLogging)
            {
                _cts?.Cancel();
                try
                {
                    _logWriterTask?.Wait(TimeSpan.FromSeconds(5));
                }
                catch { }
            }
        }

        /// <summary>
        /// Path to the log folder
        /// </summary>
        public static string LogFolder => _logFolder;

        /// <summary>
        /// Path to the current log file
        /// </summary>
        public static string LogFilePath => _currentLogFilePath;

        /// <summary>
        /// Current minimum log level
        /// </summary>
        public static LogLevel MinLogLevel => _minLogLevel;

        /// <summary>
        /// Whether debug logging is currently enabled
        /// </summary>
        public static bool IsDebugEnabled => _minLogLevel <= LogLevel.Debug;

        /// <summary>
        /// Mask a value for log output.
        /// Visible characters are controlled by INI key LogMaskDigits (default 5).
        /// Set to 0 to disable masking entirely.
        /// Example (5): "+4951147402435" -> "*********02435"
        /// </summary>
        private static string MaskValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            int visible = AppConfig.GetInt(ConfigKeys.LogMaskDigits, 5);

            if (visible <= 0 || value.Length <= visible)
                return value;

            return new string('*', value.Length - visible) + value.Substring(value.Length - visible);
        }

        /// <summary>
        /// Mask a phone number for log output.
        /// </summary>
        public static string Mask(string number) => MaskValue(number);

        /// <summary>
        /// Mask a contact name for log output.
        /// </summary>
        public static string MaskName(string name) => MaskValue(name);

        /// <summary>
        /// Enable or disable verbose (debug) logging at runtime.
        /// When disabled, restores the originally configured log level.
        /// </summary>
        public static void SetDebugMode(bool enabled)
        {
            if (enabled)
            {
                _minLogLevel = LogLevel.Debug;
                Log(LogLevel.Info, "Verbose logging ENABLED");
            }
            else
            {
                _minLogLevel = _configuredLogLevel;
                Log(LogLevel.Info, "Verbose logging DISABLED");
            }
        }
    }
}
