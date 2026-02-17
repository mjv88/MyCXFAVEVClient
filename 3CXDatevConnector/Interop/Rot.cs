using System;
using System.Runtime.InteropServices;

namespace DatevConnector.Interop
{
    public class Rot
    {
        [DllImport("oleaut32.dll", EntryPoint = "GetActiveObject",
            CharSet = CharSet.Unicode, ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        public static extern uint GetActiveObject(ref Guid clsId,
            ref uint reserved,
            [MarshalAs(UnmanagedType.IUnknown)] out object pprot);


        [DllImport("oleaut32.dll", EntryPoint = "RegisterActiveObject",
            CharSet = CharSet.Unicode, ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        public static extern uint RegisterActiveObject([MarshalAs(UnmanagedType.IUnknown)] object pIUnknown,
            ref Guid refclsid, uint flags, out uint pdwRegister);


        [DllImport("oleaut32.dll", EntryPoint = "RevokeActiveObject",
            CharSet = CharSet.Unicode, ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        public static extern uint RevokeActiveObject(uint pdwRegister, IntPtr reserved);
    }
}
