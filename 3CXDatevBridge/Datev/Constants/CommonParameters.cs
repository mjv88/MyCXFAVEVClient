using System;

namespace DatevBridge.Datev.Constants
{
    public static class CommonParameters
    {
        // Application info
        public const string AppName = "DatevBridge";
        public const string AppFullName = "DATEV-3CX Bridge";

        // Configuration
        public const string ConfigFileName = "DatevBridge.config";
        
        // Logging
        public const string LogFileName = "3CXDatevBridge.log";

        // DATEV COM GUID
        public static readonly Guid ClsIdDatev = new Guid("A299D197-E7C3-43E2-8ACC-C608FC2A7806");
    }
}
