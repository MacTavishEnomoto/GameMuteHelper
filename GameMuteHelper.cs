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
[assembly: System.Reflection.AssemblyDescription("后台失焦自动静音/降音量与 CPU 配额限帧助手")]
[assembly: System.Reflection.AssemblyProduct("GameMuteHelper")]
[assembly: System.Reflection.AssemblyVersion("3.6.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("3.6.0.0")]

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
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs ue)
                {
                    try { AppLogger.Log("Fatal: " + (ue != null && ue.ExceptionObject != null ? ue.ExceptionObject.ToString() : "?"), "ERROR"); } catch { }
                    try { SuspendCore.StopAll(); } catch { }
                    try { CpuQuotaCore.RestoreAll(); } catch { }
                };
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs te)
                {
                    try { AppLogger.Log("UI error: " + (te != null && te.Exception != null ? te.Exception.ToString() : "?"), "ERROR"); } catch { }
                    try { SuspendCore.StopAll(); } catch { }
                };
                // 进程正常退出（Environment.Exit 等）时也确保恢复，参考 GamePauser 的做法
                AppDomain.CurrentDomain.ProcessExit += delegate
                {
                    try { SuspendCore.StopAll(); } catch { }
                    try { CpuQuotaCore.RestoreAll(); } catch { }
                };

                bool debugPriv = ProcessPrivileges.EnableDebugPrivilege();
                AppLogger.Init();
                AppLogger.Log(string.Format("Starting GameMuteHelper v3.6 (Admin={0}, Background={1}, 64Bit={2}, SeDebug={3})",
                    IsAdministrator(), background, Environment.Is64BitProcess, debugPriv));

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
    // 进程特权（SeDebugPrivilege）— 参考 Process Governor / System Informer
    // =========================================================================
    internal static class ProcessPrivileges
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LookupPrivilegeValue(string systemName, string name, out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
            ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID_AND_ATTRIBUTES Privileges; }

        private const uint TokenAdjustPrivileges = 0x0020;
        private const uint TokenQuery = 0x0008;
        private const uint SePrivilegeEnabled = 0x0002;

        public static bool EnableDebugPrivilege()
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token)) return false;

                LUID luid;
                if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out luid)) return false;

                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Privileges.Luid = luid;
                tp.Privileges.Attributes = SePrivilegeEnabled;

                if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
                return Marshal.GetLastWin32Error() == 0; // 1300 = ERROR_NOT_ALL_ASSIGNED
            }
            catch { return false; }
            finally { if (token != IntPtr.Zero) CloseHandle(token); }
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
        private static readonly object LogSync = new object();

        public static void Init()
        {
            lock (LogSync)
            {
                DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameMuteHelper");
                if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);
                ConfigPath = Path.Combine(DataDir, "config.json");
                LogPath = Path.Combine(DataDir, "app.log");
            }
        }

        public static void Log(string msg, string level)
        {
            lock (LogSync)
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
        }

        public static void Log(string msg) { Log(msg, "INFO"); }
    }

    internal static class LimitMethods
    {
        // Rtss 常量保留仅用于旧配置迁移（迁移到 Cpu），不再提供 UI 与运行时支持
        private const string RtssLegacy = "rtss";
        public const string Cpu = "cpu";
        public const string Suspend = "suspend";

        public static string Normalize(string m)
        {
            if (string.Equals(m, Cpu, StringComparison.OrdinalIgnoreCase)) return Cpu;
            if (string.Equals(m, Suspend, StringComparison.OrdinalIgnoreCase)) return Suspend;
            return Cpu; // 默认/旧 rtss 配置一律迁移为 CPU 配额
        }

        public static bool IsCpu(string m) { return Normalize(m) == Cpu; }
        public static bool IsSuspend(string m) { return Normalize(m) == Suspend; }

        public static int Clamp(string m, int v)
        {
            int max = IsCpu(m) ? 100 : 240;
            if (v < 1) v = 1;
            if (v > max) v = max;
            return v;
        }

        public static string ShortLabel(string m)
        {
            return IsSuspend(m) ? "挂起" : "CPU";
        }

        public static string DisplayValue(string m, int v)
        {
            return IsCpu(m) ? v + "%" : v.ToString();
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
        [DataMember(EmitDefaultValue = false)] public bool? SuspendFallbackCpu { get; set; }
        [DataMember(EmitDefaultValue = false)] public string AudioMode { get; set; }
        [DataMember(EmitDefaultValue = false)] public int? AudioVolume { get; set; }

        public AppRule()
        {
            Name = "";
            Method = LimitMethods.Cpu;
            Mute = true;
            LimitFps = false;
            Fps = 5;
            SuspendFallbackCpu = true;
            AudioMode = AudioModes.Mute;
            AudioVolume = AudioModes.DefaultVolume;
        }
    }

    [DataContract]
    internal class ConfigData
    {
        [DataMember] public string Version { get; set; }
        [DataMember] public int DebounceMs { get; set; }
        [DataMember] public int FadeMs { get; set; }
        [DataMember] public List<AppRule> Apps { get; set; }

        [DataMember(EmitDefaultValue = false)] public List<string> Watch { get; set; }
        [DataMember(EmitDefaultValue = false)] public int? TargetFps { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool? LimitFps { get; set; }

        public ConfigData()
        {
            Version = "3.6";
            DebounceMs = 500;
            FadeMs = 500;
            Apps = new List<AppRule>();
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

                if (data.DebounceMs < 0) data.DebounceMs = 0;
                if (data.DebounceMs > 5000) data.DebounceMs = 5000;
                if (data.FadeMs < 0) data.FadeMs = 0;          // 0 = 瞬间切换（无渐变）
                if (data.FadeMs > 5000) data.FadeMs = 5000;

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
                    // 旧配置的 rtss 方式在 Normalize 里统一迁移为 CPU 配额
                    r.Method = LimitMethods.Normalize(r.Method);
                    r.Fps = LimitMethods.Clamp(r.Method, r.Fps);

                    // 3.4：每应用音频模式（完全静音 / 降低音量），旧配置默认静音
                    r.AudioMode = AudioModes.Normalize(r.AudioMode);
                    r.AudioVolume = AudioModes.ClampVolume(r.AudioVolume.HasValue ? r.AudioVolume.Value : AudioModes.DefaultVolume);

                    // 3.6：挂起失败时默认自动回退到 CPU 配额
                    if (!r.SuspendFallbackCpu.HasValue) r.SuspendFallbackCpu = true;
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
                data.Version = "3.6";
                data.Watch = null;
                data.TargetFps = null;
                data.LimitFps = null;

                DataContractJsonSerializerSettings settings = new DataContractJsonSerializerSettings();
                settings.UseSimpleDictionaryFormat = true;
                DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(ConfigData), settings);
                using (MemoryStream ms = new MemoryStream())
                {
                    ser.WriteObject(ms, data);
                    byte[] bytes = ms.ToArray();
                    string tmpPath = AppLogger.ConfigPath + ".tmp";
                    File.WriteAllBytes(tmpPath, bytes);
                    if (File.Exists(AppLogger.ConfigPath))
                    {
                        try
                        {
                            File.Replace(tmpPath, AppLogger.ConfigPath, null);
                        }
                        catch
                        {
                            File.Copy(tmpPath, AppLogger.ConfigPath, true);
                            try { File.Delete(tmpPath); } catch { }
                        }
                    }
                    else
                    {
                        File.Move(tmpPath, AppLogger.ConfigPath);
                    }
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
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
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

        private static void CollectDeviceSessions(IMMDevice device, List<AudioSessionInfo> result)
        {
            IAudioSessionManager2 mgr = null;
            IAudioSessionEnumerator se = null;
            try
            {
                object obj;
                Guid iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, 0x17, IntPtr.Zero, out obj) != 0 || obj == null) return;
                mgr = obj as IAudioSessionManager2;
                if (mgr == null) return;
                if (mgr.GetSessionEnumerator(out se) != 0 || se == null) return;

                int count;
                se.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl ctl = null;
                    try
                    {
                        if (se.GetSession(i, out ctl) != 0 || ctl == null) continue;
                        int state;
                        if (ctl.GetState(out state) == 0 && state == 2) continue; // 跳过 AudioSessionStateExpired
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
            }
        }

        public static List<AudioSessionInfo> GetSessions()
        {
            List<AudioSessionInfo> result = new List<AudioSessionInfo>();
            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection collection = null;

            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                // 枚举所有活跃播放端点（兼容多设备/游戏单独指定耳机输出）
                if (enumerator.EnumAudioEndpoints(0, 1, out collection) == 0 && collection != null)
                {
                    uint devCount;
                    if (collection.GetCount(out devCount) == 0 && devCount > 0)
                    {
                        for (uint d = 0; d < devCount; d++)
                        {
                            IMMDevice device = null;
                            try
                            {
                                if (collection.Item(d, out device) == 0 && device != null)
                                    CollectDeviceSessions(device, result);
                            }
                            finally { SafeRelease(device); }
                        }
                        return result;
                    }
                }

                // 回退到默认多媒体播放设备
                IMMDevice defDevice = null;
                try
                {
                    if (enumerator.GetDefaultAudioEndpoint(0, 1, out defDevice) == 0 && defDevice != null)
                        CollectDeviceSessions(defDevice, result);
                }
                finally { SafeRelease(defDevice); }
            }
            catch { }
            finally
            {
                SafeRelease(collection);
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

        private static bool ApplyToDeviceSession(IMMDevice device, string instanceId, Func<ISimpleAudioVolume, bool> action, out bool found)
        {
            found = false;
            IAudioSessionManager2 mgr = null;
            IAudioSessionEnumerator se = null;
            try
            {
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
                        found = true;
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
            }
            return false;
        }

        // 按 InstanceId 在所有活跃播放端点中找到会话并对 ISimpleAudioVolume 执行操作
        private static bool ApplyToSession(string instanceId, Func<ISimpleAudioVolume, bool> action)
        {
            if (string.IsNullOrEmpty(instanceId)) return false;
            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection collection = null;

            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                if (enumerator.EnumAudioEndpoints(0, 1, out collection) == 0 && collection != null)
                {
                    uint devCount;
                    if (collection.GetCount(out devCount) == 0 && devCount > 0)
                    {
                        for (uint d = 0; d < devCount; d++)
                        {
                            IMMDevice device = null;
                            try
                            {
                                if (collection.Item(d, out device) != 0 || device == null) continue;
                                bool found;
                                bool ok = ApplyToDeviceSession(device, instanceId, action, out found);
                                if (found) return ok;
                            }
                            finally { SafeRelease(device); }
                        }
                        return false;
                    }
                }

                IMMDevice defDevice = null;
                try
                {
                    if (enumerator.GetDefaultAudioEndpoint(0, 1, out defDevice) == 0 && defDevice != null)
                    {
                        bool found;
                        return ApplyToDeviceSession(defDevice, instanceId, action, out found);
                    }
                }
                finally { SafeRelease(defDevice); }
            }
            catch { return false; }
            finally
            {
                SafeRelease(collection);
                SafeRelease(enumerator);
            }
            return false;
        }
    }

    // =========================================================================
    // 进程亲缘判定：模拟器/游戏的主窗口进程与音频进程常是两个不同进程
    // （如 MuMuPlayer 持窗口、MuMuNxDevice 出声音）。前台判定若只比对
    // PID，点窗口时永远判定「在后台」，降音量就永远不恢复。
    // 这里通过父进程链与可执行文件目录判定「是否同一应用家族」。
    // =========================================================================
    internal static class ProcessKinship
    {
        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr hProcess, int pic,
            ref ProcessBasicInformation pbi, int cb, out int pSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder exeName, ref int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2a;
            public IntPtr Reserved2b;
            public IntPtr UniquePid;
            public IntPtr InheritedFromUniquePid; // PPID
        }

        private const uint ProcessQueryLimitedInformation = 0x1000;

        // 进程 PID → 路径与父 PID 缓存（避免每 tick 重复查询）
        private static readonly Dictionary<int, string> ExePathByPid = new Dictionary<int, string>();
        private static readonly Dictionary<int, int> ParentByPid = new Dictionary<int, int>();
        private static DateTime _cacheClearedUtc = DateTime.UtcNow;

        private static void EnsureCacheFresh()
        {
            if ((DateTime.UtcNow - _cacheClearedUtc).TotalSeconds > 30)
            {
                ExePathByPid.Clear();
                ParentByPid.Clear();
                _cacheClearedUtc = DateTime.UtcNow;
            }
        }

        public static int GetParentPid(int pid)
        {
            if (pid <= 0) return 0;
            EnsureCacheFresh();
            int cached;
            if (ParentByPid.TryGetValue(pid, out cached)) return cached;

            int ppid = 0;
            IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    ProcessBasicInformation pbi = new ProcessBasicInformation();
                    int size;
                    if (NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf(typeof(ProcessBasicInformation)), out size) == 0)
                    {
                        ppid = (int)pbi.InheritedFromUniquePid;
                    }
                }
                catch { }
                finally { CloseHandle(h); }
            }
            ParentByPid[pid] = ppid;
            return ppid;
        }

        public static string GetExePath(int pid)
        {
            if (pid <= 0) return "";
            EnsureCacheFresh();
            string cached;
            if (ExePathByPid.TryGetValue(pid, out cached)) return cached;
            string path = "";
            IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    StringBuilder sb = new StringBuilder(1024);
                    int len = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref len))
                        path = sb.ToString();
                }
                catch { }
                finally { CloseHandle(h); }
            }
            ExePathByPid[pid] = path;
            return path;
        }

        private static bool IsSharedSystemOrRootDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return true;
            string d = dir.TrimEnd('\\', '/');
            // 磁盘根目录如 C: / D:
            if (d.Length <= 2 && d.EndsWith(":")) return true;
            string winDir = (Environment.GetFolderPath(Environment.SpecialFolder.Windows) ?? "").TrimEnd('\\', '/');
            if (winDir.Length > 0 &&
                (d.Equals(winDir, StringComparison.OrdinalIgnoreCase)
                 || d.Equals(Path.Combine(winDir, "System32"), StringComparison.OrdinalIgnoreCase)
                 || d.Equals(Path.Combine(winDir, "SysWOW64"), StringComparison.OrdinalIgnoreCase)))
                return true;

            string pf = (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) ?? "").TrimEnd('\\', '/');
            string pf86 = (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) ?? "").TrimEnd('\\', '/');
            string desktop = (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) ?? "").TrimEnd('\\', '/');
            string localApp = (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) ?? "").TrimEnd('\\', '/');
            if ((pf.Length > 0 && d.Equals(pf, StringComparison.OrdinalIgnoreCase))
                || (pf86.Length > 0 && d.Equals(pf86, StringComparison.OrdinalIgnoreCase))
                || (desktop.Length > 0 && d.Equals(desktop, StringComparison.OrdinalIgnoreCase))
                || (localApp.Length > 0 && d.Equals(localApp, StringComparison.OrdinalIgnoreCase)))
                return true;

            return false;
        }

        // 判定窗口进程(fgPid)与接管进程(pid)是否属于同一应用：
        // 同进程 / 同可执行文件 / 同独立安装目录 / 父链上互见（最多 4 级）
        public static bool SameApp(int fgPid, int pid)
        {
            if (fgPid <= 0 || pid <= 0) return false;
            if (fgPid == pid) return true;

            string fgPath = GetExePath(fgPid);
            string myPath = GetExePath(pid);
            if (fgPath.Length > 0 && myPath.Length > 0)
            {
                if (string.Equals(fgPath, myPath, StringComparison.OrdinalIgnoreCase)) return true;
                string fgDir = Path.GetDirectoryName(fgPath) ?? "";
                string myDir = Path.GetDirectoryName(myPath) ?? "";
                // MuMuPlayer\shell 与 MuMuPlayer\nx_main 同属一个安装根；
                // 但排除 System32 / Program Files / Desktop 等公共目录，避免无关程序误判同应用
                string fgRoot = RootOf(fgDir);
                string myRoot = RootOf(myDir);
                if (fgRoot.Length > 0 && !IsSharedSystemOrRootDir(fgRoot)
                    && string.Equals(fgRoot, myRoot, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // 父进程链互见：fg 的祖先里有 pid，或反过来
            if (OnAncestry(fgPid, pid) || OnAncestry(pid, fgPid)) return true;
            return false;
        }

        private static bool OnAncestry(int childPid, int ancestorPid)
        {
            int cur = childPid;
            for (int i = 0; i < 4 && cur > 0; i++)
            {
                cur = GetParentPid(cur);
                if (cur == ancestorPid) return true;
            }
            return false;
        }

        // 取安装根目录：向上最多两级，忽略常见子目录名
        private static string RootOf(string dir)
        {
            if (dir.Length == 0) return "";
            string d = dir.TrimEnd('\\', '/');
            for (int i = 0; i < 2; i++)
            {
                string parent = Path.GetDirectoryName(d);
                if (string.IsNullOrEmpty(parent)) break;
                string leaf = Path.GetFileName(d);
                // 子目录名是常见结构词时继续上溯
                if (leaf.Equals("shell", StringComparison.OrdinalIgnoreCase)
                    || leaf.Equals("nx_main", StringComparison.OrdinalIgnoreCase)
                    || leaf.Equals("nx_device", StringComparison.OrdinalIgnoreCase)
                    || leaf.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || leaf.Equals("win", StringComparison.OrdinalIgnoreCase)
                    || leaf.Equals("x64", StringComparison.OrdinalIgnoreCase)
                    || leaf.Equals("bin64", StringComparison.OrdinalIgnoreCase))
                {
                    d = parent;
                }
                else break;
            }
            return d;
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
        private static readonly object Sync = new object();

        public static bool SetRate(int pid, int percent)
        {
            if (pid <= 0) return false;
            if (percent < 1) percent = 1;
            if (percent > 100) percent = 100;

            lock (Sync)
            {
                if (percent >= 100)
                {
                    RestorePidUnlocked(pid);
                    return true;
                }

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
        }

        private static void RestorePidUnlocked(int pid)
        {
            IntPtr job;
            if (!Jobs.TryGetValue(pid, out job)) return;
            ApplyRate(job, 100); // 先解除限制，再销毁 Job
            CloseHandle(job);
            Jobs.Remove(pid);
        }

        public static void RestorePid(int pid)
        {
            lock (Sync) { RestorePidUnlocked(pid); }
        }

        public static void RestoreAll()
        {
            lock (Sync)
            {
                foreach (KeyValuePair<int, IntPtr> kv in Jobs) { ApplyRate(kv.Value, 100); }
                foreach (KeyValuePair<int, IntPtr> kv in Jobs) { CloseHandle(kv.Value); }
                Jobs.Clear();
            }
        }

        public static void Cleanup(HashSet<int> livePids)
        {
            lock (Sync)
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
                for (int i = 0; i < dead.Count; i++) RestorePidUnlocked(dead[i]);
            }
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
        private const uint ProcessQueryLimitedInformation = 0x1000;

        private class Worker
        {
            public int Pid;
            public IntPtr Handle;
            public int Fps;
            public ManualResetEvent Stop;
            public Thread Thread;
            public volatile bool Failed;
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
            fps = LimitMethods.Clamp(LimitMethods.Suspend, fps);

            lock (Sync)
            {
                Worker w;
                if (Workers.TryGetValue(pid, out w))
                {
                    if (!w.Failed && w.Fps == fps) return true;
                    if (w.Failed && DateTime.UtcNow < w.RetryAfterUtc) return false;
                    StopUnlocked(pid);
                }

                IntPtr h = OpenProcess(ProcessSuspendResume | Synchronize | ProcessQueryLimitedInformation, false, pid);
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

        public static bool HasActiveWorkers()
        {
            lock (Sync)
            {
                foreach (Worker w in Workers.Values)
                {
                    if (!w.Failed) return true;
                }
                return false;
            }
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
            using (Process p = Process.Start(psi))
            {
                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(); } catch { }
                    return -1;
                }
                return p.ExitCode;
            }
        }

        private static string RunSchtasksOutput(string args, out int exitCode)
        {
            exitCode = -1;
            ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            using (Process p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(); } catch { }
                    return "";
                }
                exitCode = p.ExitCode;
                return output ?? "";
            }
        }

        public static bool IsEnabled()
        {
            try { return RunSchtasks("/Query /TN \"" + TaskName + "\"") == 0; }
            catch { return false; }
        }

        // 读取计划任务当前指向的可执行文件路径（解析标准 XML 的 <Command> 节点，不受系统语言影响）
        public static string GetRegisteredExePath()
        {
            try
            {
                int code;
                string xml = RunSchtasksOutput("/Query /TN \"" + TaskName + "\" /XML", out code);
                if (code != 0 || string.IsNullOrEmpty(xml)) return "";
                int s = xml.IndexOf("<Command>", StringComparison.OrdinalIgnoreCase);
                if (s < 0) return "";
                s += "<Command>".Length;
                int e = xml.IndexOf("</Command>", s, StringComparison.OrdinalIgnoreCase);
                if (e < 0) return "";
                return xml.Substring(s, e - s).Trim().Trim('"');
            }
            catch { return ""; }
        }

        // 若已开启开机自启，但计划任务仍指向旧版本/旧目录的 EXE，自动更新到当前 EXE 路径
        public static bool SyncPathIfEnabled(bool isAdmin)
        {
            try
            {
                if (!IsEnabled()) return false;
                string currentExe = (Application.ExecutablePath ?? "").Trim().Trim('"');
                string registered = GetRegisteredExePath();
                if (string.IsNullOrEmpty(currentExe)) return false;
                if (string.Equals(registered, currentExe, StringComparison.OrdinalIgnoreCase)) return false;

                bool ok = SetEnabled(true, isAdmin);
                AppLogger.Log(string.Format("Autostart path synced: '{0}' -> '{1}' (ok={2})",
                    registered, currentExe, ok), ok ? "INFO" : "WARN");
                return ok;
            }
            catch (Exception ex)
            {
                AppLogger.Log("Autostart sync error: " + ex.Message, "WARN");
                return false;
            }
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

        // Flat Minimalist Neutral Palette (Linear / Notion / Win11 inspired)
        public static readonly Color Bg = Color.FromArgb(250, 250, 250);
        public static readonly Color Surface = Color.FromArgb(255, 255, 255);
        public static readonly Color CardBg = Color.FromArgb(255, 255, 255);
        public static readonly Color CardHover = Color.FromArgb(248, 248, 249);
        public static readonly Color InputBg = Color.FromArgb(244, 244, 245);
        public static readonly Color InputHover = Color.FromArgb(232, 232, 236);
        public static readonly Color Border = Color.FromArgb(234, 234, 236);
        public static readonly Color BorderHover = Color.FromArgb(209, 209, 214);

        public static readonly Color Text = Color.FromArgb(24, 24, 27);
        public static readonly Color TextMuted = Color.FromArgb(113, 113, 122);
        public static readonly Color TextDim = Color.FromArgb(161, 161, 170);

        public static readonly Color Accent = Color.FromArgb(24, 24, 27);
        public static readonly Color AccentHover = Color.FromArgb(52, 52, 58);
        public static readonly Color AccentSoft = Color.FromArgb(244, 244, 245);

        public static readonly Color Green = Color.FromArgb(22, 163, 74);
        public static readonly Color GreenSoft = Color.FromArgb(240, 253, 244);

        public static readonly Color Amber = Color.FromArgb(180, 83, 9);
        public static readonly Color AmberSoft = Color.FromArgb(254, 243, 199);

        public static readonly Color Red = Color.FromArgb(220, 38, 38);
        public static readonly Color RedSoft = Color.FromArgb(254, 242, 242);

        public static readonly Font FontBrand = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold);
        public static readonly Font FontHeading = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
        public static readonly Font FontBody = new Font("Microsoft YaHei UI", 8.8f, FontStyle.Regular);
        public static readonly Font FontBodyBold = new Font("Microsoft YaHei UI", 8.8f, FontStyle.Bold);
        public static readonly Font FontSmall = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular);
        public static readonly Font FontSmallBold = new Font("Microsoft YaHei UI", 8f, FontStyle.Bold);

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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static Icon CreateAppIcon(Color accentColor, bool mutedSlash)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (GraphicsPath bgPath = RoundedRect(new Rectangle(2, 2, 28, 28), 8))
                using (SolidBrush bg = new SolidBrush(accentColor))
                using (SolidBrush fg = new SolidBrush(Color.White))
                using (Pen wave = new Pen(Color.White, 2.2f))
                {
                    g.FillPath(bg, bgPath);

                    Point[] pts = new Point[]
                    {
                        new Point(7, 13), new Point(12, 13), new Point(17, 8),
                        new Point(17, 24), new Point(12, 19), new Point(7, 19)
                    };
                    g.FillPolygon(fg, pts);

                    wave.StartCap = LineCap.Round;
                    wave.EndCap = LineCap.Round;
                    if (mutedSlash)
                    {
                        g.DrawLine(wave, 20, 13, 25, 19);
                        g.DrawLine(wave, 25, 13, 20, 19);
                    }
                    else
                    {
                        g.DrawArc(wave, 15, 11, 8, 10, -50, 100);
                    }
                }
                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(hIcon))
                    {
                        return (Icon)tmp.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
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
            Size = new Size(Theme.S(34), Theme.S(18));
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
                trackColor = Color.FromArgb(234, 234, 236);
            else if (_checked)
                trackColor = _hovered ? Theme.AccentHover : Theme.Accent;
            else
                trackColor = _hovered ? Color.FromArgb(196, 196, 202) : Color.FromArgb(216, 216, 222);

            using (GraphicsPath p = Theme.RoundedRect(track, track.Height / 2))
            using (SolidBrush br = new SolidBrush(trackColor))
            {
                g.FillPath(br, p);
            }

            int pad = Math.Max(2, Theme.S(3));
            int knobSize = Height - pad * 2;
            int knobX = _checked ? (Width - knobSize - pad) : pad;
            Rectangle knob = new Rectangle(knobX, pad, knobSize, knobSize);
            Color knobColor = Enabled ? Color.White : Color.FromArgb(248, 248, 250);
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
        private int _radius = 6;

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

        protected override void OnMouseEnter(EventArgs e) { if (Enabled) { _hovered = true; Invalidate(); } base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (Enabled && e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { if (_pressed) { _pressed = false; Invalidate(); } base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Rectangle r = new Rectangle(1, 1, Width - 3, Height - 3);
            Color fill = Color.Transparent;
            Color border = Color.Transparent;
            Color fg = Theme.Text;

            if (!Enabled)
            {
                if (_style == PillStyle.Subtle)
                {
                    fill = Theme.Bg;
                    border = Theme.Border;
                }
                fg = Theme.TextDim;
            }
            else if (_style == PillStyle.Accent)
            {
                fill = _pressed ? Color.FromArgb(12, 12, 14) : (_hovered ? Theme.AccentHover : Theme.Accent);
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
            if (e.Button == MouseButtons.Left) SelectedIndex = (e.X < Width / 2) ? 0 : 1;
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Rectangle outer = new Rectangle(1, 1, Width - 3, Height - 3);
            using (GraphicsPath p = Theme.RoundedRect(outer, Theme.S(7)))
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
                using (GraphicsPath p = Theme.RoundedRect(r, Theme.S(5)))
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
        private string _lastQuery = "";
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
            Cursor = Cursors.IBeam;
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
            _tb.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape && _tb.Text != Placeholder && _tb.Text.Length > 0)
                {
                    e.SuppressKeyPress = true;
                    _tb.Text = "";
                }
            };
            _tb.TextChanged += (s, e) =>
            {
                string q = Query;
                if (string.Equals(q, _lastQuery, StringComparison.Ordinal)) return;
                _lastQuery = q;
                if (QueryChanged != null) QueryChanged(this, EventArgs.Empty);
            };
            Click += (s, e) => _tb.Focus();
            Controls.Add(_tb);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(1, 1, Width - 3, Height - 3);
            using (GraphicsPath p = Theme.RoundedRect(r, Theme.S(6)))
            using (SolidBrush br = new SolidBrush(Theme.Surface))
            using (Pen bd = new Pen(_focused ? Theme.Accent : Theme.Border, _focused ? 1.2f : 1f))
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
            Size = new Size(Theme.S(60), Theme.S(26));
            Font = Theme.FontSmallBold;
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
            TabStop = false;

            _repeatTimer = new System.Windows.Forms.Timer();
            _repeatTimer.Interval = 350;
            _repeatTimer.Tick += delegate
            {
                if (_repeatTimer.Interval != 100) _repeatTimer.Interval = 100;
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
            int old = _value;
            Value = _value + delta;
            if (_value != old && EditCommitted != null) EditCommitted(this, EventArgs.Empty);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            if (!Enabled) { base.OnMouseEnter(e); return; }
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
            if (_repeatTimer != null) _repeatTimer.Stop();
            if (!IsDisposed) Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!Enabled) { base.OnMouseMove(e); return; }
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
            if (!Enabled || e.Button != MouseButtons.Left) return;
            Focus();

            if (UpRect().Contains(e.Location))
            {
                _pressedUp = true;
                Bump(_step);
                if (!IsDisposed && _repeatTimer != null)
                {
                    _repeatTimer.Interval = 350;
                    _repeatTimer.Start();
                    Invalidate();
                }
            }
            else if (DownRect().Contains(e.Location))
            {
                _pressedDown = true;
                Bump(-_step);
                if (!IsDisposed && _repeatTimer != null)
                {
                    _repeatTimer.Interval = 350;
                    _repeatTimer.Start();
                    Invalidate();
                }
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
            if (_repeatTimer != null) _repeatTimer.Stop();
            if (wasRepeat && !IsDisposed) Invalidate();
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
            bool done = false; // 一次性守卫：dispose 过程中 LostFocus 会再次触发
            commit = delegate
            {
                if (done) return;
                done = true;
                int v;
                if (!int.TryParse(tb.Text.Trim().TrimEnd('%'), out v)) v = _value;
                int old = _value;
                tb.Dispose();
                _editing = false;
                Controls.Remove(tb);
                Value = v;
                Invalidate();
                if (_value != old && EditCommitted != null) EditCommitted(this, EventArgs.Empty);
            };
            cancel = delegate
            {
                if (done) return;
                done = true;
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

            // 点击编辑框以外的任何地方（包括不可聚焦的卡片背景/标签/禁用控件）
            // 都自动提交：LostFocus 只在焦点移到可聚焦控件时触发，
            // 点卡片背景这类不可聚焦区域时不会触发，所以用消息过滤器补位
            EditCommitFilter clickFilter = new EditCommitFilter(tb, delegate { commit(null, EventArgs.Empty); });
            Application.AddMessageFilter(clickFilter);

            // 切到其他应用或点窗口标题栏时也提交
            Form hostForm = FindForm();
            EventHandler onDeactivate = delegate { commit(null, EventArgs.Empty); };
            if (hostForm != null) hostForm.Deactivate += onDeactivate;

            tb.Disposed += (s, e) =>
            {
                Application.RemoveMessageFilter(clickFilter);
                if (hostForm != null) hostForm.Deactivate -= onDeactivate;
            };
        }

        // 编辑期间：任何落在编辑框之外的鼠标按下都触发提交（消息不拦截）
        private class EditCommitFilter : IMessageFilter
        {
            private readonly Control _editor;
            private readonly Action _commit;

            public EditCommitFilter(Control editor, Action commit)
            {
                _editor = editor;
                _commit = commit;
            }

            public bool PreFilterMessage(ref Message m)
            {
                const int WM_LBUTTONDOWN = 0x0201;
                const int WM_NCLBUTTONDOWN = 0x00A1;
                if (m.Msg != WM_LBUTTONDOWN && m.Msg != WM_NCLBUTTONDOWN) return false;
                if (_editor.IsDisposed || !_editor.Focused) return false;

                // 点在编辑框内部：交给 TextBox 正常处理
                if (m.Msg == WM_LBUTTONDOWN)
                {
                    Control target = Control.FromHandle(m.HWnd);
                    if (target != null && (target == _editor || _editor.Contains(target)))
                    {
                        Point pt = target.PointToClient(Cursor.Position);
                        if (target.ClientRectangle.Contains(pt)) return false;
                    }
                }

                // 其他任何地方：提交数值，不拦截消息
                try { _commit(); } catch { }
                return false;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Rectangle r = new Rectangle(1, 1, Width - 3, Height - 3);
            Color fill = Enabled ? Theme.Surface : Theme.Bg;
            Color bdColor = !Enabled ? Theme.Border : (_hovered ? Theme.BorderHover : Theme.Border);
            using (GraphicsPath p = Theme.RoundedRect(r, Theme.S(6)))
            using (SolidBrush br = new SolidBrush(fill))
            using (Pen bd = new Pen(bdColor, 1f))
            {
                g.FillPath(br, p);
                g.DrawPath(bd, p);
            }

            // 数字区与箭头区之间的极细分隔线
            int sepX = Width - ArrowWidth() - Theme.S(7);
            using (Pen sp = new Pen(Theme.Border, 1f))
            {
                g.DrawLine(sp, sepX, Theme.S(5), sepX, Height - Theme.S(6));
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
            Color c = !Enabled ? Theme.TextDim : (pressed ? Theme.Accent : (hovered ? Theme.AccentHover : Theme.TextMuted));
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
        public bool IsAlreadyManaged { get; private set; }

        private string _windowTitle;
        private string _statusText;
        private Color _statusFg;
        private Color _statusBg;
        private Image _appIcon;
        private bool _hovered;
        private ToolTip _tips;

        private ToggleSwitch _muteToggle;
        private ToggleSwitch _fpsToggle;
        private SpinnerInput _fpsSpinner;
        private SpinnerInput _volumeSpinner;
        private PillButton _actionBtn;
        private PillButton _methodBtn;
        private PillButton _audioBtn;
        private ContextMenuStrip _methodMenu;
        private ContextMenuStrip _audioMenu;
        private ToolStripMenuItem _fallbackMenuItem;
        private string _method = LimitMethods.Cpu;
        private int _value = 5;
        private bool _suspendFallbackCpu = true;
        private string _audioMode = AudioModes.Mute;
        private int _volumePercent = AudioModes.DefaultVolume;

        public event Action<string, bool, int, string, bool> LimitRuleChanged;
        public event Action<string, bool, string, int> AudioRuleChanged;
        public event Action<string> RemoveClicked;
        public event Action<string> AddClicked;

        public AppCardControl(string procName, string title, Image icon, bool isManagedView,
                              AppRule rule, bool alreadyManaged, ToolTip tips,
                              string method, bool suspendFallbackCpu)
        {
            ProcessName = procName;
            _windowTitle = title ?? "";
            _appIcon = icon;
            IsManagedView = isManagedView;
            IsAlreadyManaged = alreadyManaged;
            _tips = tips;
            _method = LimitMethods.Normalize(method);
            _suspendFallbackCpu = suspendFallbackCpu;
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

            Height = Theme.S(62);
            BackColor = Theme.Bg;

            if (isManagedView && rule != null)
            {
                BuildManagedControls(rule, tips);
            }
            else
            {
                BuildRunningControls(alreadyManaged);
            }

            EventHandler checkLeave = (s, e) =>
            {
                if (!IsDisposed && !ClientRectangle.Contains(PointToClient(Cursor.Position)))
                {
                    if (_hovered) { _hovered = false; Invalidate(); }
                }
            };
            MouseEnter += (s, e) => { if (!_hovered) { _hovered = true; Invalidate(); } };
            MouseLeave += checkLeave;
            for (int i = 0; i < Controls.Count; i++)
            {
                Controls[i].MouseEnter += (s, e) => { if (!_hovered) { _hovered = true; Invalidate(); } };
                Controls[i].MouseLeave += checkLeave;
            }
        }

        private void BuildManagedControls(AppRule rule, ToolTip tips)
        {
            _muteToggle = new ToggleSwitch();
            _muteToggle.Checked = rule.Mute;
            _muteToggle.CheckedChanged += (s, e) =>
            {
                if (_volumeSpinner != null) _volumeSpinner.Enabled = _muteToggle.Checked;
                Invalidate();
                NotifyAudioChanged();
            };
            Controls.Add(_muteToggle);

            // 音频方式按钮：完全静音 / 降低音量到指定百分比
            _audioBtn = new PillButton();
            _audioBtn.Style = PillStyle.Subtle;
            _audioBtn.Font = Theme.FontSmall;
            _audioBtn.CornerRadius = 6;
            _audioBtn.Size = new Size(Theme.S(76), Theme.S(26));
            _audioBtn.Text = AudioModes.ShortLabel(_audioMode) + " ▾";
            _audioBtn.Click += (s, e) => ShowAudioMenu();
            if (tips != null) tips.SetToolTip(_audioBtn, "点击切换后台音频方式：完全静音或降低到指定百分比");
            Controls.Add(_audioBtn);

            _volumeSpinner = new SpinnerInput();
            _volumeSpinner.Minimum = AudioModes.MinVolumePercent;
            _volumeSpinner.Maximum = AudioModes.MaxVolumePercent;
            _volumeSpinner.Step = 5;
            _volumeSpinner.Suffix = "%";
            _volumeSpinner.Value = _volumePercent;
            _volumeSpinner.Enabled = _muteToggle.Checked;
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
                NotifyLimitChanged();
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
            Controls.Add(_fpsSpinner);

            // 每个应用独立的限帧方式选择
            _methodBtn = new PillButton();
            _methodBtn.Style = PillStyle.Subtle;
            _methodBtn.Font = Theme.FontSmall;
            _methodBtn.CornerRadius = 6;
            _methodBtn.Size = new Size(Theme.S(56), Theme.S(26));
            _methodBtn.Text = LimitMethods.ShortLabel(_method) + " ▾";
            _methodBtn.Click += (s, e) => ShowMethodMenu();
            if (tips != null) tips.SetToolTip(_methodBtn, "点击切换该应用的限帧方式");
            Controls.Add(_methodBtn);

            _actionBtn = new PillButton();
            _actionBtn.Size = new Size(Theme.S(26), Theme.S(26));
            _actionBtn.CornerRadius = 6;
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
            if (_audioBtn != null) _audioBtn.Text = AudioModes.ShortLabel(_audioMode) + " ▾";
            if (_volumeSpinner != null)
            {
                _volumeSpinner.Visible = atten;
                _volumeSpinner.Enabled = _muteToggle != null && _muteToggle.Checked;
                if (atten) _volumeSpinner.Value = _volumePercent;
            }
            LayoutChildControls();
            Invalidate();
            NotifyAudioChanged();
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
            NotifyAudioChanged();
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

        // RTSS 方式已移除：CPU 与挂起两种方式始终可用
        private bool MethodAvailable(string method) { return true; }

        private static string ValueTooltip(string method)
        {
            string m = LimitMethods.Normalize(method);
            if (m == LimitMethods.Suspend) return "挂起进程：后台按每秒次数周期性挂起/放行（实验性）";
            return "CPU 配额：后台限制 CPU 占用百分比（不保证帧率）";
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
            NotifyLimitChanged();
        }

        private void NotifyLimitChanged()
        {
            if (LimitRuleChanged != null && _fpsToggle != null)
            {
                LimitRuleChanged(ProcessName, _fpsToggle.Checked, ParseValue(), _method, _suspendFallbackCpu);
            }
        }

        private void NotifyAudioChanged()
        {
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

                ToolStripMenuItem cpu = new ToolStripMenuItem("CPU 配额（按百分比）");
                cpu.Click += (s, e) => ChangeMethod(LimitMethods.Cpu);
                _methodMenu.Items.Add(cpu);

                ToolStripMenuItem suspend = new ToolStripMenuItem("挂起进程（实验性，可能卡顿/声音异常）");
                suspend.Click += (s, e) => ChangeMethod(LimitMethods.Suspend);
                _methodMenu.Items.Add(suspend);

                _methodMenu.Items.Add(new ToolStripSeparator());
                _fallbackMenuItem = new ToolStripMenuItem("挂起失败时自动改用 CPU 配额");
                _fallbackMenuItem.Click += (s, e) =>
                {
                    _suspendFallbackCpu = !_suspendFallbackCpu;
                    _fallbackMenuItem.Checked = _suspendFallbackCpu;
                    NotifyLimitChanged();
                };
                _methodMenu.Items.Add(_fallbackMenuItem);
            }
            if (_fallbackMenuItem != null) _fallbackMenuItem.Checked = _suspendFallbackCpu;
            _methodMenu.Show(_methodBtn, new Point(0, _methodBtn.Height));
        }

        private void ChangeMethod(string method)
        {
            string m = LimitMethods.Normalize(method);
            if (m == _method) return;

            _method = m;
            _value = LimitMethods.Clamp(_method, _value);
            if (_methodBtn != null) _methodBtn.Text = LimitMethods.ShortLabel(_method) + " ▾";

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
                if (_tips != null) _tips.SetToolTip(_fpsSpinner, ValueTooltip(_method) + "；点击数字直接输入，箭头 ±5");
            }
            Invalidate();
            NotifyLimitChanged();
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

        // 原地更新窗口标题（软刷新用，不销毁卡片、不丢编辑/滚动状态）
        public void UpdateTitle(string title)
        {
            string t = title ?? "";
            if (_windowTitle == t) return;
            _windowTitle = t;
            Invalidate();
        }

        // 原地更新图标（异步扫描完成后补全图标）
        public void UpdateIcon(Image icon)
        {
            if (_appIcon == icon) return;
            _appIcon = icon;
            Invalidate();
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

        private int _dividerX;
        private int _controlsLeft;

        private void LayoutChildControls()
        {
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            if (w <= 0) return;

            if (IsManagedView)
            {
                int gap = Theme.S(6);
                int x = w - Theme.S(12);

                // 移除按钮
                if (_actionBtn != null)
                {
                    x -= _actionBtn.Width;
                    _actionBtn.Location = new Point(x, (h - _actionBtn.Height) / 2);
                    x -= Theme.S(10);
                }

                // 限帧组（左→右一致顺序：开关 → 方式 → 数值）
                if (_fpsSpinner != null)
                {
                    x -= _fpsSpinner.Width;
                    _fpsSpinner.Location = new Point(x, (h - _fpsSpinner.Height) / 2);
                    x -= gap;
                }
                if (_methodBtn != null)
                {
                    x -= _methodBtn.Width;
                    _methodBtn.Location = new Point(x, (h - _methodBtn.Height) / 2);
                    x -= gap;
                }
                if (_fpsToggle != null)
                {
                    x -= _fpsToggle.Width;
                    _fpsToggle.Location = new Point(x, (h - _fpsToggle.Height) / 2);
                }

                // 音频组与限帧组之间的极细竖向分隔线
                x -= Theme.S(10);
                _dividerX = x;
                x -= Theme.S(10);

                // 音频组（左→右一致顺序：开关 → 方式 → [百分比]）
                if (_volumeSpinner != null && _volumeSpinner.Visible)
                {
                    x -= _volumeSpinner.Width;
                    _volumeSpinner.Location = new Point(x, (h - _volumeSpinner.Height) / 2);
                    x -= gap;
                }
                if (_audioBtn != null)
                {
                    x -= _audioBtn.Width;
                    _audioBtn.Location = new Point(x, (h - _audioBtn.Height) / 2);
                    x -= gap;
                }
                if (_muteToggle != null)
                {
                    x -= _muteToggle.Width;
                    _muteToggle.Location = new Point(x, (h - _muteToggle.Height) / 2);
                }

                _controlsLeft = x - Theme.S(14);
            }
            else
            {
                _dividerX = 0;
                if (_actionBtn != null)
                {
                    int ax = w - Theme.S(14) - _actionBtn.Width;
                    _actionBtn.Location = new Point(ax, (h - _actionBtn.Height) / 2);
                    _controlsLeft = ax - Theme.S(12);
                }
                else
                {
                    _controlsLeft = w - Theme.S(14);
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Rectangle card = new Rectangle(1, 1, Width - 3, Height - 3);
            using (GraphicsPath path = Theme.RoundedRect(card, Theme.S(8)))
            using (SolidBrush bg = new SolidBrush(_hovered ? Theme.CardHover : Theme.CardBg))
            using (Pen bd = new Pen(_hovered ? Theme.BorderHover : Theme.Border, 1f))
            {
                g.FillPath(bg, path);
                g.DrawPath(bd, path);
            }

            // 音频组与限帧组分隔线
            if (IsManagedView && _dividerX > Theme.S(180))
            {
                int divPad = Theme.S(18);
                using (Pen divPen = new Pen(Theme.Border, 1f))
                {
                    g.DrawLine(divPen, _dividerX, divPad, _dividerX, Height - divPad);
                }
            }

            int icoSize = Theme.S(34);
            Rectangle iconBox = new Rectangle(Theme.S(14), (Height - icoSize) / 2, icoSize, icoSize);
            if (_appIcon != null)
            {
                g.DrawImage(_appIcon, iconBox);
            }
            else
            {
                using (GraphicsPath ip = Theme.RoundedRect(iconBox, Theme.S(7)))
                using (SolidBrush ibr = new SolidBrush(Theme.InputBg))
                {
                    g.FillPath(ibr, ip);
                }
                string initial = string.IsNullOrEmpty(ProcessName) ? "?" : ProcessName.Substring(0, 1).ToUpperInvariant();
                TextRenderer.DrawText(g, initial, Theme.FontHeading, iconBox, Theme.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            int textX = Theme.S(58);
            int rightLimit = _controlsLeft > textX + Theme.S(60) ? _controlsLeft : (Width - Theme.S(16));
            int availW = Math.Max(Theme.S(80), rightLimit - textX);

            // 状态标签紧随进程名右侧（扁平小胶囊），既不挤压右侧控件也不遮挡长标题
            string status = _statusText ?? "";
            Size statusSize = TextRenderer.MeasureText(g, status, Theme.FontSmallBold,
                new Size(Theme.S(160), Theme.S(20)), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            int pillW = Math.Max(Theme.S(46), Math.Min(Theme.S(160), statusSize.Width + Theme.S(14)));
            int pillH = Theme.S(20);

            Size nameSize = TextRenderer.MeasureText(g, ProcessName ?? "", Theme.FontBodyBold,
                new Size(availW, Theme.S(20)), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            int maxNameW = Math.Max(Theme.S(64), availW - pillW - Theme.S(8));
            int drawNameW = Math.Min(nameSize.Width + Theme.S(2), maxNameW);

            Rectangle nameRect = new Rectangle(textX, Theme.S(11), drawNameW, Theme.S(20));
            TextRenderer.DrawText(g, ProcessName, Theme.FontBodyBold, nameRect, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            int pillX = textX + drawNameW + Theme.S(6);
            if (pillX + pillW <= rightLimit + Theme.S(4))
            {
                Rectangle pillRect = new Rectangle(pillX, Theme.S(11), pillW, pillH);
                using (GraphicsPath pp = Theme.RoundedRect(pillRect, Theme.S(5)))
                using (SolidBrush pbr = new SolidBrush(_statusBg))
                {
                    g.FillPath(pbr, pp);
                }
                TextRenderer.DrawText(g, status, Theme.FontSmallBold, pillRect, _statusFg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            Rectangle titleRect = new Rectangle(textX, Theme.S(33), availW, Theme.S(18));
            string sub = string.IsNullOrEmpty(_windowTitle) ? (ProcessName + ".exe") : _windowTitle;
            TextRenderer.DrawText(g, sub, Theme.FontSmall, titleRect, Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // 滚轮转发过滤器：按鼠标位置滚动卡片容器，
    // 解决焦点在搜索框时鼠标停在列表上无法滚动的问题
    internal class WheelScrollFilter : IMessageFilter
    {
        private const int WM_MOUSEWHEEL = 0x020A;

        private readonly MainForm _form;

        public WheelScrollFilter(MainForm form) { _form = form; }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;
            if (_form == null || _form.IsDisposed || !_form.Visible) return false;

            // 鼠标在卡片列表区域内才处理（用 Bounds 覆盖滚动条区域）
            Point pt = Control.MousePosition;
            Panel host = _form.CardsHost;
            if (host == null || host.IsDisposed || !host.VerticalScroll.Visible) return false;
            Rectangle hostRect = host.Parent != null
                ? host.Parent.RectangleToScreen(host.Bounds)
                : host.RectangleToScreen(host.ClientRectangle);
            if (!hostRect.Contains(pt)) return false;

            // 滚轮 delta 在 WParam 的 high-order word（有符号 16 位），向上为正
            int delta = unchecked((short)((m.WParam.ToInt64() >> 16) & 0xffff));
            if (delta == 0) return false;
            int notchStep = Math.Max(36, host.Font.Height * 3);
            int move = (int)Math.Round(notchStep * (delta / 120.0));
            if (move == 0) move = delta > 0 ? 12 : -12;

            // AutoScrollPosition 的约定：getter 返回负值（向下滚为负），
            // setter 却要求正值（向下滚为正）。
            int curDown = -host.AutoScrollPosition.Y; // 当前向下滚动量（>= 0）
            int newDown = curDown - move;
            int maxDown = Math.Max(0, host.VerticalScroll.Maximum - host.VerticalScroll.LargeChange + 1);
            if (newDown < 0) newDown = 0;
            if (newDown > maxDown) newDown = maxDown;
            Point cur = host.AutoScrollPosition;
            host.AutoScrollPosition = new Point(-cur.X, newDown);
            return true; // 已处理，阻止原焦点控件也滚动
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

        // 降音量/恢复的线性渐变：时长由配置 FadeMs 决定（默认 500ms，0 = 瞬间切换），
        // 步进间隔固定 50ms
        private const int FadeStepMs = 50;

        // 当前生效的渐变时长（毫秒）；从配置读取，UI 修改后即时生效
        private int _fadeMs = 500;

        private int EffectiveFadeMs
        {
            get { return _fadeMs > 0 ? _fadeMs : 1; } // 0 视为瞬间：progress 立即到 1
        }

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
            public DateTime FadeRetryAfterUtc; // 渐变写入失败后的重试退避时间
            public AfterUpKind AfterUp;    // 渐变到原值后的动作
            public DateTime LastSeenUtc;  // 会话最后一次出现在枚举中

            // 会话已消失超过 5 秒视为陈旧（进程重启后旧记录不能当新会话的真原值来源）
            public bool IsStale() { return (DateTime.UtcNow - LastSeenUtc).TotalSeconds > 5.0; }
        }

        // 同一 PID 的旧会话记录（会话被游戏重建时，新会话要继承原值，
        // 否则新记录会把「我们降过的音量」错记为原始音量，回前台就恢复不到 100%）
        private AudioTrack FindTrackByPid(uint pid)
        {
            if (pid == 0) return null;
            foreach (KeyValuePair<string, AudioTrack> kv in _audioByUs)
            {
                if (kv.Value.Pid == pid && !kv.Value.IsStale()) return kv.Value;
            }
            return null;
        }

        // 按应用名记住最近一次「可信的原始音量」。
        // 模拟器/游戏重启后新会话会带着我们上次的静音/降音量状态出现，
        // 此时不能把当前状态当原值（否则永远恢复不回去），要用这里记的真原值。
        private class OriginalSnapshot
        {
            public bool Muted;
            public float Volume;
            public bool Intervened; // 标记本程序是否曾对该应用施加过尚未清理的音频干预
            public DateTime TimeUtc;
        }
        private Dictionary<string, OriginalSnapshot> _lastGoodOriginal;
        private const int OriginalSnapshotTtlMinutes = 30;

        private readonly EventWaitHandle _showEvent;
        private readonly bool _isAdmin;
        private readonly string _selfProcName;

        private ConfigData _config;
        private Dictionary<string, AppRule> _rules;
        private Dictionary<string, AudioTrack> _audioByUs;
        private Dictionary<int, int> _cpuApplied;
        private HashSet<string> _suspendWarned;
        private HashSet<string> _fallbackFromSuspend;
        private Dictionary<string, DateTime> _inactiveSince;
        private Dictionary<string, string> _liveState;
        private Dictionary<string, Image> _iconCache;
        private readonly object _iconCacheSync = new object();
        private Dictionary<string, string> _runningTitles;
        private List<AppCardControl> _visibleCards;

        private bool _paused;
        private bool _exiting;
        private bool _autostartUpdating;
        private string _lastTrayKey = "";
        private string _lastSearchQuery = "";
        private bool _scanInFlight;
        private int _lastViewIndex = -1;

        private ToolTip _tips;
        private SegmentedTabs _tabs;
        private SearchInput _searchInput;
        private PillButton _addExeBtn;
        private PillButton _pauseBtn;
        private PillButton _refreshBtn;
        private PillButton _debounceBtn;
        private PillButton _fadeBtn;
        private PillButton _hideBtn;
        private ToggleSwitch _autostartToggle;
        private Label _statusBadge;
        private Label _footerStatus;
        private Panel _cardsHost;
        private Label _emptyLabel;
        private ContextMenuStrip _debounceMenu;
        private ContextMenuStrip _fadeMenu;

        private NotifyIcon _tray;
        private ToolStripMenuItem _miPause;
        private Icon _iconActive;
        private Icon _iconMuted;
        private Icon _iconPaused;
        private System.Windows.Forms.Timer _monitorTimer;
        private System.Windows.Forms.Timer _fadeTimer;
        private IMessageFilter _wheelFilter;

        public MainForm(EventWaitHandle showEvent, bool startBackground, bool isAdmin)
        {
            _showEvent = showEvent;
            _isAdmin = isAdmin;
            using (Process cur = Process.GetCurrentProcess()) _selfProcName = cur.ProcessName;

            _audioByUs = new Dictionary<string, AudioTrack>(StringComparer.OrdinalIgnoreCase);
            _lastGoodOriginal = new Dictionary<string, OriginalSnapshot>(StringComparer.OrdinalIgnoreCase);
            _cpuApplied = new Dictionary<int, int>();
            _suspendWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _fallbackFromSuspend = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _inactiveSince = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            _liveState = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _iconCache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
            _runningTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _visibleCards = new List<AppCardControl>();

            Theme.InitScale(this);
            LoadState();
            BuildIcons();
            BuildUI();
            BuildTray();

            SystemEvents.SessionEnding += OnSessionEnding;
            ScanRunningProcesses(); // 后台扫描；首次结果通过 BeginInvoke 回填并按需刷新
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

            // 滚轮转发：WinForms 把滚轮交给焦点控件（如搜索框），
            // 导致鼠标停在卡片列表上却无法滚动。按鼠标位置转发给卡片容器。
            _wheelFilter = new WheelScrollFilter(this);
            Application.AddMessageFilter(_wheelFilter);

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
            _fadeMs = _config.FadeMs;
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
            ClientSize = new Size(Theme.S(880), Theme.S(580));
            MinimumSize = new Size(Theme.S(760), Theme.S(460));
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

            // --- 1. Flat Minimalist Header ---
            Panel header = new Panel();
            header.Size = new Size(w, Theme.S(60));
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
            brand.Location = new Point(Theme.S(20), Theme.S(11));
            header.Controls.Add(brand);

            _statusBadge = new Label();
            _statusBadge.Font = Theme.FontSmall;
            _statusBadge.ForeColor = Theme.Green;
            _statusBadge.AutoSize = true;
            _statusBadge.Location = new Point(Theme.S(21), Theme.S(35));
            header.Controls.Add(_statusBadge);

            int btnY = Theme.S(15);
            _pauseBtn = new PillButton();
            _pauseBtn.Text = "暂停";
            _pauseBtn.Style = PillStyle.Subtle;
            _pauseBtn.CornerRadius = 6;
            _pauseBtn.Size = new Size(Theme.S(68), Theme.S(30));
            _pauseBtn.Location = new Point(w - Theme.S(88), btnY);
            _pauseBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _pauseBtn.Click += (s, e) => SetPaused(!_paused);
            header.Controls.Add(_pauseBtn);

            _addExeBtn = new PillButton();
            _addExeBtn.Text = "+ 添加";
            _addExeBtn.Style = PillStyle.Accent;
            _addExeBtn.CornerRadius = 6;
            _addExeBtn.Size = new Size(Theme.S(76), Theme.S(30));
            _addExeBtn.Location = new Point(w - Theme.S(172), btnY);
            _addExeBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _addExeBtn.Click += OnAddExeClicked;
            _tips.SetToolTip(_addExeBtn, "手动选择 .exe 程序加入接管列表");
            header.Controls.Add(_addExeBtn);

            _tabs = new SegmentedTabs();
            _tabs.Size = new Size(Theme.S(204), Theme.S(30));
            _tabs.Location = new Point(w - Theme.S(386), btnY);
            _tabs.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _tabs.SelectedIndexChanged += (s, e) =>
            {
                if (_tabs.SelectedIndex == 1) ScanRunningProcesses();
                _refreshBtn.Visible = (_tabs.SelectedIndex == 1);
                RebuildCardList();
            };
            header.Controls.Add(_tabs);

            // --- 2. Compact Search & Action Strip ---
            Panel toolbar = new Panel();
            toolbar.Size = new Size(w, Theme.S(48));
            toolbar.Dock = DockStyle.Top;
            toolbar.BackColor = Theme.Bg;

            _searchInput = new SearchInput();
            _searchInput.Location = new Point(Theme.S(20), Theme.S(10));
            _searchInput.Size = new Size(Theme.S(320), Theme.S(30));
            _searchInput.QueryChanged += (s, e) => RebuildCardList();
            toolbar.Controls.Add(_searchInput);

            _refreshBtn = new PillButton();
            _refreshBtn.Text = "刷新列表";
            _refreshBtn.Style = PillStyle.Subtle;
            _refreshBtn.CornerRadius = 6;
            _refreshBtn.Font = Theme.FontSmall;
            _refreshBtn.Size = new Size(Theme.S(78), Theme.S(30));
            _refreshBtn.Location = new Point(Theme.S(348), Theme.S(10));
            _refreshBtn.Visible = false;
            _refreshBtn.Click += (s, e) =>
            {
                // 扫描已转为后台执行；完成后在 UI 线程自动合并并刷新列表
                ScanRunningProcesses();
                RebuildCardList();
                SetFooterMessage("正在后台刷新运行中的程序列表...");
            };
            toolbar.Controls.Add(_refreshBtn);

            Label colHint = new Label();
            colHint.Text = "音频控制  ·  后台限帧";
            colHint.Font = Theme.FontSmall;
            colHint.ForeColor = Theme.TextDim;
            colHint.AutoSize = true;
            colHint.Location = new Point(w - Theme.S(160), Theme.S(18));
            colHint.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            toolbar.Controls.Add(colHint);
            _tabs.SelectedIndexChanged += (s, e) => { colHint.Visible = (_tabs.SelectedIndex == 0); };

            // --- 3. Flat Minimalist Footer ---
            Panel footer = new Panel();
            footer.Size = new Size(w, Theme.S(46));
            footer.Dock = DockStyle.Bottom;
            footer.BackColor = Theme.Surface;
            footer.Paint += (s, e) =>
            {
                using (Pen p = new Pen(Theme.Border, 1f))
                    e.Graphics.DrawLine(p, 0, 0, footer.Width, 0);
            };

            _autostartToggle = new ToggleSwitch();
            _autostartToggle.Location = new Point(Theme.S(20), (footer.Height - _autostartToggle.Height) / 2);
            AutostartManager.SyncPathIfEnabled(_isAdmin);
            _autostartToggle.Checked = AutostartManager.IsEnabled();
            _autostartToggle.CheckedChanged += (s, e) =>
            {
                if (_autostartUpdating) return;
                bool want = _autostartToggle.Checked;
                bool ok = AutostartManager.SetEnabled(want, _isAdmin);
                if (!ok)
                {
                    _autostartUpdating = true;
                    _autostartToggle.Checked = !want;
                    _autostartUpdating = false;
                }
                SetFooterMessage(ok
                    ? (want ? "已开启开机自启（后台托盘）" : "已关闭开机自启")
                    : "设置开机自启失败，请检查管理员权限");
            };
            footer.Controls.Add(_autostartToggle);

            Label autoLbl = new Label();
            autoLbl.Text = "开机自启";
            autoLbl.Font = Theme.FontSmall;
            autoLbl.ForeColor = Theme.TextMuted;
            autoLbl.AutoSize = true;
            autoLbl.Location = new Point(Theme.S(60), Theme.S(15));
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
            _debounceBtn.CornerRadius = 6;
            _debounceBtn.Size = new Size(Theme.S(96), Theme.S(26));
            _debounceBtn.Location = new Point(Theme.S(126), (footer.Height - _debounceBtn.Height) / 2);
            _debounceBtn.Click += (s, e) => _debounceMenu.Show(_debounceBtn, new Point(0, -_debounceMenu.PreferredSize.Height));
            footer.Controls.Add(_debounceBtn);

            // 渐变时长选择：降音量/恢复的线性过渡时长
            _fadeMenu = new ContextMenuStrip();
            _fadeMenu.Renderer = new LightMenuRenderer();
            _fadeMenu.BackColor = Theme.Surface;
            _fadeMenu.ForeColor = Theme.Text;
            _fadeMenu.Font = Theme.FontSmall;
            _fadeMenu.ShowImageMargin = false;
            int[] fadePresets = new int[] { 0, 200, 500, 1000, 2000, 3000 };
            for (int i = 0; i < fadePresets.Length; i++)
            {
                int val = fadePresets[i];
                ToolStripMenuItem item = new ToolStripMenuItem(val == 0 ? "0 ms (瞬间切换)" : (val + " ms"));
                item.Click += (s, e) =>
                {
                    _config.FadeMs = val;
                    _fadeMs = val;
                    _fadeBtn.Text = string.Format("渐变 {0}ms ▾", val);
                    SaveState();
                    SetFooterMessage(val == 0
                        ? "音量过渡已设为瞬间切换"
                        : "音量渐变时长已更新为 " + val + " ms");
                };
                _fadeMenu.Items.Add(item);
            }

            _fadeBtn = new PillButton();
            _fadeBtn.Text = string.Format("渐变 {0}ms ▾", _config.FadeMs);
            _fadeBtn.Style = PillStyle.Subtle;
            _fadeBtn.Font = Theme.FontSmall;
            _fadeBtn.CornerRadius = 6;
            _fadeBtn.Size = new Size(Theme.S(96), Theme.S(26));
            _fadeBtn.Location = new Point(Theme.S(230), (footer.Height - _fadeBtn.Height) / 2);
            _fadeBtn.Click += (s, e) => _fadeMenu.Show(_fadeBtn, new Point(0, -_fadeMenu.PreferredSize.Height));
            _tips.SetToolTip(_fadeBtn, "切后台降音量 / 切前台恢复音量的线性过渡时长");
            footer.Controls.Add(_fadeBtn);

            PillButton logBtn = new PillButton();
            logBtn.Text = "数据目录";
            logBtn.Style = PillStyle.Ghost;
            logBtn.CornerRadius = 6;
            logBtn.Font = Theme.FontSmall;
            logBtn.Size = new Size(Theme.S(70), Theme.S(26));
            logBtn.Location = new Point(Theme.S(334), (footer.Height - logBtn.Height) / 2);
            logBtn.Click += (s, e) => { try { Process.Start("explorer.exe", AppLogger.DataDir); } catch { } };
            footer.Controls.Add(logBtn);

            _hideBtn = new PillButton();
            _hideBtn.Text = "收起托盘";
            _hideBtn.Style = PillStyle.Subtle;
            _hideBtn.CornerRadius = 6;
            _hideBtn.Font = Theme.FontSmall;
            _hideBtn.Size = new Size(Theme.S(78), Theme.S(26));
            _hideBtn.Location = new Point(w - Theme.S(98), (footer.Height - _hideBtn.Height) / 2);
            _hideBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _hideBtn.Click += (s, e) => Hide();
            footer.Controls.Add(_hideBtn);

            _footerStatus = new Label();
            _footerStatus.Font = Theme.FontSmall;
            _footerStatus.ForeColor = Theme.TextDim;
            _footerStatus.AutoEllipsis = true;
            _footerStatus.TextAlign = ContentAlignment.MiddleRight;
            _footerStatus.Location = new Point(Theme.S(414), Theme.S(13));
            _footerStatus.Size = new Size(Math.Max(Theme.S(80), w - Theme.S(522)), Theme.S(20));
            _footerStatus.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            _footerStatus.Text = "更改将自动保存并即时生效";
            footer.Controls.Add(_footerStatus);

            // --- 4. Scrollable Card Host ---
            _cardsHost = new Panel();
            _cardsHost.Dock = DockStyle.Fill;
            _cardsHost.AutoScroll = true;
            _cardsHost.BackColor = Theme.Bg;
            _cardsHost.Padding = new Padding(Theme.S(20), Theme.S(4), Theme.S(20), Theme.S(14));
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

        // 刷新入口：后台线程扫描（图标提取是磁盘 IO，同步做会冻结 UI），
        // 完成后回 UI 线程合并结果并刷新卡片。扫描中重复调用自动合并为一次。
        private void ScanRunningProcesses()
        {
            if (_scanInFlight) return;
            _scanInFlight = true;

            ThreadPool.QueueUserWorkItem(delegate
            {
                Dictionary<string, string> found = null;
                Dictionary<string, Image> newIcons = null;
                try
                {
                    ScanRunningProcessesWorker(out found, out newIcons);
                }
                catch (Exception ex)
                {
                    AppLogger.Log("Scan running processes failed: " + ex.Message, "ERROR");
                }

                try
                {
                    if (IsDisposed || !IsHandleCreated)
                    {
                        _scanInFlight = false;
                        return;
                    }
                    BeginInvoke((Action)delegate
                    {
                        _scanInFlight = false;
                        if (found != null)
                        {
                            _runningTitles = found;
                            lock (_iconCacheSync)
                            {
                                foreach (KeyValuePair<string, Image> kv in newIcons)
                                {
                                    if (!_iconCache.ContainsKey(kv.Key)) _iconCache[kv.Key] = kv.Value;
                                    else kv.Value.Dispose(); // 已有缓存（并发扫描竞态），丢弃重复
                                }
                            }
                            if (_tabs != null && _tabs.SelectedIndex == 1)
                            {
                                RebuildCardList();
                            }
                            else if (_tabs != null && _tabs.SelectedIndex == 0)
                            {
                                // 已接管标签页：原地回填扫描到的窗口标题与图标，不重建卡片
                                for (int i = 0; i < _visibleCards.Count; i++)
                                {
                                    AppCardControl card = _visibleCards[i];
                                    string title;
                                    if (_runningTitles.TryGetValue(card.ProcessName, out title))
                                        card.UpdateTitle(title);
                                    Image ico = null;
                                    lock (_iconCacheSync)
                                    {
                                        if (_iconCache.ContainsKey(card.ProcessName)) ico = _iconCache[card.ProcessName];
                                    }
                                    if (ico != null) card.UpdateIcon(ico);
                                }
                            }
                        }
                    });
                }
                catch
                {
                    _scanInFlight = false;
                }
            });
        }

        // 实际扫描逻辑（后台线程执行）
        private void ScanRunningProcessesWorker(out Dictionary<string, string> foundOut, out Dictionary<string, Image> newIconsOut)
        {
            Dictionary<string, Image> newIcons = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
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

                        lock (_iconCacheSync)
                        {
                            if (!_iconCache.ContainsKey(pn) && !newIcons.ContainsKey(pn))
                            {
                                try
                                {
                                    string exePath = p.MainModule.FileName;
                                    if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                                    {
                                        using (Icon ico = Icon.ExtractAssociatedIcon(exePath))
                                        {
                                            if (ico != null) newIcons[pn] = ico.ToBitmap();
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            foundOut = found;
            newIconsOut = newIcons;
        }

        private void RebuildCardList()
        {
            _tabs.SetLabels(string.Format("已接管 ({0})", _rules.Count), "运行中");
            string q = _searchInput.Query.ToLowerInvariant();
            bool managedTab = (_tabs.SelectedIndex == 0);
            bool resetScroll = (_lastViewIndex != _tabs.SelectedIndex) || !string.Equals(_lastSearchQuery, q, StringComparison.Ordinal);

            // 软刷新：运行中标签页、无搜索词、卡片集合与接管状态均无变化时，
            // 只原地更新标题与状态，不销毁卡片（保滚动位置与编辑状态）
            if (!managedTab && string.IsNullOrEmpty(q) && TrySoftRefreshRunningCards())
            {
                _lastViewIndex = _tabs.SelectedIndex;
                _lastSearchQuery = q;
                return;
            }

            // WinForms AutoScroll 面板在滚动非 0 时添加子控件会以当前视口偏移为基准，
            // 必须先保存滚动量、归零后再布局，最后恢复滚动量，否则顶部会出现大片空白。
            int savedScrollY = resetScroll ? 0 : Math.Max(0, -_cardsHost.AutoScrollPosition.Y);
            _lastViewIndex = _tabs.SelectedIndex;
            _lastSearchQuery = q;

            _cardsHost.SuspendLayout();
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

            int padX = Theme.S(20);
            int y = Theme.S(2);
            int cardW = Math.Max(Theme.S(300), _cardsHost.ClientSize.Width - padX * 2);

            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                string title = _runningTitles.ContainsKey(name) ? _runningTitles[name] : "";
                if (!string.IsNullOrEmpty(q))
                {
                    bool match = name.ToLowerInvariant().Contains(q) || title.ToLowerInvariant().Contains(q);
                    if (!match) continue;
                }

                Image icon = null;
                lock (_iconCacheSync) { if (_iconCache.ContainsKey(name)) icon = _iconCache[name]; }
                AppRule rule = _rules.ContainsKey(name) ? _rules[name] : null;
                AppCardControl card = new AppCardControl(name, title, icon, managedTab, rule, rule != null,
                    _tips, rule != null ? rule.Method : LimitMethods.Cpu,
                    rule == null || !rule.SuspendFallbackCpu.HasValue || rule.SuspendFallbackCpu.Value);
                card.Location = new Point(padX, y);
                card.Width = cardW;

                if (managedTab)
                {
                    card.LimitRuleChanged += OnLimitRuleChanged;
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
                y += card.Height + Theme.S(6);
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

            _cardsHost.ResumeLayout(true);
            ResizeCards();
            if (savedScrollY > 0 && _cardsHost.VerticalScroll.Visible)
            {
                int maxDown = Math.Max(0, _cardsHost.VerticalScroll.Maximum - _cardsHost.VerticalScroll.LargeChange + 1);
                _cardsHost.AutoScrollPosition = new Point(0, Math.Min(savedScrollY, maxDown));
            }
        }

        // 运行中标签页软刷新：卡片集合与接管状态均未变时返回 true（调用方无需重建）
        private bool TrySoftRefreshRunningCards()
        {
            if (_visibleCards.Count == 0) return false;

            // 卡片必须本来就是「运行中」视图构建的，且每张卡片的「是否已接管」状态与当前规则一致
            for (int i = 0; i < _visibleCards.Count; i++)
            {
                AppCardControl card = _visibleCards[i];
                if (card.IsManagedView) return false;
                if (card.IsAlreadyManaged != _rules.ContainsKey(card.ProcessName)) return false;
            }

            // 集合一致性检查：现卡片与最新扫描的进程名集合必须完全一致（顺序无关）
            if (_visibleCards.Count != _runningTitles.Count) return false;
            HashSet<string> current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _visibleCards.Count; i++) current.Add(_visibleCards[i].ProcessName);
            if (current.Count != _runningTitles.Count) return false;
            foreach (string k in _runningTitles.Keys)
            {
                if (!current.Contains(k)) return false;
            }

            // 原地更新：标题 + 图标 + 状态徽章
            for (int i = 0; i < _visibleCards.Count; i++)
            {
                AppCardControl card = _visibleCards[i];
                string title;
                _runningTitles.TryGetValue(card.ProcessName, out title);
                card.UpdateTitle(title ?? "");
                Image ico = null;
                lock (_iconCacheSync)
                {
                    if (_iconCache.ContainsKey(card.ProcessName)) ico = _iconCache[card.ProcessName];
                }
                if (ico != null) card.UpdateIcon(ico);
                ApplyCardStatus(card);
            }
            return true;
        }

        private void ResizeCards()
        {
            int padX = Theme.S(20);
            // ClientSize.Width 已自动扣除垂直滚动条宽度，不再重复扣除
            int w = Math.Max(Theme.S(300), _cardsHost.ClientSize.Width - padX * 2);
            for (int i = 0; i < _visibleCards.Count; i++)
            {
                _visibleCards[i].Width = w;
            }
        }

        // 未接管卡片的实时存活进程名集合（由 MonitorStep 每次扫描更新）
        private HashSet<string> _liveProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void ApplyCardStatus(AppCardControl card)
        {
            if (_paused && card.IsManagedView)
            {
                card.SetStatus("已暂停", Theme.TextMuted, Theme.InputBg);
                return;
            }

            string st;
            if (_liveState.ContainsKey(card.ProcessName))
            {
                st = _liveState[card.ProcessName];
            }
            else
            {
                // 未接管的进程：用监控循环实时维护的存活集合，不用陈旧的 _runningTitles 快照。
                // 监控暂停或没有规则时集合不更新，此时回退到扫描快照。
                st = _liveProcessNames.Count > 0
                    ? (_liveProcessNames.Contains(card.ProcessName) ? "运行中" : "未运行")
                    : (_runningTitles.ContainsKey(card.ProcessName) ? "运行中" : "未运行");
            }

            if (st.Contains("静音") || st.Contains("FPS") || st.Contains("CPU") || st.Contains("挂起") || st.Contains("降音量") || st.Contains("回退"))
                card.SetStatus(st, Theme.Amber, Theme.AmberSoft);
            else if (st == "前台" || st == "运行中")
                card.SetStatus(st, Theme.Green, Theme.GreenSoft);
            else
                card.SetStatus(st, Theme.TextMuted, Theme.InputBg);
        }

        private void OnLimitRuleChanged(string procName, bool limitFps, int value, string method, bool suspendFallbackCpu)
        {
            if (!_rules.ContainsKey(procName)) return;
            AppRule r = _rules[procName];
            string newMethod = LimitMethods.Normalize(method);
            bool methodChanged = !newMethod.Equals(LimitMethods.Normalize(r.Method), StringComparison.OrdinalIgnoreCase);

            r.Method = newMethod;
            r.Fps = LimitMethods.Clamp(newMethod, value);
            r.LimitFps = limitFps;
            bool oldFallback = !r.SuspendFallbackCpu.HasValue || r.SuspendFallbackCpu.Value;
            bool fallbackChanged = oldFallback != suspendFallbackCpu;
            r.SuspendFallbackCpu = suspendFallbackCpu;

            // 只有方式/回退设置变化才重置临时回退
            if (methodChanged || fallbackChanged) _fallbackFromSuspend.Remove(procName);

            // 切换限帧方式或关闭限帧时，解除旧方式的干预
            if (methodChanged || !r.LimitFps) RestoreRuleLimits(procName);

            SaveState();
            if (!r.LimitFps)
            {
                SetFooterMessage(string.Format("已关闭 {0} 后台限帧", procName));
            }
            else
            {
                SetFooterMessage(string.Format("已更新 {0} 限帧（{1} {2}{3}）", procName,
                    LimitMethods.ShortLabel(r.Method),
                    LimitMethods.DisplayValue(r.Method, r.Fps),
                    newMethod == LimitMethods.Suspend ? (suspendFallbackCpu ? " · 失败回退CPU" : " · 不回退") : ""));
            }
        }

        private void OnAudioRuleChanged(string procName, bool audioOn, string audioMode, int volumePercent)
        {
            if (!_rules.ContainsKey(procName)) return;
            AppRule r = _rules[procName];
            bool muteChanged = (r.Mute != audioOn);
            bool modeChanged = !AudioModes.Normalize(audioMode).Equals(AudioModes.Normalize(r.AudioMode), StringComparison.OrdinalIgnoreCase);

            r.Mute = audioOn;
            r.AudioMode = AudioModes.Normalize(audioMode);
            r.AudioVolume = AudioModes.ClampVolume(volumePercent);

            // 只在干预方式本身变化（静音开关 / 静音<->降音量）时才立即恢复会话。
            // 改百分比不恢复：监控循环会从当前值平滑渐变到新目标。
            // 此前无条件恢复，导致改 FPS 等无关设置时后台音量先跳回原值再重新渐降。
            if (muteChanged || modeChanged) RestoreRuleAudio(procName, true);
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
                    lock (_iconCacheSync)
                    {
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

        // 供滚轮转发过滤器访问卡片容器
        internal Panel CardsHost { get { return _cardsHost; } }

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
            _liveProcessNames.Clear();
            Process[] allProcs = Process.GetProcesses();
            for (int i = 0; i < allProcs.Length; i++)
            {
                Process p = allProcs[i];
                try
                {
                    livePids.Add(p.Id);
                    string pn = p.ProcessName;
                    if (!string.IsNullOrEmpty(pn)) _liveProcessNames.Add(pn);
                    if (_rules.ContainsKey(pn))
                    {
                        List<int> list;
                        if (!procsByName.TryGetValue(pn, out list))
                        {
                            list = new List<int>();
                            procsByName[pn] = list;
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

                List<int> pids;
                if (!procsByName.TryGetValue(name, out pids))
                {
                    _inactiveSince.Remove(name);
                    _fallbackFromSuspend.Remove(name);
                    _suspendWarned.Remove(name);
                    _liveState[name] = "未运行";
                    continue;
                }

                // 前台判定：窗口进程与接管进程可能是两个不同进程
                //（MuMuPlayer 持窗口、MuMuNxDevice 出声音），用亲缘判定
                bool active = false;
                if (fgPid > 0)
                {
                    for (int i = 0; i < pids.Count; i++)
                    {
                        if (pids[i] == fgPid || ProcessKinship.SameApp(fgPid, pids[i]))
                        {
                            active = true;
                            break;
                        }
                    }
                }
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

                                // 会话重建（模拟器/游戏关闭重开）：仅当同 PID 有存活记录，或本程序之前
                                // 确实干预过该应用且尚未清理时，才从快照继承真原值；
                                // 首次接管时必须尊重用户在音量合成器里自设的音量（如 50%），绝不强行改成 100%。
                                AudioTrack legacy = FindTrackByPid(s.Pid);
                                OriginalSnapshot snapshot;
                                bool hasSnapshot = _lastGoodOriginal.TryGetValue(name, out snapshot)
                                    && (now - snapshot.TimeUtc).TotalMinutes <= OriginalSnapshotTtlMinutes;
                                bool stateSuspicious = s.Muted || (hasSnapshot && s.Volume + 0.005f < snapshot.Volume);

                                if (legacy != null && !legacy.IsStale())
                                {
                                    // 同 PID 旧记录还活着：优先继承它的真原值
                                    track.WasMuted = legacy.WasMuted;
                                    track.WasVolume = legacy.WasVolume;
                                    if (legacy.WeMuted && s.Muted)
                                    {
                                        track.WeMuted = true;
                                        track.CurrentLevel = -1f;
                                        AppLogger.Log(string.Format(
                                            "Session rebuilt (mute inherited): pid={0}", s.Pid), "INFO");
                                    }
                                    // 静音会话也可能同时带着被降低的音量：两个标志都要继承，
                                    // 否则恢复时只解除静音、音量仍卡在 30%
                                    if (s.Volume + 0.005f < track.WasVolume)
                                    {
                                        track.Attenuated = true;
                                        track.AppliedPct = (int)Math.Round(s.Volume * 100);
                                        track.CurrentLevel = s.Volume;
                                    }
                                }
                                else if (stateSuspicious && hasSnapshot && snapshot.Intervened)
                                {
                                    // 进程重启后的新会话带着本程序上次的干预残留：用记住的真原值，
                                    // 并把当前状态识别为干预残留，这样恢复才能真正解除。
                                    track.WasMuted = snapshot.Muted;
                                    track.WasVolume = snapshot.Volume;
                                    if (s.Muted && !track.WasMuted) track.WeMuted = true;
                                    if (s.Volume + 0.005f < track.WasVolume)
                                    {
                                        track.Attenuated = true;
                                        track.AppliedPct = (int)Math.Round(s.Volume * 100);
                                        track.CurrentLevel = s.Volume;
                                    }
                                    AppLogger.Log(string.Format(
                                        "Session restart with stale audio state: pid={0} muted={1} vol={2:0.00} -> restore to {3:0.00}",
                                        s.Pid, s.Muted, s.Volume, snapshot.Volume), "INFO");
                                }

                                _audioByUs[s.InstanceId] = track;
                            }
                            track.AppName = name;
                            track.LastSeenUtc = now;

                            // 原值可信时记入快照：供进程重启后新会话恢复使用
                            OriginalSnapshot curSnap;
                            if (!_lastGoodOriginal.TryGetValue(name, out curSnap))
                            {
                                curSnap = new OriginalSnapshot();
                                _lastGoodOriginal[name] = curSnap;
                            }
                            curSnap.Muted = track.WasMuted;
                            curSnap.Volume = track.WasVolume;
                            curSnap.TimeUtc = now;

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
                                    // 后台目标音量不超过原始音量，避免原本更小声的应用切后台反而变大声
                                    float target = Math.Min(track.WasVolume, wantPct / 100f);
                                    if (!track.Attenuated)
                                    {
                                        // 首次降音量：从会话当前音量线性渐变到目标
                                        float from = track.CurrentLevel >= 0f ? track.CurrentLevel : s.Volume;
                                        track.Attenuated = true;
                                        track.AppliedPct = wantPct;
                                        curSnap.Intervened = true;
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
                                        curSnap.Intervened = true;
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
                                        curSnap.Intervened = true;
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
                                        curSnap.Intervened = true;
                                        SetFooterMessage(name + " 切至后台 · 已静音");
                                    }
                                }
                                mutedNow = true;
                            }
                        }
                        else
                        {
                            // 前台：仅当本应用开启音频接管且曾被我们干预过，才清理重启后可能残留的静音/低音量；
                            // 绝不干涉用户在系统音量合成器里手动静音或调低的程序。
                            if (rule.Mute && active && !_audioByUs.ContainsKey(s.InstanceId))
                            {
                                OriginalSnapshot snap;
                                bool hasSnap = _lastGoodOriginal.TryGetValue(name, out snap)
                                    && snap.Intervened
                                    && (now - snap.TimeUtc).TotalMinutes <= OriginalSnapshotTtlMinutes;
                                bool polluted = hasSnap && ((s.Muted && !snap.Muted) || s.Volume + 0.005f < snap.Volume);
                                if (polluted && !string.IsNullOrEmpty(s.InstanceId))
                                {
                                    float target = snap.Volume;
                                    bool okMute = (!s.Muted || snap.Muted) || MuteCore.SetMute(s.InstanceId, snap.Muted);
                                    bool okVol = Math.Abs(s.Volume - target) < 0.005f || MuteCore.SetVolume(s.InstanceId, target);
                                    if (okMute && okVol)
                                    {
                                        snap.Intervened = false;
                                        AppLogger.Log(string.Format(
                                            "Cleared leftover audio state on foreground: pid={0} -> vol={1:0.00}",
                                            s.Pid, target), "INFO");
                                    }
                                }
                            }
                            if (RestoreSessionAudio(s.InstanceId, name, "前台", AfterUpKind.RemoveRecord, s.Volume, s.Muted))
                            {
                                OriginalSnapshot snap;
                                if (_lastGoodOriginal.TryGetValue(name, out snap)) snap.Intervened = false;
                            }
                        }
                    }
                }

                int appliedCpu = 0;
                int appliedSuspend = 0;
                bool suspendFailed = false;
                bool fallbackActive = false;
                string method = LimitMethods.Normalize(rule.Method);
                if (rule.LimitFps && method == LimitMethods.Suspend && _fallbackFromSuspend.Contains(name))
                {
                    // 本会话内挂起被拒，按设置自动改用 CPU 配额
                    fallbackActive = true;
                    method = LimitMethods.Cpu;
                }
                string cpuTag = fallbackActive ? "回退CPU" : "CPU";

                if (rule.LimitFps && method == LimitMethods.Cpu)
                {
                    // CPU 配额：按 PID 挂到独立 Job 上限制 CPU 时间
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
                        if (changed)
                        {
                            SetFooterMessage(fallbackActive
                                ? string.Format("{0} 挂起被拒 · 已回退 CPU 配额 {1}%", name, pct)
                                : string.Format("{0} 切至后台 · CPU 配额 {1}%", name, pct));
                        }
                    }
                    else
                    {
                        for (int i = 0; i < pids.Count; i++) RestoreQuotaForPid(pids[i]);
                    }
                }
                else if (rule.LimitFps && method == LimitMethods.Suspend)
                {
                    // 挂起进程：周期性挂起/放行，实验性
                    for (int i = 0; i < pids.Count; i++) RestoreQuotaForPid(pids[i]);

                    int suspendFps = LimitMethods.Clamp(LimitMethods.Suspend, rule.Fps);
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

                        bool fallbackCpu = !rule.SuspendFallbackCpu.HasValue || rule.SuspendFallbackCpu.Value;
                        if (started > 0 && !anyFailed)
                        {
                            appliedSuspend = suspendFps;
                            _suspendWarned.Remove(name);
                        }
                        else
                        {
                            suspendFailed = true;
                            if (fallbackCpu)
                            {
                                _fallbackFromSuspend.Add(name);
                                StopSuspendForPids(pids);
                                SetFooterMessage(name + " 挂起失败：进程受保护，已自动改用 CPU 配额");
                                AppLogger.Log("Suspend fallback to CPU for " + name, "WARN");
                            }
                            else if (_suspendWarned.Add(name))
                            {
                                SetFooterMessage(name + " 挂起失败：进程可能受反作弊保护（自动回退已关闭）");
                                AppLogger.Log("Suspend unsupported for " + name + " (fallback disabled)", "WARN");
                            }
                        }
                    }
                    else
                    {
                        StopSuspendForPids(pids);
                    }
                }
                else
                {
                    // 未开启限帧：解除所有方式的干预
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
                    _liveState[name] = string.Format("降音量{0}%·{1}{2}%", attNowPct, cpuTag, appliedCpu);
                else if (attNowPct > 0)
                    _liveState[name] = string.Format("降音量{0}%", attNowPct);
                else if (mutedNow && suspendFailed)
                    _liveState[name] = "静音·挂起失败";
                else if (mutedNow && appliedSuspend > 0)
                    _liveState[name] = string.Format("静音·挂起{0}", appliedSuspend);
                else if (mutedNow && appliedCpu > 0)
                    _liveState[name] = string.Format("静音·{0}{1}%", cpuTag, appliedCpu);
                else if (mutedNow)
                    _liveState[name] = "已静音";
                else if (suspendFailed)
                    _liveState[name] = "挂起失败";
                else if (fallbackActive && appliedCpu == 0)
                    _liveState[name] = "回退失败";
                else if (appliedSuspend > 0)
                    _liveState[name] = string.Format("挂起 {0}", appliedSuspend);
                else if (appliedCpu > 0)
                    _liveState[name] = string.Format("{0} {1}%", cpuTag, appliedCpu);
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
                // 所属进程已退出：立即清理记录，不拖延 30 秒导致托盘一直显示节流中
                if (kv.Value.Pid > 0 && !livePids.Contains((int)kv.Value.Pid))
                {
                    expired.Add(kv.Key);
                    continue;
                }
                if (kv.Value.LastSeenUtc >= staleCutoff) continue;           // 进程仍存活但会话短暂消失，再等等
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
                if (now < t.FadeRetryAfterUtc) continue;

                double progress = (now - t.FadeStartUtc).TotalMilliseconds / EffectiveFadeMs;
                if (progress > 1.0) progress = 1.0;
                float level = t.FadeFrom + (t.FadeTo - t.FadeFrom) * (float)progress;
                if (progress >= 1.0) level = t.FadeTo;

                if (MuteCore.SetVolume(id, level))
                {
                    t.CurrentLevel = level;
                }
                else
                {
                    // 写入失败（会话短暂消失/COM 忙）：保留真实进度并退避 500ms，避免高频刷 COM
                    t.FadeFrom = t.CurrentLevel >= 0f ? t.CurrentLevel : t.FadeFrom;
                    t.FadeStartUtc = now;
                    t.FadeRetryAfterUtc = now.AddMilliseconds(500);
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
                        OriginalSnapshot snap;
                        if (!string.IsNullOrEmpty(t.AppName) && _lastGoodOriginal.TryGetValue(t.AppName, out snap))
                            snap.Intervened = false;
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
            return RestoreSessionAudio(instanceId, name, reason, AfterUpKind.RemoveRecord, -1f, false);
        }

        // 带会话实际状态的重载：监控循环里直接传入，避免重复 COM 枚举。
        // actualVolume < 0 表示调用方没有实际状态，会自行枚举一次。
        private bool RestoreSessionAudio(string instanceId, string name, string reason, AfterUpKind afterUp,
                                         float actualVolume, bool actualMuted)
        {
            AudioTrack track;
            if (!_audioByUs.TryGetValue(instanceId, out track)) return false;

            // 以会话实际状态为准：若偏离原值则启动向上渐变。
            // 不能只依赖 track.Attenuated 标志——标志可能在建记录时不完整，
            // 导致音量卡在 30%（静音解除了但音量没恢复）。
            if (actualVolume < 0f)
            {
                List<AudioSessionInfo> nowSessions = MuteCore.GetSessions();
                for (int k = 0; k < nowSessions.Count; k++)
                {
                    if (nowSessions[k].InstanceId == instanceId)
                    {
                        actualVolume = nowSessions[k].Volume;
                        actualMuted = nowSessions[k].Muted;
                        break;
                    }
                }
            }
            bool volumeNeedsRestore = actualVolume >= 0f && Math.Abs(actualVolume - track.WasVolume) >= 0.005f;

            // 有音量干预或实际音量偏离原值：启动向上渐变
            if (track.Attenuated || volumeNeedsRestore)
            {
                if (track.Fade == FadeKind.Up)
                {
                    // 已在向上渐变：只更新完成动作，不重启计时
                    track.AfterUp = afterUp;
                    return false;
                }
                float from = volumeNeedsRestore ? actualVolume
                    : (track.CurrentLevel >= 0f ? track.CurrentLevel : track.WasVolume);
                track.Attenuated = true;
                if (Math.Abs(from - track.WasVolume) < 0.005f)
                {
                    // 已经等于原值：直接写入确认。失败时保留记录下个 tick 重试。
                    if (MuteCore.SetVolume(instanceId, track.WasVolume))
                    {
                        track.CurrentLevel = track.WasVolume;
                        track.Attenuated = false;
                    }
                    else
                    {
                        AppLogger.Log(string.Format("Audio set-volume retry: {0}", instanceId), "WARN");
                        if (actualMuted && MuteCore.SetMute(instanceId, track.WasMuted)) { /* mute fixed */ }
                        return false;
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
                    return false;
                }
            }

            // 静音干预或实际仍静音（但原值非静音）：立即解除
            bool ok = true;
            if (track.WeMuted || (actualMuted && !track.WasMuted))
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
            CpuQuotaCore.RestoreAll();
            SuspendCore.StopAll();
            _cpuApplied.Clear();
            _suspendWarned.Clear();
            _fallbackFromSuspend.Clear();
        }

        private void RestoreRuleLimits(string procName)
        {
            RestoreQuotaForProcess(procName);
            StopSuspendForProcess(procName);
            _suspendWarned.Remove(procName);
            _fallbackFromSuspend.Remove(procName);
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

        private void SetPaused(bool paused)
        {
            _paused = paused;
            if (paused)
            {
                RestoreAllMuted();
                RestoreAllLimits();
                _inactiveSince.Clear();
                _liveState.Clear();
                SetFooterMessage("已暂停监控，并恢复所有音量与帧率");
            }
            else
            {
                SetFooterMessage("已恢复后台监控");
            }
            if (Visible)
            {
                for (int i = 0; i < _visibleCards.Count; i++) ApplyCardStatus(_visibleCards[i]);
            }
            UpdateHeaderAndTray();
        }

        private void UpdateHeaderAndTray()
        {
            bool throttling = _audioByUs.Count > 0 || _cpuApplied.Count > 0 || SuspendCore.HasActiveWorkers();
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
                RebuildCardList();
                Show();
                ScanRunningProcesses(); // 放在 Show() 之后，确保窗口句柄已创建，异步回填不丢结果
            }
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            AppLogger.Log("Session ending; restoring audio and CPU limits.");
            RestoreAudio(true);
            RestoreAllLimits();
        }

        private void ExitApplication()
        {
            if (_exiting) return;
            _exiting = true;
            AppLogger.Log("Exiting GameMuteHelper.");
            try { SystemEvents.SessionEnding -= OnSessionEnding; } catch { }
            try { if (_monitorTimer != null) { _monitorTimer.Stop(); _monitorTimer.Dispose(); } } catch { }
            try { if (_fadeTimer != null) { _fadeTimer.Stop(); _fadeTimer.Dispose(); } } catch { }
            try { if (_wheelFilter != null) Application.RemoveMessageFilter(_wheelFilter); } catch { }
            try { RestoreAudio(true); } catch { }
            try { RestoreAllLimits(); } catch { }
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
            try { if (_debounceMenu != null) _debounceMenu.Dispose(); } catch { }
            try { if (_fadeMenu != null) _fadeMenu.Dispose(); } catch { }
            try { if (_tips != null) _tips.Dispose(); } catch { }
            try { if (_iconActive != null) _iconActive.Dispose(); } catch { }
            try { if (_iconMuted != null) _iconMuted.Dispose(); } catch { }
            try { if (_iconPaused != null) _iconPaused.Dispose(); } catch { }
            try
            {
                lock (_iconCacheSync)
                {
                    foreach (Image img in _iconCache.Values) { try { img.Dispose(); } catch { } }
                    _iconCache.Clear();
                }
            }
            catch { }
            Application.Exit();
        }
    }
}
