using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DatevConnector.Datev.COMs
{
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public class CallData : IDatevCtiData
    {
        public CallData()
        {
            AdressatenId = string.Empty;
            Adressatenname = string.Empty;
            CallID = string.Empty;
            CalledNumber = string.Empty;
            CallState = ENUM_CALLSTATE.eCSOffered;
            DataSource = string.Empty;
            Direction = ENUM_DIRECTION.eDirUnknown;
            Note = string.Empty;
            SyncID = string.Empty;
        }

        #region IDatevCtiData Members

        public string AdressatenId { get; set; }
        public string Adressatenname { get; set; }
        public DateTime Begin { get; set; }
        public string CallID { get; set; }
        public ENUM_CALLSTATE CallState { get; set; }
        public string CalledNumber { get; set; }
        public string DataSource { get; set; }
        public ENUM_DIRECTION Direction { get; set; }
        public DateTime End { get; set; }
        public string Note { get; set; }
        public string SyncID { get; set; }

        #endregion

        public override string ToString()
        {
            StringBuilder builder = new StringBuilder();

            builder.Append("AdressatenId: " + AdressatenId + "\n");
            builder.Append("Adressatenname: " + Adressatenname + "\n");
            builder.Append("CallID: " + CallID + "\n");
            builder.Append("CalledNumber: " + CalledNumber + "\n");

            switch (CallState)
            {
                case ENUM_CALLSTATE.eCSAbsence:
                    builder.Append("CallState: Abwesend\n");
                    break;
                case ENUM_CALLSTATE.eCSConnected:
                    builder.Append("CallState: Verbunden\n");
                    break;
                case ENUM_CALLSTATE.eCSFinished:
                    builder.Append("CallState: Beendet\n");
                    break;
                case ENUM_CALLSTATE.eCSOffered:
                    builder.Append("CallState: Läutend\n");
                    break;
            }

            builder.Append("DataSource: " + DataSource + "\n");

            switch (Direction)
            {
                case ENUM_DIRECTION.eDirIncoming:
                    builder.Append("Direction: Eingehend\n");
                    break;
                case ENUM_DIRECTION.eDirOutgoing:
                    builder.Append("Direction: Ausgehend\n");
                    break;
                case ENUM_DIRECTION.eDirUnknown:
                    builder.Append("Direction: Unbekannt\n");
                    break;
            }

            builder.Append("Begin: " + Begin.ToString("dd.MM.yyyy HH:mm:ss") + "\n");
            builder.Append("End: " + End.ToString("dd.MM.yyyy HH:mm:ss") + "\n");
            builder.Append("Note: " + Note + "\n");
            builder.Append("SyncID: " + SyncID + "\n");

            return builder.ToString();
        }
    }
}
