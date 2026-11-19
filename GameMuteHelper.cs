using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("GameMuteHelper")]
[assembly: System.Reflection.AssemblyDescription("后台失焦自动静音/降音量与 RTSS / CPU 配额限帧助手")]
[assembly: System.Reflection.AssemblyProduct("GameMuteHelper")]
[assembly: System.Reflection.AssemblyVersion("3.4.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("3.4.1.0")]

namespace GameMuteHelper
{
    // =========================================================================
    // 1. Entry Point, UAC Elevation & Single-Instance Guard
    // =========================================================================
    internal static class Program
    {
        private const string MutexName = @"Local\GameMuteHelper_CSharp_v3_Mutex";
        private const string ShowEventName = @"Local\GameMuteHelper_CSharp_v3_Show";

        [STAThread]
        private static void Main(string[] args)
        {
            bool background = false;
            bool noElevate = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i] ?? "";
                if (a.Equals("-Background", StringComparison.OrdinalIgnoreCase) || a.Equals("--background", StringComparison.OrdinalIgnoreCase))
                    background = true;
                if (a.Equals("-NoElevate", StringComparison.OrdinalIgnoreCase) || a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase))
                    noElevate = true;
            }

            if (!IsAdministrator() && !noElevate)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = Application.ExecutablePath;
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                    if (background) psi.Arguments = "-Background";
                    Process.Start(psi);
                    return;
                }
                catch { }
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            using (EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
            {
                if (!createdNew)
                {
                    try { showEvent.Set(); } catch { }
                    return;
                }

                NativeUI.EnableDpiAwareness();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // 兜底：任何未处理异常都先恢复被挂起/限流的进程，避免目标永久挂起
                AppDomain.CurrentDomain.UnhandledException += delegate
                {
                    try { SuspendCore.StopAll(); } catch { }
                    try { CpuQuotaCore.RestoreAll(); } catch { }
                };
                Application.ThreadException += delegate
                {
                    try { SuspendCore.StopAll(); } catch { }
                };

                AppLogger.Init();
                AppLogger.Log(string.Format("Starting GameMuteHelper v3.4.1 (Admin={0}, Background={1}, 64Bit={2})",
                    IsAdministrator(), background, Environment.Is64BitProcess));

                MainForm form = new MainForm(showEvent, background, IsAdministrator());
                if (background)
                {
                    Application.Run();
                }
                else
                {
                    Application.Run(form);
                }
            }
        }

        public static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(id);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }
    }

    // =========================================================================
    // 2. Rolling Logger & Configuration Store
    // =========================================================================
    internal static class AppLogger
    {
        public static string DataDir { get; private set; }
        public static string ConfigPath { get; private set; }
        public static string LogPath { get; private set; }

        public static void Init()
        {
            DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameMuteHelper");
            if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);
            ConfigPath = Path.Combine(DataDir, "config.json");
            LogPath = Path.Combine(DataDir, "app.log");
        }

        public static void Log(string msg, string level)
        {
            try
            {
                if (string.IsNullOrEmpty(LogPath)) Init();
                if (File.Exists(LogPath))
                {
                    FileInfo fi = new FileInfo(LogPath);
                    if (fi.Length > 524288)
                    {
                        string bak = LogPath + ".old";
                        if (File.Exists(bak)) File.Delete(bak);
                        File.Move(LogPath, bak);
                    }
                }
                string line = string.Format("[{0}] [{1}] {2}{3}",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), level, msg, Environment.NewLine);
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
            catch { }
        }

        public static void Log(string msg) { Log(msg, "INFO"); }
    }

    internal static class LimitMethods
    {
        public const string Rtss = "rtss";
        public const string Cpu = "cpu";
        public const string Suspend = "suspend";

        public static string Normalize(string m)
        {
            if (string.Equals(m, Cpu, StringComparison.OrdinalIgnoreCase)) return Cpu;
            if (string.Equals(m, Suspend, StringComparison.OrdinalIgnoreCase)) return Suspend;
            return Rtss;
        }

        public static int Clamp(string m, int v)
        {
            int max = Normalize(m) == Cpu ? 100 : 240;
            if (v < 1) v = 1;
            if (v > max) v = max;
            return v;
        }

        public static string ShortLabel(string m)
        {
            string n = Normalize(m);
            if (n == Cpu) return "CPU";
            if (n == Suspend) return "挂起";
            return "RTSS";
        }

        public static string DisplayValue(string m, int v)
        {
            return Normalize(m) == Cpu ? v + "%" : v.ToString();
        }
    }

    internal static class AudioModes
    {
        public const string Mute = "mute";
        public const string Attenuate = "attenuate";

        public const int MinVolumePercent = 5;
        public const int MaxVolumePercent = 90;

        public static string Normalize(string m)
        {
            return string.Equals(m, Attenuate, StringComparison.OrdinalIgnoreCase) ? Attenuate : Mute;
        }

        public static bool IsAttenuate(string m)
        {
            return Normalize(m) == Attenuate;
        }

        public static int ClampVolume(int v)
        {
            if (v < MinVolumePercent) v = MinVolumePercent;
            if (v > MaxVolumePercent) v = MaxVolumePercent;
            return v;
        }

        public static string ShortLabel(string m)
        {
            return IsAttenuate(m) ? "降低音量" : "完全静音";
        }

        public static int DefaultVolume { get { return 30; } }
    }

    [DataContract]
    internal class AppRule
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public string Method { get; set; }
        [DataMember] public bool Mute { get; set; }
        [DataMember] public bool LimitFps { get; set; }
        [DataMember] public int Fps { get; set; }
        [DataMember(EmitDefaultValue = false)] public string AudioMode { get; set; }
        [DataMember(EmitDefaultValue = false)] public int? AudioVolume { get; set; }

        public AppRule()
        {
            Name = "";
            Method = LimitMethods.Rtss;
            Mute = true;
            LimitFps = false;
            Fps = 5;
        }
    }

    [DataContract]
    internal class ConfigData
    {
        [DataMember] public string Version { get; set; }
        [DataMember] public int DebounceMs { get; set; }
        [DataMember] public string LimitMethod { get; set; }
        [DataMember] public int CpuQuotaPercent { get; set; }
        [DataMember] public List<AppRule> Apps { get; set; }
        [DataMember] public Dictionary<string, uint> OriginalLimits { get; set; }

        [DataMember(EmitDefaultValue = false)] public List<string> Watch { get; set; }
        [DataMember(EmitDefaultValue = false)] public int? TargetFps { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool? LimitFps { get; set; }

        public ConfigData()
        {
            Version = "3.3";
            DebounceMs = 500;
            LimitMethod = "rtss";
            CpuQuotaPercent = 5;
            Apps = new List<AppRule>();
            OriginalLimits = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static class ConfigStore
    {
        public static ConfigData Load()
        {
            ConfigData data = new ConfigData();
            try
            {
                if (!File.Exists(AppLogger.ConfigPath)) return data;
                byte[] bytes = File.ReadAllBytes(AppLogger.ConfigPath);
                if (bytes.Length == 0) return data;

                int offset = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
                DataContractJsonSerializerSettings settings = new DataContractJsonSerializerSettings();
                settings.UseSimpleDictionaryFormat = true;
                DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(ConfigData), settings);
                using (MemoryStream ms = new MemoryStream(bytes, offset, bytes.Length - offset))
                {
                    ConfigData loaded = ser.ReadObject(ms) as ConfigData;
                    if (loaded != null) data = loaded;
                }

                if (data.Apps == null) data.Apps = new List<AppRule>();
                if (data.OriginalLimits == null)
                    data.OriginalLimits = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                else
                    data.OriginalLimits = new Dictionary<string, uint>(data.OriginalLimits, StringComparer.OrdinalIgnoreCase);

                if (data.DebounceMs < 0) data.DebounceMs = 0;
                if (data.DebounceMs > 5000) data.DebounceMs = 5000;

                if (string.IsNullOrEmpty(data.LimitMethod)) data.LimitMethod = "rtss";
                if (!data.LimitMethod.Equals("cpu", StringComparison.OrdinalIgnoreCase)) data.LimitMethod = "rtss";
                if (data.CpuQuotaPercent < 1 || data.CpuQuotaPercent > 100) data.CpuQuotaPercent = 5;

                if (data.Apps.Count == 0 && data.Watch != null && data.Watch.Count > 0)
                {
                    int legacyFps = (data.TargetFps.HasValue && data.TargetFps.Value >= 1 && data.TargetFps.Value <= 240)
                        ? data.TargetFps.Value : 5;
                    bool legacyLimit = data.LimitFps.HasValue && data.LimitFps.Value;
                    for (int i = 0; i < data.Watch.Count; i++)
                    {
                        string w = data.Watch[i];
                        if (string.IsNullOrEmpty(w)) continue;
                        data.Apps.Add(new AppRule { Name = w, Mute = true, LimitFps = legacyLimit, Fps = legacyFps });
                    }
                    data.Watch = null;
                    data.TargetFps = null;
                    data.LimitFps = null;
                }

                for (int i = 0; i < data.Apps.Count; i++)
                {
                    AppRule r = data.Apps[i];
                    if (string.IsNullOrEmpty(r.Method))
                    {
                        // 3.2 及更早版本是全局方式，迁移到每个应用自己的 Method
                        if (string.Equals(data.LimitMethod, "cpu", StringComparison.OrdinalIgnoreCase) && r.LimitFps)
                        {
                            r.Method = LimitMethods.Cpu;
                            r.Fps = data.CpuQuotaPercent;
                        }
                        else
                        {
                            r.Method = LimitMethods.Rtss;
                        }
                    }
                    r.Method = LimitMethods.Normalize(r.Method);
                    r.Fps = LimitMethods.Clamp(r.Method, r.Fps);

                    // 3.4：每应用音频模式（完全静音 / 降低音量），旧配置默认静音
                    r.AudioMode = AudioModes.Normalize(r.AudioMode);
                    r.AudioVolume = AudioModes.ClampVolume(r.AudioVolume.HasValue ? r.AudioVolume.Value : AudioModes.DefaultVolume);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log("Load config failed: " + ex.Message, "ERROR");
            }
            return data;
        }

        public static void Save(ConfigData data)
        {
            try
            {
                data.Version = "3.3";
                data.Watch = null;
                data.TargetFps = null;
                data.LimitFps = null;

                DataContractJsonSerializerSettings settings = new DataContractJsonSerializerSettings();
                settings.UseSimpleDictionaryFormat = true;
                DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(ConfigData), settings);
                using (MemoryStream ms = new MemoryStream())
                {
                    ser.WriteObject(ms, data);
                    File.WriteAllBytes(AppLogger.ConfigPath, ms.ToArray());
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log("Save config failed: " + ex.Message, "ERROR");
            }
        }
    }

    // =========================================================================
    // 3. Native Windows Interop: DPI, DWM Light Titlebar, Core Audio, Dynamic RTSS
    // =========================================================================
    internal static class NativeUI
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        private static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static void EnableDpiAwareness()
        {
            try
            {
                IntPtr ctx = new IntPtr(-4); // PerMonitorV2
                if (SetProcessDpiAwarenessContext(ctx) == IntPtr.Zero) SetProcessDPIAware();
            }
            catch
            {
                try { SetProcessDPIAware(); } catch { }
            }
        }

        public static void ApplyWindowLightTheme(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            int dark = 0;
            try
            {
                if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
            }
            catch { }

            int corner = 2; // DWMWCP_ROUND
            try { DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int)); } catch { }
        }
    }

    internal class AudioSessionInfo
    {
        public uint Pid;
        public string InstanceId;
        public bool Muted;
        public float Volume;
    }

    internal static class MuteCore
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IntPtr props);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
        private interface IAudioSessionControl
        {
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
            [PreserveSig] int GetGroupingParam(out Guid grouping);
            [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid eventContext);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
        private interface IAudioSessionControl2
        {
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
            [PreserveSig] int GetGroupingParam(out Guid grouping);
            [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid eventContext);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out uint pid);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference(bool optOut);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
        private interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(ref Guid audioSessionGuid, int streamFlags, out IAudioSessionControl sessionControl);
            [PreserveSig] int GetSimpleAudioVolume(ref Guid audioSessionGuid, int streamFlags, out ISimpleAudioVolume audioVolume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
            [PreserveSig] int RegisterSessionNotification(IntPtr client);
            [PreserveSig] int UnregisterSessionNotification(IntPtr client);
            [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr client);
            [PreserveSig] int UnregisterDuckNotification(IntPtr client);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
        private interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute(bool mute, ref Guid eventContext);
            [PreserveSig] int GetMute(out bool mute);
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        public static int GetForegroundPid()
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return 0;
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            return (int)pid;
        }

        public static string GetTitle(IntPtr h)
        {
            if (h == IntPtr.Zero) return "";
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(h, sb, 512);
            return sb.ToString();
        }

        private static void SafeRelease(object comObj)
        {
            if (comObj != null && Marshal.IsComObject(comObj))
            {
                try { Marshal.ReleaseComObject(comObj); } catch { }
            }
        }

        public static List<AudioSessionInfo> GetSessions()
        {
            List<AudioSessionInfo> result = new List<AudioSessionInfo>();
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioSessionManager2 mgr = null;
            IAudioSessionEnumerator se = null;

            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out device) != 0 || device == null) return result;
                object obj;
                Guid iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, 0x17, IntPtr.Zero, out obj) != 0 || obj == null) return result;
                mgr = obj as IAudioSessionManager2;
                if (mgr == null) return result;
                if (mgr.GetSessionEnumerator(out se) != 0 || se == null) return result;

                int count;
                se.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl ctl = null;
                    try
                    {
                        if (se.GetSession(i, out ctl) != 0 || ctl == null) continue;
                        IAudioSessionControl2 c2 = ctl as IAudioSessionControl2;
                        uint pid = 0;
                        string inst = "";
                        if (c2 != null)
                        {
                            c2.GetProcessId(out pid);
                            c2.GetSessionInstanceIdentifier(out inst);
                        }
                        ISimpleAudioVolume vol = ctl as ISimpleAudioVolume;
                        bool muted = false;
                        float volume = 1f;
                        if (vol != null)
                        {
                            vol.GetMute(out muted);
                            vol.GetMasterVolume(out volume);
                        }
                        result.Add(new AudioSessionInfo
                        {
                            Pid = pid,
                            InstanceId = inst ?? "",
                            Muted = muted,
                            Volume = volume
                        });
                    }
                    finally
                    {
                        SafeRelease(ctl);
                    }
                }
            }
            catch { }
            finally
            {
                SafeRelease(se);
                SafeRelease(mgr);
                SafeRelease(device);
                SafeRelease(enumerator);
            }
            return result;
        }

        public static bool SetMute(string instanceId, bool mute)
        {
            return ApplyToSession(instanceId, delegate(ISimpleAudioVolume vol)
            {
                Guid ctx = Guid.Empty;
                return vol.SetMute(mute, ref ctx) == 0;
            });
        }

        public static bool SetVolume(string instanceId, float level)
        {
            return ApplyToSession(instanceId, delegate(ISimpleAudioVolume vol)
            {
                Guid ctx = Guid.Empty;
                return vol.SetMasterVolume(level, ref ctx) == 0;
            });
        }

        // 按 InstanceId 找到会话并对 ISimpleAudioVolume 执行操作
        private static bool ApplyToSession(string instanceId, Func<ISimpleAudioVolume, bool> action)
        {
            if (string.IsNullOrEmpty(instanceId)) return false;
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioSessionManager2 mgr = null;
            IAudioSessionEnumerator se = null;

            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out device) != 0 || device == null) return false;
                object obj;
                Guid iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, 0x17, IntPtr.Zero, out obj) != 0 || obj == null) return false;
                mgr = obj as IAudioSessionManager2;
                if (mgr == null) return false;
                if (mgr.GetSessionEnumerator(out se) != 0 || se == null) return false;

                int count;
                se.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl ctl = null;
                    try
                    {
                        if (se.GetSession(i, out ctl) != 0 || ctl == null) continue;
                        IAudioSessionControl2 c2 = ctl as IAudioSessionControl2;
                        if (c2 == null) continue;
                        string inst;
                        c2.GetSessionInstanceIdentifier(out inst);
                        if (inst != instanceId) continue;
                        ISimpleAudioVolume vol = ctl as ISimpleAudioVolume;
                        if (vol == null) return false;
                        return action(vol);
                    }
                    finally
                    {
                        SafeRelease(ctl);
                    }
                }
            }
            catch { return false; }
            finally
            {
                SafeRelease(se);
                SafeRelease(mgr);
                SafeRelease(device);
                SafeRelease(enumerator);
            }
            return false;
        }
    }

    internal static class RtssBridge
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private delegate void LoadProfileDelegate(string profile);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool GetProfilePropertyDelegate(string name, byte[] data, uint size);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool SetProfilePropertyDelegate(string name, byte[] data, uint size);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private delegate void SaveProfileDelegate(string profile);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void UpdateProfilesDelegate();

        private static IntPtr _hMod = IntPtr.Zero;
        private static LoadProfileDelegate _loadProfile;
        private static GetProfilePropertyDelegate _getProp;
        private static SetProfilePropertyDelegate _setProp;
        private static SaveProfileDelegate _saveProfile;
        private static UpdateProfilesDelegate _updateProfiles;

        public static string RtssDir { get; private set; }
        public static bool IsInstalled { get { return _hMod != IntPtr.Zero; } }
        public static bool IsRunning { get { return IsRtssProcessRunning(); } }

        public static bool Initialize()
        {
            if (IsInstalled) return true;
            string dllName = Environment.Is64BitProcess ? "RTSSHooks64.dll" : "RTSSHooks.dll";
            List<string> candidates = new List<string>();

            string[] regKeys = new string[]
            {
                @"SOFTWARE\WOW6432Node\Unwinder\RTSS",
                @"SOFTWARE\Unwinder\RTSS",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\RTSS",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RTSS"
            };
            for (int i = 0; i < regKeys.Length; i++)
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(regKeys[i]))
                    {
                        if (key == null) continue;
                        string[] vals = new string[] { "InstallDir", "InstallPath", "InstallLocation" };
                        for (int v = 0; v < vals.Length; v++)
                        {
                            object o = key.GetValue(vals[v]);
                            if (o != null)
                            {
                                string s = o.ToString();
                                if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = Path.GetDirectoryName(s);
                                if (!string.IsNullOrEmpty(s)) candidates.Add(s);
                            }
                        }
                    }
                }
                catch { }
            }

            try
            {
                Process[] rtssProcs = Process.GetProcessesByName("RTSS");
                for (int i = 0; i < rtssProcs.Length; i++)
                {
                    try
                    {
                        string fn = rtssProcs[i].MainModule.FileName;
                        if (!string.IsNullOrEmpty(fn)) candidates.Add(Path.GetDirectoryName(fn));
                    }
                    catch { }
                    finally { rtssProcs[i].Dispose(); }
                }
            }
            catch { }

            candidates.Add(@"C:\Program Files (x86)\RivaTuner Statistics Server");
            candidates.Add(@"C:\Program Files\RivaTuner Statistics Server");

            for (int i = 0; i < candidates.Count; i++)
            {
                string dir = candidates[i];
                if (string.IsNullOrEmpty(dir)) continue;
                string dllPath = Path.Combine(dir, dllName);
                if (!File.Exists(dllPath)) continue;

                IntPtr h = LoadLibrary(dllPath);
                if (h == IntPtr.Zero) continue;

                IntPtr pLoad = GetProcAddress(h, "LoadProfile");
                IntPtr pGet = GetProcAddress(h, "GetProfileProperty");
                IntPtr pSet = GetProcAddress(h, "SetProfileProperty");
                IntPtr pSave = GetProcAddress(h, "SaveProfile");
                IntPtr pUpd = GetProcAddress(h, "UpdateProfiles");
                if (pLoad == IntPtr.Zero || pGet == IntPtr.Zero || pSet == IntPtr.Zero || pSave == IntPtr.Zero || pUpd == IntPtr.Zero)
                    continue;

                _loadProfile = (LoadProfileDelegate)Marshal.GetDelegateForFunctionPointer(pLoad, typeof(LoadProfileDelegate));
                _getProp = (GetProfilePropertyDelegate)Marshal.GetDelegateForFunctionPointer(pGet, typeof(GetProfilePropertyDelegate));
                _setProp = (SetProfilePropertyDelegate)Marshal.GetDelegateForFunctionPointer(pSet, typeof(SetProfilePropertyDelegate));
                _saveProfile = (SaveProfileDelegate)Marshal.GetDelegateForFunctionPointer(pSave, typeof(SaveProfileDelegate));
                _updateProfiles = (UpdateProfilesDelegate)Marshal.GetDelegateForFunctionPointer(pUpd, typeof(UpdateProfilesDelegate));
                _hMod = h;
                RtssDir = dir;
                return true;
            }
            return false;
        }

        public static bool EnsureRunning()
        {
            if (!IsInstalled || string.IsNullOrEmpty(RtssDir)) return false;
            string exePath = Path.Combine(RtssDir, "RTSS.exe");
            if (!File.Exists(exePath)) return false;
            if (IsRtssProcessRunning()) return true;

            try
            {
                Process.Start(exePath);
                for (int i = 0; i < 10; i++)
                {
                    Thread.Sleep(250);
                    if (IsRtssProcessRunning()) return true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log("Failed to launch RTSS: " + ex.Message, "WARN");
            }
            return IsRtssProcessRunning();
        }

        private static bool IsRtssProcessRunning()
        {
            Process[] ps = Process.GetProcessesByName("RTSS");
            bool running = ps.Length > 0;
            for (int i = 0; i < ps.Length; i++) ps[i].Dispose();
            return running;
        }

        public static uint GetLimit(string exe)
        {
            if (!IsInstalled) return 0;
            try
            {
                _loadProfile(exe);
                byte[] buf = new byte[4];
                if (_getProp("FramerateLimit", buf, 4))
                    return BitConverter.ToUInt32(buf, 0);
            }
            catch { }
            return 0;
        }

        public static bool SetLimit(string exe, uint limit)
        {
            if (!IsInstalled) return false;
            try
            {
                _loadProfile(exe);
                byte[] buf = BitConverter.GetBytes(limit);
                if (_setProp("FramerateLimit", buf, 4))
                {
                    _saveProfile(exe);
                    _updateProfiles();
                    return true;
                }
            }
            catch { }
            return false;
        }
    }

    // =========================================================================
    // CPU 配额限速（Job Object）— 只限制 CPU 时间占比，不直接控制帧率
    // =========================================================================
    internal static class CpuQuotaCore
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint infoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const int JobObjectCpuRateControlInformation = 15;
        private const uint CpuRateControlEnable = 0x1;
        private const uint CpuRateControlHardCap = 0x4; // 注意：不是 0x2（0x2 是 WEIGHT_BASED）
        private const uint ProcessSetQuota = 0x0100;
        private const uint ProcessTerminate = 0x0001;

        [StructLayout(LayoutKind.Sequential)]
        private struct CpuRateInfo
        {
            public uint ControlFlags;
            public uint CpuRate; // 1..10000，单位 0.01%
        }

        // PID → Job 句柄。每个进程一个 Job，才能单独解除限制。
        private static readonly Dictionary<int, IntPtr> Jobs = new Dictionary<int, IntPtr>();

        public static bool SetRate(int pid, int percent)
        {
            if (pid <= 0) return false;
            if (percent < 1) percent = 1;
            if (percent > 100) percent = 100;

            IntPtr job;
            if (!Jobs.TryGetValue(pid, out job) || job == IntPtr.Zero)
            {
                job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return false;

                IntPtr hProc = OpenProcess(ProcessSetQuota | ProcessTerminate, false, pid);
                if (hProc == IntPtr.Zero)
                {
                    CloseHandle(job);
                    return false;
                }
                bool assigned;
                try { assigned = AssignProcessToJobObject(job, hProc); }
                finally { CloseHandle(hProc); }
                if (!assigned)
                {
                    CloseHandle(job);
                    return false;
                }
                Jobs[pid] = job;
            }
            return ApplyRate(job, percent);
        }

        public static void RestorePid(int pid)
        {
            IntPtr job;
            if (!Jobs.TryGetValue(pid, out job)) return;
            ApplyRate(job, 100); // 先解除限制，再销毁 Job
            CloseHandle(job);
            Jobs.Remove(pid);
        }

        public static void RestoreAll()
        {
            foreach (KeyValuePair<int, IntPtr> kv in Jobs) { ApplyRate(kv.Value, 100); }
            foreach (KeyValuePair<int, IntPtr> kv in Jobs) { CloseHandle(kv.Value); }
            Jobs.Clear();
        }

        public static void Cleanup(HashSet<int> livePids)
        {
            if (Jobs.Count == 0) return;
            List<int> dead = null;
            foreach (int pid in Jobs.Keys)
            {
                if (!livePids.Contains(pid))
                {
                    if (dead == null) dead = new List<int>();
                    dead.Add(pid);
                }
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++) RestorePid(dead[i]);
        }

        private static bool ApplyRate(IntPtr job, int percent)
        {
            CpuRateInfo info = new CpuRateInfo();
            if (percent >= 100)
            {
                info.ControlFlags = 0; // 关闭速率控制
                info.CpuRate = 0;
            }
            else
            {
                info.ControlFlags = CpuRateControlEnable | CpuRateControlHardCap;
                info.CpuRate = (uint)(percent * 100);
            }

            int size = Marshal.SizeOf(typeof(CpuRateInfo));
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buf, false);
                return SetInformationJobObject(job, JobObjectCpuRateControlInformation, buf, (uint)size);
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    // =========================================================================
    // 挂起进程限速（NtSuspendProcess）— 实验性，周期性挂起/恢复
    // =========================================================================
    internal static class SuspendCore
    {
        [DllImport("ntdll.dll")]
        private static extern int NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        private static extern int NtResumeProcess(IntPtr processHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        private const uint ProcessSuspendResume = 0x0800;
        private const uint Synchronize = 0x00100000;

        private class Worker
        {
            public int Pid;
            public IntPtr Handle;
            public int Fps;
            public ManualResetEvent Stop;
            public Thread Thread;
            public bool Failed;
            public int LastStatus;
            public DateTime RetryAfterUtc;
            public bool LoggedOk;
        }

        private static readonly Dictionary<int, Worker> Workers = new Dictionary<int, Worker>();
        private static readonly object Sync = new object();

        public static bool Start(int pid, int fps, out bool changed)
        {
            changed = false;
            if (pid <= 0) return false;
            fps = LimitMethods.Clamp(LimitMethods.Rtss, fps);

            lock (Sync)
            {
                Worker w;
                if (Workers.TryGetValue(pid, out w))
                {
                    if (!w.Failed && w.Fps == fps) return true;
                    if (w.Failed && DateTime.UtcNow < w.RetryAfterUtc) return false;
                    StopUnlocked(pid);
                }

                IntPtr h = OpenProcess(ProcessSuspendResume | Synchronize, false, pid);
                if (h == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    AppLogger.Log(string.Format("Suspend OpenProcess failed: pid={0} err={1}", pid, err), "WARN");
                    return false;
                }

                w = new Worker();
                w.Pid = pid;
                w.Handle = h;
                w.Fps = fps;
                w.Stop = new ManualResetEvent(false);
                w.Thread = new Thread(delegate() { Loop(w); });
                w.Thread.IsBackground = true;
                Workers[pid] = w;
                w.Thread.Start();
                changed = true;
                return true;
            }
        }

        private static void Loop(Worker w)
        {
            // 每秒 fps 个周期：挂起大部分时间，短暂放行一小段
            int cycle = 1000 / w.Fps;
            if (cycle < 2) cycle = 2;
            int run = Math.Max(1, cycle / 10);
            int suspend = Math.Max(1, cycle - run);

            while (!w.Stop.WaitOne(0))
            {
                if (WaitForSingleObject(w.Handle, 0) == 0)
                {
                    w.Failed = true;
                    w.RetryAfterUtc = DateTime.UtcNow.AddSeconds(30);
                    break; // 目标进程已退出
                }

                int st = NtSuspendProcess(w.Handle);
                if (st != 0)
                {
                    w.Failed = true;
                    w.LastStatus = st;
                    w.RetryAfterUtc = DateTime.UtcNow.AddSeconds(10);
                    AppLogger.Log(string.Format("Suspend failed: pid={0} status=0x{1:X8}", w.Pid, st), "WARN");
                    break;
                }
                if (!w.LoggedOk)
                {
                    w.LoggedOk = true;
                    AppLogger.Log(string.Format("Suspend active: pid={0} fps={1}", w.Pid, w.Fps), "INFO");
                }

                bool stopping = w.Stop.WaitOne(suspend);
                NtResumeProcess(w.Handle);
                if (stopping) break;
                if (w.Stop.WaitOne(run)) break;
            }
        }

        public static void Stop(int pid)
        {
            lock (Sync) { StopUnlocked(pid); }
        }

        private static void StopUnlocked(int pid)
        {
            Worker w;
            if (!Workers.TryGetValue(pid, out w)) return;
            Workers.Remove(pid);

            try { w.Stop.Set(); } catch { }
            try { w.Thread.Join(2000); } catch { }
            // 兜底：无论线程以何种方式结束，都确保进程被恢复，避免永久挂起
            try { NtResumeProcess(w.Handle); } catch { }
            try { CloseHandle(w.Handle); } catch { }
            try { w.Stop.Close(); } catch { }
        }

        public static void StopAll()
        {
            lock (Sync)
            {
                List<int> pids = new List<int>(Workers.Keys);
                for (int i = 0; i < pids.Count; i++) StopUnlocked(pids[i]);
            }
        }

        public static void Cleanup(HashSet<int> livePids)
        {
            lock (Sync)
            {
                List<int> dead = null;
                foreach (int pid in Workers.Keys)
                {
                    if (!livePids.Contains(pid))
                    {
                        if (dead == null) dead = new List<int>();
                        dead.Add(pid);
                    }
                }
                if (dead == null) return;
                for (int i = 0; i < dead.Count; i++) StopUnlocked(dead[i]);
            }
        }

        public static bool IsActive(int pid)
        {
            lock (Sync) { return Workers.ContainsKey(pid); }
        }

        public static bool HasFailed(int pid)
        {
            lock (Sync)
            {
                Worker w;
                return Workers.TryGetValue(pid, out w) && w.Failed;
            }
        }
    }

    internal static class AutostartManager
    {
        private const string TaskName = "GameMuteHelper";

        private static int RunSchtasks(string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                p.WaitForExit(5000);
                return p.ExitCode;
            }
        }

        public static bool IsEnabled()
        {
            try { return RunSchtasks("/Query /TN \"" + TaskName + "\"") == 0; }
            catch { return false; }
        }

        public static bool SetEnabled(bool enable, bool isAdmin)
        {
            try
            {
                if (enable)
                {
                    string exe = Application.ExecutablePath;
                    string rl = isAdmin ? "HIGHEST" : "LIMITED";
                    string args = string.Format("/Create /F /SC ONLOGON /RL {0} /TN \"{1}\" /TR \"\\\"{2}\\\" -Background\"",
                        rl, TaskName, exe);
                    return RunSchtasks(args) == 0;
                }
                else
                {
                    RunSchtasks("/Delete /F /TN \"" + TaskName + "\"");
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log("Autostart error: " + ex.Message, "ERROR");
                return false;
            }
        }
    }

    // =========================================================================
    // 4. Modern Minimalist Light Theme & DPI-Scaled Custom Controls
    // =========================================================================
    internal static class Theme
    {
        public static float Scale = 1.0f;

        public static int S(int px)
        {
            return (int)Math.Round(px * Scale);
        }

        public static void InitScale(Control ctrl)
        {
            try
            {
                using (Graphics g = ctrl.CreateGraphics())
                {
                    Scale = g.DpiX / 96f;
                }
            }
            catch
            {
                Scale = 1.0f;
            }
            if (Scale < 1.0f) Scale = 1.0f;
        }

        // Crisp, airy Daylight Palette (Apple / Win11 Mica inspired)
        public static readonly Color Bg = Color.FromArgb(245, 247, 250);
        public static readonly Color Surface = Color.FromArgb(255, 255, 255);
        public static readonly Color CardBg = Color.FromArgb(255, 255, 255);
        public static readonly Color CardHover = Color.FromArgb(249, 251, 255);
        public static readonly Color InputBg = Color.FromArgb(238, 241, 246);
        public static readonly Color InputHover = Color.FromArgb(230, 235, 243);
        public static readonly Color Border = Color.FromArgb(226, 232, 240);
        public static readonly Color BorderHover = Color.FromArgb(203, 213, 225);

        public static readonly Color Text = Color.FromArgb(17, 24, 39);
        public static readonly Color TextMuted = Color.FromArgb(100, 116, 139);
        public static readonly Color TextDim = Color.FromArgb(148, 163, 184);

        public static readonly Color Accent = Color.FromArgb(37, 99, 235);
        public static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
        public static readonly Color AccentSoft = Color.FromArgb(239, 246, 255);

        public static readonly Color Green = Color.FromArgb(5, 150, 105);
        public static readonly Color GreenSoft = Color.FromArgb(236, 253, 245);

        public static readonly Color Amber = Color.FromArgb(217, 119, 6);
        public static readonly Color AmberSoft = Color.FromArgb(255, 251, 235);

        public static readonly Color Red = Color.FromArgb(220, 38, 38);
        public static readonly Color RedSoft = Color.FromArgb(254, 242, 242);

        public static readonly Font FontBrand = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
        public static readonly Font FontHeading = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
        public static readonly Font FontBody = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
        public static readonly Font FontBodyBold = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
        public static readonly Font FontSmall = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Regular);
        public static readonly Font FontSmallBold = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Bold);

        public static GraphicsPath RoundedRect(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            if (d > rect.Width) d = rect.Width;
            if (d > rect.Height) d = rect.Height;
            if (d <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Icon CreateAppIcon(Color accentColor, bool mutedSlash)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (SolidBrush bg = new SolidBrush(Color.White))
                using (Pen border = new Pen(accentColor, 2.4f))
                using (SolidBrush fg = new SolidBrush(accentColor))
                using (Pen wave = new Pen(accentColor, 2.2f))
                {
                    g.FillEllipse(bg, 2, 2, 28, 28);
                    g.DrawEllipse(border, 2, 2, 28, 28);

                    Point[] pts = new Point[]
                    {
                        new Point(8, 13), new Point(13, 13), new Point(18, 8),
                        new Point(18, 24), new Point(13, 19), new Point(8, 19)
                    };
                    g.FillPolygon(fg, pts);

                    wave.StartCap = LineCap.Round;
                    wave.EndCap = LineCap.Round;
                    if (mutedSlash)
                    {
                        g.DrawLine(wave, 21, 13, 26, 19);
                        g.DrawLine(wave, 26, 13, 21, 19);
                    }
                    else
                    {
                        g.DrawArc(wave, 16, 11, 8, 10, -50, 100);
                    }
                }
                IntPtr hIcon = bmp.GetHicon();
                return Icon.FromHandle(hIcon);
            }
        }
    }

    internal class ToggleSwitch : Control
    {
        private bool _checked;
        private bool _hovered;

        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(Theme.S(38), Theme.S(20));
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e)
        {
            if (Enabled) Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle track = new Rectangle(1, 1, Width - 3, Height - 3);
            Color trackColor;
            if (!Enabled)
                trackColor = Color.FromArgb(226, 232, 240);
            else if (_checked)
                trackColor = _hovered ? Theme.AccentHover : Theme.Accent;
            else
                trackColor = _hovered ? Color.FromArgb(186, 197, 212) : Color.FromArgb(203, 213, 225);

            using (GraphicsPath p = Theme.RoundedRect(track, track.Height / 2))
            using (SolidBrush br = new SolidBrush(trackColor))
            {
                g.FillPath(br, p);
            }

            int pad = Math.Max(2, Theme.S(3));
            int knobSize = Height - pad * 2;
            int knobX = _checked ? (Width - knobSize - pad) : pad;
            Rectangle knob = new Rectangle(knobX, pad, knobSize, knobSize);
            Color knobColor = Enabled ? Color.White : Color.FromArgb(248, 250, 252);
            using (SolidBrush kbr = new SolidBrush(knobColor))
            {
                g.FillEllipse(kbr, knob);
            }
        }
    }

    internal enum PillStyle { Subtle, Accent, Ghost, DangerIcon }

    internal class PillButton : Control
    {
        private bool _hovered;
        private bool _pressed;
        private PillStyle _style = PillStyle.Subtle;
        private int _radius = 8;

        public PillStyle Style
        {
            get { return _style; }
            set { _style = value; Invalidate(); }
        }

        public int CornerRadius
        {
            get { return _radius; }
            set { _radius = value; Invalidate(); }
        }

        public PillButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(Theme.S(92), Theme.S(32));
            Font = Theme.FontBody;
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Rectangle r = new Rectangle(1, 1, Width - 3, Height - 3);
            Color fill = Color.Transparent;
            Color border = Color.Transparent;
            Color fg = Theme.Text;

            if (_style == PillStyle.Accent)
            {
                fill = _pressed ? Color.FromArgb(29, 78, 216) : (_hovered ? Theme.AccentHover : Theme.Accent);
                fg = Color.White;
            }
            else if (_style == PillStyle.Subtle)
            {
                fill = _pressed ? Theme.InputHover : (_hovered ? Theme.InputBg : Theme.Surface);
                border = _hovered ? Theme.BorderHover : Theme.Border;
                fg = Theme.Text;
            }
            else if (_style == PillStyle.DangerIcon)
            {
                fill = _hovered ? Theme.RedSoft : Color.Transparent;
                fg = _hovered ? Theme.Red : Theme.TextDim;
            }
            else
            {
                fill = _hovered ? Theme.InputBg : Color.Transparent;
                fg = _hovered ? Theme.Text : Theme.TextMuted;
            }

            using (GraphicsPath p = Theme.RoundedRect(r, Theme.S(_radius)))
            {
                if (fill != Color.Transparent)
                {
                    using (SolidBrush br = new SolidBrush(fill)) g.FillPath(br, p);
                }
                if (border != Color.Transparent)
                {
                    using (Pen pen = new Pen(border, 1f)) g.DrawPath(pen, p);
                }
            }

            if (_style == PillStyle.DangerIcon)
            {
                // Draw razor-sharp vector 'X' (never truncates to '×...' at any DPI)
                int cx = Width / 2;
                int cy = Height / 2;
                int arm = Math.Max(4, Theme.S(4));
                using (Pen xPen = new Pen(fg, Math.Max(1.5f, 1.5f * Theme.Scale)))
                {
                    xPen.StartCap = LineCap.Round;
                    xPen.EndCap = LineCap.Round;
                    g.DrawLine(xPen, cx - arm, cy - arm, cx + arm, cy + arm);
                    g.DrawLine(xPen, cx + arm, cy - arm, cx - arm, cy + arm);
                }
                return;
            }

            TextRenderer.DrawText(g, Text, Font, r, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    internal class SegmentedTabs : Control
    {
        private int _selectedIndex;
        private int _hoverIndex = -1;
        private string _tab0 = "已接管";
        private string _tab1 = "运行中";

        public event EventHandler SelectedIndexChanged;

        public int SelectedIndex
        {
            get { return _selectedIndex; }
            set
            {
                if (_selectedIndex == value) return;
                _selectedIndex = value;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        public void SetLabels(string tab0, string tab1)
        {
            if (_tab0 == tab0 && _tab1 == tab1) return;
            _tab0 = tab0;
            _tab1 = tab1;
            Invalidate();
        }

        public SegmentedTabs()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(Theme.S(210), Theme.S(34));
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int idx = (e.X < Width / 2) ? 0 : 1;
            if (idx != _hoverIndex) { _hoverIndex = idx; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hoverIndex = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            SelectedIndex = (e.X < Width / 2) ? 0 : 1;
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Rectangle outer = new Rectangle(1, 1, Width - 3, Height - 3);
            using (GraphicsPath p = Theme.RoundedRect(outer, Theme.S(9)))
            using (SolidBrush bg = new SolidBrush(Theme.InputBg))
            {
                g.FillPath(bg, p);
            }

            int pad = Theme.S(3);
            int half = (Width - pad * 2) / 2;
            Rectangle r0 = new Rectangle(pad, pad, half, Height - pad * 2);
            Rectangle r1 = new Rectangle(pad + half, pad, Width - pad * 2 - half, Height - pad * 2);

            DrawSegment(g, r0, _tab0, _selectedIndex == 0, _hoverIndex == 0);
            DrawSegment(g, r1, _tab1, _selectedIndex == 1, _hoverIndex == 1);
        }

        private void DrawSegment(Graphics g, Rectangle r, string text, bool active, bool hovered)
        {
            if (active)
            {
                using (GraphicsPath p = Theme.RoundedRect(r, Theme.S(7)))
                using (SolidBrush br = new SolidBrush(Theme.Surface))
                using (Pen bd = new Pen(Theme.Border, 1f))
                {
                    g.FillPath(br, p);
                    g.DrawPath(bd, p);
                }
            }
            Color fg = active ? Theme.Text : (hovered ? Theme.Text : Theme.TextMuted);
            Font f = active ? Theme.FontBodyBold : Theme.FontBody;
            TextRenderer.DrawText(g, text, f, r, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    internal class SearchInput : Panel
    {
        private readonly TextBox _tb;
        private bool _focused;
        private const string Placeholder = "搜索游戏、进程名或窗口标题...";

        public event EventHandler QueryChanged;

        public string Query
        {
            get
            {
                if (_tb.Text == Placeholder) return "";
                return _tb.Text.Trim();
            }
        }

        public SearchInput()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(Theme.S(300), Theme.S(34));
            BackColor = Theme.Bg;
            Padding = new Padding(Theme.S(32), Theme.S(7), Theme.S(10), Theme.S(5));

            _tb = new TextBox();
            _tb.BorderStyle = BorderStyle.None;
            _tb.BackColor = Theme.Surface;
            _tb.ForeColor = Theme.TextDim;
            _tb.Font = Theme.FontBody;
            _tb.Dock = DockStyle.Fill;
            _tb.Text = Placeholder;

            _tb.GotFocus += (s, e) =>
            {
                _focused = true;
                if (_tb.Text == Placeholder)
                {
                    _tb.Text = "";
                    _tb.ForeColor = Theme.Text;
                }
                Invalidate();
            };
            _tb.LostFocus += (s, e) =>
            {
                _focused = false;
                if (string.IsNullOrWhiteSpace(_tb.Text))
                {
                    _tb.Text = Placeholder;
                    _tb.ForeColor = Theme.TextDim;
                }
                Invalidate();
            };
            _tb.TextChanged += (s, e) =>
            {
                if (QueryChanged != null) QueryChanged(this, EventArgs.Empty);
            };
            Controls.Add(_tb);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(1, 1, Width - 3, Height - 3);
            using (GraphicsPath p = Theme.RoundedRect(r, Theme.S(9)))
            using (SolidBrush br = new SolidBrush(Theme.Surface))
            using (Pen bd = new Pen(_focused ? Theme.Accent : Theme.Border, _focused ? 1.4f : 1f))
            {
                g.FillPath(br, p);
                g.DrawPath(bd, p);
            }

            int ix = Theme.S(11);
            int iy = (Height - Theme.S(12)) / 2;
            int id = Theme.S(9);
            using (Pen iconPen = new Pen(_focused ? Theme.Accent : Theme.TextDim, Math.Max(1.4f, 1.4f * Theme.Scale)))
            {
                iconPen.StartCap = LineCap.Round;
                iconPen.EndCap = LineCap.Round;
                g.DrawEllipse(iconPen, ix, iy, id, id);
                g.DrawLine(iconPen, ix + id - 1, iy + id - 1, ix + id + Theme.S(3), iy + id + Theme.S(3));
            }
        }
    }

    // =========================================================================
    // 数值微调控件：可点击输入 + 右侧上下箭头（点一下 ±Step，按住持续步进）
    // 替代「裸 TextBox + 面板转发点击」的脆弱方案
    // =========================================================================
    internal class SpinnerInput : Control
    {
        private int _value;
        private int _min = 1;
        private int _max = 100;
        private int _step = 5;
        private string _suffix = "";
        private bool _hovered;
        private bool _hoverUp;
        private bool _hoverDown;
        private bool _pressedUp;
        private bool _pressedDown;
        private bool _editing;

        private System.Windows.Forms.Timer _repeatTimer;

        public event EventHandler ValueChanged;
        public event EventHandler EditCommitted;

        public int Value
        {
            get { return _value; }
            set
            {
                int v = Clamp(value);
                if (_value == v) return;
                _value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        public int Minimum
        {
            get { return _min; }
            set { _min = value; _value = Clamp(_value); Invalidate(); }
        }

        public int Maximum
        {
            get { return _max; }
            set { _max = value; _value = Clamp(_value); Invalidate(); }
        }

        public int Step
        {
            get { return _step; }
            set { if (value > 0) _step = value; }
        }

        // 数字后缀，如 "%"（纯装饰，不参与取值）
        public string Suffix
        {
            get { return _suffix; }
            set { _suffix = value ?? ""; Invalidate(); }
        }

        public bool IsEditing { get { return _editing; } }

        public SpinnerInput()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(Theme.S(64), Theme.S(24));
            Font = Theme.FontSmallBold;
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
            TabStop = false;

            _repeatTimer = new System.Windows.Forms.Timer();
            _repeatTimer.Interval = 110;
            _repeatTimer.Tick += delegate
            {
                if (_pressedUp) Bump(_step);
                else if (_pressedDown) Bump(-_step);
            };
        }

        private int Clamp(int v)
        {
            if (v < _min) v = _min;
            if (v > _max) v = _max;
            return v;
        }

        private Rectangle UpRect()
        {
            int aw = ArrowWidth();
            return new Rectangle(Width - aw - Theme.S(4), Theme.S(3), aw, (Height - Theme.S(6)) / 2);
        }

        private Rectangle DownRect()
        {
            int aw = ArrowWidth();
            int half = (Height - Theme.S(6)) / 2;
            return new Rectangle(Width - aw - Theme.S(4), Theme.S(3) + half + 1, aw, half);
        }

        private int ArrowWidth()
        {
            return Math.Max(Theme.S(10), FontHeight / 2);
        }

        private Rectangle ValueRect()
        {
            int aw = ArrowWidth();
            return new Rectangle(Theme.S(4), 1, Width - aw - Theme.S(10), Height - 2);
        }

        private void Bump(int delta)
        {
            if (_editing) return;
            Value = _value + delta;
            if (EditCommitted != null) EditCommitted(this, EventArgs.Empty);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            _hoverUp = false;
            _hoverDown = false;
            _pressedUp = false;
            _pressedDown = false;
            _repeatTimer.Stop();
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool hu = UpRect().Contains(e.Location);
            bool hd = DownRect().Contains(e.Location);
            if (hu != _hoverUp || hd != _hoverDown)
            {
                _hoverUp = hu;
                _hoverDown = hd;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled) return;
            Focus();

            if (UpRect().Contains(e.Location))
            {
                _pressedUp = true;
                Bump(_step);
                _repeatTimer.Start();
                Invalidate();
            }
            else if (DownRect().Contains(e.Location))
            {
                _pressedDown = true;
                Bump(-_step);
                _repeatTimer.Start();
                Invalidate();
            }
            else if (ValueRect().Contains(e.Location))
            {
                BeginEdit();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool wasRepeat = _pressedUp || _pressedDown;
            _pressedUp = false;
            _pressedDown = false;
            _repeatTimer.Stop();
            if (wasRepeat) Invalidate();
            base.OnMouseUp(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Up || keyData == Keys.Down) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!Enabled) return;
            if (e.KeyCode == Keys.Up)
            {
                Bump(_step);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Down)
            {
                Bump(-_step);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                BeginEdit();
                e.Handled = true;
            }
        }

        // 进入编辑态：创建临时输入框覆盖在数字区域上
        private void BeginEdit()
        {
            if (_editing || !Enabled) return;
            _editing = true;

            Rectangle vr = ValueRect();
            TextBox tb = new TextBox();
            tb.BorderStyle = BorderStyle.None;
            tb.BackColor = Theme.Surface;
            tb.ForeColor = Theme.Text;
            tb.Font = Theme.FontSmallBold;
            tb.TextAlign = HorizontalAlignment.Center;
            tb.MaxLength = 4;
            tb.Text = _value.ToString();
            tb.SetBounds(vr.X, vr.Y + (vr.Height - tb.PreferredHeight) / 2, vr.Width, tb.PreferredHeight);
            Controls.Add(tb);
            tb.BringToFront();
            tb.Focus();
            tb.SelectAll();

            EventHandler commit = null;
            EventHandler cancel = null;
            commit = delegate
            {
                int v;
                if (!int.TryParse(tb.Text.Trim().TrimEnd('%'), out v)) v = _value;
                tb.Dispose();
                _editing = false;
                Controls.Remove(tb);
                Value = v;
                Invalidate();
                if (EditCommitted != null) EditCommitted(this, EventArgs.Empty);
            };
            cancel = delegate
            {
                tb.Dispose();
                _editing = false;
                Controls.Remove(tb);
                Invalidate();
            };
            tb.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; commit(null, EventArgs.Empty); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; cancel(null, EventArgs.Empty); }
            };
            tb.LostFocus += (s, e) => commit(null, EventArgs.Empty);
            // 防止在 TextBox 内按上下方向键时被控件的编辑框吃掉：编辑态下传给输入框本身
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Rectangle r = new Rectangle(1, 1, Width - 3, Height - 3);
            using (GraphicsPath p = Theme.RoundedRect(r, Theme.S(7)))
            using (SolidBrush br = new SolidBrush(Theme.InputBg))
            using (Pen bd = new Pen(_hovered ? Theme.BorderHover : Theme.Border, 1f))
            {
                g.FillPath(br, p);
                g.DrawPath(bd, p);
            }

            // 数字区
            Rectangle vr = ValueRect();
            Color numColor = Enabled ? Theme.Text : Theme.TextDim;
            if (!_editing)
            {
                TextRenderer.DrawText(g, _value.ToString() + _suffix, Font, vr, numColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            // 上下箭头区
            DrawArrow(g, UpRect(), _hoverUp, _pressedUp, true);
            DrawArrow(g, DownRect(), _hoverDown, _pressedDown, false);
        }

        private void DrawArrow(Graphics g, Rectangle r, bool hovered, bool pressed, bool up)
        {
            Color c = pressed ? Theme.Accent : (hovered ? Theme.AccentHover : Theme.TextMuted);
            int cx = r.X + r.Width / 2;
            int cy = r.Y + r.Height / 2;
            int half = Math.Max(2, Math.Min(r.Width, r.Height) / 2 - 1);

            using (Pen pen = new Pen(c, Math.Max(1.4f, 1.4f * Theme.Scale)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                if (up) g.DrawLine(pen, cx - half, cy + 1, cx, cy - 1);
                else g.DrawLine(pen, cx - half, cy - 1, cx, cy + 1);
                if (up) g.DrawLine(pen, cx, cy - 1, cx + half, cy + 1);
                else g.DrawLine(pen, cx, cy + 1, cx + half, cy - 1);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_repeatTimer != null) { _repeatTimer.Stop(); _repeatTimer.Dispose(); _repeatTimer = null; }
            }
            base.Dispose(disposing);
        }
    }

    internal class LightColorTable : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Theme.InputBg; } }
        public override Color MenuItemBorder { get { return Theme.Border; } }
        public override Color ToolStripDropDownBackground { get { return Theme.Surface; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Surface; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Surface; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Surface; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Border; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color CheckBackground { get { return Theme.AccentSoft; } }
        public override Color CheckSelectedBackground { get { return Theme.AccentSoft; } }
    }

    internal class LightMenuRenderer : ToolStripProfessionalRenderer
    {
        public LightMenuRenderer() : base(new LightColorTable()) { }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Theme.Text;
            base.OnRenderItemText(e);
        }
    }

    // =========================================================================
    // 5. Modern Minimalist Light App Card
    // =========================================================================
    internal class AppCardControl : Panel
    {
        public string ProcessName { get; private set; }
        public bool IsManagedView { get; private set; }

        private string _windowTitle;
        private string _statusText;
        private Color _statusFg;
        private Color _statusBg;
        private Image _appIcon;
        private bool _hovered;

        private ToggleSwitch _muteToggle;
        private ToggleSwitch _fpsToggle;
        private SpinnerInput _fpsSpinner;
        private SpinnerInput _volumeSpinner;
        private PillButton _actionBtn;
        private PillButton _methodBtn;
        private PillButton _audioBtn;
        private ContextMenuStrip _methodMenu;
        private ContextMenuStrip _audioMenu;
        private bool _rtssInstalled;
        private string _method = LimitMethods.Rtss;
        private int _value = 5;
        private string _audioMode = AudioModes.Mute;
        private int _volumePercent = AudioModes.DefaultVolume;

        public event Action<string, bool, bool, int, string> RuleChanged;
        public event Action<string, bool, string, int> AudioRuleChanged;
        public event Action<string> RemoveClicked;
        public event Action<string> AddClicked;

        public AppCardControl(string procName, string title, Image icon, bool isManagedView,
                              AppRule rule, bool alreadyManaged, bool rtssInstalled, ToolTip tips,
                              string method)
        {
            ProcessName = procName;
            _windowTitle = title ?? "";
            _appIcon = icon;
            IsManagedView = isManagedView;
            _rtssInstalled = rtssInstalled;
            _method = LimitMethods.Normalize(method);
            _statusText = "—";
            _statusFg = Theme.TextMuted;
            _statusBg = Theme.InputBg;

            if (rule != null)
            {
                _audioMode = AudioModes.Normalize(rule.AudioMode);
                _volumePercent = AudioModes.ClampVolume(rule.AudioVolume.HasValue ? rule.AudioVolume.Value : AudioModes.DefaultVolume);
            }

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            Height = Theme.S(64);
            BackColor = Theme.Bg;

            if (isManagedView && rule != null)
            {
                BuildManagedControls(rule, tips);
            }
            else
            {
                BuildRunningControls(alreadyManaged);
            }

            MouseEnter += (s, e) => { _hovered = true; Invalidate(); };
            MouseLeave += (s, e) =>
            {
                if (!ClientRectangle.Contains(PointToClient(Cursor.Position)))
                {
                    _hovered = false;
                    Invalidate();
                }
            };
        }

        private void BuildManagedControls(AppRule rule, ToolTip tips)
        {
            _muteToggle = new ToggleSwitch();
            _muteToggle.Checked = rule.Mute;
            _muteToggle.CheckedChanged += (s, e) => NotifyRuleChanged();
            Controls.Add(_muteToggle);

            // 音频方式按钮：完全静音 / 降低音量到指定百分比
            _audioBtn = new PillButton();
            _audioBtn.Style = PillStyle.Subtle;
            _audioBtn.Font = Theme.FontSmall;
            _audioBtn.CornerRadius = 7;
            _audioBtn.Size = new Size(Theme.S(72), Theme.S(22));
            _audioBtn.Text = AudioModes.ShortLabel(_audioMode);
            _audioBtn.Click += (s, e) => ShowAudioMenu();
            if (tips != null) tips.SetToolTip(_audioBtn, "点击切换后台音频方式：完全静音或降低到指定百分比");
            Controls.Add(_audioBtn);

            _volumeSpinner = new SpinnerInput();
            _volumeSpinner.Minimum = AudioModes.MinVolumePercent;
            _volumeSpinner.Maximum = AudioModes.MaxVolumePercent;
            _volumeSpinner.Step = 5;
            _volumeSpinner.Suffix = "%";
            _volumeSpinner.Value = _volumePercent;
            _volumeSpinner.Visible = _audioMode == AudioModes.Attenuate;
            _volumeSpinner.EditCommitted += (s, e) => CommitVolume();
            Controls.Add(_volumeSpinner);
            if (tips != null) tips.SetToolTip(_volumeSpinner, "后台音量百分比：点击数字直接输入，箭头 ±5");

            _fpsToggle = new ToggleSwitch();
            _fpsToggle.Enabled = MethodAvailable(_method);
            _fpsToggle.Checked = _fpsToggle.Enabled && rule.LimitFps;
            _fpsToggle.CheckedChanged += (s, e) =>
            {
                if (_fpsSpinner != null)
                {
                    _fpsSpinner.Enabled = _fpsToggle.Checked;
                }
                Invalidate();
                NotifyRuleChanged();
            };
            Controls.Add(_fpsToggle);

            _fpsSpinner = new SpinnerInput();
            _value = LimitMethods.Clamp(_method, rule.Fps);
            _fpsSpinner.Minimum = 1;
            _fpsSpinner.Maximum = LimitMethods.Normalize(_method) == LimitMethods.Cpu ? 100 : 240;
            _fpsSpinner.Step = 5;
            _fpsSpinner.Suffix = LimitMethods.Normalize(_method) == LimitMethods.Cpu ? "%" : "";
            _fpsSpinner.Value = _value;
            _fpsSpinner.Enabled = _fpsToggle.Checked;
            _fpsSpinner.EditCommitted += (s, e) => CommitValue();
            if (tips != null) tips.SetToolTip(_fpsSpinner, ValueTooltip(_method) + "；点击数字直接输入，箭头 ±5");

            // 每个应用独立的限帧方式选择
            _methodBtn = new PillButton();
            _methodBtn.Style = PillStyle.Subtle;
            _methodBtn.Font = Theme.FontSmall;
            _methodBtn.CornerRadius = 7;
            _methodBtn.Size = new Size(Theme.S(58), Theme.S(22));
            _methodBtn.Text = LimitMethods.ShortLabel(_method);
            _methodBtn.Click += (s, e) => ShowMethodMenu();
            if (tips != null) tips.SetToolTip(_methodBtn, "点击切换该应用的限帧方式");
            Controls.Add(_methodBtn);

            _actionBtn = new PillButton();
            _actionBtn.Size = new Size(Theme.S(28), Theme.S(28));
            _actionBtn.CornerRadius = 7;
            _actionBtn.Style = PillStyle.DangerIcon;
            _actionBtn.Click += (s, e) => { if (RemoveClicked != null) RemoveClicked(ProcessName); };
            if (tips != null) tips.SetToolTip(_actionBtn, "从接管列表中移除");
            Controls.Add(_actionBtn);

            LayoutChildControls();
        }

        private void ShowAudioMenu()
        {
            if (_audioMenu == null)
            {
                _audioMenu = new ContextMenuStrip();
                _audioMenu.Renderer = new LightMenuRenderer();
                _audioMenu.BackColor = Theme.Surface;
                _audioMenu.ForeColor = Theme.Text;
                _audioMenu.Font = Theme.FontSmall;
                _audioMenu.ShowImageMargin = false;

                ToolStripMenuItem muteItem = new ToolStripMenuItem("完全静音");
                muteItem.Click += (s, e) => ChangeAudioMode(AudioModes.Mute);
                _audioMenu.Items.Add(muteItem);

                ToolStripMenuItem attenItem = new ToolStripMenuItem("降低音量（可设百分比）");
                attenItem.Click += (s, e) => ChangeAudioMode(AudioModes.Attenuate);
                _audioMenu.Items.Add(attenItem);
            }
            _audioMenu.Show(_audioBtn, new Point(0, _audioBtn.Height));
        }

        private void ChangeAudioMode(string mode)
        {
            string m = AudioModes.Normalize(mode);
            if (m == _audioMode) return;

            _audioMode = m;
            bool atten = AudioModes.IsAttenuate(_audioMode);
            if (_audioBtn != null) _audioBtn.Text = AudioModes.ShortLabel(_audioMode);
            if (_volumeSpinner != null)
            {
                _volumeSpinner.Visible = atten;
                if (atten) _volumeSpinner.Value = _volumePercent;
            }
            Invalidate();
            NotifyRuleChanged();
        }

        private int ParseVolume()
        {
            int val = _volumeSpinner == null ? AudioModes.DefaultVolume : _volumeSpinner.Value;
            _volumePercent = AudioModes.ClampVolume(val);
            return _volumePercent;
        }

        private void CommitVolume()
        {
            if (_volumeSpinner == null || !_volumeSpinner.Visible) return;
            ParseVolume();
            NotifyRuleChanged();
        }

        private void BuildRunningControls(bool alreadyManaged)
        {
            _actionBtn = new PillButton();
            _actionBtn.Size = new Size(Theme.S(84), Theme.S(30));
            _actionBtn.CornerRadius = 8;
            if (alreadyManaged)
            {
                _actionBtn.Text = "✓ 已接管";
                _actionBtn.Style = PillStyle.Ghost;
                _actionBtn.Enabled = false;
            }
            else
            {
                _actionBtn.Text = "+ 接管";
                _actionBtn.Style = PillStyle.Accent;
                _actionBtn.Click += (s, e) => { if (AddClicked != null) AddClicked(ProcessName); };
            }
            Controls.Add(_actionBtn);
            LayoutChildControls();
        }

        private bool MethodAvailable(string method)
        {
            return LimitMethods.Normalize(method) != LimitMethods.Rtss || _rtssInstalled;
        }

        private static string ValueTooltip(string method)
        {
            string m = LimitMethods.Normalize(method);
            if (m == LimitMethods.Cpu) return "CPU 配额：后台限制 CPU 占用百分比（不保证帧率）";
            if (m == LimitMethods.Suspend) return "挂起进程：后台按每秒次数周期性挂起/放行（实验性）";
            return "RTSS：后台目标帧率";
        }

        private int ParseValue()
        {
            int val = _fpsSpinner == null ? 5 : _fpsSpinner.Value;
            _value = LimitMethods.Clamp(_method, val);
            return _value;
        }

        private void CommitValue()
        {
            if (_fpsSpinner == null) return;
            ParseValue();
            _fpsSpinner.Value = _value; // 同步回控件（含 clamp 后的显示）
            NotifyRuleChanged();
        }

        private void NotifyRuleChanged()
        {
            if (RuleChanged != null && _muteToggle != null && _fpsToggle != null)
            {
                RuleChanged(ProcessName, _muteToggle.Checked, _fpsToggle.Checked, ParseValue(), _method);
            }
            if (AudioRuleChanged != null && _muteToggle != null)
            {
                AudioRuleChanged(ProcessName, _muteToggle.Checked, _audioMode, _volumePercent);
            }
        }

        private void ShowMethodMenu()
        {
            if (_methodMenu == null)
            {
                _methodMenu = new ContextMenuStrip();
                _methodMenu.Renderer = new LightMenuRenderer();
                _methodMenu.BackColor = Theme.Surface;
                _methodMenu.ForeColor = Theme.Text;
                _methodMenu.Font = Theme.FontSmall;
                _methodMenu.ShowImageMargin = false;

                ToolStripMenuItem rtss = new ToolStripMenuItem("RTSS 精确限帧（按 FPS）");
                rtss.Click += (s, e) => ChangeMethod(LimitMethods.Rtss);
                _methodMenu.Items.Add(rtss);

                ToolStripMenuItem cpu = new ToolStripMenuItem("CPU 配额（按百分比）");
                cpu.Click += (s, e) => ChangeMethod(LimitMethods.Cpu);
                _methodMenu.Items.Add(cpu);

                ToolStripMenuItem suspend = new ToolStripMenuItem("挂起进程（实验性，可能卡顿/声音异常）");
                suspend.Click += (s, e) => ChangeMethod(LimitMethods.Suspend);
                _methodMenu.Items.Add(suspend);
            }
            _methodMenu.Show(_methodBtn, new Point(0, _methodBtn.Height));
        }

        private void ChangeMethod(string method)
        {
            string m = LimitMethods.Normalize(method);
            if (m == _method) return;

            _method = m;
            _value = LimitMethods.Clamp(_method, _value);
            if (_methodBtn != null) _methodBtn.Text = LimitMethods.ShortLabel(_method);

            bool available = MethodAvailable(_method);
            if (_fpsToggle != null)
            {
                _fpsToggle.Enabled = available;
                if (!available) _fpsToggle.Checked = false;
            }
            if (_fpsSpinner != null)
            {
                _fpsSpinner.Maximum = m == LimitMethods.Cpu ? 100 : 240;
                _fpsSpinner.Suffix = m == LimitMethods.Cpu ? "%" : "";
                _fpsSpinner.Value = _value;
                _fpsSpinner.Enabled = _fpsToggle != null && _fpsToggle.Checked;
            }
            Invalidate();
            NotifyRuleChanged();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_methodMenu != null) { _methodMenu.Dispose(); _methodMenu = null; }
                if (_audioMenu != null) { _audioMenu.Dispose(); _audioMenu = null; }
            }
            base.Dispose(disposing);
        }

        public void SetStatus(string text, Color fg, Color bg)
        {
            if (_statusText == text && _statusFg == fg) return;
            _statusText = text;
            _statusFg = fg;
            _statusBg = bg;
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildControls();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            // SpinnerInput 自带点击处理（箭头 ±5 / 数字区直接输入），无需转发
        }

        private void LayoutChildControls()
        {
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            if (w <= 0) return;

            if (IsManagedView)
            {
                if (_actionBtn != null)
                    _actionBtn.Location = new Point(w - Theme.S(40), (h - _actionBtn.Height) / 2);
                if (_fpsSpinner != null)
                    _fpsSpinner.Location = new Point(w - Theme.S(114), (h - _fpsSpinner.Height) / 2);
                if (_fpsToggle != null)
                    _fpsToggle.Location = new Point(w - Theme.S(156), (h - _fpsToggle.Height) / 2);
                if (_methodBtn != null)
                    _methodBtn.Location = new Point(w - Theme.S(216), (h - _methodBtn.Height) / 2);
                if (_muteToggle != null)
                    _muteToggle.Location = new Point(w - Theme.S(256), (h - _muteToggle.Height) / 2);
                if (_audioBtn != null)
                    _audioBtn.Location = new Point(w - Theme.S(330), (h - _audioBtn.Height) / 2);
                if (_volumeSpinner != null)
                    _volumeSpinner.Location = new Point(w - Theme.S(400), (h - _volumeSpinner.Height) / 2);
            }
            else
            {
                if (_actionBtn != null)
                    _actionBtn.Location = new Point(w - Theme.S(98), (h - _actionBtn.Height) / 2);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Rectangle card = new Rectangle(1, 1, Width - 3, Height - 3);
            using (GraphicsPath path = Theme.RoundedRect(card, Theme.S(11)))
            using (SolidBrush bg = new SolidBrush(_hovered ? Theme.CardHover : Theme.CardBg))
            using (Pen bd = new Pen(_hovered ? Theme.BorderHover : Theme.Border, 1f))
            {
                g.FillPath(bg, path);
                g.DrawPath(bd, path);
            }

            int icoSize = Theme.S(34);
            Rectangle iconBox = new Rectangle(Theme.S(14), (Height - icoSize) / 2, icoSize, icoSize);
            if (_appIcon != null)
            {
                g.DrawImage(_appIcon, iconBox);
            }
            else
            {
                using (GraphicsPath ip = Theme.RoundedRect(iconBox, Theme.S(9)))
                using (SolidBrush ibr = new SolidBrush(Theme.AccentSoft))
                {
                    g.FillPath(ibr, ip);
                }
                string initial = string.IsNullOrEmpty(ProcessName) ? "?" : ProcessName.Substring(0, 1).ToUpperInvariant();
                TextRenderer.DrawText(g, initial, Theme.FontHeading, iconBox, Theme.Accent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            // 右侧控件（从右到左）：X(40) FPS框(114) FPS开关(156) 方式(216) 音频开关(256)
            // 音频按钮(330) 音量框(400) 标签(448) 状态胶囊(536)
            int rightReserve = IsManagedView ? Theme.S(536) : Theme.S(204);
            int textX = Theme.S(60);
            int textWidth = Math.Max(Theme.S(100), Width - textX - rightReserve);

            Rectangle nameRect = new Rectangle(textX, Theme.S(12), textWidth, Theme.S(20));
            Rectangle titleRect = new Rectangle(textX, Theme.S(34), textWidth, Theme.S(18));
            TextRenderer.DrawText(g, ProcessName, Theme.FontBodyBold, nameRect, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            string sub = string.IsNullOrEmpty(_windowTitle) ? (ProcessName + ".exe") : _windowTitle;
            TextRenderer.DrawText(g, sub, Theme.FontSmall, titleRect, Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            int pillW = Theme.S(88);
            int pillH = Theme.S(24);
            int pillX = IsManagedView ? (Width - Theme.S(536)) : (Width - Theme.S(198));
            if (pillX > Theme.S(160))
            {
                Rectangle pillRect = new Rectangle(pillX, (Height - pillH) / 2, pillW, pillH);
                using (GraphicsPath pp = Theme.RoundedRect(pillRect, pillH / 2))
                using (SolidBrush pbr = new SolidBrush(_statusBg))
                {
                    g.FillPath(pbr, pp);
                }
                TextRenderer.DrawText(g, _statusText, Theme.FontSmallBold, pillRect, _statusFg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            if (IsManagedView)
            {
                int lblH = Theme.S(24);
                int lblY = (Height - lblH) / 2;
                Rectangle muteLblRect = new Rectangle(Width - Theme.S(448), lblY, Theme.S(46), lblH);
                TextRenderer.DrawText(g, "音频", Theme.FontSmall, muteLblRect, Theme.TextMuted,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        }
    }

    // =========================================================================
    // 6. Main Application Form & Real-Time Monitor
    // =========================================================================
    internal class MainForm : Form
    {
        private enum FadeKind { None, Down, Up }

        // 渐变到原值后要做什么：移除记录（回前台）/ 设静音（切回静音方式）/ 无
        private enum AfterUpKind { None, RemoveRecord, SetMute }

        // 降音量/恢复的线性渐变：全程 FadeMs 毫秒，每 FadeStepMs 步进一次
        private const int FadeMs = 1000;
        private const int FadeStepMs = 50;

        // 我们改过的音频会话：记录原始状态；恢复失败时保留记录下个 tick 重试
        private class AudioTrack
        {
            public uint Pid;              // 会话所属进程，用于孤儿记录清理
            public string AppName;        // 所属应用名（渐变完成提示用）
            public bool WasMuted;         // 会话原始静音状态
            public float WasVolume;       // 会话原始音量
            public bool WeMuted;          // 我们设置的静音（需要解除）
            public bool Attenuated;       // 我们正在/曾干预会话音量（需要写回原值）
            public int AppliedPct;        // 当前应用的后台音量百分比
            public float CurrentLevel;    // 渐变过程中最近一次写入的音量；-1 = 尚未干预
            public FadeKind Fade;         // 当前渐变方向（None = 无进行中的渐变）
            public float FadeFrom;        // 渐变起始音量
            public float FadeTo;          // 渐变目标音量
            public DateTime FadeStartUtc; // 渐变开始时间
            public AfterUpKind AfterUp;   // 渐变到原值后的动作
            public DateTime LastSeenUtc;  // 会话最后一次出现在枚举中
        }

        private readonly EventWaitHandle _showEvent;
        private readonly bool _isAdmin;
        private readonly string _selfProcName;

        private ConfigData _config;
        private Dictionary<string, AppRule> _rules;
        private Dictionary<string, AudioTrack> _audioByUs;
        private Dictionary<int, int> _cpuApplied;
        private HashSet<string> _suspendWarned;
        private Dictionary<string, uint> _limitApplied;
        private Dictionary<string, DateTime> _inactiveSince;
        private Dictionary<string, string> _liveState;
        private Dictionary<string, Image> _iconCache;
        private Dictionary<string, string> _runningTitles;
        private List<AppCardControl> _visibleCards;

        private bool _rtssAvailable;
        private bool _paused;
        private bool _exiting;
        private string _lastTrayKey = "";
        private int _rtssUiTick;
        private bool _rtssStartInFlight;
        private DateTime _lastRtssAutoRestart = DateTime.MinValue;

        private ToolTip _tips;
        private SegmentedTabs _tabs;
        private SearchInput _searchInput;
        private PillButton _addExeBtn;
        private PillButton _startRtssBtn;
        private PillButton _pauseBtn;
        private PillButton _refreshBtn;
        private PillButton _debounceBtn;
        private PillButton _hideBtn;
        private ToggleSwitch _autostartToggle;
        private Label _statusBadge;
        private Label _rtssBadge;
        private Label _footerStatus;
        private Panel _cardsHost;
        private Label _emptyLabel;
        private ContextMenuStrip _debounceMenu;

        private NotifyIcon _tray;
        private ToolStripMenuItem _miPause;
        private Icon _iconActive;
        private Icon _iconMuted;
        private Icon _iconPaused;
        private System.Windows.Forms.Timer _monitorTimer;
        private System.Windows.Forms.Timer _fadeTimer;

        public MainForm(EventWaitHandle showEvent, bool startBackground, bool isAdmin)
        {
            _showEvent = showEvent;
            _isAdmin = isAdmin;
            using (Process cur = Process.GetCurrentProcess()) _selfProcName = cur.ProcessName;

            _audioByUs = new Dictionary<string, AudioTrack>(StringComparer.OrdinalIgnoreCase);
            _cpuApplied = new Dictionary<int, int>();
            _suspendWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _limitApplied = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            _inactiveSince = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            _liveState = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _iconCache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
            _runningTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _visibleCards = new List<AppCardControl>();

            Theme.InitScale(this);
            RtssBridge.Initialize();
            LoadState();
            BuildIcons();
            BuildUI();
            BuildTray();
            if (AnyRuleUsesRtss()) StartRtssAsync(false); // 启动时顺带拉起 RTSS（若已安装且未运行）

            SystemEvents.SessionEnding += OnSessionEnding;
            RecoverFromCrash();
            ScanRunningProcesses();
            RebuildCardList();
            UpdateHeaderAndTray();

            _monitorTimer = new System.Windows.Forms.Timer();
            _monitorTimer.Interval = 500;
            _monitorTimer.Tick += OnMonitorTick;
            _monitorTimer.Start();

            // 渐变步进定时器：常开低频无负担（tick 内无渐变时直接返回）
            _fadeTimer = new System.Windows.Forms.Timer();
            _fadeTimer.Interval = FadeStepMs;
            _fadeTimer.Tick += delegate { try { FadeTick(); } catch (Exception ex) { AppLogger.Log("Fade tick error: " + ex.Message, "ERROR"); } };
            _fadeTimer.Start();

            if (startBackground)
            {
                WindowState = FormWindowState.Minimized;
                ShowInTaskbar = false;
            }
        }

        private void LoadState()
        {
            _config = ConfigStore.Load();
            _rules = new Dictionary<string, AppRule>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _config.Apps.Count; i++)
            {
                AppRule r = _config.Apps[i];
                if (!string.IsNullOrEmpty(r.Name)) _rules[r.Name] = r;
            }

            // 限帧是否可用只看当前状态；RTSS 稍后会由 StartRtssAsync 自动拉起，
            // 因此这里不再把规则的 LimitFps 永久关闭。
            if (RtssBridge.IsInstalled) _rtssAvailable = RtssBridge.IsRunning;
        }

        private void SaveState()
        {
            _config.Apps = _rules.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            ConfigStore.Save(_config);
        }

        private void BuildIcons()
        {
            _iconActive = Theme.CreateAppIcon(Theme.Accent, false);
            _iconMuted = Theme.CreateAppIcon(Theme.Amber, true);
            _iconPaused = Theme.CreateAppIcon(Theme.TextMuted, true);
        }

        private void BuildUI()
        {
            _tips = new ToolTip();

            Text = "GameMuteHelper";
            Icon = _iconActive;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.FontBody;
            AutoScaleMode = AutoScaleMode.None; // We handle exact DPI scaling cleanly via Theme.S()
            ClientSize = new Size(Theme.S(860), Theme.S(580));
            MinimumSize = new Size(Theme.S(780), Theme.S(480));
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;

            HandleCreated += (s, e) => NativeUI.ApplyWindowLightTheme(Handle);
            Shown += (s, e) => { ActiveControl = null; }; // Keep search placeholder visible initially
            FormClosing += (s, e) =>
            {
                if (!_exiting)
                {
                    e.Cancel = true;
                    Hide();
                }
            };

            int w = ClientSize.Width;

            // --- 1. Crisp Light Header ---
            Panel header = new Panel();
            header.Size = new Size(w, Theme.S(74));
            header.Dock = DockStyle.Top;
            header.BackColor = Theme.Surface;
            header.Paint += (s, e) =>
            {
                using (Pen p = new Pen(Theme.Border, 1f))
                    e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1);
            };

            Label brand = new Label();
            brand.Text = "GameMuteHelper";
            brand.Font = Theme.FontBrand;
            brand.ForeColor = Theme.Text;
            brand.AutoSize = true;
            brand.Location = new Point(Theme.S(22), Theme.S(14));
            header.Controls.Add(brand);

            _statusBadge = new Label();
            _statusBadge.Font = Theme.FontSmall;
            _statusBadge.ForeColor = Theme.Green;
            _statusBadge.AutoSize = true;
            _statusBadge.Location = new Point(Theme.S(23), Theme.S(44));
            header.Controls.Add(_statusBadge);

            int btnY = Theme.S(20);
            _pauseBtn = new PillButton();
            _pauseBtn.Text = "暂停";
            _pauseBtn.Style = PillStyle.Subtle;
            _pauseBtn.Size = new Size(Theme.S(74), Theme.S(34));
            _pauseBtn.Location = new Point(w - Theme.S(96), btnY);
            _pauseBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _pauseBtn.Click += (s, e) => SetPaused(!_paused);
            header.Controls.Add(_pauseBtn);

            _addExeBtn = new PillButton();
            _addExeBtn.Text = "+ 添加EXE";
            _addExeBtn.Style = PillStyle.Subtle;
            _addExeBtn.Size = new Size(Theme.S(104), Theme.S(34));
            _addExeBtn.Location = new Point(w - Theme.S(210), btnY);
            _addExeBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _addExeBtn.Click += OnAddExeClicked;
            header.Controls.Add(_addExeBtn);

            _tabs = new SegmentedTabs();
            _tabs.Size = new Size(Theme.S(212), Theme.S(34));
            _tabs.Location = new Point(w - Theme.S(434), btnY);
            _tabs.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _tabs.SelectedIndexChanged += (s, e) =>
            {
                if (_tabs.SelectedIndex == 1) ScanRunningProcesses();
                _refreshBtn.Visible = (_tabs.SelectedIndex == 1);
                RebuildCardList();
            };
            header.Controls.Add(_tabs);

            // 手动启动 RTSS（仅在已安装但未运行时显示）
            _startRtssBtn = new PillButton();
            _startRtssBtn.Text = "启动 RTSS";
            _startRtssBtn.Style = PillStyle.Subtle;
            _startRtssBtn.Font = Theme.FontSmall;
            _startRtssBtn.Size = new Size(Theme.S(104), Theme.S(34));
            _startRtssBtn.Location = new Point(Theme.S(200), btnY);
            _startRtssBtn.Visible = false;
            _startRtssBtn.Click += (s, e) => StartRtssAsync(true);
            _tips.SetToolTip(_startRtssBtn, "启动 RivaTuner Statistics Server（后台限帧需要）");
            header.Controls.Add(_startRtssBtn);

            // --- 2. Airy Search & Status Bar ---
            Panel toolbar = new Panel();
            toolbar.Size = new Size(w, Theme.S(56));
            toolbar.Dock = DockStyle.Top;
            toolbar.BackColor = Theme.Bg;

            _searchInput = new SearchInput();
            _searchInput.Location = new Point(Theme.S(22), Theme.S(12));
            _searchInput.Size = new Size(Theme.S(310), Theme.S(34));
            _searchInput.QueryChanged += (s, e) => RebuildCardList();
            toolbar.Controls.Add(_searchInput);

            _refreshBtn = new PillButton();
            _refreshBtn.Text = "刷新列表";
            _refreshBtn.Style = PillStyle.Subtle;
            _refreshBtn.Size = new Size(Theme.S(86), Theme.S(32));
            _refreshBtn.Location = new Point(Theme.S(344), Theme.S(13));
            _refreshBtn.Visible = false;
            _refreshBtn.Click += (s, e) =>
            {
                ScanRunningProcesses();
                RebuildCardList();
                SetFooterMessage("已刷新运行中的程序列表");
            };
            toolbar.Controls.Add(_refreshBtn);

            _rtssBadge = new Label();
            _rtssBadge.Font = Theme.FontSmall;
            _rtssBadge.AutoSize = false;
            _rtssBadge.Size = new Size(Theme.S(250), Theme.S(24));
            _rtssBadge.TextAlign = ContentAlignment.MiddleRight;
            _rtssBadge.Location = new Point(w - Theme.S(272), Theme.S(17));
            _rtssBadge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            if (RtssBridge.IsInstalled)
            {
                _rtssBadge.Text = "● RTSS 已就绪 · 支持后台限帧";
                _rtssBadge.ForeColor = Theme.Green;
            }
            else
            {
                _rtssBadge.Text = "○ 未检测到 RTSS · 仅静音可用";
                _rtssBadge.ForeColor = Theme.TextMuted;
            }
            toolbar.Controls.Add(_rtssBadge);

            // --- 3. Unified Light Minimalist Footer (No ugly Win32 ComboBox) ---
            Panel footer = new Panel();
            footer.Size = new Size(w, Theme.S(54));
            footer.Dock = DockStyle.Bottom;
            footer.BackColor = Theme.Surface;
            footer.Paint += (s, e) =>
            {
                using (Pen p = new Pen(Theme.Border, 1f))
                    e.Graphics.DrawLine(p, 0, 0, footer.Width, 0);
            };

            _autostartToggle = new ToggleSwitch();
            _autostartToggle.Location = new Point(Theme.S(22), (footer.Height - _autostartToggle.Height) / 2);
            _autostartToggle.Checked = AutostartManager.IsEnabled();
            _autostartToggle.CheckedChanged += (s, e) =>
            {
                bool ok = AutostartManager.SetEnabled(_autostartToggle.Checked, _isAdmin);
                SetFooterMessage(ok
                    ? (_autostartToggle.Checked ? "已开启开机自启（后台托盘）" : "已关闭开机自启")
                    : "设置开机自启失败，请检查管理员权限");
            };
            footer.Controls.Add(_autostartToggle);

            Label autoLbl = new Label();
            autoLbl.Text = "开机自启";
            autoLbl.Font = Theme.FontSmall;
            autoLbl.ForeColor = Theme.TextMuted;
            autoLbl.AutoSize = true;
            autoLbl.Location = new Point(Theme.S(68), Theme.S(19));
            footer.Controls.Add(autoLbl);

            // Custom Light Pill Selector for Debounce (replaces ugly ComboBox)
            _debounceMenu = new ContextMenuStrip();
            _debounceMenu.Renderer = new LightMenuRenderer();
            _debounceMenu.BackColor = Theme.Surface;
            _debounceMenu.ForeColor = Theme.Text;
            _debounceMenu.Font = Theme.FontSmall;
            _debounceMenu.ShowImageMargin = false;
            int[] presets = new int[] { 0, 300, 500, 1000, 2000, 3000 };
            for (int i = 0; i < presets.Length; i++)
            {
                int val = presets[i];
                ToolStripMenuItem item = new ToolStripMenuItem(val == 0 ? "0 ms (立即触发)" : (val + " ms"));
                item.Click += (s, e) =>
                {
                    _config.DebounceMs = val;
                    _debounceBtn.Text = string.Format("防抖 {0}ms ▾", val);
                    SaveState();
                    SetFooterMessage("失焦防抖已更新为 " + val + " ms");
                };
                _debounceMenu.Items.Add(item);
            }

            _debounceBtn = new PillButton();
            _debounceBtn.Text = string.Format("防抖 {0}ms ▾", _config.DebounceMs);
            _debounceBtn.Style = PillStyle.Subtle;
            _debounceBtn.Font = Theme.FontSmall;
            _debounceBtn.CornerRadius = 7;
            _debounceBtn.Size = new Size(Theme.S(102), Theme.S(28));
            _debounceBtn.Location = new Point(Theme.S(142), (footer.Height - _debounceBtn.Height) / 2);
            _debounceBtn.Click += (s, e) => _debounceMenu.Show(_debounceBtn, new Point(0, -_debounceMenu.PreferredSize.Height));
            footer.Controls.Add(_debounceBtn);

            PillButton logBtn = new PillButton();
            logBtn.Text = "数据目录";
            logBtn.Style = PillStyle.Ghost;
            logBtn.Font = Theme.FontSmall;
            logBtn.Size = new Size(Theme.S(78), Theme.S(28));
            logBtn.Location = new Point(Theme.S(254), (footer.Height - logBtn.Height) / 2);
            logBtn.Click += (s, e) => { try { Process.Start("explorer.exe", AppLogger.DataDir); } catch { } };
            footer.Controls.Add(logBtn);

            _hideBtn = new PillButton();
            _hideBtn.Text = "收起托盘";
            _hideBtn.Style = PillStyle.Subtle;
            _hideBtn.Font = Theme.FontSmall;
            _hideBtn.Size = new Size(Theme.S(88), Theme.S(28));
            _hideBtn.Location = new Point(w - Theme.S(110), (footer.Height - _hideBtn.Height) / 2);
            _hideBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _hideBtn.Click += (s, e) => Hide();
            footer.Controls.Add(_hideBtn);

            _footerStatus = new Label();
            _footerStatus.Font = Theme.FontSmall;
            _footerStatus.ForeColor = Theme.TextMuted;
            _footerStatus.AutoEllipsis = true;
            _footerStatus.TextAlign = ContentAlignment.MiddleRight;
            _footerStatus.Location = new Point(Theme.S(344), Theme.S(17));
            _footerStatus.Size = new Size(Math.Max(Theme.S(80), w - Theme.S(468)), Theme.S(20));
            _footerStatus.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            _footerStatus.Text = "更改将自动保存并即时生效";
            footer.Controls.Add(_footerStatus);

            // --- 4. Scrollable Card Host ---
            _cardsHost = new Panel();
            _cardsHost.Dock = DockStyle.Fill;
            _cardsHost.AutoScroll = true;
            _cardsHost.BackColor = Theme.Bg;
            _cardsHost.Padding = new Padding(Theme.S(22), Theme.S(4), Theme.S(22), Theme.S(14));
            _cardsHost.Resize += (s, e) => ResizeCards();

            _emptyLabel = new Label();
            _emptyLabel.Font = Theme.FontBody;
            _emptyLabel.ForeColor = Theme.TextMuted;
            _emptyLabel.TextAlign = ContentAlignment.MiddleCenter;
            _emptyLabel.Dock = DockStyle.Fill;
            _emptyLabel.Visible = false;
            _cardsHost.Controls.Add(_emptyLabel);

            Controls.Add(_cardsHost);
            Controls.Add(toolbar);
            Controls.Add(header);
            Controls.Add(footer);
        }

        private void BuildTray()
        {
            _tray = new NotifyIcon();
            _tray.Icon = _iconActive;
            _tray.Text = "GameMuteHelper";
            _tray.Visible = true;
            _tray.DoubleClick += (s, e) => ShowMainWindow();

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new LightMenuRenderer();
            menu.BackColor = Theme.Surface;
            menu.ForeColor = Theme.Text;
            menu.Font = Theme.FontBody;
            menu.ShowImageMargin = false;

            ToolStripMenuItem miOpen = new ToolStripMenuItem("打开主界面");
            miOpen.Click += (s, e) => ShowMainWindow();
            menu.Items.Add(miOpen);

            _miPause = new ToolStripMenuItem("暂停监控");
            _miPause.Click += (s, e) => SetPaused(!_paused);
            menu.Items.Add(_miPause);

            ToolStripMenuItem miData = new ToolStripMenuItem("打开配置与日志目录");
            miData.Click += (s, e) => { try { Process.Start("explorer.exe", AppLogger.DataDir); } catch { } };
            menu.Items.Add(miData);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miExit = new ToolStripMenuItem("退出程序");
            miExit.Click += (s, e) => ExitApplication();
            menu.Items.Add(miExit);

            _tray.ContextMenuStrip = menu;
        }

        private void ScanRunningProcesses()
        {
            HashSet<int> audioPids = new HashSet<int>();
            List<AudioSessionInfo> sessions = MuteCore.GetSessions();
            for (int i = 0; i < sessions.Count; i++)
            {
                if (sessions[i].Pid > 0) audioPids.Add((int)sessions[i].Pid);
            }

            Dictionary<string, string> found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Process[] procs = Process.GetProcesses();
            for (int i = 0; i < procs.Length; i++)
            {
                Process p = procs[i];
                try
                {
                    string pn = p.ProcessName;
                    if (pn.Equals(_selfProcName, StringComparison.OrdinalIgnoreCase)) continue;
                    bool hasWin = p.MainWindowHandle != IntPtr.Zero;
                    if (hasWin || audioPids.Contains(p.Id))
                    {
                        string title = p.MainWindowTitle;
                        if (string.IsNullOrEmpty(title) && hasWin) title = MuteCore.GetTitle(p.MainWindowHandle);
                        if (!found.ContainsKey(pn) || (string.IsNullOrEmpty(found[pn]) && !string.IsNullOrEmpty(title)))
                            found[pn] = title ?? "";

                        if (!_iconCache.ContainsKey(pn))
                        {
                            try
                            {
                                string exePath = p.MainModule.FileName;
                                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                                {
                                    using (Icon ico = Icon.ExtractAssociatedIcon(exePath))
                                    {
                                        if (ico != null) _iconCache[pn] = ico.ToBitmap();
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            _runningTitles = found;
        }

        private void RebuildCardList()
        {
            _tabs.SetLabels(string.Format("已接管 ({0})", _rules.Count), "运行中");
            string q = _searchInput.Query.ToLowerInvariant();
            bool managedTab = (_tabs.SelectedIndex == 0);

            _cardsHost.SuspendLayout();
            _cardsHost.AutoScrollPosition = new Point(0, 0);
            _cardsHost.AutoScrollPosition = new Point(0, 0);
            for (int i = 0; i < _visibleCards.Count; i++)
            {
                _cardsHost.Controls.Remove(_visibleCards[i]);
                _visibleCards[i].Dispose();
            }
            _visibleCards.Clear();

            List<string> names;
            if (managedTab)
            {
                names = _rules.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            }
            else
            {
                names = _runningTitles.Keys
                    .OrderBy(k => _rules.ContainsKey(k) ? 1 : 0)
                    .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            int padX = Theme.S(22);
            int y = Theme.S(4);
            int cardW = Math.Max(Theme.S(300), _cardsHost.ClientSize.Width - padX * 2 - SystemInformation.VerticalScrollBarWidth);

            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                string title = _runningTitles.ContainsKey(name) ? _runningTitles[name] : "";
                if (!string.IsNullOrEmpty(q))
                {
                    bool match = name.ToLowerInvariant().Contains(q) || title.ToLowerInvariant().Contains(q);
                    if (!match) continue;
                }

                Image icon = _iconCache.ContainsKey(name) ? _iconCache[name] : null;
                AppRule rule = _rules.ContainsKey(name) ? _rules[name] : null;
                AppCardControl card = new AppCardControl(name, title, icon, managedTab, rule, rule != null,
                    RtssBridge.IsInstalled, _tips, rule != null ? rule.Method : LimitMethods.Rtss);
                card.Location = new Point(padX, y);
                card.Width = cardW;

                if (managedTab)
                {
                    card.RuleChanged += OnRuleChanged;
                    card.AudioRuleChanged += OnAudioRuleChanged;
                    card.RemoveClicked += OnRemoveRule;
                }
                else
                {
                    card.AddClicked += OnAddRunningApp;
                }

                ApplyCardStatus(card);
                _cardsHost.Controls.Add(card);
                _visibleCards.Add(card);
                y += card.Height + Theme.S(8);
            }

            if (_visibleCards.Count == 0)
            {
                _emptyLabel.Text = managedTab
                    ? (string.IsNullOrEmpty(q) ? "尚未接管任何应用\r\n切换到「运行中」一键添加，或点击右上角「+ 添加EXE」" : "未找到匹配的已接管应用")
                    : "未找到匹配的运行中应用";
                _emptyLabel.Visible = true;
            }
            else
            {
                _emptyLabel.Visible = false;
            }

            _cardsHost.ResumeLayout();
            ResizeCards();
        }

        private void ResizeCards()
        {
            int padX = Theme.S(22);
            int scrollbar = _cardsHost.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
            int w = Math.Max(Theme.S(300), _cardsHost.ClientSize.Width - padX * 2 - scrollbar);
            for (int i = 0; i < _visibleCards.Count; i++)
            {
                _visibleCards[i].Width = w;
            }
        }

        private void ApplyCardStatus(AppCardControl card)
        {
            string st;
            if (_liveState.ContainsKey(card.ProcessName))
                st = _liveState[card.ProcessName];
            else if (_runningTitles.ContainsKey(card.ProcessName))
                st = "运行中";
            else
                st = "未运行";

            if (st.Contains("静音") || st.Contains("FPS") || st.Contains("CPU") || st.Contains("挂起"))
                card.SetStatus(st, Theme.Amber, Theme.AmberSoft);
            else if (st == "前台" || st == "运行中")
                card.SetStatus(st, Theme.Green, Theme.GreenSoft);
            else
                card.SetStatus(st, Theme.TextMuted, Theme.InputBg);
        }

        private void OnRuleChanged(string procName, bool mute, bool limitFps, int value, string method)
        {
            if (!_rules.ContainsKey(procName)) return;
            AppRule r = _rules[procName];
            string newMethod = LimitMethods.Normalize(method);
            bool methodChanged = !newMethod.Equals(LimitMethods.Normalize(r.Method), StringComparison.OrdinalIgnoreCase);

            r.Mute = mute;
            r.Method = newMethod;
            r.Fps = LimitMethods.Clamp(newMethod, value);
            r.LimitFps = limitFps;

            if (r.LimitFps && newMethod == LimitMethods.Rtss && !_rtssAvailable)
            {
                _rtssAvailable = RtssBridge.EnsureRunning();
                if (!_rtssAvailable)
                {
                    r.LimitFps = false;
                    SetFooterMessage("RTSS 启动失败，已关闭限帧；后台静音仍正常");
                    RebuildCardList();
                }
            }

            if (methodChanged || !r.LimitFps) RestoreRuleLimits(procName);

            if (r.LimitFps && newMethod == LimitMethods.Rtss)
            {
                string exe = procName + ".exe";
                if (!_config.OriginalLimits.ContainsKey(exe))
                    _config.OriginalLimits[exe] = RtssBridge.GetLimit(exe);
            }

            SaveState();
            SetFooterMessage(string.Format("已更新 {0} 配置（{1}）", procName, LimitMethods.ShortLabel(r.Method)));
        }

        private void OnAudioRuleChanged(string procName, bool audioOn, string audioMode, int volumePercent)
        {
            if (!_rules.ContainsKey(procName)) return;
            AppRule r = _rules[procName];
            r.Mute = audioOn;
            r.AudioMode = AudioModes.Normalize(audioMode);
            r.AudioVolume = AudioModes.ClampVolume(volumePercent);

            // 音频设置变化：立即恢复该应用所有会话的当前干预，让下个 tick 按新模式应用
            RestoreRuleAudio(procName, true);
            SaveState();
            string modeText = AudioModes.IsAttenuate(r.AudioMode)
                ? string.Format("后台降至 {0}%", r.AudioVolume)
                : "后台完全静音";
            SetFooterMessage(string.Format("已更新 {0}：{1}", procName, audioOn ? modeText : "音频干预关闭"));
        }

        // 恢复某个应用名下所有进程的音频干预记录（按 PID 找会话再恢复）。
        // immediate=true：瞬间恢复（改设置时用，让新设置立即生效）
        private void RestoreRuleAudio(string procName, bool immediate)
        {
            try
            {
                Process[] ps = Process.GetProcessesByName(procName);
                if (ps.Length == 0) return;
                HashSet<int> pids = new HashSet<int>();
                for (int i = 0; i < ps.Length; i++)
                {
                    try { pids.Add(ps[i].Id); } finally { ps[i].Dispose(); }
                }
                List<AudioSessionInfo> sessions = MuteCore.GetSessions();
                for (int i = 0; i < sessions.Count; i++)
                {
                    AudioSessionInfo s = sessions[i];
                    if (s.Pid <= 0 || !pids.Contains((int)s.Pid)) continue;
                    if (immediate)
                    {
                        // 瞬间恢复：结束渐变并直接写回原值
                        AudioTrack t;
                        if (!_audioByUs.TryGetValue(s.InstanceId, out t)) continue;
                        t.Fade = FadeKind.None;
                        bool ok = true;
                        if (t.Attenuated)
                        {
                            if (MuteCore.SetVolume(s.InstanceId, t.WasVolume))
                            {
                                t.Attenuated = false;
                                t.CurrentLevel = t.WasVolume;
                            }
                            else ok = false;
                        }
                        if (t.WeMuted)
                        {
                            if (MuteCore.SetMute(s.InstanceId, t.WasMuted)) t.WeMuted = false;
                            else ok = false;
                        }
                        if (ok) _audioByUs.Remove(s.InstanceId);
                    }
                    else
                    {
                        RestoreSessionAudio(s.InstanceId, procName, "新设置");
                    }
                }
            }
            catch { }
        }

        private void OnRemoveRule(string procName)
        {
            if (!_rules.ContainsKey(procName)) return;
            _rules.Remove(procName);
            RestoreRuleAudio(procName, true);
            RestoreRuleLimits(procName);
            _inactiveSince.Remove(procName);
            _liveState.Remove(procName);
            SaveState();
            RebuildCardList();
            UpdateHeaderAndTray();
            SetFooterMessage("已移除 " + procName);
        }

        private void OnAddRunningApp(string procName)
        {
            if (!_rules.ContainsKey(procName))
            {
                _rules[procName] = new AppRule { Name = procName, Mute = true, LimitFps = false, Fps = 5 };
                SaveState();
                RebuildCardList();
                UpdateHeaderAndTray();
                SetFooterMessage("已接管 " + procName + "（默认开启后台静音）");
            }
        }

        private void OnAddExeClicked(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择要接管的游戏或程序 (.exe)";
                dlg.Filter = "可执行文件 (*.exe)|*.exe";
                dlg.Multiselect = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                int added = 0;
                for (int i = 0; i < dlg.FileNames.Length; i++)
                {
                    string fp = dlg.FileNames[i];
                    string pn = Path.GetFileNameWithoutExtension(fp);
                    if (string.IsNullOrEmpty(pn) || pn.Equals(_selfProcName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!_rules.ContainsKey(pn))
                    {
                        _rules[pn] = new AppRule { Name = pn, Mute = true, LimitFps = false, Fps = 5 };
                        added++;
                    }
                    if (!_iconCache.ContainsKey(pn))
                    {
                        try
                        {
                            using (Icon ico = Icon.ExtractAssociatedIcon(fp))
                            {
                                if (ico != null) _iconCache[pn] = ico.ToBitmap();
                            }
                        }
                        catch { }
                    }
                }
                if (added > 0)
                {
                    SaveState();
                    _tabs.SelectedIndex = 0;
                    RebuildCardList();
                    UpdateHeaderAndTray();
                    SetFooterMessage(string.Format("已添加 {0} 个程序并即时生效", added));
                }
            }
        }

        private void SetFooterMessage(string msg)
        {
            if (_footerStatus != null) _footerStatus.Text = msg;
        }

        // =====================================================================
        // 7. Monitoring Loop & Restoration
        // =====================================================================
        private void OnMonitorTick(object sender, EventArgs e)
        {
            try
            {
                if (_showEvent.WaitOne(0)) ShowMainWindow();
                _rtssUiTick++;
                if ((_rtssUiTick % 4) == 0) UpdateRtssUi(); // 约每 2 秒同步一次 RTSS 状态
                MonitorStep();
                if (Visible)
                {
                    for (int i = 0; i < _visibleCards.Count; i++) ApplyCardStatus(_visibleCards[i]);
                }
                UpdateHeaderAndTray();
            }
            catch (Exception ex)
            {
                AppLogger.Log("Monitor tick error: " + ex.Message, "ERROR");
            }
        }

        private void MonitorStep()
        {
            if (_paused || _rules.Count == 0) return;

            Dictionary<string, List<int>> procsByName = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            HashSet<int> livePids = new HashSet<int>();
            Process[] allProcs = Process.GetProcesses();
            for (int i = 0; i < allProcs.Length; i++)
            {
                Process p = allProcs[i];
                try
                {
                    livePids.Add(p.Id);
                    if (_rules.ContainsKey(p.ProcessName))
                    {
                        List<int> list;
                        if (!procsByName.TryGetValue(p.ProcessName, out list))
                        {
                            list = new List<int>();
                            procsByName[p.ProcessName] = list;
                        }
                        list.Add(p.Id);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }

            if (procsByName.Count == 0)
            {
                foreach (string name in _rules.Keys)
                {
                    RestoreAppLimit(name + ".exe");
                    _inactiveSince.Remove(name);
                    _liveState[name] = "未运行";
                }
                RestoreAudio(restoreFailedToo: true);
                CpuQuotaCore.RestoreAll();
                SuspendCore.StopAll();
                _cpuApplied.Clear();
                _suspendWarned.Clear();
                return;
            }

            List<AudioSessionInfo> sessions = MuteCore.GetSessions();
            Dictionary<int, List<AudioSessionInfo>> byPid = new Dictionary<int, List<AudioSessionInfo>>();
            HashSet<string> alive = new HashSet<string>();
            for (int i = 0; i < sessions.Count; i++)
            {
                AudioSessionInfo s = sessions[i];
                if (!string.IsNullOrEmpty(s.InstanceId)) alive.Add(s.InstanceId);
                if (s.Pid > 0 && !string.IsNullOrEmpty(s.InstanceId))
                {
                    List<AudioSessionInfo> list;
                    if (!byPid.TryGetValue((int)s.Pid, out list))
                    {
                        list = new List<AudioSessionInfo>();
                        byPid[(int)s.Pid] = list;
                    }
                    list.Add(s);
                }
            }

            int fgPid = MuteCore.GetForegroundPid();
            DateTime now = DateTime.UtcNow;

            foreach (KeyValuePair<string, AppRule> kv in _rules)
            {
                string name = kv.Key;
                AppRule rule = kv.Value;
                string exe = name + ".exe";

                List<int> pids;
                if (!procsByName.TryGetValue(name, out pids))
                {
                    RestoreAppLimit(exe);
                    _inactiveSince.Remove(name);
                    _liveState[name] = "未运行";
                    continue;
                }

                bool active = pids.Contains(fgPid);
                bool shouldTrigger;
                if (active)
                {
                    _inactiveSince.Remove(name);
                    shouldTrigger = false;
                }
                else
                {
                    DateTime since;
                    if (!_inactiveSince.TryGetValue(name, out since))
                    {
                        since = now;
                        _inactiveSince[name] = since;
                    }
                    shouldTrigger = (now - since).TotalMilliseconds >= _config.DebounceMs;
                }

                bool attenuate = rule.Mute && AudioModes.IsAttenuate(rule.AudioMode);
                int wantPct = AudioModes.ClampVolume(rule.AudioVolume.HasValue ? rule.AudioVolume.Value : AudioModes.DefaultVolume);
                bool mutedNow = false;
                int attNowPct = 0;
                for (int i = 0; i < pids.Count; i++)
                {
                    List<AudioSessionInfo> list;
                    if (!byPid.TryGetValue(pids[i], out list)) continue;
                    for (int j = 0; j < list.Count; j++)
                    {
                        AudioSessionInfo s = list[j];
                        if (shouldTrigger && rule.Mute)
                        {
                            AudioTrack track;
                            if (!_audioByUs.TryGetValue(s.InstanceId, out track))
                            {
                                track = new AudioTrack();
                                track.Pid = s.Pid;
                                track.AppName = name;
                                track.WasMuted = s.Muted;
                                track.WasVolume = s.Volume;
                                track.CurrentLevel = -1f;
                                _audioByUs[s.InstanceId] = track;
                            }
                            track.AppName = name;
                            track.LastSeenUtc = now;

                            if (attenuate)
                            {
                                // 从「静音」切到「降音量」：先解除我们自己设的静音（音量未动过，取会话当前值）
                                if (track.WeMuted && MuteCore.SetMute(s.InstanceId, false))
                                {
                                    track.WeMuted = false;
                                    track.CurrentLevel = s.Volume;
                                }
                                if (!track.WasMuted)
                                {
                                    float target = wantPct / 100f;
                                    if (!track.Attenuated)
                                    {
                                        // 首次降音量：从会话当前音量线性渐变到目标（约 1 秒）
                                        float from = track.CurrentLevel >= 0f ? track.CurrentLevel : s.Volume;
                                        track.Attenuated = true;
                                        track.AppliedPct = wantPct;
                                        if (Math.Abs(from - target) < 0.005f)
                                        {
                                            track.CurrentLevel = target;
                                        }
                                        else
                                        {
                                            track.Fade = FadeKind.Down;
                                            track.FadeFrom = from;
                                            track.FadeTo = target;
                                            track.FadeStartUtc = now;
                                            track.AfterUp = AfterUpKind.None;
                                            SetFooterMessage(string.Format("{0} 切至后台 · 音量渐降至 {1}%", name, wantPct));
                                        }
                                    }
                                    else if (track.Fade == FadeKind.Up)
                                    {
                                        // 回前台渐变中途又切回后台：从当前值折返向下
                                        track.AppliedPct = wantPct;
                                        float from = track.CurrentLevel >= 0f ? track.CurrentLevel : s.Volume;
                                        if (Math.Abs(from - target) >= 0.005f)
                                        {
                                            track.Fade = FadeKind.Down;
                                            track.FadeFrom = from;
                                            track.FadeTo = target;
                                            track.FadeStartUtc = now;
                                            track.AfterUp = AfterUpKind.None;
                                            SetFooterMessage(string.Format("{0} 切至后台 · 音量渐降至 {1}%", name, wantPct));
                                        }
                                        else
                                        {
                                            track.Fade = FadeKind.None;
                                            track.CurrentLevel = target;
                                        }
                                    }
                                    else if (track.AppliedPct != wantPct)
                                    {
                                        // 后台期间调整目标百分比：从当前值渐变到新目标
                                        track.AppliedPct = wantPct;
                                        float from = track.CurrentLevel >= 0f ? track.CurrentLevel : s.Volume;
                                        if (track.Fade == FadeKind.Down)
                                        {
                                            track.FadeTo = target; // 已在渐降中，只改终点
                                        }
                                        else if (Math.Abs(from - target) >= 0.005f)
                                        {
                                            track.Fade = FadeKind.Down;
                                            track.FadeFrom = from;
                                            track.FadeTo = target;
                                            track.FadeStartUtc = now;
                                            SetFooterMessage(string.Format("{0} 后台音量渐调至 {1}%", name, wantPct));
                                        }
                                    }
                                    attNowPct = track.AppliedPct > 0 ? track.AppliedPct : wantPct;
                                }
                                mutedNow = true;
                            }
                            else
                            {
                                // 静音方式（含从「降音量」切过来）：先把音量渐回原值，完成后设我们的静音
                                if (track.Attenuated)
                                {
                                    if (track.Fade == FadeKind.Up)
                                    {
                                        track.AfterUp = AfterUpKind.SetMute; // 已在渐回，只更新完成动作
                                    }
                                    else
                                    {
                                        float from = track.CurrentLevel >= 0f ? track.CurrentLevel : s.Volume;
                                        if (Math.Abs(from - track.WasVolume) < 0.005f)
                                        {
                                            if (MuteCore.SetVolume(s.InstanceId, track.WasVolume))
                                            {
                                                track.CurrentLevel = track.WasVolume;
                                                track.Attenuated = false;
                                            }
                                        }
                                        else
                                        {
                                            track.Fade = FadeKind.Up;
                                            track.FadeFrom = from;
                                            track.FadeTo = track.WasVolume;
                                            track.FadeStartUtc = now;
                                            track.AfterUp = AfterUpKind.SetMute;
                                        }
                                    }
                                }
                                if (!track.WasMuted && !track.WeMuted && !s.Muted && !track.Attenuated)
                                {
                                    if (MuteCore.SetMute(s.InstanceId, true))
                                    {
                                        track.WeMuted = true;
                                        SetFooterMessage(name + " 切至后台 · 已静音");
                                    }
                                }
                                mutedNow = true;
                            }
                        }
                        else
                        {
                            RestoreSessionAudio(s.InstanceId, name, "前台");
                        }
                    }
                }

                uint appliedFps = 0;
                int appliedCpu = 0;
                int appliedSuspend = 0;
                bool suspendFailed = false;
                string method = LimitMethods.Normalize(rule.Method);

                if (rule.LimitFps && method == LimitMethods.Cpu)
                {
                    // CPU 配额：不写 RTSS profile，按 PID 挂到独立 Job 上限制 CPU 时间
                    RestoreAppLimit(exe);
                    StopSuspendForPids(pids);
                    int pct = LimitMethods.Clamp(LimitMethods.Cpu, rule.Fps);
                    if (shouldTrigger)
                    {
                        bool anyApplied = false;
                        bool changed = false;
                        for (int i = 0; i < pids.Count; i++)
                        {
                            int cur;
                            if (_cpuApplied.TryGetValue(pids[i], out cur) && cur == pct)
                            {
                                anyApplied = true;
                                continue;
                            }
                            if (CpuQuotaCore.SetRate(pids[i], pct))
                            {
                                _cpuApplied[pids[i]] = pct;
                                anyApplied = true;
                                changed = true;
                            }
                        }
                        if (anyApplied) appliedCpu = pct;
                        if (changed) SetFooterMessage(string.Format("{0} 切至后台 · CPU 配额 {1}%", name, pct));
                    }
                    else
                    {
                        for (int i = 0; i < pids.Count; i++) RestoreQuotaForPid(pids[i]);
                    }
                }
                else if (rule.LimitFps && method == LimitMethods.Suspend)
                {
                    // 挂起进程：周期性挂起/放行，实验性
                    RestoreAppLimit(exe);
                    for (int i = 0; i < pids.Count; i++) RestoreQuotaForPid(pids[i]);

                    int suspendFps = LimitMethods.Clamp(LimitMethods.Rtss, rule.Fps);
                    if (shouldTrigger)
                    {
                        int started = 0;
                        for (int i = 0; i < pids.Count; i++)
                        {
                            bool changed;
                            if (SuspendCore.Start(pids[i], suspendFps, out changed))
                            {
                                started++;
                                if (changed) SetFooterMessage(string.Format("{0} 切至后台 · 挂起循环 {1} 次/秒", name, suspendFps));
                            }
                        }

                        bool anyFailed = false;
                        for (int i = 0; i < pids.Count; i++)
                        {
                            if (SuspendCore.HasFailed(pids[i])) anyFailed = true;
                        }

                        if (started > 0 && !anyFailed)
                        {
                            appliedSuspend = suspendFps;
                            _suspendWarned.Remove(name);
                        }
                        else
                        {
                            suspendFailed = true;
                            if (_suspendWarned.Add(name))
                            {
                                SetFooterMessage(name + " 挂起失败：进程可能受反作弊保护，请改用 RTSS 或 CPU 配额");
                                AppLogger.Log("Suspend unsupported for " + name, "WARN");
                            }
                        }
                    }
                    else
                    {
                        StopSuspendForPids(pids);
                    }
                }
                else if (rule.LimitFps && method == LimitMethods.Rtss && _rtssAvailable)
                {
                    for (int i = 0; i < pids.Count; i++) RestoreQuotaForPid(pids[i]);
                    StopSuspendForPids(pids);

                    if (shouldTrigger)
                    {
                        uint want = (uint)rule.Fps;
                        uint cur;
                        if (!_limitApplied.TryGetValue(exe, out cur) || cur != want)
                        {
                            if (!_config.OriginalLimits.ContainsKey(exe))
                            {
                                _config.OriginalLimits[exe] = RtssBridge.GetLimit(exe);
                                SaveState();
                            }
                            if (RtssBridge.SetLimit(exe, want))
                            {
                                _limitApplied[exe] = want;
                                SetFooterMessage(string.Format("{0} 切至后台 · 限帧 {1} FPS", name, want));
                            }
                        }
                        _limitApplied.TryGetValue(exe, out appliedFps);
                    }
                    else
                    {
                        if (RestoreAppLimit(exe)) SetFooterMessage(name + " 切回前台 · 已恢复帧率");
                    }
                }
                else
                {
                    RestoreAppLimit(exe);
                    for (int i = 0; i < pids.Count; i++) RestoreQuotaForPid(pids[i]);
                    StopSuspendForPids(pids);
                }

                if (active)
                    _liveState[name] = "前台";
                else if (attNowPct > 0 && suspendFailed)
                    _liveState[name] = string.Format("降音量{0}%·挂起失败", attNowPct);
                else if (attNowPct > 0 && appliedSuspend > 0)
                    _liveState[name] = string.Format("降音量{0}%·挂起{1}", attNowPct, appliedSuspend);
                else if (attNowPct > 0 && appliedCpu > 0)
                    _liveState[name] = string.Format("降音量{0}%·CPU{1}%", attNowPct, appliedCpu);
                else if (attNowPct > 0 && appliedFps > 0)
                    _liveState[name] = string.Format("降音量{0}%·{1}FPS", attNowPct, appliedFps);
                else if (attNowPct > 0)
                    _liveState[name] = string.Format("降音量{0}%", attNowPct);
                else if (mutedNow && suspendFailed)
                    _liveState[name] = "静音·挂起失败";
                else if (mutedNow && appliedSuspend > 0)
                    _liveState[name] = string.Format("静音·挂起{0}", appliedSuspend);
                else if (mutedNow && appliedCpu > 0)
                    _liveState[name] = string.Format("静音·CPU{0}%", appliedCpu);
                else if (mutedNow && appliedFps > 0)
                    _liveState[name] = string.Format("静音·{0}FPS", appliedFps);
                else if (mutedNow)
                    _liveState[name] = "已静音";
                else if (suspendFailed)
                    _liveState[name] = "挂起失败";
                else if (appliedSuspend > 0)
                    _liveState[name] = string.Format("挂起 {0}", appliedSuspend);
                else if (appliedCpu > 0)
                    _liveState[name] = string.Format("CPU {0}%", appliedCpu);
                else if (appliedFps > 0)
                    _liveState[name] = appliedFps + " FPS";
                else
                    _liveState[name] = "后台";
            }

            // 关键修复：会话短暂从枚举消失（常见于快速切窗口/游戏重建会话）时，
            // 不立即丢弃恢复记录——保留 30 秒宽限，期间持续尝试恢复。
            // 彻底过期时先做一次最后恢复尝试（会话可能只是换了端点/设备），
            // 再放弃；此时若会话真已销毁，静音随会话一起消失，无残留影响。
            DateTime staleCutoff = now.AddSeconds(-30);
            List<string> expired = new List<string>();
            foreach (KeyValuePair<string, AudioTrack> kv in _audioByUs)
            {
                if (alive.Contains(kv.Key)) continue;                       // 会话还活着
                if (kv.Value.LastSeenUtc >= staleCutoff) continue;           // 短暂消失，再等等
                expired.Add(kv.Key);                                        // 彻底过期才放弃
            }
            for (int i = 0; i < expired.Count; i++)
            {
                string id = expired[i];
                AudioTrack t = _audioByUs[id];
                t.Fade = FadeKind.None; // 结束可能进行中的渐变
                if (t.Attenuated) { if (MuteCore.SetVolume(id, t.WasVolume)) { t.Attenuated = false; t.CurrentLevel = t.WasVolume; } }
                if (t.WeMuted) { if (MuteCore.SetMute(id, t.WasMuted)) t.WeMuted = false; }
                _audioByUs.Remove(id);
            }

            CpuQuotaCore.Cleanup(livePids);
            SuspendCore.Cleanup(livePids);

            if (_cpuApplied.Count > 0)
            {
                List<int> deadQuota = _cpuApplied.Keys.Where(k => !livePids.Contains(k)).ToList();
                for (int i = 0; i < deadQuota.Count; i++) _cpuApplied.Remove(deadQuota[i]);
            }
        }

        // 渐变步进：每 FadeStepMs 把进行中的渐变推进一步（线性插值，全程 FadeMs）
        private void FadeTick()
        {
            if (_audioByUs.Count == 0) return;

            DateTime now = DateTime.UtcNow;
            List<KeyValuePair<string, AudioTrack>> fading = null;
            foreach (KeyValuePair<string, AudioTrack> kv in _audioByUs)
            {
                if (kv.Value.Fade != FadeKind.None) { if (fading == null) fading = new List<KeyValuePair<string, AudioTrack>>(); fading.Add(kv); }
            }
            if (fading == null) return;

            for (int i = 0; i < fading.Count; i++)
            {
                string id = fading[i].Key;
                AudioTrack t = fading[i].Value;

                double progress = (now - t.FadeStartUtc).TotalMilliseconds / FadeMs;
                if (progress > 1.0) progress = 1.0;
                float level = t.FadeFrom + (t.FadeTo - t.FadeFrom) * (float)progress;
                if (progress >= 1.0) level = t.FadeTo;

                if (MuteCore.SetVolume(id, level))
                {
                    t.CurrentLevel = level;
                }
                else
                {
                    // 写入失败（会话短暂消失/COM 忙）：从当前实际位置重新起算
                    t.FadeFrom = level;
                    t.FadeStartUtc = now;
                    continue;
                }

                if (progress >= 1.0)
                {
                    t.Fade = FadeKind.None;
                    t.FadeFrom = t.FadeTo;
                    FinishFade(id, t);
                }
            }
        }

        // 渐变完成后的收尾：按方向处理恢复/应用静音/移除记录
        private void FinishFade(string id, AudioTrack t)
        {
            if (t.FadeTo >= 0f && Math.Abs(t.FadeTo - t.WasVolume) < 0.005f)
            {
                // 向上渐变完成：音量已回到原值
                t.Attenuated = false;
                t.CurrentLevel = t.WasVolume;

                if (t.AfterUp == AfterUpKind.RemoveRecord)
                {
                    // 前台恢复路径：解除可能存在的静音后移除记录
                    bool ok = true;
                    if (t.WeMuted && !MuteCore.SetMute(id, t.WasMuted)) ok = false;
                    if (ok)
                    {
                        t.WeMuted = false;
                        _audioByUs.Remove(id);
                        SetFooterMessage((string.IsNullOrEmpty(t.AppName) ? "应用" : t.AppName) + " · 音量已恢复");
                    }
                }
                else if (t.AfterUp == AfterUpKind.SetMute && !t.WasMuted)
                {
                    // 静音方式路径：恢复原值后立即设我们自己的静音
                    if (MuteCore.SetMute(id, true))
                    {
                        t.WeMuted = true;
                        t.AfterUp = AfterUpKind.None;
                        SetFooterMessage((string.IsNullOrEmpty(t.AppName) ? "应用" : t.AppName) + " 切至后台 · 已静音");
                    }
                }
                else if (t.AfterUp == AfterUpKind.SetMute && t.WasMuted)
                {
                    t.AfterUp = AfterUpKind.None;
                }
            }
            // 向下渐变完成：音量已在目标值，保持 Attenuated=true 等待恢复时机，无需处理
        }

        // 恢复单个会话的音频到原始状态（默认：渐变完成后移除记录）
        private bool RestoreSessionAudio(string instanceId, string name, string reason)
        {
            return RestoreSessionAudio(instanceId, name, reason, AfterUpKind.RemoveRecord);
        }

        private bool RestoreSessionAudio(string instanceId, string name, string reason, AfterUpKind afterUp)
        {
            AudioTrack track;
            if (!_audioByUs.TryGetValue(instanceId, out track)) return false;

            // 有音量干预：启动向上渐变（从当前值到原值，约 1 秒），完成后再执行后续动作
            if (track.Attenuated)
            {
                if (track.Fade == FadeKind.Up)
                {
                    // 已在向上渐变（可能由静音方式切换发起）：只更新完成动作，不重启计时
                    track.AfterUp = afterUp;
                    return false;
                }
                float from = track.CurrentLevel >= 0f ? track.CurrentLevel : track.WasVolume;
                if (Math.Abs(from - track.WasVolume) < 0.005f)
                {
                    // 已经等于原值，直接完成
                    if (MuteCore.SetVolume(instanceId, track.WasVolume))
                    {
                        track.CurrentLevel = track.WasVolume;
                        track.Attenuated = false;
                    }
                }
                else
                {
                    track.Fade = FadeKind.Up;
                    track.FadeFrom = from;
                    track.FadeTo = track.WasVolume;
                    track.FadeStartUtc = DateTime.UtcNow;
                    track.AfterUp = afterUp;
                    SetFooterMessage(name + " 切回" + reason + " · 音量渐变恢复中");
                    return false; // 渐变完成后由 FinishFade 处理后续
                }
            }

            // 静音干预或无干预：立即解除（静音切换本身没有渐变，保持瞬时）
            bool ok = true;
            if (track.WeMuted)
            {
                if (MuteCore.SetMute(instanceId, track.WasMuted)) track.WeMuted = false;
                else ok = false;
            }

            if (ok)
            {
                if (afterUp == AfterUpKind.SetMute && !track.WasMuted)
                {
                    // 切回静音方式：恢复原值后立即设我们自己的静音
                    if (MuteCore.SetMute(instanceId, true))
                    {
                        track.WeMuted = true;
                        SetFooterMessage(track.AppName + " 切至后台 · 已静音");
                        return true;
                    }
                    return false;
                }
                _audioByUs.Remove(instanceId);
                SetFooterMessage(name + " 切回" + reason + " · 已恢复音量");
                return true;
            }
            // 恢复失败（COM 忙、设备切换等）：保留记录，下个 tick 重试
            AppLogger.Log(string.Format("Audio restore retry: {0}", instanceId), "WARN");
            return false;
        }

        // 恢复全部会话（暂停/移除/退出/注销时）：立即恢复，不走渐变。
        // restoreFailedToo=true 时（退出/注销）即使失败也清空记录。
        private void RestoreAudio(bool restoreFailedToo)
        {
            List<string> done = new List<string>();
            foreach (KeyValuePair<string, AudioTrack> kv in _audioByUs)
            {
                AudioTrack t = kv.Value;
                if (!t.Attenuated && !t.WeMuted && t.Fade == FadeKind.None)
                {
                    done.Add(kv.Key); // 只是记录了原始状态还没改过，直接丢
                    continue;
                }
                // 立即结束任何进行中的渐变
                t.Fade = FadeKind.None;
                if (t.Attenuated && MuteCore.SetVolume(kv.Key, t.WasVolume))
                {
                    t.Attenuated = false;
                    t.CurrentLevel = t.WasVolume;
                }
                if (t.WeMuted && MuteCore.SetMute(kv.Key, t.WasMuted)) t.WeMuted = false;
                if (!t.Attenuated && !t.WeMuted) done.Add(kv.Key);
            }
            for (int i = 0; i < done.Count; i++) _audioByUs.Remove(done[i]);
            if (restoreFailedToo)
            {
                // 强制退出路径：即使失败也清空记录，避免残留
                _audioByUs.Clear();
            }
        }

        private void RestoreAllMuted()
        {
            RestoreAudio(false);
        }

        private void RestoreAllLimits()
        {
            foreach (string exe in _limitApplied.Keys.ToList())
            {
                RestoreAppLimit(exe);
            }
            CpuQuotaCore.RestoreAll();
            SuspendCore.StopAll();
            _cpuApplied.Clear();
            _suspendWarned.Clear();
        }

        private void RestoreRuleLimits(string procName)
        {
            RestoreAppLimit(procName + ".exe");
            RestoreQuotaForProcess(procName);
            StopSuspendForProcess(procName);
            _suspendWarned.Remove(procName);
        }

        private void StopSuspendForPids(List<int> pids)
        {
            for (int i = 0; i < pids.Count; i++) SuspendCore.Stop(pids[i]);
        }

        private void StopSuspendForProcess(string procName)
        {
            try
            {
                Process[] ps = Process.GetProcessesByName(procName);
                for (int i = 0; i < ps.Length; i++)
                {
                    try { SuspendCore.Stop(ps[i].Id); }
                    finally { ps[i].Dispose(); }
                }
            }
            catch { }
        }

        private void RestoreQuotaForPid(int pid)
        {
            CpuQuotaCore.RestorePid(pid);
            _cpuApplied.Remove(pid);
        }

        private void RestoreQuotaForProcess(string procName)
        {
            try
            {
                Process[] ps = Process.GetProcessesByName(procName);
                for (int i = 0; i < ps.Length; i++)
                {
                    try { RestoreQuotaForPid(ps[i].Id); }
                    finally { ps[i].Dispose(); }
                }
            }
            catch { }
        }

        private bool RestoreAppLimit(string exe)
        {
            if (!_limitApplied.ContainsKey(exe)) return false;
            uint orig;
            _config.OriginalLimits.TryGetValue(exe, out orig);
            RtssBridge.SetLimit(exe, orig);
            _limitApplied.Remove(exe);
            return true;
        }

        private void RecoverFromCrash()
        {
            if (!_rtssAvailable) return;
            foreach (AppRule r in _rules.Values)
            {
                if (!r.LimitFps) continue;
                if (LimitMethods.Normalize(r.Method) != LimitMethods.Rtss) continue;
                string exe = r.Name + ".exe";
                uint orig;
                if (_config.OriginalLimits.TryGetValue(exe, out orig) && RtssBridge.GetLimit(exe) == (uint)r.Fps)
                {
                    AppLogger.Log(string.Format("Crash recovery: restoring {0} FPS to {1}", exe, orig));
                    RtssBridge.SetLimit(exe, orig);
                }
            }
        }

        private void SetPaused(bool paused)
        {
            _paused = paused;
            if (paused)
            {
                RestoreAllMuted();
                RestoreAllLimits();
                _inactiveSince.Clear();
                SetFooterMessage("已暂停监控，并恢复所有音量与帧率");
            }
            else
            {
                SetFooterMessage("已恢复后台监控");
            }
            UpdateHeaderAndTray();
        }

        // ---------------------------------------------------------------------
        // RTSS 自动启动 / 手动启动
        // ---------------------------------------------------------------------
        private void StartRtssAsync(bool notify)
        {
            if (!RtssBridge.IsInstalled) return;

            if (RtssBridge.IsRunning)
            {
                _rtssAvailable = true;
                UpdateRtssUi();
                if (notify) SetFooterMessage("RTSS 已在运行 · 后台限帧可用");
                return;
            }

            if (_rtssStartInFlight) return;
            _rtssStartInFlight = true;

            if (_startRtssBtn != null) _startRtssBtn.Enabled = false;
            if (notify) SetFooterMessage("正在启动 RTSS...");

            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = RtssBridge.EnsureRunning();
                AppLogger.Log(ok ? "RTSS started." : "RTSS start failed.", ok ? "INFO" : "WARN");
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke((Action)delegate
                        {
                            _rtssStartInFlight = false;
                            _rtssAvailable = ok;
                            if (_startRtssBtn != null) _startRtssBtn.Enabled = true;
                            UpdateRtssUi();
                            if (notify)
                            {
                                SetFooterMessage(ok
                                    ? "RTSS 已启动 · 后台限帧可用"
                                    : "RTSS 启动失败，请确认 RTSS 已正确安装");
                            }
                        });
                    }
                    else
                    {
                        _rtssAvailable = ok;
                        _rtssStartInFlight = false;
                    }
                }
                catch { _rtssStartInFlight = false; }
            });
        }

        private bool AnyRuleUsesRtss()
        {
            if (_rules == null) return false;
            foreach (AppRule r in _rules.Values)
            {
                if (r.LimitFps && LimitMethods.Normalize(r.Method) == LimitMethods.Rtss) return true;
            }
            return false;
        }

        private void UpdateRtssUi()
        {
            if (_rtssBadge == null || _startRtssBtn == null) return;

            bool installed = RtssBridge.IsInstalled;
            bool running = installed && RtssBridge.IsRunning;

            string text;
            Color color;
            bool showStartBtn;

            if (!installed)
            {
                text = "○ 未检测到 RTSS · 仅静音可用";
                color = Theme.TextMuted;
                showStartBtn = false;
                _rtssAvailable = false;
            }
            else if (running)
            {
                text = "● RTSS 已就绪 · 支持后台限帧";
                color = Theme.Green;
                showStartBtn = false;
                _rtssAvailable = true;
            }
            else
            {
                text = "○ RTSS 未运行 · 限帧不可用";
                color = Theme.Amber;
                showStartBtn = true;
                _rtssAvailable = false;

                // 掉线自动重启：仅在存在限帧规则、未暂停、且距上次尝试 ≥10s 时进行
                if (!_paused && !_exiting && AnyRuleUsesRtss() && !_rtssStartInFlight
                    && (DateTime.UtcNow - _lastRtssAutoRestart).TotalSeconds >= 10)
                {
                    _lastRtssAutoRestart = DateTime.UtcNow;
                    SetFooterMessage("检测到 RTSS 已退出 · 正在自动重启...");
                    StartRtssAsync(false);
                }
            }

            if (_rtssBadge.Text != text) _rtssBadge.Text = text;
            if (_rtssBadge.ForeColor != color) _rtssBadge.ForeColor = color;
            if (_startRtssBtn.Visible != showStartBtn) _startRtssBtn.Visible = showStartBtn;
        }

        private void UpdateHeaderAndTray()
        {
            bool throttling = _audioByUs.Count > 0 || _limitApplied.Count > 0;
            string key = string.Format("{0}|{1}|{2}", _paused, throttling, _rules.Count);
            if (key == _lastTrayKey) return;
            _lastTrayKey = key;

            if (_paused)
            {
                _tray.Icon = _iconPaused;
                _tray.Text = "GameMuteHelper · 已暂停";
                _pauseBtn.Text = "恢复";
                _miPause.Text = "恢复监控";
                _statusBadge.Text = "⏸ 已暂停监控";
                _statusBadge.ForeColor = Theme.Amber;
            }
            else
            {
                _tray.Icon = throttling ? _iconMuted : _iconActive;
                string tip = throttling
                    ? string.Format("GameMuteHelper · 后台节流中 ({0})", _rules.Count)
                    : string.Format("GameMuteHelper · 监控中 ({0})", _rules.Count);
                _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
                _pauseBtn.Text = "暂停";
                _miPause.Text = "暂停监控";
                _statusBadge.Text = _isAdmin ? "● 监控中 · 管理员" : "● 监控中 · 标准模式";
                _statusBadge.ForeColor = throttling ? Theme.Amber : Theme.Green;
            }
        }

        private void ShowMainWindow()
        {
            ShowInTaskbar = true;
            if (!Visible)
            {
                ScanRunningProcesses();
                RebuildCardList();
                Show();
            }
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            AppLogger.Log("Session ending; restoring audio and RTSS limits.");
            RestoreAudio(true);
            RestoreAllLimits();
        }

        private void ExitApplication()
        {
            if (_exiting) return;
            _exiting = true;
            AppLogger.Log("Exiting GameMuteHelper.");
            try { SystemEvents.SessionEnding -= OnSessionEnding; } catch { }
            try { _monitorTimer.Stop(); } catch { }
            try { RestoreAudio(true); } catch { }
            try { RestoreAllLimits(); } catch { }
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            Application.Exit();
        }
    }
}
