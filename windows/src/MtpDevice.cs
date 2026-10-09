// Raw PTP operations over the Windows in-box MTP/PTP driver (WUDFWpdMtp).
// The driver owns the USB session and transaction IDs; we send operation codes,
// parameters and data phases through the WPD MTP extension commands.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using DslrWebcamStudio.Wpd;

namespace DslrWebcamStudio
{
    public sealed class PtpResponse
    {
        public ushort Code;
        public uint[] Params;
        public byte[] Data;
        public bool Ok { get { return Code == Ptp.RC_OK; } }
    }

    public sealed class WpdDeviceInfo
    {
        public string Id, FriendlyName, Manufacturer, Description;
        public override string ToString() { return FriendlyName + " [" + Manufacturer + "] " + Id; }
    }

    public sealed class MtpDevice : IDisposable
    {
        // Largest data phase accepted from the camera; live-view frames are ~300 KB. Guards against
        // a faulty or malicious device making the app allocate huge buffers.
        const ulong MaxTransferBytes = 64UL * 1024 * 1024;
        const uint DefaultChunkBytes = 256 * 1024;

        IPortableDevice dev;
        public readonly WpdDeviceInfo Info;

        MtpDevice(IPortableDevice dev, WpdDeviceInfo info) { this.dev = dev; Info = info; }

        // ---- enumeration -------------------------------------------------

        public static List<WpdDeviceInfo> Enumerate()
        {
            var list = new List<WpdDeviceInfo>();
            var mgr = (IPortableDeviceManager)new PortableDeviceManagerClass();
            try
            {
                mgr.RefreshDeviceList();
                uint count = 0;
                mgr.GetDevices(IntPtr.Zero, ref count);
                if (count == 0) return list;
                IntPtr ids = Marshal.AllocCoTaskMem((int)count * IntPtr.Size);
                var idList = new List<string>();
                try
                {
                    mgr.GetDevices(ids, ref count);
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr p = Marshal.ReadIntPtr(ids, i * IntPtr.Size);
                        idList.Add(Marshal.PtrToStringUni(p));
                        Marshal.FreeCoTaskMem(p);
                    }
                }
                finally { Marshal.FreeCoTaskMem(ids); }
                foreach (string id in idList)
                {
                    list.Add(new WpdDeviceInfo
                    {
                        Id = id,
                        FriendlyName = GetString(mgr, id, 0),
                        Manufacturer = GetString(mgr, id, 1),
                        Description = GetString(mgr, id, 2),
                    });
                }
            }
            finally { Marshal.ReleaseComObject(mgr); }
            return list;
        }

        static string GetString(IPortableDeviceManager mgr, string id, int which)
        {
            try
            {
                uint cch = 0;
                Call(mgr, id, IntPtr.Zero, ref cch, which);
                if (cch == 0) return "";
                IntPtr buf = Marshal.AllocHGlobal((int)cch * 2);
                try { Call(mgr, id, buf, ref cch, which); return Marshal.PtrToStringUni(buf); }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch (COMException) { return ""; }
        }

        static void Call(IPortableDeviceManager mgr, string id, IntPtr buf, ref uint cch, int which)
        {
            if (which == 0) mgr.GetDeviceFriendlyName(id, buf, ref cch);
            else if (which == 1) mgr.GetDeviceManufacturer(id, buf, ref cch);
            else mgr.GetDeviceDescription(id, buf, ref cch);
        }

        public static MtpDevice Open(WpdDeviceInfo info)
        {
            var client = NewValues();
            var k = Keys.ClientName; client.SetStringValue(ref k, "DSLR Webcam Studio");
            k = Keys.ClientMajor; client.SetUnsignedIntegerValue(ref k, 1);
            k = Keys.ClientMinor; client.SetUnsignedIntegerValue(ref k, 0);
            k = Keys.ClientRevision; client.SetUnsignedIntegerValue(ref k, 0);
            k = Keys.ClientSecurityQos; client.SetUnsignedIntegerValue(ref k, 0x20000); // SECURITY_IMPERSONATION
            var dev = (IPortableDevice)new PortableDeviceClass();
            try { dev.Open(info.Id, client); }
            catch { Marshal.ReleaseComObject(dev); throw; }
            finally { Marshal.ReleaseComObject(client); }
            return new MtpDevice(dev, info);
        }

        public void Dispose()
        {
            if (dev == null) return;
            try { dev.Close(); } catch (COMException) { }
            Marshal.ReleaseComObject(dev);
            dev = null;
        }

        // ---- operations --------------------------------------------------

        public List<ushort> GetVendorOpcodes()
        {
            var res = Send(Command(Keys.CmdGetVendorOpcodes));
            var result = new List<ushort>();
            try
            {
                IPortableDevicePropVariantCollection coll;
                var k = Keys.VendorOpcodes;
                res.GetIPortableDevicePropVariantCollectionValue(ref k, out coll);
                uint n = 0;
                coll.GetCount(ref n);
                for (uint i = 0; i < n; i++)
                {
                    var pv = new PropVariant();
                    coll.GetAt(i, ref pv);
                    result.Add((ushort)pv.UInt32Value);
                }
                Marshal.ReleaseComObject(coll);
            }
            finally { Marshal.ReleaseComObject(res); }
            return result;
        }

        public PtpResponse Execute(ushort opcode, params uint[] args)
        {
            var p = Command(Keys.CmdExecNoData);
            SetOp(p, opcode, args);
            return ReadResponse(Send(p), null);
        }

        public PtpResponse ExecuteRead(ushort opcode, params uint[] args)
        {
            var p = Command(Keys.CmdExecDataToRead);
            SetOp(p, opcode, args);
            var res = Send(p);
            string ctx;
            ulong total;
            uint optimal = 0;
            try
            {
                ctx = GetString(res, Keys.TransferContext);
                var k = Keys.TransferTotalSize; res.GetUnsignedLargeIntegerValue(ref k, out total);
                k = Keys.OptimalBufferSize;
                try { res.GetUnsignedIntegerValue(ref k, out optimal); } catch (COMException) { }
            }
            finally { Marshal.ReleaseComObject(res); }

            if (optimal == 0) optimal = DefaultChunkBytes;
            if (total > MaxTransferBytes)
            {
                EndTransfer(ctx);
                throw new InvalidDataException(string.Format(Strings.TransferTooLarge, total));
            }
            PtpResponse resp = null;
            var ms = new MemoryStream((int)total);
            try
            {
                while ((ulong)ms.Length < total)
                {
                    uint chunk = (uint)Math.Min((ulong)optimal, total - (ulong)ms.Length);
                    var rp = Command(Keys.CmdReadData);
                    var k = Keys.TransferContext; rp.SetStringValue(ref k, ctx);
                    k = Keys.TransferBytesToRead; rp.SetUnsignedIntegerValue(ref k, chunk);
                    k = Keys.TransferData; rp.SetBufferValue(ref k, new byte[chunk], chunk);
                    var rr = Send(rp);
                    try
                    {
                        uint got;
                        k = Keys.TransferBytesRead; rr.GetUnsignedIntegerValue(ref k, out got);
                        if (got == 0) break;
                        IntPtr buf;
                        uint len;
                        k = Keys.TransferData; rr.GetBufferValue(ref k, out buf, out len);
                        try
                        {
                            var bytes = new byte[Math.Min(got, len)];
                            Marshal.Copy(buf, bytes, 0, bytes.Length);
                            ms.Write(bytes, 0, bytes.Length);
                        }
                        finally { Marshal.FreeCoTaskMem(buf); }
                    }
                    finally { Marshal.ReleaseComObject(rr); }
                }
            }
            finally
            {
                // Always end the transfer so the driver releases the transaction.
                var ep = Command(Keys.CmdEndDataTransfer);
                var k = Keys.TransferContext; ep.SetStringValue(ref k, ctx);
                resp = ReadResponse(Send(ep), ms.ToArray());
            }
            return resp;
        }

        public PtpResponse ExecuteWrite(ushort opcode, byte[] data, params uint[] args)
        {
            var p = Command(Keys.CmdExecDataToWrite);
            SetOp(p, opcode, args);
            var k = Keys.TransferTotalSize; p.SetUnsignedLargeIntegerValue(ref k, (ulong)data.Length);
            var res = Send(p);
            string ctx;
            PtpResponse resp = null;
            try { ctx = GetString(res, Keys.TransferContext); }
            finally { Marshal.ReleaseComObject(res); }
            try
            {
                var wp = Command(Keys.CmdWriteData);
                k = Keys.TransferContext; wp.SetStringValue(ref k, ctx);
                k = Keys.TransferBytesToWrite; wp.SetUnsignedIntegerValue(ref k, (uint)data.Length);
                k = Keys.TransferData; wp.SetBufferValue(ref k, data, (uint)data.Length);
                Marshal.ReleaseComObject(Send(wp));
            }
            finally
            {
                var ep = Command(Keys.CmdEndDataTransfer);
                k = Keys.TransferContext; ep.SetStringValue(ref k, ctx);
                resp = ReadResponse(Send(ep), null);
            }
            return resp;
        }

        // ---- helpers -----------------------------------------------------

        void EndTransfer(string ctx)
        {
            var ep = Command(Keys.CmdEndDataTransfer);
            var k = Keys.TransferContext; ep.SetStringValue(ref k, ctx);
            Marshal.ReleaseComObject(Send(ep));
        }

        static IPortableDeviceValues NewValues() { return (IPortableDeviceValues)new PortableDeviceValuesClass(); }

        static IPortableDeviceValues Command(PropertyKey cmd)
        {
            var v = NewValues();
            var g = cmd.fmtid;
            var k = Keys.CommandCategory; v.SetGuidValue(ref k, ref g);
            k = Keys.CommandId; v.SetUnsignedIntegerValue(ref k, cmd.pid);
            return v;
        }

        static void SetOp(IPortableDeviceValues v, ushort opcode, uint[] args)
        {
            var k = Keys.OpCode; v.SetUnsignedIntegerValue(ref k, opcode);
            var coll = (IPortableDevicePropVariantCollection)new PortableDevicePropVariantCollectionClass();
            foreach (uint a in args)
            {
                var pv = PropVariant.FromUInt32(a);
                coll.Add(ref pv);
            }
            k = Keys.OpParams; v.SetIPortableDevicePropVariantCollectionValue(ref k, coll);
            Marshal.ReleaseComObject(coll);
        }

        IPortableDeviceValues Send(IPortableDeviceValues p)
        {
            if (dev == null) throw new ObjectDisposedException("MtpDevice");
            IPortableDeviceValues res;
            try { dev.SendCommand(0, p, out res); }
            finally { Marshal.ReleaseComObject(p); }
            int hr;
            var k = Keys.HResult;
            res.GetErrorValue(ref k, out hr);
            if (hr < 0)
            {
                Marshal.ReleaseComObject(res);
                throw new COMException("WPD command failed: 0x" + hr.ToString("X8"), hr);
            }
            return res;
        }

        static PtpResponse ReadResponse(IPortableDeviceValues res, byte[] data)
        {
            try
            {
                uint code;
                var k = Keys.ResponseCode; res.GetUnsignedIntegerValue(ref k, out code);
                var ps = new List<uint>();
                try
                {
                    IPortableDevicePropVariantCollection coll;
                    k = Keys.ResponseParams; res.GetIPortableDevicePropVariantCollectionValue(ref k, out coll);
                    uint n = 0;
                    coll.GetCount(ref n);
                    for (uint i = 0; i < n; i++) { var pv = new PropVariant(); coll.GetAt(i, ref pv); ps.Add(pv.UInt32Value); }
                    Marshal.ReleaseComObject(coll);
                }
                catch (COMException) { }
                return new PtpResponse { Code = (ushort)code, Params = ps.ToArray(), Data = data };
            }
            finally { Marshal.ReleaseComObject(res); }
        }

        static string GetString(IPortableDeviceValues v, PropertyKey key)
        {
            IntPtr p;
            v.GetStringValue(ref key, out p);
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
    }
}
