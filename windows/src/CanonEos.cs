// Canon EOS live view over PTP: our own implementation, no Canon DLLs.
// Uses the Canon EOS vendor operations (the same ones libgphoto2 and Canon's EDSDK use):
//   SetRemoteMode 0x9114, SetEventMode 0x9115, GetEvent 0x9116,
//   SetDevicePropValueEx 0x9110 (EVF output device 0xD1B0 = PC),
//   GetViewFinderData 0x9153 -> JPEG frame, KeepDeviceOn 0x911D.
// The session behaviour (busy retries, keep-alive, restarting live view the camera dropped)
// follows what Canon's EOS Webcam Utility does through EDSDK; the macOS app does the same.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace DslrWebcamStudio
{
    public static class Ptp
    {
        public const ushort OC_GetDeviceInfo = 0x1001;

        public const ushort OC_EOS_SetDevicePropValueEx = 0x9110;
        public const ushort OC_EOS_SetRemoteMode = 0x9114;
        public const ushort OC_EOS_SetEventMode = 0x9115;
        public const ushort OC_EOS_GetEvent = 0x9116;
        public const ushort OC_EOS_KeepDeviceOn = 0x911D;
        public const ushort OC_EOS_GetViewFinderData = 0x9153;

        public const uint DPC_EOS_Aperture = 0xD101;
        public const uint DPC_EOS_ShutterSpeed = 0xD102;
        public const uint DPC_EOS_ISOSpeed = 0xD103;
        public const uint DPC_EOS_ExpCompensation = 0xD104;
        public const uint DPC_EOS_AutoExposureMode = 0xD105;
        public const uint DPC_EOS_EVFOutputDevice = 0xD1B0;
        public const uint DPC_EOS_EVFMode = 0xD1B3;
        public const uint EVF_OUTPUT_OFF = 0, EVF_OUTPUT_TFT = 1, EVF_OUTPUT_PC = 2;
        // Output device bits that send live view to the computer (PC, and PC small on newer bodies).
        public const uint EVF_TO_PC_MASK = 0x0A;

        // GetEvent record types: a property changed, and the camera is about to auto power off.
        public const uint EC_EOS_PropValueChanged = 0xC189;
        public const uint EC_EOS_WillSoonShutdown = 0xC18D;

        public const ushort RC_OK = 0x2001;
        public const ushort RC_ParameterNotSupported = 0x2006;
        public const ushort RC_DeviceBusy = 0x2019;
        public const ushort RC_InvalidParameter = 0x201D;
        public const ushort RC_CANON_NotReady = 0xA102;

        // Canon's EDSDK retries remote/event mode while the camera answers DeviceBusy (10 tries).
        public const int BusyRetries = 10, BusyRetryMs = 100;
        public const int KeepAliveMs = 60000;

        // GetViewFinderData parameters: libgphoto2's value (tested on the 700D), then the one Canon's
        // EDSDK sends for PC output, used if a camera rejects the first.
        public static readonly uint[][] ViewFinderParams = { new uint[] { 0x00100000 }, new uint[] { 0, 0, 0 } };

        public static string RcName(ushort rc)
        {
            switch (rc)
            {
                case 0x2001: return "OK";
                case 0x2002: return "GeneralError";
                case 0x2003: return "SessionNotOpen";
                case 0x2005: return "OperationNotSupported";
                case 0x2006: return "ParameterNotSupported";
                case 0x2019: return "DeviceBusy";
                case 0x201D: return "InvalidParameter";
                case 0xA102: return "Canon NotReady";
                default: return "0x" + rc.ToString("X4");
            }
        }
    }

    public sealed class PtpDeviceInfo
    {
        public uint VendorExtensionId;
        public string VendorExtensionDesc, Manufacturer, Model, DeviceVersion, SerialNumber;
        public List<ushort> Operations = new List<ushort>();
        public List<ushort> Properties = new List<ushort>();

        public static PtpDeviceInfo Parse(byte[] d)
        {
            var r = new PtpReader(d);
            var info = new PtpDeviceInfo();
            r.U16();                                   // StandardVersion
            info.VendorExtensionId = r.U32();
            r.U16();                                   // VendorExtensionVersion
            info.VendorExtensionDesc = r.Str();
            r.U16();                                   // FunctionalMode
            info.Operations = r.U16Array();
            r.U16Array();                              // Events
            info.Properties = r.U16Array();
            r.U16Array(); r.U16Array();                // Capture / image formats
            info.Manufacturer = r.Str();
            info.Model = r.Str();
            info.DeviceVersion = r.Str();
            info.SerialNumber = r.Str();
            return info;
        }
    }

    // Reads PTP datasets from the camera. The data is untrusted, so every read is bounds-checked
    // and malformed input raises InvalidDataException instead of reading past the buffer.
    sealed class PtpReader
    {
        readonly byte[] d;
        int pos;
        public PtpReader(byte[] data) { d = data ?? new byte[0]; }

        void Need(long bytes)
        {
            if (bytes < 0 || pos + bytes > d.Length) throw new InvalidDataException(Strings.MalformedData);
        }

        public ushort U16() { Need(2); var v = BitConverter.ToUInt16(d, pos); pos += 2; return v; }
        public uint U32() { Need(4); var v = BitConverter.ToUInt32(d, pos); pos += 4; return v; }

        public List<ushort> U16Array()
        {
            uint n = U32();
            Need((long)n * 2);
            var l = new List<ushort>((int)n);
            for (uint i = 0; i < n; i++) l.Add(U16());
            return l;
        }

        public string Str()
        {
            Need(1);
            int n = d[pos++];
            if (n == 0) return "";
            Need(n * 2);
            var s = Encoding.Unicode.GetString(d, pos, n * 2).TrimEnd('\0');
            pos += n * 2;
            return s;
        }
    }

    // Keeps live view running the way Canon's own webcam service does: when the camera switches its
    // live view away from the computer (Live View button, mode dial, menu), turn it back on; when
    // frames stop arriving, re-assert the output device, and reconnect if that doesn't help.
    public sealed class LiveViewWatchdog
    {
        public enum Action { None, RestartEvf, Reconnect }
        public const double RestartAfter = 3.0, ReconnectAfter = 12.0, MinGap = 1.0;
        double lastFrame, lastRestart = double.NegativeInfinity;
        public int Restarts;

        public LiveViewWatchdog(double now) { lastFrame = now; }

        public void Frame(double now) { lastFrame = now; }

        public Action Check(double now, uint? evfOutput)
        {
            if (now - lastFrame >= ReconnectAfter) return Action.Reconnect;
            bool lostPC = evfOutput.HasValue && (evfOutput.Value & Ptp.EVF_TO_PC_MASK) == 0;
            bool stalled = now - lastFrame >= RestartAfter;
            if (!lostPC && !stalled) return Action.None;
            if (now - lastRestart < (lostPC ? MinGap : RestartAfter)) return Action.None;
            lastRestart = now;
            Restarts++;
            return Action.RestartEvf;
        }
    }

    public sealed class CanonLiveView : IDisposable
    {
        readonly MtpDevice dev;
        readonly Action<string> log;
        bool liveViewOn;
        // Latest camera property values, filled from GetEvent "property changed" records.
        public readonly Dictionary<uint, uint> Props = new Dictionary<uint, uint>();
        DateTime lastEventPoll = DateTime.MinValue;
        DateTime lastKeepAlive = DateTime.UtcNow;
        int viewFinderParams; // index into Ptp.ViewFinderParams
        public PtpDeviceInfo DeviceInfo;

        CanonLiveView(MtpDevice dev, Action<string> log) { this.dev = dev; this.log = log ?? (s => { }); }

        public string Model { get { return DeviceInfo != null && DeviceInfo.Model != "" ? DeviceInfo.Model : dev.Info.FriendlyName; } }

        // The camera's live view output device, as last reported (or as we last set it).
        public uint? EvfOutput { get { return Prop(Ptp.DPC_EOS_EVFOutputDevice); } }

        // Without a device info dataset assume the camera has the op and let it answer.
        public bool Supports(ushort op) { return DeviceInfo == null || DeviceInfo.Operations.Contains(op); }

        // First WPD device made by Canon (USB VID 04A9).
        public static WpdDeviceInfo FindCamera()
        {
            foreach (var d in MtpDevice.Enumerate())
            {
                var id = (d.Id ?? "").ToLowerInvariant();
                if (id.Contains("vid_04a9") || (d.Manufacturer ?? "").IndexOf("Canon", StringComparison.OrdinalIgnoreCase) >= 0)
                    return d;
            }
            return null;
        }

        public static CanonLiveView Open(Action<string> log)
        {
            var info = FindCamera();
            if (info == null)
                throw new InvalidOperationException(Strings.NoCanonCamera);
            var dev = MtpDevice.Open(info);
            var lv = new CanonLiveView(dev, log);
            try { lv.Start(); }
            catch { lv.Dispose(); throw; }
            return lv;
        }

        void Start()
        {
            log("Device: " + dev.Info);
            var di = dev.ExecuteRead(Ptp.OC_GetDeviceInfo);
            if (di.Ok && di.Data != null && di.Data.Length > 0)
            {
                DeviceInfo = PtpDeviceInfo.Parse(di.Data);
                log("Model: " + DeviceInfo.Model + "  firmware " + DeviceInfo.DeviceVersion +
                    "  vendor ext 0x" + DeviceInfo.VendorExtensionId.ToString("X"));
                foreach (var op in new[] { Ptp.OC_EOS_SetRemoteMode, Ptp.OC_EOS_SetDevicePropValueEx, Ptp.OC_EOS_GetViewFinderData })
                    if (!DeviceInfo.Operations.Contains(op))
                        throw new NotSupportedException(string.Format(Strings.NoLiveViewOp, op));
            }
            else log("GetDeviceInfo: " + Ptp.RcName(di.Code));

            Check("SetRemoteMode", RetryBusy(() => dev.Execute(Ptp.OC_EOS_SetRemoteMode, 1)));
            Check("SetEventMode", RetryBusy(() => dev.Execute(Ptp.OC_EOS_SetEventMode, 1)));
            DrainEvents();

            // EVFMode is read-only on some bodies, so a failure here is only logged.
            var m = SetProp(Ptp.DPC_EOS_EVFMode, 1);
            log("EVFMode=1: " + Ptp.RcName(m.Code));
            Check("Set EVF output to PC", SetEvfToPC());
            liveViewOn = true;
            DrainEvents();
        }

        // Sends live view to the computer. Remembers the value so a stale event from before the
        // change doesn't look like the camera switching it off again.
        PtpResponse SetEvfToPC()
        {
            var r = RetryBusy(() => SetProp(Ptp.DPC_EOS_EVFOutputDevice, Ptp.EVF_OUTPUT_PC));
            if (r.Ok) Props[Ptp.DPC_EOS_EVFOutputDevice] = Ptp.EVF_OUTPUT_PC;
            return r;
        }

        // Turns live view back on after the camera dropped it (Canon's service does the same).
        public void RestartLiveView()
        {
            SetProp(Ptp.DPC_EOS_EVFMode, 1);
            Check("Restart live view", SetEvfToPC());
            DrainEvents();
        }

        // Retries while the camera answers DeviceBusy, as Canon's EDSDK does for remote/event mode.
        static PtpResponse RetryBusy(Func<PtpResponse> op)
        {
            var r = op();
            for (int i = 1; r.Code == Ptp.RC_DeviceBusy && i < Ptp.BusyRetries; i++)
            {
                Thread.Sleep(Ptp.BusyRetryMs);
                r = op();
            }
            return r;
        }

        // Sets a camera property (ISO, shutter, ...) and refreshes Props from the camera's events.
        public PtpResponse SetProperty(uint prop, uint value)
        {
            var r = SetProp(prop, value);
            DrainEvents();
            return r;
        }

        PtpResponse SetProp(uint prop, uint value)
        {
            var data = new byte[12];
            BitConverter.GetBytes(12u).CopyTo(data, 0);
            BitConverter.GetBytes(prop).CopyTo(data, 4);
            BitConverter.GetBytes(value).CopyTo(data, 8);
            return dev.ExecuteWrite(Ptp.OC_EOS_SetDevicePropValueEx, data);
        }

        // Canon queues property/state events after remote mode; reading them keeps the camera responsive.
        void DrainEvents()
        {
            var ev = dev.ExecuteRead(Ptp.OC_EOS_GetEvent);
            lastEventPoll = DateTime.UtcNow;
            if (!ev.Ok) { log("GetEvent: " + Ptp.RcName(ev.Code)); return; }
            if (ParseEvents(ev.Data, Props)) KeepAwake();
        }

        // Resets the camera's auto power off timer (Canon's service answers the camera's
        // "will soon shut down" event this way; we also send it every minute).
        void KeepAwake()
        {
            lastKeepAlive = DateTime.UtcNow;
            if (Supports(Ptp.OC_EOS_KeepDeviceOn)) dev.Execute(Ptp.OC_EOS_KeepDeviceOn);
        }

        // Event data is a list of [u32 size][u32 type][payload] records ending with a size-8 terminator.
        // Type 0xC189 (property value changed) carries [u32 prop][u32 value]. Returns true if the
        // camera warned it will soon power off (0xC18D).
        public static bool ParseEvents(byte[] d, Dictionary<uint, uint> props)
        {
            if (d == null) return false;
            int pos = 0;
            bool shutdownSoon = false;
            while (pos + 8 <= d.Length)
            {
                int size = BitConverter.ToInt32(d, pos);
                uint type = BitConverter.ToUInt32(d, pos + 4);
                if (size < 8 || pos + size > d.Length || type == 0) break;
                if (type == Ptp.EC_EOS_PropValueChanged && size >= 16)
                    props[BitConverter.ToUInt32(d, pos + 8)] = BitConverter.ToUInt32(d, pos + 12);
                if (type == Ptp.EC_EOS_WillSoonShutdown) shutdownSoon = true;
                pos += size;
            }
            return shutdownSoon;
        }

        public uint? Prop(uint code)
        {
            uint v;
            return Props.TryGetValue(code, out v) ? v : (uint?)null;
        }

        // Returns one live-view JPEG, or null if the camera has no new frame yet.
        public byte[] ReadJpeg()
        {
            if ((DateTime.UtcNow - lastEventPoll).TotalMilliseconds > Timing.EventPollMs) DrainEvents();
            if ((DateTime.UtcNow - lastKeepAlive).TotalMilliseconds > Ptp.KeepAliveMs) KeepAwake();
            var r = dev.ExecuteRead(Ptp.OC_EOS_GetViewFinderData, Ptp.ViewFinderParams[viewFinderParams]);
            if (r.Code == Ptp.RC_CANON_NotReady || r.Code == Ptp.RC_DeviceBusy) return null;
            if ((r.Code == Ptp.RC_InvalidParameter || r.Code == Ptp.RC_ParameterNotSupported)
                && viewFinderParams + 1 < Ptp.ViewFinderParams.Length)
            {
                viewFinderParams++;
                log("GetViewFinderData: " + Ptp.RcName(r.Code) + ", switching to Canon SDK parameters");
                return null;
            }
            Check("GetViewFinderData", r);
            return ExtractJpeg(r.Data);
        }

        // Viewfinder data is a series of [u32 length][u32 type][payload] blocks; type 1 is the JPEG.
        // Some bodies return a bare JPEG, so fall back to scanning for SOI/EOI markers.
        public static byte[] ExtractJpeg(byte[] d)
        {
            if (d == null || d.Length < 4) return null;
            int pos = 0;
            while (pos + 8 <= d.Length)
            {
                int len = BitConverter.ToInt32(d, pos);
                int type = BitConverter.ToInt32(d, pos + 4);
                if (len < 8 || pos + len > d.Length) break;
                if (type == 1 && len > 10 && d[pos + 8] == 0xFF && d[pos + 9] == 0xD8)
                {
                    var j = new byte[len - 8];
                    Buffer.BlockCopy(d, pos + 8, j, 0, j.Length);
                    return j;
                }
                pos += len;
            }
            for (int i = 0; i + 1 < d.Length; i++)
            {
                if (d[i] != 0xFF || d[i + 1] != 0xD8) continue;
                for (int e = d.Length - 2; e > i; e--)
                {
                    if (d[e] == 0xFF && d[e + 1] == 0xD9)
                    {
                        var j = new byte[e + 2 - i];
                        Buffer.BlockCopy(d, i, j, 0, j.Length);
                        return j;
                    }
                }
                break;
            }
            return null;
        }

        static void Check(string what, PtpResponse r)
        {
            if (!r.Ok) throw new InvalidOperationException(string.Format(Strings.PtpFailed, what, Ptp.RcName(r.Code)));
        }

        public void Dispose()
        {
            try
            {
                if (liveViewOn)
                {
                    RetryBusy(() => SetProp(Ptp.DPC_EOS_EVFOutputDevice, Ptp.EVF_OUTPUT_OFF));
                    liveViewOn = false;
                }
                RetryBusy(() => dev.Execute(Ptp.OC_EOS_SetEventMode, 0));
                RetryBusy(() => dev.Execute(Ptp.OC_EOS_SetRemoteMode, 0));
            }
            catch (Exception e) { log("Shutdown: " + e.Message); }
            dev.Dispose();
        }
    }
}
