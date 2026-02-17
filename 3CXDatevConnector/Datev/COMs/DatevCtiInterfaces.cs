using System;
using System.Runtime.InteropServices;

namespace DatevConnector.Datev.COMs
{
    // === Enums (from DatevCtiData.idl) ===

    public enum ENUM_DIRECTION
    {
        eDirUnknown = 0,
        eDirIncoming = 1,
        eDirOutgoing = 2
    }

    public enum ENUM_CALLSTATE
    {
        eCSAbsence = 0,
        eCSOffered = 1,
        eCSConnected = 2,
        eCSFinished = 3
    }

    // === Interfaces (GUIDs from DatevCtiBuddy.idl) ===

    [ComImport]
    [Guid("A4F23D58-64A0-412b-8A9A-0088026DC967")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IDatevCtiData
    {
        string CallID { get; set; }
        string SyncID { get; set; }
        ENUM_DIRECTION Direction { get; set; }
        DateTime Begin { get; set; }
        DateTime End { get; set; }
        ENUM_CALLSTATE CallState { get; set; }
        string Adressatenname { get; set; }
        string AdressatenId { get; set; }
        string DataSource { get; set; }
        string CalledNumber { get; set; }
        string Note { get; set; }
    }

    [ComImport]
    [Guid("867665AF-5040-4134-8D8D-3B7E9013EA12")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDatevCtiControl
    {
        void Dial([MarshalAs(UnmanagedType.IDispatch)] object pCallData);
        void Drop([MarshalAs(UnmanagedType.IDispatch)] object pCallData);
    }

    [ComImport]
    [Guid("9B1B44F5-222A-49a1-A200-56DE9DF10676")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDatevCtiHistory
    {
        void GetCallData([MarshalAs(UnmanagedType.IDispatch)] object pCallData);
    }

    [ComImport]
    [Guid("2C706C43-A0E8-4b18-98C2-BF1629AAD7C4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IDatevCtiNotification
    {
        void NewCall([MarshalAs(UnmanagedType.IDispatch)] object pCallData);
        void NewJournal([MarshalAs(UnmanagedType.IDispatch)] object pCallData);
        void CallStateChanged([MarshalAs(UnmanagedType.IDispatch)] object pCallData);
        void CallAdressatChanged([MarshalAs(UnmanagedType.IDispatch)] object pCallData);
    }
}
