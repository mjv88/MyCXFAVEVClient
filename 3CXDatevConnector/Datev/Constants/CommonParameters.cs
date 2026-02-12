using System;

namespace DatevConnector.Datev.Constants
{
    public static class CommonParameters
    {
        // Application info
        public const string AppName = "DatevConnector";
        public const string AppFullName = "3CX - DATEV Connector";

        // Configuration
        public const string ConfigFileName = "DatevConnector.config";
        
        // Logging
        public const string LogFileName = "3CXDatevConnector.log";

        // DATEV COM GUID
        public static readonly Guid ClsIdDatev = new Guid("A299D197-E7C3-43E2-8ACC-C608FC2A7806");
    }
}
