// "DSLR Webcam Studio" virtual camera (Windows 11): lets OBS, Streamlabs, Zoom, Teams,
// browsers and the Camera app use the output as a webcam.
//
// Install (once, elevated): the media source DLL embedded in this exe is copied to
// %ProgramData%\DSLR Webcam Studio and registered as a COM server, so Windows' Camera
// Frame Server can load it. Each run: the camera is registered with MFCreateVirtualCamera,
// and every output frame is written into the shared section the media source reads
// (layout: windows/vcam/vcam_shared.h).
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

namespace DslrWebcamStudio
{
    public static class VirtualCamera
    {
        public const string ClsidString = "{8B5C6FE4-7003-435D-B12F-2E76435ADEBA}";
        const string DllName = "DSLRWebcamStudioVCam.dll";
        const string GlobalSection = "Global\\DSLRWebcamStudio_VirtualCamera_v1";
        const string LocalSection = "Local\\DSLRWebcamStudio_VirtualCamera_v1";
        const uint Magic = 0x4D414344, Version = 1;
        public const int MaxW = 1920, MaxH = 1080, HeaderSize = 64;
        const long SectionSize = HeaderSize + (long)MaxW * MaxH * 4;

        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DSLR Webcam Studio"); }
        }

        static string DllPath { get { return Path.Combine(InstallDir, DllName); } }

        // The Windows virtual camera API (MFCreateVirtualCamera) exists on Windows 11 and later.
        public static bool Supported
        {
            get
            {
                IntPtr lib = LoadLibrary("mfsensorgroup.dll");
                bool ok = lib != IntPtr.Zero && GetProcAddress(lib, "MFCreateVirtualCamera") != IntPtr.Zero;
                if (lib != IntPtr.Zero) FreeLibrary(lib);
                return ok;
            }
        }

        public static bool Installed
        {
            get
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"Software\Classes\CLSID\" + ClsidString + @"\InprocServer32"))
                    return k != null && File.Exists(DllPath);
            }
        }

        public static bool IsElevated
        {
            get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        }

        // Re-runs this exe elevated to install or remove; returns true on success.
        public static bool RunElevated(string arg)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, arg)
                {
                    Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
                });
                p.WaitForExit();
                return p.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception) { return false; } // UAC prompt declined
        }

        // Elevated: copy the media source to ProgramData and register it for Frame Server.
        public static int Install()
        {
            Directory.CreateDirectory(InstallDir);
            // Let normal users update vcam.ini (preferred format) in this folder.
            var sec = Directory.GetAccessControl(InstallDir);
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(InstallDir, sec);

            using (var res = typeof(VirtualCamera).Assembly.GetManifestResourceStream(DllName))
            {
                if (res == null) return 2;
                string tmp = DllPath + ".new";
                using (var f = File.Create(tmp)) res.CopyTo(f);
                try { if (File.Exists(DllPath)) File.Delete(DllPath); }
                catch (IOException) { File.Move(DllPath, DllPath + ".old" + DateTime.Now.Ticks); } // in use by Frame Server
                File.Move(tmp, DllPath);
            }
            return CallExport("DllRegisterServer") >= 0 ? 0 : 3;
        }

        // Elevated: unregister and delete the media source.
        public static int Uninstall()
        {
            if (File.Exists(DllPath)) CallExport("DllUnregisterServer");
            try { Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\CLSID\" + ClsidString, false); } catch (Exception) { }
            try { foreach (var f in Directory.GetFiles(InstallDir, DllName + "*")) File.Delete(f); } catch (Exception) { }
            return 0;
        }

        static int CallExport(string name)
        {
            IntPtr lib = LoadLibrary(DllPath);
            if (lib == IntPtr.Zero) return -1;
            var fn = (HResultFn)Marshal.GetDelegateForFunctionPointer(GetProcAddress(lib, name), typeof(HResultFn));
            return fn();
        }

        // ---- registration with Windows for this run ----------------------------------------------

        static IntPtr camera; // IMFVirtualCamera*
        static IntPtr module;

        // Registers and starts the camera. Returns null on success or an error message.
        public static string Start()
        {
            if (camera != IntPtr.Zero) return null;
            if (!Installed) return "not installed";
            module = LoadLibrary(DllPath);
            if (module == IntPtr.Zero) return "could not load " + DllPath;
            var create = (CreateFn)Marshal.GetDelegateForFunctionPointer(GetProcAddress(module, "DwsVCamCreate"), typeof(CreateFn));
            // Session lifetime: the camera exists while the app runs and disappears cleanly when it closes.
            int hr = create(0, out camera);
            return hr < 0 ? "Windows refused the virtual camera (0x" + hr.ToString("X8") + ")" : null;
        }

        public static void Stop()
        {
            if (camera == IntPtr.Zero) return;
            var remove = (RemoveFn)Marshal.GetDelegateForFunctionPointer(GetProcAddress(module, "DwsVCamRemove"), typeof(RemoveFn));
            remove(camera);
            Marshal.Release(camera);
            camera = IntPtr.Zero;
        }

        public static bool Running { get { return camera != IntPtr.Zero; } }

        // Tells the media source which format to offer first (what the app outputs).
        public static void WritePreferredFormat(StreamSettings s)
        {
            try
            {
                Directory.CreateDirectory(InstallDir);
                File.WriteAllText(Path.Combine(InstallDir, "vcam.ini"),
                    "[VirtualCamera]\r\nWidth=" + s.Width + "\r\nHeight=" + s.Height + "\r\nFps=" + s.Fps + "\r\n");
            }
            catch (Exception) { } // folder not writable until installed
        }

        // ---- frame writer -------------------------------------------------------------------------

        // Writes output frames into the section created by the media source while an app uses the camera.
        public sealed class Writer : IDisposable
        {
            IntPtr section, view;
            DateTime nextOpen = DateTime.MinValue;

            // True while some app (OBS, Zoom, ...) has the camera open.
            public bool Connected { get { return view != IntPtr.Zero; } }

            public void Write(Bitmap frame, int fps)
            {
                if (view == IntPtr.Zero && !TryOpen()) return;
                int w = Math.Min(frame.Width, MaxW), h = Math.Min(frame.Height, MaxH);
                var data = frame.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    unsafe
                    {
                        var hd = (uint*)view;
                        Interlocked.Increment(ref *(int*)(hd + 2)); // seq odd: writing
                        hd[0] = Magic; hd[1] = Version; hd[3] = (uint)w; hd[4] = (uint)h; hd[5] = (uint)fps;
                        byte* dst = (byte*)view + HeaderSize;
                        byte* src = (byte*)data.Scan0;
                        for (int y = 0; y < h; y++)
                            Buffer.MemoryCopy(src + (long)y * data.Stride, dst + (long)y * w * 4, w * 4, w * 4);
                        *(long*)(hd + 6) = (long)GetTickCount64();
                        Interlocked.Increment(ref *(int*)(hd + 2)); // seq even: complete
                    }
                }
                finally { frame.UnlockBits(data); }
            }

            bool TryOpen()
            {
                if (DateTime.UtcNow < nextOpen) return false;
                nextOpen = DateTime.UtcNow.AddSeconds(1); // the section appears once an app opens the camera
                section = OpenFileMapping(FILE_MAP_WRITE | FILE_MAP_READ, false, GlobalSection);
                if (section == IntPtr.Zero) section = OpenFileMapping(FILE_MAP_WRITE | FILE_MAP_READ, false, LocalSection);
                if (section == IntPtr.Zero) return false;
                view = MapViewOfFile(section, FILE_MAP_WRITE | FILE_MAP_READ, 0, 0, (UIntPtr)SectionSize);
                if (view == IntPtr.Zero) { CloseHandle(section); section = IntPtr.Zero; return false; }
                return true;
            }

            public void Dispose()
            {
                if (view != IntPtr.Zero) { UnmapViewOfFile(view); view = IntPtr.Zero; }
                if (section != IntPtr.Zero) { CloseHandle(section); section = IntPtr.Zero; }
            }
        }

        // ---- interop ------------------------------------------------------------------------------

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int HResultFn();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateFn(int lifetime, out IntPtr camera);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int RemoveFn(IntPtr camera);

        const uint FILE_MAP_WRITE = 2, FILE_MAP_READ = 4;
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr lib, string name);
        [DllImport("kernel32")] static extern bool FreeLibrary(IntPtr lib);
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);
        [DllImport("kernel32", SetLastError = true)] static extern IntPtr MapViewOfFile(IntPtr section, uint access, uint offHigh, uint offLow, UIntPtr bytes);
        [DllImport("kernel32")] static extern bool UnmapViewOfFile(IntPtr view);
        [DllImport("kernel32")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32")] static extern ulong GetTickCount64();
    }
}
