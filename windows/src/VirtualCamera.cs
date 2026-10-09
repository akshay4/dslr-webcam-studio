// "DSLR Webcam Studio" virtual camera (Windows 11): lets OBS, Streamlabs, Zoom, Teams,
// browsers and the Camera app use the output as a webcam.
//
// Install (once, elevated): the media source DLL embedded in this exe is copied to
// %ProgramFiles%\DSLR Webcam Studio (writable by administrators only, because Windows' camera
// service loads it) and registered as a COM server. Each run: the camera is registered with
// MFCreateVirtualCamera, and every output frame is written into the shared section the media
// source reads (layout: windows/vcam/vcam_shared.h). Only vcam.ini, the preferred format, lives
// in the user-writable %ProgramData%\DSLR Webcam Studio.
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
        public const string InstallArg = "--install-vcam";
        public const string UninstallArg = "--uninstall-vcam";
        const string DllName = "DSLRWebcamStudioVCam.dll";
        const string FolderName = "DSLR Webcam Studio";
        const string PrefsFile = "vcam.ini";
        const string ClsidKey = @"Software\Classes\CLSID\" + ClsidString;
        const string InprocKey = ClsidKey + @"\InprocServer32";
        const string SensorGroupDll = "mfsensorgroup.dll";
        const string GlobalSection = "Global\\DSLRWebcamStudio_VirtualCamera_v1";
        const string LocalSection = "Local\\DSLRWebcamStudio_VirtualCamera_v1";
        const uint Magic = 0x4D414344, Version = 1;
        const int SessionLifetime = 0; // the camera exists while the app runs and disappears when it closes
        public const int MaxW = 1920, MaxH = 1080, HeaderSize = 64;
        const long SectionSize = HeaderSize + (long)MaxW * MaxH * 4;

        // Exit codes of the elevated install/uninstall run.
        public const int ExitOk = 0, ExitMissingResource = 2, ExitRegisterFailed = 3;

        // Admin-only folder for the DLL that Windows' camera service loads.
        static string ProgramDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), FolderName); }
        }

        // User-writable folder for the preferred-format file the media source reads.
        public static string ConfigDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), FolderName); }
        }

        static string DllPath { get { return Path.Combine(ProgramDir, DllName); } }

        // The Windows virtual camera API (MFCreateVirtualCamera) exists on Windows 11 and later.
        public static bool Supported
        {
            get
            {
                IntPtr lib = LoadSystemLibrary(SensorGroupDll);
                bool ok = lib != IntPtr.Zero && GetProcAddress(lib, "MFCreateVirtualCamera") != IntPtr.Zero;
                if (lib != IntPtr.Zero) FreeLibrary(lib);
                return ok;
            }
        }

        // Installed means registered to exactly our protected DLL path (an older or tampered
        // registration elsewhere counts as not installed, so the app offers to reinstall it).
        public static bool Installed
        {
            get
            {
                using (var k = Registry.LocalMachine.OpenSubKey(InprocKey))
                {
                    var path = k == null ? null : k.GetValue(null) as string;
                    return path != null && string.Equals(path, DllPath, StringComparison.OrdinalIgnoreCase) && File.Exists(DllPath);
                }
            }
        }

        // Re-runs this exe elevated with a fixed argument; returns true on success.
        public static bool RunElevated(string arg)
        {
            if (arg != InstallArg && arg != UninstallArg) return false;
            try
            {
                var p = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, arg)
                {
                    Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
                });
                p.WaitForExit();
                return p.ExitCode == ExitOk;
            }
            catch (System.ComponentModel.Win32Exception) { return false; } // UAC prompt declined
        }

        // Elevated: copy the media source into Program Files and register it for Frame Server.
        public static int Install()
        {
            RemoveLegacyCopy();
            Directory.CreateDirectory(ProgramDir); // inherits Program Files' admin-only write access
            using (var res = typeof(VirtualCamera).Assembly.GetManifestResourceStream(DllName))
            {
                if (res == null) return ExitMissingResource;
                string tmp = DllPath + ".new";
                using (var f = File.Create(tmp)) res.CopyTo(f);
                try { if (File.Exists(DllPath)) File.Delete(DllPath); }
                catch (IOException) { File.Move(DllPath, DllPath + ".old" + DateTime.Now.Ticks); } // in use by Frame Server
                catch (UnauthorizedAccessException) { File.Move(DllPath, DllPath + ".old" + DateTime.Now.Ticks); }
                File.Move(tmp, DllPath);
            }
            CreateConfigDir();
            return CallExport(DllPath, "DllRegisterServer") >= 0 ? ExitOk : ExitRegisterFailed;
        }

        // Elevated: unregister and delete the media source (current and legacy locations).
        public static int Uninstall()
        {
            if (File.Exists(DllPath)) CallExport(DllPath, "DllUnregisterServer");
            try { Registry.LocalMachine.DeleteSubKeyTree(ClsidKey, false); } catch (UnauthorizedAccessException) { }
            DeleteMatching(ProgramDir, DllName + "*");
            RemoveLegacyCopy();
            return ExitOk;
        }

        // v1.0 pre-releases put the DLL in ProgramData, which normal users can write to.
        static void RemoveLegacyCopy() { DeleteMatching(ConfigDir, DllName + "*"); }

        static void DeleteMatching(string dir, string pattern)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, pattern))
            {
                try { File.Delete(f); }
                catch (IOException) { } // still loaded by Frame Server; removed on next reboot/uninstall
                catch (UnauthorizedAccessException) { }
            }
        }

        // Users may update the preferred-format file; nothing executable lives here.
        static void CreateConfigDir()
        {
            Directory.CreateDirectory(ConfigDir);
            var sec = Directory.GetAccessControl(ConfigDir);
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(ConfigDir, sec);
        }

        static int CallExport(string dll, string name)
        {
            IntPtr lib = LoadLibrary(dll);
            if (lib == IntPtr.Zero) return -1;
            IntPtr fn = GetProcAddress(lib, name);
            if (fn == IntPtr.Zero) return -1;
            return ((HResultFn)Marshal.GetDelegateForFunctionPointer(fn, typeof(HResultFn)))();
        }

        // ---- registration with Windows for this run ----------------------------------------------

        static IntPtr camera; // IMFVirtualCamera*
        static IntPtr module;

        // Registers and starts the camera. Returns null on success or an error message.
        public static string Start()
        {
            if (camera != IntPtr.Zero) return null;
            if (!Installed) return Strings.VcamNotInstalled;
            module = LoadLibrary(DllPath);
            if (module == IntPtr.Zero) return Strings.VcamLoadFailed;
            IntPtr fn = GetProcAddress(module, "DwsVCamCreate");
            if (fn == IntPtr.Zero) return Strings.VcamLoadFailed;
            int hr = ((CreateFn)Marshal.GetDelegateForFunctionPointer(fn, typeof(CreateFn)))(SessionLifetime, out camera);
            return hr < 0 ? string.Format(Strings.VcamRefused, hr) : null;
        }

        public static void Stop()
        {
            if (camera == IntPtr.Zero) return;
            IntPtr fn = GetProcAddress(module, "DwsVCamRemove");
            if (fn != IntPtr.Zero) ((RemoveFn)Marshal.GetDelegateForFunctionPointer(fn, typeof(RemoveFn)))(camera);
            Marshal.Release(camera);
            camera = IntPtr.Zero;
        }

        public static bool Running { get { return camera != IntPtr.Zero; } }

        // Tells the media source which format to offer first (what the app outputs).
        public static void WritePreferredFormat(StreamSettings s)
        {
            try
            {
                if (!Directory.Exists(ConfigDir)) return; // created by the installer
                File.WriteAllText(Path.Combine(ConfigDir, PrefsFile),
                    "[VirtualCamera]\r\nWidth=" + s.Width + "\r\nHeight=" + s.Height + "\r\nFps=" + s.Fps + "\r\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
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
                nextOpen = DateTime.UtcNow.AddMilliseconds(Timing.VcamReconnectMs); // the section appears once an app opens the camera
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
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string name, IntPtr file, uint flags);
        const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x800;
        // Windows DLLs are loaded from System32 only, never from the app's (user-writable) folder.
        static IntPtr LoadSystemLibrary(string name) { return LoadLibraryEx(name, IntPtr.Zero, LOAD_LIBRARY_SEARCH_SYSTEM32); }
        [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr lib, string name);
        [DllImport("kernel32")] static extern bool FreeLibrary(IntPtr lib);
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);
        [DllImport("kernel32", SetLastError = true)] static extern IntPtr MapViewOfFile(IntPtr section, uint access, uint offHigh, uint offLow, UIntPtr bytes);
        [DllImport("kernel32")] static extern bool UnmapViewOfFile(IntPtr view);
        [DllImport("kernel32")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32")] static extern ulong GetTickCount64();
    }
}
