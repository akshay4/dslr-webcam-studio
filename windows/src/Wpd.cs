// COM interop for the Windows Portable Devices API (PortableDeviceApi.dll, part of Windows).
// Only the members needed to enumerate devices and send raw PTP/MTP commands are typed;
// vtable order matches PortableDeviceApi.idl / PortableDeviceTypes.idl.
using System;
using System.Runtime.InteropServices;

namespace DslrWebcamStudio.Wpd
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
        public PropertyKey(Guid g, uint p) { fmtid = g; pid = p; }
    }

    // PROPVARIANT: 2-byte vt, 6 reserved bytes, then a 16-byte union (24 bytes on x64).
    [StructLayout(LayoutKind.Sequential)]
    public struct PropVariant
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr p1;
        public IntPtr p2;

        public const ushort VT_UI4 = 19;

        public static PropVariant FromUInt32(uint v)
        {
            var pv = new PropVariant();
            pv.vt = VT_UI4;
            pv.p1 = new IntPtr((long)v);
            return pv;
        }

        public uint UInt32Value { get { return (uint)(p1.ToInt64() & 0xFFFFFFFF); } }
    }

    [ComImport, Guid("a1567595-4c2f-4574-a6fa-ecef917b9a40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPortableDeviceManager
    {
        void GetDevices(IntPtr pPnPDeviceIDs, ref uint pcPnPDeviceIDs); // LPWSTR array, caller-allocated
        void RefreshDeviceList();
        void GetDeviceFriendlyName([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr name, ref uint cch);
        void GetDeviceDescription([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr desc, ref uint cch);
        void GetDeviceManufacturer([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mfr, ref uint cch);
    }

    [ComImport, Guid("0af10cec-2ecd-4b92-9581-34f6ae0637f3")]
    public class PortableDeviceManagerClass { }

    [ComImport, Guid("625e2df8-6392-4cf0-9ad1-3cfa5f17775c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPortableDevice
    {
        void Open([MarshalAs(UnmanagedType.LPWStr)] string pszPnPDeviceID, IPortableDeviceValues pClientInfo);
        void SendCommand(uint dwFlags, IPortableDeviceValues pParameters, out IPortableDeviceValues ppResults);
        void Content(out IntPtr ppContent);
        void Capabilities(out IntPtr ppCapabilities);
        void Cancel();
        void Close();
    }

    [ComImport, Guid("728a21c5-3d9e-48d7-9810-864848f0f404")]
    public class PortableDeviceClass { }

    [ComImport, Guid("6848f6f2-3155-4f86-b6f5-263eeeab3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPortableDeviceValues
    {
        void GetCount(out uint pcelt);
        void GetAt(uint index, ref PropertyKey pKey, ref PropVariant pValue);
        void SetValue(ref PropertyKey key, ref PropVariant pValue);
        void GetValue(ref PropertyKey key, out PropVariant pValue);
        void SetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string Value);
        void GetStringValue(ref PropertyKey key, out IntPtr pValue);
        void SetUnsignedIntegerValue(ref PropertyKey key, uint Value);
        void GetUnsignedIntegerValue(ref PropertyKey key, out uint pValue);
        void SetSignedIntegerValue(ref PropertyKey key, int Value);
        void GetSignedIntegerValue(ref PropertyKey key, out int pValue);
        void SetUnsignedLargeIntegerValue(ref PropertyKey key, ulong Value);
        void GetUnsignedLargeIntegerValue(ref PropertyKey key, out ulong pValue);
        void SetSignedLargeIntegerValue(ref PropertyKey key, long Value);
        void GetSignedLargeIntegerValue(ref PropertyKey key, out long pValue);
        void SetFloatValue(ref PropertyKey key, float Value);
        void GetFloatValue(ref PropertyKey key, out float pValue);
        void SetErrorValue(ref PropertyKey key, int Value);
        void GetErrorValue(ref PropertyKey key, out int pValue);
        void SetKeyValue(ref PropertyKey key, ref PropertyKey Value);
        void GetKeyValue(ref PropertyKey key, out PropertyKey pValue);
        void SetBoolValue(ref PropertyKey key, int Value);
        void GetBoolValue(ref PropertyKey key, out int pValue);
        void SetIUnknownValue(ref PropertyKey key, [MarshalAs(UnmanagedType.IUnknown)] object pValue);
        void GetIUnknownValue(ref PropertyKey key, [MarshalAs(UnmanagedType.IUnknown)] out object ppValue);
        void SetGuidValue(ref PropertyKey key, ref Guid Value);
        void GetGuidValue(ref PropertyKey key, out Guid pValue);
        void SetBufferValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] pValue, uint cbValue);
        void GetBufferValue(ref PropertyKey key, out IntPtr ppValue, out uint pcbValue);
        void SetIPortableDeviceValuesValue(ref PropertyKey key, IPortableDeviceValues pValue);
        void GetIPortableDeviceValuesValue(ref PropertyKey key, out IPortableDeviceValues ppValue);
        void SetIPortableDevicePropVariantCollectionValue(ref PropertyKey key, IPortableDevicePropVariantCollection pValue);
        void GetIPortableDevicePropVariantCollectionValue(ref PropertyKey key, out IPortableDevicePropVariantCollection ppValue);
        void SetIPortableDeviceKeyCollectionValue(ref PropertyKey key, IntPtr pValue);
        void GetIPortableDeviceKeyCollectionValue(ref PropertyKey key, out IntPtr ppValue);
        void SetIPortableDeviceValuesCollectionValue(ref PropertyKey key, IntPtr pValue);
        void GetIPortableDeviceValuesCollectionValue(ref PropertyKey key, out IntPtr ppValue);
        void RemoveValue(ref PropertyKey key);
        void CopyValuesFromPropertyStore(IntPtr pStore);
        void CopyValuesToPropertyStore(IntPtr pStore);
        void Clear();
    }

    [ComImport, Guid("0c15d503-d017-47ce-9016-7b3f978721cc")]
    public class PortableDeviceValuesClass { }

    [ComImport, Guid("89b2e422-4f1b-4316-bcef-a44afea83eb3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPortableDevicePropVariantCollection
    {
        void GetCount(ref uint pcElems);
        void GetAt(uint dwIndex, ref PropVariant pValue);
        void Add(ref PropVariant pValue);
        void GetType(out ushort pvt);
        void ChangeType(ushort vt);
        void Clear();
        void RemoveAt(uint dwIndex);
    }

    [ComImport, Guid("08a99e2f-6d6d-4b80-af5a-baf2bcbe4cb9")]
    public class PortableDevicePropVariantCollectionClass { }

    public static class Keys
    {
        static readonly Guid Common = new Guid("F0422A9C-5DC8-4440-B5BD-5DF28835658A");
        public static readonly PropertyKey CommandCategory = new PropertyKey(Common, 1001);
        public static readonly PropertyKey CommandId = new PropertyKey(Common, 1002);
        public static readonly PropertyKey HResult = new PropertyKey(Common, 1003);

        static readonly Guid Client = new Guid("204D9F0C-2292-4080-9F42-40664E70F859");
        public static readonly PropertyKey ClientName = new PropertyKey(Client, 2);
        public static readonly PropertyKey ClientMajor = new PropertyKey(Client, 3);
        public static readonly PropertyKey ClientMinor = new PropertyKey(Client, 4);
        public static readonly PropertyKey ClientRevision = new PropertyKey(Client, 5);
        public static readonly PropertyKey ClientSecurityQos = new PropertyKey(Client, 8);

        // WPD_CATEGORY_MTP_EXT_VENDOR_OPERATIONS: raw PTP/MTP operations through the in-box driver.
        public static readonly Guid MtpExt = new Guid("4d545058-1a2e-4106-a357-771e0819fc56");
        public static readonly PropertyKey CmdGetVendorOpcodes = new PropertyKey(MtpExt, 11);
        public static readonly PropertyKey CmdExecNoData = new PropertyKey(MtpExt, 12);
        public static readonly PropertyKey CmdExecDataToRead = new PropertyKey(MtpExt, 13);
        public static readonly PropertyKey CmdExecDataToWrite = new PropertyKey(MtpExt, 14);
        public static readonly PropertyKey CmdReadData = new PropertyKey(MtpExt, 15);
        public static readonly PropertyKey CmdWriteData = new PropertyKey(MtpExt, 16);
        public static readonly PropertyKey CmdEndDataTransfer = new PropertyKey(MtpExt, 17);

        public static readonly PropertyKey OpCode = new PropertyKey(MtpExt, 1001);
        public static readonly PropertyKey OpParams = new PropertyKey(MtpExt, 1002);
        public static readonly PropertyKey ResponseCode = new PropertyKey(MtpExt, 1003);
        public static readonly PropertyKey ResponseParams = new PropertyKey(MtpExt, 1004);
        public static readonly PropertyKey VendorOpcodes = new PropertyKey(MtpExt, 1005);
        public static readonly PropertyKey TransferContext = new PropertyKey(MtpExt, 1006);
        public static readonly PropertyKey TransferTotalSize = new PropertyKey(MtpExt, 1007);
        public static readonly PropertyKey TransferBytesToRead = new PropertyKey(MtpExt, 1008);
        public static readonly PropertyKey TransferBytesRead = new PropertyKey(MtpExt, 1009);
        public static readonly PropertyKey TransferBytesToWrite = new PropertyKey(MtpExt, 1010);
        public static readonly PropertyKey TransferBytesWritten = new PropertyKey(MtpExt, 1011);
        public static readonly PropertyKey TransferData = new PropertyKey(MtpExt, 1012);
        public static readonly PropertyKey OptimalBufferSize = new PropertyKey(MtpExt, 1013);
    }
}
