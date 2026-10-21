using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Net.NetworkInformation;
using System.Text;

namespace WinCleaner
{
    public class CleanResult
    {
        public int Files = 0;
        public int RegKeys = 0;
        public long Bytes = 0;
        public List<string> Errors = new List<string>();

        public void Merge(CleanResult o)
        {
            Files += o.Files;
            RegKeys += o.RegKeys;
            Bytes += o.Bytes;
            Errors.AddRange(o.Errors);
        }
    }

    public static class NativeMethods
    {
        [DllImport("shell32.dll")]
        public static extern void SHAddToRecentDocs(uint uFlags, IntPtr pv);

        [DllImport("user32.dll")]
        public static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll")]
        public static extern bool EmptyClipboard();

        [DllImport("user32.dll")]
        public static extern bool CloseClipboard();

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hWnd, string appName, string idList);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(int processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern uint GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes, int ImpersonationLevel, int TokenType, out IntPtr phNewToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool ImpersonateLoggedOnUser(IntPtr hToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool RevertToSelf();

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr OpenSCManager(string lpMachineName, string lpDatabaseName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ControlService(IntPtr hService, uint dwControl, ref SERVICE_STATUS lpServiceStatus);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseServiceHandle(IntPtr hSCObject);

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_STATUS
        {
            public int dwServiceType;
            public int dwCurrentState;
            public int dwControlsAccepted;
            public int dwWin32ExitCode;
            public int dwServiceSpecificExitCode;
            public int dwCheckPoint;
            public int dwWaitHint;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

        public const int AF_INET = 2;
        public const int TCP_TABLE_OWNER_PID_ALL = 5;
        public const int MIB_TCP_STATE_DELETE_TCB = 12;

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            public uint localPort;
            public uint remoteAddr;
            public uint remotePort;
            public uint owningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCPROW
        {
            public uint dwState;
            public uint dwLocalAddr;
            public uint dwLocalPort;
            public uint dwRemoteAddr;
            public uint dwRemotePort;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable,
            ref int dwOutBufLen,
            bool sort,
            int ipVersion,
            int tblClass,
            uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint SetTcpEntry(ref MIB_TCPROW pTcpRow);

        [DllImport("wininet.dll", SetLastError = true)]
        public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        public const uint RPC_C_AUTHN_WINNT = 10;
        public const uint FWPM_SESSION_FLAG_DYNAMIC = 0x00000001;
        public const uint FWP_ACTION_BLOCK = 0x00001001;
        public const uint FWP_ACTION_PERMIT = 0x00001002;

        public static readonly Guid FWPM_LAYER_ALE_AUTH_CONNECT_V4 = new Guid("c38d57d1-05a7-4c33-904f-7fbceee60e82");
        public static readonly Guid FWPM_LAYER_ALE_AUTH_CONNECT_V6 = new Guid("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
        public static readonly Guid FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4 = new Guid("e1cd9fe7-f4b5-4273-96c0-592e487b8650");
        public static readonly Guid FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6 = new Guid("a3b42c97-9f04-4672-b87e-cee9c483257f");
        public static readonly Guid FWPM_CONDITION_ALE_APP_ID = new Guid("d78e1e87-8644-4ea5-9437-d809ecefc971");
        public static readonly Guid FWPM_CONDITION_IP_REMOTE_PORT = new Guid("c35a604d-d22b-4e1a-91b4-68f674ee674b");
        public static readonly Guid FWPM_CONDITION_IP_REMOTE_ADDRESS = new Guid("b235ae9a-1d64-49b8-a44c-5ff3d9095045");

        public enum FWP_DATA_TYPE : int
        {
            FWP_EMPTY = 0,
            FWP_UINT8 = 1,
            FWP_UINT16 = 2,
            FWP_UINT32 = 3,
            FWP_UINT64 = 4,
            FWP_INT8 = 5,
            FWP_INT16 = 6,
            FWP_INT32 = 7,
            FWP_INT64 = 8,
            FWP_FLOAT = 9,
            FWP_DOUBLE = 10,
            FWP_BYTE_ARRAY16_TYPE = 11,
            FWP_BYTE_BLOB_TYPE = 12,
            FWP_SID = 13,
            FWP_SECURITY_DESCRIPTOR_TYPE = 14,
            FWP_TOKEN_INFORMATION_TYPE = 15,
            FWP_TOKEN_ACCESS_INFORMATION_TYPE = 16,
            FWP_UNICODE_STRING_TYPE = 17,
            FWP_BYTE_ARRAY6_TYPE = 18
        }

        public enum FWP_MATCH_TYPE : int
        {
            FWP_MATCH_EQUAL = 0,
            FWP_MATCH_GREATER = 1,
            FWP_MATCH_LESS = 2,
            FWP_MATCH_GREATER_OR_EQUAL = 3,
            FWP_MATCH_LESS_OR_EQUAL = 4,
            FWP_MATCH_RANGE = 5,
            FWP_MATCH_FLAGS_ALL_SET = 6,
            FWP_MATCH_FLAGS_ANY_SET = 7,
            FWP_MATCH_FLAGS_NONE_SET = 8,
            FWP_MATCH_EQUAL_CASE_INSENSITIVE = 9,
            FWP_MATCH_NOT_EQUAL = 10
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct FWPM_DISPLAY_DATA0
        {
            public string name;
            public string description;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FWPM_SESSION0
        {
            public Guid sessionKey;
            public FWPM_DISPLAY_DATA0 displayData;
            public uint flags;
            public uint txnWaitTimeoutInMSec;
            public uint processId;
            public IntPtr sid;
            public IntPtr username;
            [MarshalAs(UnmanagedType.Bool)]
            public bool kernelMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FWP_BYTE_BLOB
        {
            public uint size;
            public IntPtr data;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct FWP_VALUE0_UNION
        {
            [FieldOffset(0)]
            public byte uint8;
            [FieldOffset(0)]
            public ushort uint16;
            [FieldOffset(0)]
            public uint uint32;
            [FieldOffset(0)]
            public IntPtr uint64;
            [FieldOffset(0)]
            public IntPtr byteBlob;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FWP_VALUE0
        {
            public FWP_DATA_TYPE type;
            public FWP_VALUE0_UNION value;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FWP_CONDITION_VALUE0
        {
            public FWP_DATA_TYPE type;
            public FWP_VALUE0_UNION value;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FWPM_FILTER_CONDITION0
        {
            public Guid fieldKey;
            public FWP_MATCH_TYPE matchType;
            public FWP_CONDITION_VALUE0 conditionValue;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct FWPM_ACTION0
        {
            [FieldOffset(0)]
            public uint type;
            [FieldOffset(4)]
            public Guid filterType;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FWPM_FILTER0
        {
            public Guid filterKey;
            public FWPM_DISPLAY_DATA0 displayData;
            public uint flags;
            public IntPtr providerKey;
            public FWP_BYTE_BLOB providerData;
            public Guid layerKey;
            public Guid subLayerKey;
            public FWP_VALUE0 weight;
            public uint numFilterConditions;
            public IntPtr filterCondition;
            public FWPM_ACTION0 action;
            public ulong rawContext;
            public IntPtr reserved;
            public ulong filterId;
            public FWP_VALUE0 effectiveWeight;
        }

        [DllImport("fwpuclnt.dll", EntryPoint = "FwpmEngineOpen0", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        public static extern uint FwpmEngineOpen0(
            string serverName,
            uint authnService,
            IntPtr authIdentity,
            ref FWPM_SESSION0 session,
            out IntPtr engineHandle);

        [DllImport("fwpuclnt.dll", EntryPoint = "FwpmEngineClose0", CallingConvention = CallingConvention.StdCall)]
        public static extern uint FwpmEngineClose0(IntPtr engineHandle);

        [DllImport("fwpuclnt.dll", EntryPoint = "FwpmFilterAdd0", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        public static extern uint FwpmFilterAdd0(
            IntPtr engineHandle,
            ref FWPM_FILTER0 filter,
            IntPtr sd,
            out ulong id);

        [DllImport("fwpuclnt.dll", EntryPoint = "FwpmGetAppIdFromFileName0", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        public static extern uint FwpmGetAppIdFromFileName0(
            string fileName,
            out IntPtr appId);

        [DllImport("fwpuclnt.dll", EntryPoint = "FwpmFreeMemory0", CallingConvention = CallingConvention.StdCall)]
        public static extern void FwpmFreeMemory0(ref IntPtr p);
    }

    public static class AppBlocker
    {
        static readonly string[] Targets = new string[]
        {
            "ProcessHacker.exe",
            "ProcessHacker32.exe",
            "SystemInformer.exe"
        };

        const string IfeoBase = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        const string DebuggerValue = "systray.exe";

        static volatile bool _watchdogActive = false;
        static Thread _watchdogThread = null;

        public static bool IsAdministrator()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    System.Security.Principal.WindowsPrincipal p = new System.Security.Principal.WindowsPrincipal(id);
                    return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        public static bool IsBlocked()
        {
            if (_watchdogActive) return true;
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default))
                using (RegistryKey ifeo = baseKey.OpenSubKey(IfeoBase, false))
                {
                    if (ifeo != null)
                    {
                        foreach (string t in Targets)
                        {
                            using (RegistryKey sub = ifeo.OpenSubKey(t, false))
                            {
                                if (sub != null)
                                {
                                    object dbg = sub.GetValue("Debugger");
                                    if (dbg != null && string.Equals(dbg.ToString(), DebuggerValue, StringComparison.OrdinalIgnoreCase))
                                        return true;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        public static CleanResult ToggleBlock()
        {
            WinCleaner.Licensing.LicenseGuard.AssertOperational();
            CleanResult r = new CleanResult();
            bool willBlock = !IsBlocked();

            KillTargets();

            if (willBlock) StartWatchdog();
            else StopWatchdog();

            RegistryView[] views = Environment.Is64BitOperatingSystem
                ? new RegistryView[] { RegistryView.Registry64, RegistryView.Registry32 }
                : new RegistryView[] { RegistryView.Registry32 };

            foreach (RegistryView v in views)
            {
                try
                {
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, v))
                    using (RegistryKey ifeo = baseKey.OpenSubKey(IfeoBase, true))
                    {
                        if (ifeo == null) continue;
                        foreach (string t in Targets)
                        {
                            if (willBlock)
                            {
                                using (RegistryKey sub = ifeo.CreateSubKey(t))
                                {
                                    if (sub != null)
                                    {
                                        sub.SetValue("Debugger", DebuggerValue, RegistryValueKind.String);
                                        r.RegKeys++;
                                    }
                                }
                            }
                            else
                            {
                                try { ifeo.DeleteSubKeyTree(t, false); r.RegKeys++; } catch { }
                            }
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    if (willBlock && !IsAdministrator())
                    {
                        r.Errors.Add("Для системной блокировки IFEO запустите WinCleaner от имени администратора. Запущен локальный перехват.");
                    }
                }
                catch (Exception ex) { r.Errors.Add("IFEO: " + ex.Message); }
            }
            return r;
        }

        public static void StartWatchdog()
        {
            if (_watchdogActive) return;
            _watchdogActive = true;
            _watchdogThread = new Thread(WatchdogLoop);
            _watchdogThread.IsBackground = true;
            _watchdogThread.Priority = ThreadPriority.BelowNormal;
            _watchdogThread.Start();
        }

        public static void StopWatchdog() { _watchdogActive = false; }

        static void WatchdogLoop()
        {
            while (_watchdogActive)
            {
                KillTargets();
                Thread.Sleep(1000);
            }
        }

        public static void KillTargets()
        {
            string[] names = new string[] { "ProcessHacker", "ProcessHacker32", "SystemInformer" };
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    Process[] procs = Process.GetProcessesByName(names[i]);
                    for (int j = 0; j < procs.Length; j++)
                    {
                        try { procs[j].Kill(); } catch { }
                        finally { try { procs[j].Dispose(); } catch { } }
                    }
                }
                catch { }
            }
        }
    }

    public static class NetLockdown
    {
        const string RegKeyPath = @"Software\WinCleaner";
        const string RegValueName = "NetLocked";
        const string InternetSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        static readonly object _sync = new object();
        static volatile bool _isLocked = false;
        static IntPtr _wfpEngine = IntPtr.Zero;
        static Thread _killerThread = null;
        static HashSet<string> _allowedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static readonly string[] AllowedProcessNames = new string[]
        {
            "AnyDesk", "javaw", "java", "MinecraftLauncher", "Minecraft",
            "Minecraft.Windows", "TLauncher", "PrismLauncher", "ModrinthApp",
            "CurseForge", "Lunar Client", "BadlionClient", "Feather Client",
            "svchost", "System", "lsass", "services"
        };

        static NetLockdown()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, false))
                {
                    if (k != null)
                    {
                        object ps = k.GetValue("ProxyServer");
                        if (ps != null && string.Equals(ps.ToString(), "127.0.0.1:1", StringComparison.OrdinalIgnoreCase))
                        {
                            SetSystemProxyTrap(false);
                        }
                    }
                }
            }
            catch { }
        }

        public static bool IsLocked() { return _isLocked; }

        public static CleanResult Toggle()
        {
            WinCleaner.Licensing.LicenseGuard.AssertOperational();
            CleanResult r = new CleanResult();
            lock (_sync)
            {
                bool willLock = !_isLocked;
                try
                {
                    if (willLock) { EnableLockdown(r); _isLocked = true; }
                    else { DisableLockdown(r); _isLocked = false; }

                    try
                    {
                        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegKeyPath))
                        {
                            if (k != null)
                                k.SetValue(RegValueName, _isLocked ? 1 : 0, RegistryValueKind.DWord);
                        }
                    }
                    catch { }
                }
                catch (Exception ex) { r.Errors.Add("Сетевая изоляция: " + ex.Message); }
            }
            return r;
        }

        public static void ForceUnlockOnExit()
        {
            lock (_sync)
            {
                if (_isLocked)
                {
                    CleanResult dummy = new CleanResult();
                    DisableLockdown(dummy);
                    _isLocked = false;
                }
            }
        }

        static void EnableLockdown(CleanResult r)
        {
            List<string> paths = FindAllowedExecutables();
            _allowedPaths = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            if (StartWfpSession(paths)) r.RegKeys += paths.Count;
            SetSystemProxyTrap(true);
            r.RegKeys++;
            StartTcpKiller();
            SweepTcpConnections();
        }

        static void DisableLockdown(CleanResult r)
        {
            StopTcpKiller();
            StopWfpSession();
            SetSystemProxyTrap(false);
            r.RegKeys++;
        }

        static bool StartWfpSession(List<string> allowedFiles)
        {
            StopWfpSession();
            try
            {
                NativeMethods.FWPM_SESSION0 session = new NativeMethods.FWPM_SESSION0();
                session.displayData.name = "WinCleaner Kernel NetLock";
                session.displayData.description = "Direct WFP Isolation";
                session.flags = NativeMethods.FWPM_SESSION_FLAG_DYNAMIC;

                IntPtr engine;
                uint status = NativeMethods.FwpmEngineOpen0(null, NativeMethods.RPC_C_AUTHN_WINNT, IntPtr.Zero, ref session, out engine);
                if (status != 0 || engine == IntPtr.Zero) return false;

                _wfpEngine = engine;

                AddPortPermitFilter(engine, NativeMethods.FWPM_LAYER_ALE_AUTH_CONNECT_V4, 53);
                AddPortPermitFilter(engine, NativeMethods.FWPM_LAYER_ALE_AUTH_CONNECT_V6, 53);
                AddPortPermitFilter(engine, NativeMethods.FWPM_LAYER_ALE_AUTH_CONNECT_V4, 67);
                AddPortPermitFilter(engine, NativeMethods.FWPM_LAYER_ALE_AUTH_CONNECT_V4, 68);
                AddPortPermitFilter(engine, NativeMethods.FWPM_LAYER_ALE_AUTH_CONNECT_V4, 123);

                foreach (string file in allowedFiles)
                {
                    if (string.IsNullOrEmpty(file) || !File.Exists(file)) continue;
                    AddAppPermitFilter(engine, NativeMethods.FWPM_LAYER_ALE_AUTH_CONNECT_V4, file);
                    AddAppPermitFilter(engine, NativeMethods.FWPM_LAYER_ALE_AUTH_CONNECT_V6, file);
                    AddAppPermitFilter(engine, NativeMethods.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4, file);
                    AddAppPermitFilter(engine, NativeMethods.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6, file);
                }

                AddBlockAllFilter(engine, NativeMethods.FWPM_LAYER_ALE_AUTH_CONNECT_V4);
                AddBlockAllFilter(engine, NativeMethods.FWPM_LAYER_ALE_AUTH_CONNECT_V6);

                return true;
            }
            catch
            {
                StopWfpSession();
                return false;
            }
        }

        static void StopWfpSession()
        {
            if (_wfpEngine != IntPtr.Zero)
            {
                try { NativeMethods.FwpmEngineClose0(_wfpEngine); } catch { }
                _wfpEngine = IntPtr.Zero;
            }
        }

        static void AddAppPermitFilter(IntPtr engine, Guid layer, string filePath)
        {
            IntPtr appBlobPtr = IntPtr.Zero, condPtr = IntPtr.Zero, weightPtr = IntPtr.Zero;
            try
            {
                uint st = NativeMethods.FwpmGetAppIdFromFileName0(filePath, out appBlobPtr);
                if (st != 0 || appBlobPtr == IntPtr.Zero) return;

                NativeMethods.FWPM_FILTER_CONDITION0 cond = new NativeMethods.FWPM_FILTER_CONDITION0();
                cond.fieldKey = NativeMethods.FWPM_CONDITION_ALE_APP_ID;
                cond.matchType = NativeMethods.FWP_MATCH_TYPE.FWP_MATCH_EQUAL;
                cond.conditionValue.type = NativeMethods.FWP_DATA_TYPE.FWP_BYTE_BLOB_TYPE;
                cond.conditionValue.value.byteBlob = appBlobPtr;

                condPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeMethods.FWPM_FILTER_CONDITION0)));
                Marshal.StructureToPtr(cond, condPtr, false);

                weightPtr = Marshal.AllocHGlobal(8);
                Marshal.WriteInt64(weightPtr, unchecked((long)0xFFFFFFFFFFFFFFFEUL));

                NativeMethods.FWPM_FILTER0 filter = new NativeMethods.FWPM_FILTER0();
                filter.displayData.name = "WinCleaner Allow App";
                filter.layerKey = layer;
                filter.weight.type = NativeMethods.FWP_DATA_TYPE.FWP_UINT64;
                filter.weight.value.uint64 = weightPtr;
                filter.numFilterConditions = 1;
                filter.filterCondition = condPtr;
                filter.action.type = NativeMethods.FWP_ACTION_PERMIT;

                ulong id;
                NativeMethods.FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out id);
            }
            catch { }
            finally
            {
                if (weightPtr != IntPtr.Zero) Marshal.FreeHGlobal(weightPtr);
                if (condPtr != IntPtr.Zero) Marshal.FreeHGlobal(condPtr);
                if (appBlobPtr != IntPtr.Zero) NativeMethods.FwpmFreeMemory0(ref appBlobPtr);
            }
        }

        static void AddPortPermitFilter(IntPtr engine, Guid layer, ushort port)
        {
            IntPtr condPtr = IntPtr.Zero, weightPtr = IntPtr.Zero;
            try
            {
                NativeMethods.FWPM_FILTER_CONDITION0 cond = new NativeMethods.FWPM_FILTER_CONDITION0();
                cond.fieldKey = NativeMethods.FWPM_CONDITION_IP_REMOTE_PORT;
                cond.matchType = NativeMethods.FWP_MATCH_TYPE.FWP_MATCH_EQUAL;
                cond.conditionValue.type = NativeMethods.FWP_DATA_TYPE.FWP_UINT16;
                cond.conditionValue.value.uint16 = port;

                condPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeMethods.FWPM_FILTER_CONDITION0)));
                Marshal.StructureToPtr(cond, condPtr, false);

                weightPtr = Marshal.AllocHGlobal(8);
                Marshal.WriteInt64(weightPtr, unchecked((long)0xFFFFFFFFFFFFFFFDUL));

                NativeMethods.FWPM_FILTER0 filter = new NativeMethods.FWPM_FILTER0();
                filter.displayData.name = "WinCleaner Allow Core Port";
                filter.layerKey = layer;
                filter.weight.type = NativeMethods.FWP_DATA_TYPE.FWP_UINT64;
                filter.weight.value.uint64 = weightPtr;
                filter.numFilterConditions = 1;
                filter.filterCondition = condPtr;
                filter.action.type = NativeMethods.FWP_ACTION_PERMIT;

                ulong id;
                NativeMethods.FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out id);
            }
            catch { }
            finally
            {
                if (weightPtr != IntPtr.Zero) Marshal.FreeHGlobal(weightPtr);
                if (condPtr != IntPtr.Zero) Marshal.FreeHGlobal(condPtr);
            }
        }

        static void AddBlockAllFilter(IntPtr engine, Guid layer)
        {
            IntPtr condPtr = IntPtr.Zero, weightPtr = IntPtr.Zero;
            try
            {
                NativeMethods.FWPM_FILTER_CONDITION0 cond = new NativeMethods.FWPM_FILTER_CONDITION0();
                cond.fieldKey = NativeMethods.FWPM_CONDITION_IP_REMOTE_ADDRESS;
                cond.matchType = NativeMethods.FWP_MATCH_TYPE.FWP_MATCH_NOT_EQUAL;
                cond.conditionValue.type = NativeMethods.FWP_DATA_TYPE.FWP_UINT32;
                cond.conditionValue.value.uint32 = 0x7F000001;

                condPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeMethods.FWPM_FILTER_CONDITION0)));
                Marshal.StructureToPtr(cond, condPtr, false);

                weightPtr = Marshal.AllocHGlobal(8);
                Marshal.WriteInt64(weightPtr, 100L);

                NativeMethods.FWPM_FILTER0 filter = new NativeMethods.FWPM_FILTER0();
                filter.displayData.name = "WinCleaner Block All Outbound";
                filter.layerKey = layer;
                filter.weight.type = NativeMethods.FWP_DATA_TYPE.FWP_UINT64;
                filter.weight.value.uint64 = weightPtr;
                filter.numFilterConditions = (layer == NativeMethods.FWPM_LAYER_ALE_AUTH_CONNECT_V4) ? 1u : 0u;
                filter.filterCondition = (layer == NativeMethods.FWPM_LAYER_ALE_AUTH_CONNECT_V4) ? condPtr : IntPtr.Zero;
                filter.action.type = NativeMethods.FWP_ACTION_BLOCK;

                ulong id;
                NativeMethods.FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out id);
            }
            catch { }
            finally
            {
                if (weightPtr != IntPtr.Zero) Marshal.FreeHGlobal(weightPtr);
                if (condPtr != IntPtr.Zero) Marshal.FreeHGlobal(condPtr);
            }
        }

        static void StartTcpKiller()
        {
            if (_killerThread != null && _killerThread.IsAlive) return;
            _killerThread = new Thread(TcpKillerLoop);
            _killerThread.IsBackground = true;
            _killerThread.Priority = ThreadPriority.AboveNormal;
            _killerThread.Start();
        }

        static void StopTcpKiller() { _isLocked = false; }

        static void TcpKillerLoop()
        {
            int tick = 0;
            while (_isLocked)
            {
                try
                {
                    SweepTcpConnections();
                    tick++;
                    if (tick % 20 == 0)
                    {
                        List<string> fresh = FindAllowedExecutables();
                        lock (_sync)
                        {
                            foreach (string p in fresh)
                            {
                                if (_allowedPaths.Add(p) && _wfpEngine != IntPtr.Zero)
                                {
                                    AddAppPermitFilter(_wfpEngine, NativeMethods.FWPM_LAYER_ALE_AUTH_CONNECT_V4, p);
                                    AddAppPermitFilter(_wfpEngine, NativeMethods.FWPM_LAYER_ALE_AUTH_CONNECT_V6, p);
                                }
                            }
                        }
                    }
                }
                catch { }
                Thread.Sleep(150);
            }
        }

        static HashSet<uint> GetAllowedPids()
        {
            HashSet<uint> pids = new HashSet<uint> { 0, 4 };
            try { pids.Add((uint)Process.GetCurrentProcess().Id); } catch { }
            for (int i = 0; i < AllowedProcessNames.Length; i++)
            {
                try
                {
                    Process[] procs = Process.GetProcessesByName(AllowedProcessNames[i]);
                    for (int j = 0; j < procs.Length; j++)
                    {
                        try { pids.Add((uint)procs[j].Id); } catch { }
                        finally { try { procs[j].Dispose(); } catch { } }
                    }
                }
                catch { }
            }
            return pids;
        }

        static void SweepTcpConnections()
        {
            HashSet<uint> allowedPids = GetAllowedPids();
            Dictionary<uint, bool> pidCache = new Dictionary<uint, bool>();
            int bufSize = 0;
            NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref bufSize, false, NativeMethods.AF_INET, NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0);
            if (bufSize <= 0) return;

            IntPtr tcpTablePtr = Marshal.AllocHGlobal(bufSize);
            try
            {
                if (NativeMethods.GetExtendedTcpTable(tcpTablePtr, ref bufSize, false, NativeMethods.AF_INET, NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0) != 0)
                    return;

                int numEntries = Marshal.ReadInt32(tcpTablePtr);
                IntPtr rowPtr = (IntPtr)((long)tcpTablePtr + 4);
                int rowSize = Marshal.SizeOf(typeof(NativeMethods.MIB_TCPROW_OWNER_PID));

                for (int i = 0; i < numEntries; i++)
                {
                    NativeMethods.MIB_TCPROW_OWNER_PID row = (NativeMethods.MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(rowPtr, typeof(NativeMethods.MIB_TCPROW_OWNER_PID));
                    rowPtr = (IntPtr)((long)rowPtr + rowSize);

                    if (row.state <= 2 || row.state >= 12) continue;
                    if ((row.remoteAddr & 0xFF) == 127 || (row.localAddr & 0xFF) == 127 || row.remoteAddr == 0) continue;

                    uint pid = row.owningPid;
                    if (allowedPids.Contains(pid)) continue;

                    bool isAllowed = false;
                    if (!pidCache.TryGetValue(pid, out isAllowed))
                    {
                        isAllowed = IsPidAllowedByPath((int)pid);
                        pidCache[pid] = isAllowed;
                    }

                    if (!isAllowed)
                    {
                        NativeMethods.MIB_TCPROW killRow = new NativeMethods.MIB_TCPROW
                        {
                            dwState = NativeMethods.MIB_TCP_STATE_DELETE_TCB,
                            dwLocalAddr = row.localAddr,
                            dwLocalPort = row.localPort,
                            dwRemoteAddr = row.remoteAddr,
                            dwRemotePort = row.remotePort
                        };
                        NativeMethods.SetTcpEntry(ref killRow);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(tcpTablePtr); }
        }

        static bool IsPidAllowedByPath(int pid)
        {
            try
            {
                IntPtr h = NativeMethods.OpenProcess(0x1000, false, pid);
                if (h != IntPtr.Zero)
                {
                    try
                    {
                        StringBuilder sb = new StringBuilder(1024);
                        int cap = sb.Capacity;
                        if (NativeMethods.QueryFullProcessImageName(h, 0, sb, ref cap))
                        {
                            string path = sb.ToString();
                            if (!string.IsNullOrEmpty(path))
                            {
                                lock (_sync) { if (_allowedPaths.Contains(path)) return true; }
                                string fn = Path.GetFileName(path);
                                if (fn.IndexOf("anydesk", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    fn.IndexOf("java", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    fn.IndexOf("minecraft", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    fn.IndexOf("lunar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    fn.IndexOf("feather", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    fn.IndexOf("tlauncher", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    fn.IndexOf("prism", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    fn.IndexOf("modrinth", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    lock (_sync) { _allowedPaths.Add(path); }
                                    return true;
                                }
                            }
                        }
                    }
                    finally { NativeMethods.CloseHandle(h); }
                }
            }
            catch { }
            return false;
        }

        static void SetSystemProxyTrap(bool enable)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, true))
                {
                    if (k != null)
                    {
                        if (enable)
                        {
                            k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                            k.SetValue("ProxyServer", "127.0.0.1:1", RegistryValueKind.String);
                            k.SetValue("ProxyOverride", "<local>;*.mojang.com;*.minecraft.net;*.microsoftonline.com;*.live.com;*.xboxlive.com;*.anydesk.com;*.net.anydesk.com", RegistryValueKind.String);
                        }
                        else
                        {
                            k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                            k.DeleteValue("ProxyServer", false);
                        }
                    }
                }
                NativeMethods.InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
                NativeMethods.InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
            }
            catch { }
        }

        static string GetProcessPath(Process p)
        {
            try
            {
                if (p.MainModule != null && File.Exists(p.MainModule.FileName))
                    return p.MainModule.FileName;
            }
            catch { }
            try
            {
                IntPtr h = NativeMethods.OpenProcess(0x1000, false, p.Id);
                if (h != IntPtr.Zero)
                {
                    try
                    {
                        StringBuilder sb = new StringBuilder(1024);
                        int cap = sb.Capacity;
                        if (NativeMethods.QueryFullProcessImageName(h, 0, sb, ref cap))
                        {
                            string path = sb.ToString();
                            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                        }
                    }
                    finally { NativeMethods.CloseHandle(h); }
                }
            }
            catch { }
            return null;
        }

        static void SafeCollectJava(string root, List<string> list)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            try
            {
                foreach (string file in Directory.GetFiles(root))
                {
                    string name = Path.GetFileName(file);
                    if (string.Equals(name, "javaw.exe", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "java.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!list.Contains(file)) list.Add(file);
                    }
                }
                foreach (string sub in Directory.GetDirectories(root))
                    SafeCollectJava(sub, list);
            }
            catch { }
        }

        static List<string> FindAllowedExecutables()
        {
            List<string> list = new List<string>();
            try
            {
                string own = Application.ExecutablePath;
                if (!string.IsNullOrEmpty(own) && File.Exists(own) && !list.Contains(own)) list.Add(own);
            }
            catch { }

            foreach (string pn in AllowedProcessNames)
            {
                try
                {
                    foreach (Process p in Process.GetProcessesByName(pn))
                    {
                        try
                        {
                            string path = GetProcessPath(p);
                            if (!string.IsNullOrEmpty(path) && File.Exists(path) && !list.Contains(path)) list.Add(path);
                        }
                        catch { }
                        finally { try { p.Dispose(); } catch { } }
                    }
                }
                catch { }
            }

            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pfx = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            string[] fixedPaths = new string[]
            {
                Path.Combine(pf, @"AnyDesk\AnyDesk.exe"),
                Path.Combine(pfx, @"AnyDesk\AnyDesk.exe"),
                Path.Combine(appData, @"AnyDesk\AnyDesk.exe"),
                Path.Combine(localApp, @"AnyDesk\AnyDesk.exe"),
                Path.Combine(userProfile, @"Downloads\AnyDesk.exe"),
                Path.Combine(userProfile, @"Desktop\AnyDesk.exe"),
                @"C:\ProgramData\AnyDesk\AnyDesk.exe",
                Path.Combine(pfx, @"Minecraft Launcher\MinecraftLauncher.exe"),
                Path.Combine(pf, @"Minecraft Launcher\MinecraftLauncher.exe"),
                Path.Combine(localApp, @"Programs\launcher\Minecraft.exe"),
                Path.Combine(appData, @".minecraft\launcher.exe"),
                Path.Combine(localApp, @"Programs\PrismLauncher\prismlauncher.exe"),
                Path.Combine(localApp, @"Programs\Modrinth App\Modrinth App.exe")
            };
            foreach (string p in fixedPaths)
                if (File.Exists(p) && !list.Contains(p)) list.Add(p);

            List<string> javaDirs = new List<string>
            {
                Path.Combine(appData, @".minecraft\runtime"),
                Path.Combine(appData, @".minecraft"),
                Path.Combine(appData, @".tlauncher"),
                Path.Combine(userProfile, @"curseforge\minecraft\Install\runtime"),
                Path.Combine(userProfile, @".lunarclient\offline\multiver"),
                Path.Combine(userProfile, @".feather\player-runtimes"),
                Path.Combine(localApp, @"Programs\PrismLauncher\java"),
                Path.Combine(localApp, @"Programs\Modrinth App"),
                Path.Combine(pf, @"Java"),
                Path.Combine(pfx, @"Java"),
                Path.Combine(pf, @"Eclipse Adoptium"),
                Path.Combine(pf, @"BellSoft"),
                Path.Combine(pf, @"Zulu"),
                Path.Combine(pf, @"Microsoft\jdk")
            };

            foreach (string dir in javaDirs) SafeCollectJava(dir, list);
            return list;
        }
    }

    public static class Cleaner
    {
        public static void EnsureCloudEngineReady()
        {
            WinCleaner.Licensing.LicenseGuard.AssertOperational();
            string cloudJson = WinCleaner.Licensing.LicenseManager.GetCloudPayloadJson();
            if (string.IsNullOrEmpty(cloudJson))
            {
                Process.GetCurrentProcess().Kill();
                throw new InvalidOperationException("Защита сервера: правила очистки не получены с сервера.");
            }
        }

        const string Exp = @"Software\Microsoft\Windows\CurrentVersion\Explorer\";

        static readonly string[] BagKeys = new string[]
        {
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags",
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\BagMRU",
            @"Software\Classes\Wow6432Node\Local Settings\Software\Microsoft\Windows\Shell\Bags",
            @"Software\Classes\Wow6432Node\Local Settings\Software\Microsoft\Windows\Shell\BagMRU",
            @"Software\Microsoft\Windows\Shell\Bags",
            @"Software\Microsoft\Windows\Shell\BagMRU",
            @"Software\Microsoft\Windows\ShellNoRoam\Bags",
            @"Software\Microsoft\Windows\ShellNoRoam\BagMRU",
            Exp + "Streams",
            Exp + "StreamMRU"
        };

        static readonly string[] DialogKeys = new string[]
        {
            Exp + @"ComDlg32\OpenSavePidlMRU",
            Exp + @"ComDlg32\OpenSaveMRU",
            Exp + @"ComDlg32\LastVisitedPidlMRU",
            Exp + @"ComDlg32\LastVisitedPidlMRULegacy",
            Exp + @"ComDlg32\LastVisitedMRU",
            Exp + @"ComDlg32\CIDSizeMRU",
            Exp + @"ComDlg32\FirstFolder"
        };

        public static Action<string> Progress;
        public static Action<int, string> DetailedProgress;

        public static void ReportProgress(string text) { Step(text); }

        static void Step(string text)
        {
            Action<string> p = Progress;
            if (p != null) { try { p(text); } catch { } }
        }

        static void ClearWinVHistory(CleanResult r)
        {
            string error = null;
            Thread th = new Thread(delegate()
            {
                try
                {
                    Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                    Type t = Type.GetType("Windows.ApplicationModel.DataTransfer.Clipboard, Windows.ApplicationModel.DataTransfer, ContentType=WindowsRuntime");
                    if (t != null)
                    {
                        MethodInfo m = t.GetMethod("ClearHistory", BindingFlags.Public | BindingFlags.Static);
                        if (m != null) m.Invoke(null, null);
                    }
                }
                catch (Exception ex)
                {
                    Exception e = ex.InnerException != null ? ex.InnerException : ex;
                    error = e.Message;
                }
            });
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Start();
            if (!th.Join(2000))
                r.Errors.Add("Win+V: система не ответила за 2 с, шаг пропущен");
            else if (error != null)
                r.Errors.Add("Win+V: " + error);
        }

        public static CleanResult CleanClipboard()
        {
            WinCleaner.Licensing.LicenseGuard.AssertOperational();
            CleanResult r = new CleanResult();
            Step("Буфер: история Win+V…");
            ClearWinVHistory(r);

            Step("Буфер: текущее содержимое…");
            for (int i = 0; i < 5; i++)
            {
                if (NativeMethods.OpenClipboard(IntPtr.Zero))
                {
                    NativeMethods.EmptyClipboard();
                    NativeMethods.CloseClipboard();
                    break;
                }
                Thread.Sleep(80);
            }

            Step("Буфер: кэш истории…");
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            CleanDir(Path.Combine(local, @"Microsoft\Windows\Clipboard\HistoryData"), r, true);
            return r;
        }

        public static CleanResult CleanExplorer() { return WithExplorerStopped(true, false); }
        public static CleanResult CleanShellBags() { return WithExplorerStopped(false, true); }
        public static CleanResult CleanAll()
        {
            EnsureCloudEngineReady();
            CleanResult r = CleanClipboard();
            r.Merge(WithExplorerStopped(true, true));
            return r;
        }

        static CleanResult WithExplorerStopped(bool history, bool bags)
        {
            EnsureCloudEngineReady();
            WinCleaner.Licensing.LicenseGuard.AssertOperational();
            CleanResult r = new CleanResult();
            if (history) { try { NativeMethods.SHAddToRecentDocs(0, IntPtr.Zero); } catch { } }

            Step("Перезапуск Проводника…");
            KillExplorer();
            try
            {
                Step(history && bags ? "Проводник и Shell Bags…" : (history ? "Параметры Проводника…" : "Shell Bags…"));
                if (history) HistoryPart(r);
                if (bags) BagsPart(r);
            }
            catch (Exception ex) { r.Errors.Add(ex.Message); }
            finally
            {
                Step("Запуск Проводника…");
                StartExplorer();
            }
            return r;
        }

        static void HistoryPart(CleanResult r)
        {
            string recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            try
            {
                if (Directory.Exists(recent))
                {
                    foreach (string f in Directory.GetFiles(recent, "*.lnk", SearchOption.TopDirectoryOnly))
                        DeleteFileQuiet(f, r);
                }
            }
            catch { }

            CleanDir(Path.Combine(recent, "AutomaticDestinations"), r, false);
            CleanDir(Path.Combine(recent, "CustomDestinations"), r, false);

            ClearAll(Exp + "RecentDocs", r);
            ClearValues(Exp + "TypedPaths", r);
            ClearAll(Exp + "WordWheelQuery", r);
            ClearValues(Exp + "RunMRU", r);

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            try
            {
                string expDir = Path.Combine(local, @"Microsoft\Windows\Explorer");
                if (Directory.Exists(expDir))
                {
                    foreach (string db in Directory.GetFiles(expDir, "*cache*.db", SearchOption.TopDirectoryOnly))
                        DeleteFileQuiet(db, r);
                }
            }
            catch { }
            string legacy = Path.Combine(local, "IconCache.db");
            if (File.Exists(legacy)) DeleteFileQuiet(legacy, r);
        }

        static void BagsPart(CleanResult r)
        {
            foreach (string k in BagKeys) DeleteTree(k, r);
            foreach (string k in DialogKeys) DeleteTree(k, r);
        }

        public static void ClearDialogHistory(CleanResult r)
        {
            foreach (string k in DialogKeys) DeleteTree(k, r);
        }

        public static void ForgetRecentFile(string name)
        {
            try
            {
                string recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                string lnk = Path.Combine(recent, name + ".lnk");
                if (File.Exists(lnk))
                {
                    File.SetAttributes(lnk, FileAttributes.Normal);
                    File.Delete(lnk);
                }
            }
            catch { }
        }

        public static void KillExplorer()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("explorer"))
                {
                    try { p.Kill(); p.WaitForExit(2000); } catch { }
                }
            }
            catch { }
        }

        public static void StartExplorer()
        {
            Thread.Sleep(1000);
            try
            {
                if (Process.GetProcessesByName("explorer").Length == 0)
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                }
            }
            catch { }
        }

        static void DeleteFileQuiet(string path, CleanResult r)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                long len = fi.Length;
                fi.Attributes = FileAttributes.Normal;
                fi.Delete();
                r.Bytes += len;
                r.Files++;
            }
            catch { }
        }

        static void CleanDir(string path, CleanResult r, bool recursive)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                DirectoryInfo d = new DirectoryInfo(path);
                foreach (FileInfo f in d.GetFiles()) DeleteFileQuiet(f.FullName, r);
                if (recursive)
                {
                    foreach (DirectoryInfo s in d.GetDirectories())
                    {
                        try { CleanDir(s.FullName, r, true); s.Delete(); } catch { }
                    }
                }
            }
            catch (Exception ex) { r.Errors.Add(path + ": " + ex.Message); }
        }

        static void DeleteTree(string path, CleanResult r)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(path)) { if (k == null) return; }
                Registry.CurrentUser.DeleteSubKeyTree(path, false);
                r.RegKeys++;
            }
            catch (Exception ex) { r.Errors.Add(path + ": " + ex.Message); }
        }

        static void ClearValues(string path, CleanResult r)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(path, true))
                {
                    if (k == null) return;
                    foreach (string v in k.GetValueNames()) { try { k.DeleteValue(v, false); r.RegKeys++; } catch { } }
                }
            }
            catch (Exception ex) { r.Errors.Add(path + ": " + ex.Message); }
        }

        static void ClearAll(string path, CleanResult r)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(path, true))
                {
                    if (k == null) return;
                    foreach (string s in k.GetSubKeyNames()) { try { k.DeleteSubKeyTree(s, false); r.RegKeys++; } catch { } }
                    foreach (string v in k.GetValueNames()) { try { k.DeleteValue(v, false); r.RegKeys++; } catch { } }
                }
            }
            catch (Exception ex) { r.Errors.Add(path + ": " + ex.Message); }
        }

        public static void ClearAppCompatFlags(CleanResult r)
        {
            string[] userKeys = new string[]
            {
                @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
                @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Persisted",
                @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"
            };
            foreach (string pk in userKeys) ClearValues(pk, r);

            string[] machineKeys = new string[]
            {
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"
            };
            foreach (string mk in machineKeys)
            {
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(mk, true))
                    {
                        if (k == null) continue;
                        foreach (string v in k.GetValueNames())
                        {
                            try { k.DeleteValue(v, false); r.RegKeys++; } catch { }
                        }
                    }
                }
                catch { }
            }
        }

        public static void ClearReadyBoot(CleanResult r)
        {
            try
            {
                string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string rbDir = Path.Combine(windir, @"Prefetch\ReadyBoot");
                if (Directory.Exists(rbDir))
                {
                    foreach (string f in Directory.GetFiles(rbDir, "*.*", SearchOption.TopDirectoryOnly))
                    {
                        DeleteFileQuiet(f, r);
                    }
                }
            }
            catch { }
        }

        public static CleanResult CleanWinPrefetchView()
        {
            WinCleaner.Licensing.LicenseGuard.AssertOperational();
            CleanResult r = new CleanResult();

            Step("Завершение Проводника…");
            KillExplorer();

            try
            {
                Step("Отключение службы SysMain и Prefetcher…");
                DisableAndStopPrefetch();

                Step("Очистка каталога Prefetch (*.pf) и ReadyBoot…");
                ClearPrefetchStealth(r);
                ClearReadyBoot(r);

                Step("Очистка кэша выполнения AppCompatCache (ShimCache)…");
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\AppCompatCache", true))
                    {
                        if (k != null && k.GetValue("AppCompatCache") != null)
                        {
                            k.DeleteValue("AppCompatCache", false);
                            r.RegKeys++;
                        }
                    }
                }
                catch { }

                Step("Очистка реестра UserAssist (история запусков)…");
                ClearUserAssist(r);

                Step("Очистка BAM и DAM (фоновая активность процессов)…");
                ClearBamAndDam(r);

                Step("Очистка FeatureUsage (AppSwitched / AppLaunch)…");
                ClearFeatureUsage(r);

                Step("Очистка MuiCache, RunMRU и TypedPaths…");
                ClearMuiCacheAndRun(r);

                Step("Очистка записей AppCompatFlags и Compatibility Assistant…");
                ClearAppCompatFlags(r);

                Step("Очистка списков недавних документов RecentDocs…");
                ClearRecentAndJumpLists(r);

                Step("Очистка журналов планировщика и системы…");
                ClearOperationalLogs();

                Step("Зачистка собственных следов и артефактов аудита…");
                ClearSelfTraces();
            }
            catch (Exception ex)
            {
                r.Errors.Add("WinPrefetchView: " + ex.Message);
            }
            finally
            {
                Step("Перезапуск Проводника…");
                StartExplorer();
                Thread.Sleep(1200);
                ClearPrefetchStealth(r);
                ClearSelfTraces();
            }

            return r;
        }

        public static CleanResult CleanExecutedProgramsList()
        {
            WinCleaner.Licensing.LicenseGuard.AssertOperational();
            CleanResult r = new CleanResult();

            Step("Завершение Проводника…");
            KillExplorer();

            try
            {
                Step("Отключение службы SysMain и Prefetcher…");
                DisableAndStopPrefetch();

                Step("Очистка реестра UserAssist (история запусков)…");
                ClearUserAssist(r);

                Step("Очистка BAM и DAM (фоновая активность процессов)…");
                ClearBamAndDam(r);

                Step("Очистка FeatureUsage (AppSwitched / AppLaunch)…");
                ClearFeatureUsage(r);

                Step("Очистка MuiCache, RunMRU, TypedPaths и AppCompatCache…");
                ClearMuiCacheAndRun(r);

                Step("Очистка записей AppCompatFlags и Compatibility Assistant…");
                ClearAppCompatFlags(r);

                Step("Очистка списков недавних документов RecentDocs и JumpLists…");
                ClearRecentAndJumpLists(r);

                Step("Сброс истории запусков задач TaskCache…");
                ClearTaskCacheHistory(r);

                Step("Очистка журналов планировщика и системы…");
                ClearOperationalLogs();

                Step("Очистка Prefetch без генерации новых *.pf…");
                ClearPrefetchStealth(r);

                Step("Очистка отчетов сбоев WER и дампов памяти…");
                ClearWerAndDumps(r);

                Step("Зачистка собственных следов и артефактов аудита…");
                ClearSelfTraces();
            }
            catch (Exception ex)
            {
                r.Errors.Add("ExecutedProgramsList: " + ex.Message);
            }
            finally
            {
                Step("Перезапуск Проводника…");
                StartExplorer();
                Thread.Sleep(1200);
                ClearPrefetchStealth(r);
                ClearSelfTraces();
            }

            return r;
        }

        public static CleanResult CleanLastActivityView()
        {
            WinCleaner.Licensing.LicenseGuard.AssertOperational();
            CleanResult r = new CleanResult();

            Step("Завершение Проводника…");
            KillExplorer();

            try
            {
                Step("Отключение службы SysMain и Prefetcher…");
                DisableAndStopPrefetch();

                Step("Очистка журналов планировщика TaskScheduler и системы…");
                ClearOperationalLogs();

                Step("Сброс истории запусков задач TaskCache (DynamicInfo)…");
                ClearTaskCacheHistory(r);

                Step("Очистка реестра UserAssist (история запусков)…");
                ClearUserAssist(r);

                Step("Очистка BAM и DAM (фоновая активность процессов)…");
                ClearBamAndDam(r);

                Step("Очистка FeatureUsage (AppSwitched / AppLaunch)…");
                ClearFeatureUsage(r);

                Step("Очистка истории диалогов Open/Save и списков MRU…");
                ClearDialogHistory(r);

                Step("Очистка RecentDocs, JumpLists и недавних документов…");
                ClearRecentAndJumpLists(r);

                Step("Очистка Shell Bags (просмотр папок в Explorer)…");
                BagsPart(r);

                Step("Очистка MuiCache, RunMRU, TypedPaths и AppCompatCache…");
                ClearMuiCacheAndRun(r);

                Step("Очистка Prefetch без генерации новых *.pf…");
                ClearPrefetchStealth(r);

                Step("Очистка отчетов сбоев WER и дампов памяти…");
                ClearWerAndDumps(r);

                Step("Зачистка собственных следов и артефактов аудита…");
                ClearSelfTraces();
            }
            catch (Exception ex)
            {
                r.Errors.Add("LastActivityView: " + ex.Message);
            }
            finally
            {
                Step("Перезапуск Проводника…");
                StartExplorer();
                Thread.Sleep(1200);
                ClearPrefetchStealth(r);
                ClearSelfTraces();
            }

            return r;
        }

        public static void DisableAndStopPrefetch()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", true))
                {
                    if (k != null) k.SetValue("EnablePrefetcher", 0, RegistryValueKind.DWord);
                }
            }
            catch { }

            try
            {
                IntPtr hSCM = NativeMethods.OpenSCManager(null, null, 0x0001 | 0x0004);
                if (hSCM == IntPtr.Zero) hSCM = NativeMethods.OpenSCManager(null, null, 0xF003F);
                if (hSCM != IntPtr.Zero)
                {
                    try
                    {
                        IntPtr hSvc = NativeMethods.OpenService(hSCM, "SysMain", 0x0020 | 0x0004);
                        if (hSvc != IntPtr.Zero)
                        {
                            try
                            {
                                NativeMethods.SERVICE_STATUS st = new NativeMethods.SERVICE_STATUS();
                                NativeMethods.ControlService(hSvc, 1, ref st);
                            }
                            finally { NativeMethods.CloseServiceHandle(hSvc); }
                        }
                    }
                    finally { NativeMethods.CloseServiceHandle(hSCM); }
                }
            }
            catch { }
        }

        public static void ClearOperationalLogs()
        {
            string[] channels = new string[]
            {
                "Microsoft-Windows-TaskScheduler/Operational",
                "Microsoft-Windows-Diagnostics-Performance/Operational",
                "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational",
                "Microsoft-Windows-Power-Troubleshooter/Operational",
                "Microsoft-Windows-NetworkProfile/Operational",
                "Microsoft-Windows-WLAN-AutoConfig/Operational",
                "Microsoft-Windows-Kernel-PnP/Configuration",
                "Microsoft-Windows-Bits-Client/Operational",
                "Microsoft-Windows-WindowsUpdateClient/Operational",
                "Microsoft-Windows-PowerShell/Operational",
                "Microsoft-Windows-Windows Defender/Operational"
            };
            foreach (string ch in channels)
            {
                try
                {
                    System.Diagnostics.Eventing.Reader.EventLogSession.GlobalSession.ClearLog(ch);
                }
                catch { }
            }

            string[] standardLogs = new string[] { "Application", "System", "Setup" };
            foreach (string log in standardLogs)
            {
                try
                {
                    using (EventLog el = new EventLog(log))
                    {
                        el.Clear();
                    }
                }
                catch { }
            }
        }

        public static void ClearTaskCacheHistory(CleanResult r)
        {
            bool impersonated = TryImpersonateSystem();
            try
            {
                string[] cacheKeys = new string[]
                {
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks",
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Plain",
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Boot",
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Logon"
                };

                foreach (string rootPath in cacheKeys)
                {
                    try
                    {
                        using (RegistryKey root = Registry.LocalMachine.OpenSubKey(rootPath, true))
                        {
                            if (root == null) continue;
                            foreach (string subName in root.GetSubKeyNames())
                            {
                                using (RegistryKey taskKey = root.OpenSubKey(subName, true))
                                {
                                    if (taskKey == null) continue;
                                    try
                                    {
                                        if (taskKey.GetValue("DynamicInfo") != null)
                                        {
                                            taskKey.DeleteValue("DynamicInfo", false);
                                            r.RegKeys++;
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                if (impersonated) NativeMethods.RevertToSelf();
            }
        }

        public static void ClearUserAssist(CleanResult r)
        {
            const string uaBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";
            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(uaBase, true))
                {
                    if (root == null) return;
                    foreach (string sub in root.GetSubKeyNames())
                    {
                        using (RegistryKey sk = root.OpenSubKey(sub, true))
                        {
                            if (sk == null) continue;
                            using (RegistryKey countKey = sk.OpenSubKey("Count", true))
                            {
                                if (countKey != null)
                                {
                                    foreach (string valName in countKey.GetValueNames())
                                    {
                                        try { countKey.DeleteValue(valName, false); r.RegKeys++; } catch { }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { r.Errors.Add("UserAssist: " + ex.Message); }
        }

        public static void ClearFeatureUsage(CleanResult r)
        {
            string[] featureKeys = new string[]
            {
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppSwitched",
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppLaunch",
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\ShowDesktop",
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppBadgeUpdated"
            };
            foreach (string fk in featureKeys)
            {
                ClearValues(fk, r);
            }
        }

        public static void ClearBamAndDam(CleanResult r)
        {
            bool impersonated = TryImpersonateSystem();
            try
            {
                string[] bamPaths = new string[]
                {
                    @"SYSTEM\CurrentControlSet\Services\bam\UserSettings",
                    @"SYSTEM\CurrentControlSet\Services\bam\State\UserSettings",
                    @"SYSTEM\CurrentControlSet\Services\dam\UserSettings",
                    @"SYSTEM\CurrentControlSet\Services\dam\State\UserSettings"
                };

                foreach (string basePath in bamPaths)
                {
                    try
                    {
                        using (RegistryKey baseKey = Registry.LocalMachine.OpenSubKey(basePath, true))
                        {
                            if (baseKey == null) continue;
                            foreach (string sidSub in baseKey.GetSubKeyNames())
                            {
                                using (RegistryKey userKey = baseKey.OpenSubKey(sidSub, true))
                                {
                                    if (userKey == null) continue;
                                    foreach (string vName in userKey.GetValueNames())
                                    {
                                        if (vName.Equals("Version", StringComparison.OrdinalIgnoreCase) ||
                                            vName.Equals("SequenceNumber", StringComparison.OrdinalIgnoreCase))
                                            continue;

                                        try { userKey.DeleteValue(vName, false); r.RegKeys++; } catch { }
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                if (impersonated) NativeMethods.RevertToSelf();
            }
        }

        public static void ClearMuiCacheAndRun(CleanResult r)
        {
            string[] muiKeys = new string[]
            {
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
                @"Software\Microsoft\Windows\ShellNoRoam\MUICache"
            };
            foreach (string k in muiKeys) ClearValues(k, r);

            ClearValues(Exp + "RunMRU", r);
            ClearValues(Exp + "TypedPaths", r);
            ClearAll(Exp + "WordWheelQuery", r);

            string[] pcaKeys = new string[]
            {
                @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
                @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"
            };
            foreach (string pk in pcaKeys) ClearValues(pk, r);

            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\AppCompatCache", true))
                {
                    if (k != null && k.GetValue("AppCompatCache") != null)
                    {
                        k.DeleteValue("AppCompatCache", false);
                        r.RegKeys++;
                    }
                }
            }
            catch { }
        }

        public static void ClearRecentAndJumpLists(CleanResult r)
        {
            try
            {
                NativeMethods.SHAddToRecentDocs(0, IntPtr.Zero);
            }
            catch { }

            string recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            try
            {
                if (Directory.Exists(recent))
                {
                    foreach (string f in Directory.GetFiles(recent, "*.lnk", SearchOption.TopDirectoryOnly))
                        DeleteFileQuiet(f, r);
                }
            }
            catch { }

            CleanDir(Path.Combine(recent, "AutomaticDestinations"), r, false);
            CleanDir(Path.Combine(recent, "CustomDestinations"), r, false);
            ClearAll(Exp + "RecentDocs", r);
        }

        public static void ClearPrefetchStealth(CleanResult r)
        {
            try
            {
                string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string pfDir = Path.Combine(windir, "Prefetch");
                if (Directory.Exists(pfDir))
                {
                    foreach (string pf in Directory.GetFiles(pfDir, "*.pf", SearchOption.TopDirectoryOnly))
                    {
                        DeleteFileQuiet(pf, r);
                    }
                }
            }
            catch (Exception ex) { r.Errors.Add("Prefetch: " + ex.Message); }
        }

        public static void ClearWerAndDumps(CleanResult r)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            string[] dirs = new string[]
            {
                Path.Combine(local, "CrashDumps"),
                Path.Combine(local, @"Microsoft\Windows\WER\ReportArchive"),
                Path.Combine(local, @"Microsoft\Windows\WER\ReportQueue"),
                Path.Combine(local, @"Microsoft\Windows\WER\Temp"),
                Path.Combine(progData, @"Microsoft\Windows\WER\ReportArchive"),
                Path.Combine(progData, @"Microsoft\Windows\WER\ReportQueue"),
                Path.Combine(progData, @"Microsoft\Windows\WER\Temp"),
                Path.Combine(windir, "Minidump")
            };

            foreach (string d in dirs)
            {
                CleanDir(d, r, true);
            }

            string memDump = Path.Combine(windir, "MEMORY.DMP");
            if (File.Exists(memDump)) DeleteFileQuiet(memDump, r);
        }

        public static void ClearSelfTraces()
        {
            try
            {
                string exeName = Path.GetFileName(Application.ExecutablePath);
                string exeWithoutExt = Path.GetFileNameWithoutExtension(Application.ExecutablePath).ToUpperInvariant();

                string[] targetNames = new string[]
                {
                    exeWithoutExt,
                    "WINCLEANER",
                    "LASTACTIVITYVIEW",
                    "EXECUTEDPROGRAMSLIST",
                    "WINPREFETCHVIEW",
                    "BROWSINGHISTORYVIEW",
                    "NET.EXE",
                    "FSUTIL.EXE"
                };

                string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string pfDir = Path.Combine(windir, "Prefetch");
                if (Directory.Exists(pfDir))
                {
                    foreach (string pattern in targetNames)
                    {
                        try
                        {
                            foreach (string pf in Directory.GetFiles(pfDir, "*" + pattern + "*.pf", SearchOption.TopDirectoryOnly))
                            {
                                try { File.SetAttributes(pf, FileAttributes.Normal); File.Delete(pf); } catch { }
                            }
                        }
                        catch { }
                    }
                }

                const string uaBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(uaBase, true))
                {
                    if (root != null)
                    {
                        foreach (string sub in root.GetSubKeyNames())
                        {
                            using (RegistryKey sk = root.OpenSubKey(sub, true))
                            {
                                if (sk == null) continue;
                                using (RegistryKey countKey = sk.OpenSubKey("Count", true))
                                {
                                    if (countKey != null)
                                    {
                                        foreach (string valName in countKey.GetValueNames())
                                        {
                                            string rot = Rot13(valName);
                                            foreach (string t in targetNames)
                                            {
                                                if (rot.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)
                                                {
                                                    try { countKey.DeleteValue(valName, false); } catch { }
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                using (RegistryKey mui = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", true))
                {
                    if (mui != null)
                    {
                        foreach (string v in mui.GetValueNames())
                        {
                            foreach (string t in targetNames)
                            {
                                if (v.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    try { mui.DeleteValue(v, false); } catch { }
                                    break;
                                }
                            }
                        }
                    }
                }

                string[] fuKeys = new string[]
                {
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppSwitched",
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppLaunch"
                };
                foreach (string fk in fuKeys)
                {
                    using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(fk, true))
                    {
                        if (rk != null)
                        {
                            foreach (string v in rk.GetValueNames())
                            {
                                foreach (string t in targetNames)
                                {
                                    if (v.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        try { rk.DeleteValue(v, false); } catch { }
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        public static string Rot13(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            char[] array = input.ToCharArray();
            for (int i = 0; i < array.Length; i++)
            {
                int number = (int)array[i];
                if (number >= 'a' && number <= 'z')
                {
                    if (number > 'm') number -= 13;
                    else number += 13;
                }
                else if (number >= 'A' && number <= 'Z')
                {
                    if (number > 'M') number -= 13;
                    else number += 13;
                }
                array[i] = (char)number;
            }
            return new string(array);
        }

        public static void EnablePrivilege(string priv)
        {
            IntPtr hToken;
            if (NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), 0x0020 | 0x0008, out hToken))
            {
                try
                {
                    NativeMethods.LUID luid;
                    if (NativeMethods.LookupPrivilegeValue(null, priv, out luid))
                    {
                        NativeMethods.TOKEN_PRIVILEGES tp = new NativeMethods.TOKEN_PRIVILEGES();
                        tp.PrivilegeCount = 1;
                        tp.Luid = luid;
                        tp.Attributes = 0x00000002;
                        NativeMethods.AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                finally { NativeMethods.CloseHandle(hToken); }
            }
        }

        public static bool TryImpersonateSystem()
        {
            try
            {
                EnablePrivilege("SeDebugPrivilege");
                string[] targets = new string[] { "services", "svchost", "spoolsv", "winlogon", "lsass" };
                foreach (string name in targets)
                {
                    Process[] procs = Process.GetProcessesByName(name);
                    if (procs == null || procs.Length == 0) continue;
                    foreach (Process p in procs)
                    {
                        IntPtr hProc = NativeMethods.OpenProcess(0x1000 | 0x0400, false, p.Id);
                        if (hProc == IntPtr.Zero) continue;
                        try
                        {
                            IntPtr hToken;
                            if (NativeMethods.OpenProcessToken(hProc, 0x000E, out hToken))
                            {
                                try
                                {
                                    IntPtr hDup;
                                    if (NativeMethods.DuplicateTokenEx(hToken, 0x02000000, IntPtr.Zero, 2, 2, out hDup))
                                    {
                                        try
                                        {
                                            if (NativeMethods.ImpersonateLoggedOnUser(hDup))
                                                return true;
                                        }
                                        finally
                                        {
                                            NativeMethods.CloseHandle(hDup);
                                        }
                                    }
                                }
                                finally
                                {
                                    NativeMethods.CloseHandle(hToken);
                                }
                            }
                        }
                        finally
                        {
                            NativeMethods.CloseHandle(hProc);
                        }
                    }
                }
            }
            catch { }
            return false;
        }
    }

    public static class Shredder
    {
        static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        public static CleanResult Shred(IList<string> paths)
        {
            WinCleaner.Licensing.LicenseGuard.AssertOperational();
            CleanResult r = new CleanResult();
            int n = 0;
            foreach (string p in paths)
            {
                n++;
                int pct = (int)((n * 100.0) / paths.Count);
                Cleaner.ReportProgress("Уничтожение " + n + " из " + paths.Count + "…");
                Action<int, string> dp = Cleaner.DetailedProgress;
                if (dp != null) { try { dp(pct, "Уничтожение " + pct + "%"); } catch { } }
                try
                {
                    string full = Path.GetFullPath(p);
                    if (IsProtected(full))
                    {
                        r.Errors.Add("Защищённый путь, пропущено: " + full);
                        continue;
                    }
                    if (File.Exists(full)) ShredFile(full, r);
                    else if (Directory.Exists(full)) ShredDir(full, r);
                    else r.Errors.Add("Не найден: " + full);
                }
                catch (Exception ex) { r.Errors.Add(p + ": " + ex.Message); }
            }
            Cleaner.ClearDialogHistory(r);
            return r;
        }

        static void ShredFile(string path, CleanResult r)
        {
            string name = Path.GetFileName(path);
            try
            {
                FileAttributes attr = File.GetAttributes(path);
                if ((attr & FileAttributes.ReparsePoint) != 0)
                {
                    File.Delete(path);
                    r.Files++;
                    return;
                }

                File.SetAttributes(path, FileAttributes.Normal);
                long len = new FileInfo(path).Length;

                if (len > 0)
                {
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        byte[] buf = new byte[65536];
                        for (int pass = 0; pass < 3; pass++)
                        {
                            if (pass == 0) Array.Clear(buf, 0, buf.Length);
                            else if (pass == 1) for (int i = 0; i < buf.Length; i++) buf[i] = 0xFF;

                            fs.Position = 0;
                            long left = len;
                            while (left > 0)
                            {
                                if (pass == 2) Rng.GetBytes(buf);
                                int n = (int)Math.Min((long)buf.Length, left);
                                fs.Write(buf, 0, n);
                                left -= n;
                            }
                            fs.Flush(true);
                        }
                        fs.SetLength(0);
                        fs.Flush(true);
                    }
                }

                DateTime stamp = new DateTime(2000, 1, 1, 0, 0, 0);
                try
                {
                    File.SetCreationTime(path, stamp);
                    File.SetLastWriteTime(path, stamp);
                    File.SetLastAccessTime(path, stamp);
                }
                catch { }

                string last = RenameRandom(path, false);
                File.Delete(last);

                r.Bytes += len;
                r.Files++;
                Cleaner.ForgetRecentFile(name);
            }
            catch (Exception ex) { r.Errors.Add(name + ": " + ex.Message); }
        }

        static void ShredDir(string dir, CleanResult r)
        {
            try
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(dir, false);
                    return;
                }

                foreach (string f in Directory.GetFiles(dir)) ShredFile(f, r);
                foreach (string d in Directory.GetDirectories(dir)) ShredDir(d, r);

                if (Directory.GetFileSystemEntries(dir).Length > 0)
                {
                    r.Errors.Add("Папка очищена не полностью: " + dir);
                    return;
                }

                string name = Path.GetFileName(dir);
                File.SetAttributes(dir, FileAttributes.Normal);
                string last = RenameRandom(dir, true);
                Directory.Delete(last, false);
                Cleaner.ForgetRecentFile(name);
            }
            catch (Exception ex) { r.Errors.Add(dir + ": " + ex.Message); }
        }

        static string RenameRandom(string path, bool isDir)
        {
            string parent = Path.GetDirectoryName(path);
            string cur = path;
            for (int i = 0; i < 3; i++)
            {
                string next = Path.Combine(parent, RandomName());
                if (isDir) Directory.Move(cur, next);
                else File.Move(cur, next);
                cur = next;
            }
            return cur;
        }

        static string RandomName()
        {
            const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
            byte[] b = new byte[12];
            Rng.GetBytes(b);
            char[] c = new char[b.Length];
            for (int i = 0; i < b.Length; i++) c[i] = alphabet[b[i] % alphabet.Length];
            return new string(c);
        }

        static bool IsProtected(string full)
        {
            full = full.TrimEnd('\\');
            string root = (Path.GetPathRoot(full) ?? "").TrimEnd('\\');
            if (full.Length <= root.Length) return true;

            Environment.SpecialFolder[] trees = new Environment.SpecialFolder[]
            {
                Environment.SpecialFolder.Windows,
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86
            };
            foreach (Environment.SpecialFolder sf in trees)
            {
                string t = Environment.GetFolderPath(sf).TrimEnd('\\');
                if (t.Length == 0) continue;
                if (full.Equals(t, StringComparison.OrdinalIgnoreCase) ||
                    full.StartsWith(t + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }

            Environment.SpecialFolder[] exact = new Environment.SpecialFolder[]
            {
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.Desktop,
                Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.LocalApplicationData
            };
            foreach (Environment.SpecialFolder sf in exact)
            {
                string t = Environment.GetFolderPath(sf).TrimEnd('\\');
                if (t.Length > 0 && full.Equals(t, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    // =========================================================================
    // UNIFIED 2-COLUMN iOS 18 GLASS ENGINE (1:1 MATCH TO SCREENSHOT)
    // =========================================================================
    public enum IconKind
    {
        Bolt,
        Clipboard,
        FolderSync,
        Layers,
        Shield,
        Wifi,
        Plus,
        CloudAdd,
        Flame,
        FileDoc,
        Trash,
        CheckCircle,
        Globe
    }

    public enum BtnVariant
    {
        Glass,
        Red,
        Blue,
        Green
    }

    public class VirtualGlassButton
    {
        public string Id;
        public string Title;
        public string Subtitle;
        public string RightTag;
        public IconKind Icon;
        public BtnVariant Variant = BtnVariant.Glass;
        public bool IsLarge = false;
        public bool IsActiveToggle = false;

        public RectangleF Bounds;
        public Action OnClick;

        // Animation & Spring Physics State
        public float Scale = 1.0f;
        public float TargetScale = 1.0f;
        public float ScaleVel = 0.0f;

        public float LiftY = 0.0f;
        public float TargetLiftY = 0.0f;
        public float LiftVel = 0.0f;

        public float HoverMix = 0.0f;
        public float TargetHover = 0.0f;

        public bool IsLoading = false;
        public string LoadingText = "";
        public float ProgressPct = -1f;
        public float ShimmerPos = -0.4f;

        public bool IsSuccess = false;
        public string SuccessText = "";
        public int ResetTicks = 0;
    }

    public class ShredEntry
    {
        public string Path;
        public long SizeBytes;
        public bool IsDirectory;
        public RectangleF RowBounds;
        public RectangleF DeleteBtnBounds;
        public float HoverMix = 0f;
        public float DeleteHoverMix = 0f;
    }

    public class BrowserItem
    {
        public string Id;
        public string Name;
        public string ProcessName;
        public string UserDataPath;
        public bool IsFirefox;
        public bool IsPresent;
        public bool IsClean;
        public string StatusDetail;
        public RectangleF RowBounds;
        public RectangleF IndicatorBounds;
        public RectangleF ActionBtnBounds;
        public float HoverMix = 0f;
        public float BtnHoverMix = 0f;
        public bool IsCleaning = false;
    }

    public static class BrowserScanner
    {
        public static List<BrowserItem> DetectInstalledBrowsers()
        {
            var list = new List<BrowserItem>();
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            // 1. Google Chrome
            string chromePath = Path.Combine(localApp, @"Google\Chrome\User Data");
            if (Directory.Exists(chromePath))
                list.Add(new BrowserItem { Id = "chrome", Name = "Google Chrome", ProcessName = "chrome", UserDataPath = chromePath, IsFirefox = false, IsPresent = true });

            // 2. Microsoft Edge & Windows WebCache
            string edgePath = Path.Combine(localApp, @"Microsoft\Edge\User Data");
            string wcDir = Path.Combine(localApp, @"Microsoft\Windows\WebCache");
            if (Directory.Exists(edgePath) || Directory.Exists(wcDir))
                list.Add(new BrowserItem { Id = "edge", Name = "Microsoft Edge", ProcessName = "msedge", UserDataPath = edgePath, IsFirefox = false, IsPresent = true });

            // 3. Mozilla Firefox
            string ffPath = Path.Combine(appData, @"Mozilla\Firefox\Profiles");
            if (Directory.Exists(ffPath))
                list.Add(new BrowserItem { Id = "firefox", Name = "Mozilla Firefox", ProcessName = "firefox", UserDataPath = ffPath, IsFirefox = true, IsPresent = true });

            // 4. Opera
            string operaPath = Path.Combine(appData, @"Opera Software\Opera Stable");
            if (Directory.Exists(operaPath))
                list.Add(new BrowserItem { Id = "opera", Name = "Opera", ProcessName = "opera", UserDataPath = operaPath, IsFirefox = false, IsPresent = true });

            // 5. Opera GX
            string operaGxPath = Path.Combine(appData, @"Opera Software\Opera GX Stable");
            if (Directory.Exists(operaGxPath))
                list.Add(new BrowserItem { Id = "operagx", Name = "Opera GX", ProcessName = "opera", UserDataPath = operaGxPath, IsFirefox = false, IsPresent = true });

            // 6. Yandex Browser
            string yandexPath = Path.Combine(localApp, @"Yandex\YandexBrowser\User Data");
            if (Directory.Exists(yandexPath))
                list.Add(new BrowserItem { Id = "yandex", Name = "Yandex Browser", ProcessName = "browser", UserDataPath = yandexPath, IsFirefox = false, IsPresent = true });

            // 7. Brave
            string bravePath = Path.Combine(localApp, @"BraveSoftware\Brave-Browser\User Data");
            if (Directory.Exists(bravePath))
                list.Add(new BrowserItem { Id = "brave", Name = "Brave Browser", ProcessName = "brave", UserDataPath = bravePath, IsFirefox = false, IsPresent = true });

            // 8. Vivaldi
            string vivaldiPath = Path.Combine(localApp, @"Vivaldi\User Data");
            if (Directory.Exists(vivaldiPath))
                list.Add(new BrowserItem { Id = "vivaldi", Name = "Vivaldi", ProcessName = "vivaldi", UserDataPath = vivaldiPath, IsFirefox = false, IsPresent = true });

            foreach (var b in list)
            {
                CheckBrowserClean(b);
            }

            return list;
        }

        public static bool CheckBrowserClean(BrowserItem b)
        {
            try
            {
                if (b.IsFirefox)
                {
                    if (!Directory.Exists(b.UserDataPath)) return true;
                    string[] profiles = Directory.GetDirectories(b.UserDataPath);
                    foreach (string p in profiles)
                    {
                        string places = Path.Combine(p, "places.sqlite");
                        if (File.Exists(places) && new FileInfo(places).Length > 0)
                        {
                            b.IsClean = false;
                            return false;
                        }
                        string placesWal = Path.Combine(p, "places.sqlite-wal");
                        if (File.Exists(placesWal) && new FileInfo(placesWal).Length > 0)
                        {
                            b.IsClean = false;
                            return false;
                        }
                        string cookies = Path.Combine(p, "cookies.sqlite");
                        if (File.Exists(cookies) && new FileInfo(cookies).Length > 0)
                        {
                            b.IsClean = false;
                            return false;
                        }
                        string logins = Path.Combine(p, "logins.json");
                        if (File.Exists(logins) && new FileInfo(logins).Length > 0)
                        {
                            b.IsClean = false;
                            return false;
                        }
                    }
                    b.IsClean = true;
                    return true;
                }
                else
                {
                    if (b.Id == "edge")
                    {
                        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                        string wcFile = Path.Combine(local, @"Microsoft\Windows\WebCache\WebCacheV01.dat");
                        if (File.Exists(wcFile) && new FileInfo(wcFile).Length > 0)
                        {
                            b.IsClean = false;
                            return false;
                        }
                    }

                    if (!Directory.Exists(b.UserDataPath))
                    {
                        b.IsClean = true;
                        return true;
                    }

                    List<string> profileDirs = new List<string>();
                    string defaultDir = Path.Combine(b.UserDataPath, "Default");
                    if (Directory.Exists(defaultDir)) profileDirs.Add(defaultDir);

                    try
                    {
                        foreach (string sub in Directory.GetDirectories(b.UserDataPath, "Profile *"))
                            profileDirs.Add(sub);
                    }
                    catch { }

                    profileDirs.Add(b.UserDataPath);

                    foreach (string p in profileDirs)
                    {
                        string hist = Path.Combine(p, "History");
                        if (File.Exists(hist) && new FileInfo(hist).Length > 0)
                        {
                            b.IsClean = false;
                            return false;
                        }
                        string histWal = Path.Combine(p, "History-wal");
                        if (File.Exists(histWal) && new FileInfo(histWal).Length > 0)
                        {
                            b.IsClean = false;
                            return false;
                        }
                        string visited = Path.Combine(p, "Visited Links");
                        if (File.Exists(visited) && new FileInfo(visited).Length > 0)
                        {
                            b.IsClean = false;
                            return false;
                        }
                        string login = Path.Combine(p, "Login Data");
                        if (File.Exists(login) && new FileInfo(login).Length > 0)
                        {
                            b.IsClean = false;
                            return false;
                        }
                        string cook = Path.Combine(p, "Network", "Cookies");
                        if (File.Exists(cook) && new FileInfo(cook).Length > 0)
                        {
                            b.IsClean = false;
                            return false;
                        }
                        string cookRoot = Path.Combine(p, "Cookies");
                        if (File.Exists(cookRoot) && new FileInfo(cookRoot).Length > 0)
                        {
                            b.IsClean = false;
                            return false;
                        }
                    }

                    b.IsClean = true;
                    return true;
                }
            }
            catch
            {
                b.IsClean = false;
                return false;
            }
        }

        private static void KillProcessTree(string procName)
        {
            if (string.IsNullOrEmpty(procName)) return;
            try
            {
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    try
                    {
                        IntPtr h = NativeMethods.OpenProcess(0x0001, false, p.Id);
                        if (h != IntPtr.Zero)
                        {
                            NativeMethods.TerminateProcess(h, 1);
                            NativeMethods.CloseHandle(h);
                        }
                        else
                        {
                            p.Kill();
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static void StopWininetCacheTask()
        {
            try
            {
                Type t = Type.GetTypeFromProgID("Schedule.Service");
                if (t != null)
                {
                    object s = Activator.CreateInstance(t);
                    t.InvokeMember("Connect", BindingFlags.InvokeMethod, null, s, new object[] { null, null, null, null });
                    object folder = t.InvokeMember("GetFolder", BindingFlags.InvokeMethod, null, s, new object[] { @"\Microsoft\Windows\Wininet" });
                    if (folder != null)
                    {
                        Type folderType = folder.GetType();
                        object task = folderType.InvokeMember("GetTask", BindingFlags.InvokeMethod, null, folder, new object[] { "CacheTask" });
                        if (task != null)
                        {
                            Type taskType = task.GetType();
                            taskType.InvokeMember("Stop", BindingFlags.InvokeMethod, null, task, new object[] { 0 });
                            return;
                        }
                    }
                }
            }
            catch { }

            try
            {
                using (Process p = Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = @"/End /TN \Microsoft\Windows\Wininet\CacheTask",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    p.WaitForExit(1500);
                }
            }
            catch { }
        }

        public static void CleanWindowsWebCache()
        {
            StopWininetCacheTask();
            Thread.Sleep(300);

            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string webCache = Path.Combine(local, @"Microsoft\Windows\WebCache");
                if (Directory.Exists(webCache))
                {
                    foreach (string wf in Directory.GetFiles(webCache, "*.*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            File.SetAttributes(wf, FileAttributes.Normal);
                            File.Delete(wf);
                        }
                        catch { }
                    }
                }
                string inetCache = Path.Combine(local, @"Microsoft\Windows\INetCache");
                if (Directory.Exists(inetCache))
                {
                    foreach (string inf in Directory.GetFiles(inetCache, "*.*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            File.SetAttributes(inf, FileAttributes.Normal);
                            File.Delete(inf);
                        }
                        catch { }
                    }
                }
                string inetCookies = Path.Combine(local, @"Microsoft\Windows\INetCookies");
                if (Directory.Exists(inetCookies))
                {
                    foreach (string ic in Directory.GetFiles(inetCookies, "*.*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            File.SetAttributes(ic, FileAttributes.Normal);
                            File.Delete(ic);
                        }
                        catch { }
                    }
                }
                string histFolder = Path.Combine(local, @"Microsoft\Windows\History");
                if (Directory.Exists(histFolder))
                {
                    foreach (string hf in Directory.GetFiles(histFolder, "*.*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            File.SetAttributes(hf, FileAttributes.Normal);
                            File.Delete(hf);
                        }
                        catch { }
                    }
                }
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Internet Explorer\TypedURLs", true))
                {
                    if (key != null)
                    {
                        foreach (var val in key.GetValueNames())
                        {
                            try { key.DeleteValue(val, false); } catch { }
                        }
                    }
                }
                using (var key2 = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Internet Explorer\TypedURLsTime", true))
                {
                    if (key2 != null)
                    {
                        foreach (var val in key2.GetValueNames())
                        {
                            try { key2.DeleteValue(val, false); } catch { }
                        }
                    }
                }
            }
            catch { }
        }

        public static void CleanBrowser(BrowserItem b)
        {
            WinCleaner.Licensing.LicenseGuard.AssertOperational();

            try
            {
                if (!string.IsNullOrEmpty(b.ProcessName))
                {
                    KillProcessTree(b.ProcessName);
                }

                if (b.IsFirefox)
                {
                    KillProcessTree("firefox");
                    KillProcessTree("pingsender");
                    KillProcessTree("default-browser-agent");
                    KillProcessTree("plugin-container");
                }
                else if (b.Id == "edge")
                {
                    KillProcessTree("msedge");
                    KillProcessTree("msedgewebview2");
                    CleanWindowsWebCache();
                }
                else if (b.Id == "chrome")
                {
                    KillProcessTree("chrome");
                    KillProcessTree("chrome_crashpad_handler");
                }
                else if (b.Id == "opera" || b.Id == "operagx")
                {
                    KillProcessTree("opera");
                    KillProcessTree("opera_crashreporter");
                    KillProcessTree("opera_autoupdate");
                }
                else if (b.Id == "brave")
                {
                    KillProcessTree("brave");
                    KillProcessTree("brave_crashpad_handler");
                }
                else if (b.Id == "yandex")
                {
                    KillProcessTree("browser");
                    KillProcessTree("yandex");
                }
                else if (b.Id == "vivaldi")
                {
                    KillProcessTree("vivaldi");
                }
            }
            catch { }

            Thread.Sleep(400);

            if (b.IsFirefox)
            {
                CleanFirefoxProfileDirs(b.UserDataPath);
                string localFf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Mozilla\Firefox\Profiles");
                if (Directory.Exists(localFf)) CleanFirefoxProfileDirs(localFf);
            }
            else
            {
                CleanChromiumUserData(b.UserDataPath, b.Id == "edge");
            }

            CheckBrowserClean(b);
            AntiForensicTracer.WipeAllTraces();
        }

        private static void CleanChromiumUserData(string rootPath, bool isEdge)
        {
            if (Directory.Exists(rootPath))
            {
                List<string> profileDirs = new List<string>();
                string defaultDir = Path.Combine(rootPath, "Default");
                if (Directory.Exists(defaultDir)) profileDirs.Add(defaultDir);

                try
                {
                    foreach (string sub in Directory.GetDirectories(rootPath, "Profile *"))
                        profileDirs.Add(sub);
                }
                catch { }

                profileDirs.Add(rootPath);

                string[] targetFiles = new string[]
                {
                    "History", "History-journal", "History-wal", "History-shm",
                    "Visited Links", "Top Sites", "Top Sites-journal",
                    "Favicons", "Favicons-journal", "Shortcuts", "Shortcuts-journal",
                    "Cookies", "Cookies-journal",
                    "Login Data", "Login Data-journal", "Login Data For Account",
                    "Web Data", "Web Data-journal",
                    "Current Session", "Current Tabs", "Last Session", "Last Tabs"
                };

                string[] targetSubDirs = new string[]
                {
                    "Sessions", "Cache", "Code Cache", "GPUCache", "DawnCache",
                    "ShaderCache", "GrShaderCache", "IndexedDB", "Local Storage",
                    "Session Storage", "Service Worker", "CacheStorage",
                    "Sync Data", "Sync Data Backup", "Extension State"
                };

                foreach (string p in profileDirs)
                {
                    foreach (string f in targetFiles)
                    {
                        try
                        {
                            string fp = Path.Combine(p, f);
                            if (File.Exists(fp)) File.Delete(fp);
                        }
                        catch { }
                    }

                    try
                    {
                        string netCook = Path.Combine(p, "Network", "Cookies");
                        if (File.Exists(netCook)) File.Delete(netCook);
                        string netCookJ = Path.Combine(p, "Network", "Cookies-journal");
                        if (File.Exists(netCookJ)) File.Delete(netCookJ);
                    }
                    catch { }

                    foreach (string sd in targetSubDirs)
                    {
                        try
                        {
                            string sdp = Path.Combine(p, sd);
                            if (Directory.Exists(sdp)) Directory.Delete(sdp, true);
                        }
                        catch { }
                    }
                }
            }

            if (isEdge)
            {
                CleanWindowsWebCache();
            }
        }

        private static void CleanFirefoxProfileDirs(string rootPath)
        {
            if (!Directory.Exists(rootPath)) return;
            string[] profiles;
            try { profiles = Directory.GetDirectories(rootPath); } catch { return; }

            string[] targetFiles = new string[]
            {
                "places.sqlite", "places.sqlite-wal", "places.sqlite-shm",
                "favicons.sqlite", "favicons.sqlite-wal", "favicons.sqlite-shm",
                "formhistory.sqlite", "cookies.sqlite", "cookies.sqlite-wal",
                "logins.json", "key4.db", "cert9.db",
                "sessionstore.jsonlz4"
            };

            string[] targetDirs = new string[]
            {
                "sessionstore-backups", "cache2", "storage\\default", "jumpListCache"
            };

            foreach (string p in profiles)
            {
                foreach (string f in targetFiles)
                {
                    try
                    {
                        string fp = Path.Combine(p, f);
                        if (File.Exists(fp)) File.Delete(fp);
                    }
                    catch { }
                }
                foreach (string d in targetDirs)
                {
                    try
                    {
                        string dp = Path.Combine(p, d);
                        if (Directory.Exists(dp)) Directory.Delete(dp, true);
                    }
                    catch { }
                }
            }
        }
    }
    public static class AntiForensicTracer
    {
        public static void WipeAllTraces()
        {
            WipeEventLogs();
            WipePrefetch();
            WipeCrashDumpsAndWER();
            WipeConsoleHistory();
            WipeRecentAndJumpLists();
            WipeTempFiles();
            WipeThumbnails();
            WipeUsnJournal();
        }

        public static void WipeEventLogs()
        {
            string[] standardLogs = new string[] { "Application", "Setup" };
            foreach (string log in standardLogs)
            {
                try
                {
                    using (EventLog el = new EventLog(log))
                    {
                        el.Clear();
                    }
                }
                catch { }
            }

            string[] channels = new string[]
            {
                "Microsoft-Windows-TaskScheduler/Operational",
                "Microsoft-Windows-PowerShell/Operational",
                "Microsoft-Windows-Windows Defender/Operational",
                "Microsoft-Windows-WinINet-Config/Operational",
                "Microsoft-Windows-Kernel-PnP/Configuration",
                "Microsoft-Windows-Bits-Client/Operational",
                "Microsoft-Windows-WindowsUpdateClient/Operational"
            };

            foreach (string ch in channels)
            {
                try
                {
                    System.Diagnostics.Eventing.Reader.EventLogSession.GlobalSession.ClearLog(ch);
                }
                catch { }
            }
        }

        public static void WipePrefetch()
        {
            try
            {
                string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string pfDir = Path.Combine(windir, "Prefetch");
                if (Directory.Exists(pfDir))
                {
                    foreach (string pf in Directory.GetFiles(pfDir, "*.pf", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            File.SetAttributes(pf, FileAttributes.Normal);
                            File.Delete(pf);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public static void WipeCrashDumpsAndWER()
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

                string[] dirs = new string[]
                {
                    Path.Combine(local, "CrashDumps"),
                    Path.Combine(local, @"Microsoft\Windows\WER\ReportArchive"),
                    Path.Combine(local, @"Microsoft\Windows\WER\ReportQueue"),
                    Path.Combine(local, @"Microsoft\Windows\WER\Temp"),
                    Path.Combine(progData, @"Microsoft\Windows\WER\ReportArchive"),
                    Path.Combine(progData, @"Microsoft\Windows\WER\ReportQueue"),
                    Path.Combine(progData, @"Microsoft\Windows\WER\Temp")
                };

                foreach (string d in dirs)
                {
                    if (Directory.Exists(d))
                    {
                        foreach (string f in Directory.GetFiles(d, "*.*", SearchOption.AllDirectories))
                        {
                            try
                            {
                                File.SetAttributes(f, FileAttributes.Normal);
                                File.Delete(f);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }

        public static void WipeConsoleHistory()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string psHist = Path.Combine(appData, @"Microsoft\Windows\PowerShell\PSReadLine\ConsoleHost_history.txt");
                if (File.Exists(psHist))
                {
                    File.SetAttributes(psHist, FileAttributes.Normal);
                    File.WriteAllBytes(psHist, new byte[0]);
                    File.Delete(psHist);
                }
            }
            catch { }
        }

        public static void WipeRecentAndJumpLists()
        {
            try
            {
                string recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                if (Directory.Exists(recent))
                {
                    foreach (string f in Directory.GetFiles(recent, "*.lnk", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            File.SetAttributes(f, FileAttributes.Normal);
                            File.Delete(f);
                        }
                        catch { }
                    }
                    string autoDest = Path.Combine(recent, "AutomaticDestinations");
                    if (Directory.Exists(autoDest))
                    {
                        foreach (string f in Directory.GetFiles(autoDest, "*.*", SearchOption.AllDirectories))
                        {
                            try { File.Delete(f); } catch { }
                        }
                    }
                    string custDest = Path.Combine(recent, "CustomDestinations");
                    if (Directory.Exists(custDest))
                    {
                        foreach (string f in Directory.GetFiles(custDest, "*.*", SearchOption.AllDirectories))
                        {
                            try { File.Delete(f); } catch { }
                        }
                    }
                }
                NativeMethods.SHAddToRecentDocs(0, IntPtr.Zero);
            }
            catch { }
        }

        public static void WipeTempFiles()
        {
            try
            {
                string temp1 = Path.GetTempPath();
                if (Directory.Exists(temp1))
                {
                    foreach (string f in Directory.GetFiles(temp1, "*.*", SearchOption.TopDirectoryOnly))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
                string temp2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
                if (Directory.Exists(temp2))
                {
                    foreach (string f in Directory.GetFiles(temp2, "*.*", SearchOption.TopDirectoryOnly))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch { }
        }

        public static void WipeThumbnails()
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string expDir = Path.Combine(local, @"Microsoft\Windows\Explorer");
                if (Directory.Exists(expDir))
                {
                    foreach (string db in Directory.GetFiles(expDir, "*cache*.db", SearchOption.TopDirectoryOnly))
                    {
                        try { File.Delete(db); } catch { }
                    }
                }
                string iconCache = Path.Combine(local, "IconCache.db");
                if (File.Exists(iconCache))
                {
                    try { File.Delete(iconCache); } catch { }
                }
            }
            catch { }
        }

        public static void WipeUsnJournal()
        {
            try
            {
                using (Process p = Process.Start(new ProcessStartInfo
                {
                    FileName = "fsutil.exe",
                    Arguments = "usn deletejournal /D C:",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    p.WaitForExit(2000);
                }
            }
            catch { }
        }
    }

    public class MainForm : Form
    {
        private Bitmap _backdrop;
        private readonly List<VirtualGlassButton> _buttons = new List<VirtualGlassButton>();
        private readonly List<ShredEntry> _files = new List<ShredEntry>();

        private VirtualGlassButton _btnPanic;
        private VirtualGlassButton _btnClip;
        private VirtualGlassButton _btnExplorer;
        private VirtualGlassButton _btnBags;
        private VirtualGlassButton _btnLastActivity;
        private VirtualGlassButton _btnExecutedPrograms;
        private VirtualGlassButton _btnWinPrefetch;
        private VirtualGlassButton _btnBlock;
        private VirtualGlassButton _btnNetLock;
        private VirtualGlassButton _btnSelectFiles;
        private VirtualGlassButton _btnAddFolder;
        private VirtualGlassButton _btnShred;
        private VirtualGlassButton _btnScanBrowsers;
        private VirtualGlassButton _btnCleanAllBrowsers;
        private VirtualGlassButton _btnOpenBHV;

        public enum RightTabMode { Browsers, Shredder }
        private RightTabMode _activeTab = RightTabMode.Browsers;
        private RectangleF _tabBrowsersRect;
        private RectangleF _tabShredderRect;
        private bool _hoverTabBrowsers = false;
        private bool _hoverTabShredder = false;
        private List<BrowserItem> _activeBrowsers = new List<BrowserItem>();

        private RectangleF _leftCardRect;
        private RectangleF _rightCardRect;
        private RectangleF _dropZoneRect;
        private RectangleF _clearListLinkRect;
        private bool _hoverClearLink = false;

        private bool _busy = false;
        private string _statusText = "Система готова · Все модули инициализированы";
        private bool _statusWarn = false;

        private bool _isDragOver = false;
        private float _dragPulsePhase = 0f;
        private float _dropSuccessFlash = 0f;
        private int _scrollOffset = 0;

        private readonly System.Windows.Forms.Timer _frameTimer;

        // Cached Fonts (100% standard Windows families to prevent GDI+ FontStyle exceptions)
        private readonly Font _fontCardTitle = new Font("Segoe UI", 13.5f, FontStyle.Bold);
        private readonly Font _fontCardSub = new Font("Segoe UI", 8.75f, FontStyle.Regular);
        private readonly Font _fontPanicTitle = new Font("Segoe UI", 10.75f, FontStyle.Bold);
        private readonly Font _fontBtnTitle = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        private readonly Font _fontBtnSub = new Font("Segoe UI", 8.25f, FontStyle.Regular);
        private readonly Font _fontMonoTag = new Font("Consolas", 8.5f, FontStyle.Regular);
        private readonly Font _fontFilePath = new Font("Consolas", 9.0f, FontStyle.Regular);
        private readonly Font _fontFileMeta = new Font("Consolas", 8.0f, FontStyle.Regular);
        private readonly Font _fontFooter = new Font("Segoe UI", 8.25f, FontStyle.Regular);

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                AntiForensicTracer.WipeAllTraces();
                Cleaner.ClearSelfTraces();
            }
            catch { }
            base.OnFormClosing(e);
        }

        public MainForm()
        {
            WinCleaner.Licensing.LicenseGuard.AssertOperational();
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;

            InitButtons();
            try { _activeBrowsers = BrowserScanner.DetectInstalledBrowsers(); } catch { }

            Text = "WinCleaner — iOS 18 Glass";
            BackColor = Color.FromArgb(8, 10, 22);
            ForeColor = Color.FromArgb(245, 245, 247);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1040, 670);
            MinimumSize = new Size(920, 630);

            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            ComputeLayout();
            RebuildBackdrop();

            if (AppBlocker.IsBlocked()) AppBlocker.StartWatchdog();
            SyncToggleStates();

            Cleaner.Progress = delegate(string text)
            {
                try { BeginInvoke(new Action(delegate { if (_busy) { _statusText = text; Invalidate(); } })); } catch { }
            };

            long lastTick = 0;
            Cleaner.DetailedProgress = delegate(int pct, string text)
            {
                try
                {
                    long now = Environment.TickCount;
                    if (pct < 100 && now - lastTick < 40) return;
                    lastTick = now;
                    BeginInvoke(new Action(delegate
                    {
                        if (_busy)
                        {
                            if (_btnShred.IsLoading) { _btnShred.ProgressPct = pct; _btnShred.LoadingText = text; }
                            if (_btnPanic.IsLoading) { _btnPanic.ProgressPct = pct; _btnPanic.LoadingText = text; }
                            Invalidate();
                        }
                    }));
                }
                catch { }
            };

            _frameTimer = new System.Windows.Forms.Timer { Interval = 16 }; // 60 FPS spring engine
            _frameTimer.Tick += OnFrameTick;
            _frameTimer.Start();

            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (_busy)
                {
                    DialogResult dr = MessageBox.Show(this,
                        "Операция ещё выполняется. Вы уверены, что хотите закрыть программу?",
                        "WinCleaner", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (dr != DialogResult.Yes)
                    {
                        e.Cancel = true;
                        return;
                    }
                }
                NetLockdown.ForceUnlockOnExit();
            };
        }

        private void InitButtons()
        {
            _btnPanic = new VirtualGlassButton
            {
                Id = "panic",
                Title = "PANIC — Экстренная зачистка",
                Subtitle = "",
                RightTag = "",
                Icon = IconKind.Bolt,
                Variant = BtnVariant.Red,
                IsLarge = true,
                OnClick = OnPanicClicked
            };

            _btnClip = new VirtualGlassButton
            {
                Id = "clip",
                Title = "Очистить буфер обмена",
                Subtitle = "",
                RightTag = "",
                Icon = IconKind.Clipboard,
                Variant = BtnVariant.Glass,
                OnClick = delegate
                {
                    RunAsync(_btnClip, "Очистка буфера…", Cleaner.CleanClipboard,
                        delegate { return "Буфер обмена очищен"; }, null);
                }
            };

            _btnExplorer = new VirtualGlassButton
            {
                Id = "explorer",
                Title = "Очистить параметры Проводника",
                Subtitle = "",
                RightTag = "",
                Icon = IconKind.FolderSync,
                Variant = BtnVariant.Glass,
                OnClick = delegate
                {
                    RunAsync(_btnExplorer, "Перезапуск Проводника…", Cleaner.CleanExplorer,
                        delegate { return "Параметры Проводника очищены"; }, null);
                }
            };

            _btnBags = new VirtualGlassButton
            {
                Id = "bags",
                Title = "Очистить реестр Shell Bags",
                Subtitle = "",
                RightTag = "",
                Icon = IconKind.Layers,
                Variant = BtnVariant.Glass,
                OnClick = delegate
                {
                    RunAsync(_btnBags, "Удаление веток Shell Bags…", Cleaner.CleanShellBags,
                        delegate { return "Реестр Shell Bags очищен"; }, null);
                }
            };

            _btnLastActivity = new VirtualGlassButton
            {
                Id = "lastactivity",
                Title = "Очистить LastActivityView (без следов)",
                Subtitle = "",
                RightTag = "",
                Icon = IconKind.Shield,
                Variant = BtnVariant.Glass,
                OnClick = delegate
                {
                    RunAsync(_btnLastActivity, "Очистка LastActivityView…", Cleaner.CleanLastActivityView,
                        delegate { return "LastActivityView очищен без следов"; }, null);
                }
            };

            _btnExecutedPrograms = new VirtualGlassButton
            {
                Id = "executedprograms",
                Title = "Очистить ExecutedProgramsList (без следов)",
                Subtitle = "",
                RightTag = "",
                Icon = IconKind.Flame,
                Variant = BtnVariant.Glass,
                OnClick = delegate
                {
                    RunAsync(_btnExecutedPrograms, "Очистка ExecutedProgramsList…", Cleaner.CleanExecutedProgramsList,
                        delegate { return "ExecutedProgramsList очищен без следов"; }, null);
                }
            };

            _btnWinPrefetch = new VirtualGlassButton
            {
                Id = "winprefetch",
                Title = "Очистить WinPrefetchView (без следов)",
                Subtitle = "",
                RightTag = "",
                Icon = IconKind.FolderSync,
                Variant = BtnVariant.Glass,
                OnClick = delegate
                {
                    RunAsync(_btnWinPrefetch, "Очистка WinPrefetchView…", Cleaner.CleanWinPrefetchView,
                        delegate { return "WinPrefetchView очищен без следов"; }, null);
                }
            };

            _btnBlock = new VirtualGlassButton
            {
                Id = "block",
                Title = "Блокировать Hacker / Informer",
                Subtitle = "",
                RightTag = "",
                Icon = IconKind.Shield,
                Variant = BtnVariant.Glass,
                OnClick = delegate
                {
                    RunAsync(_btnBlock, "Применение правил IFEO…", AppBlocker.ToggleBlock,
                        delegate
                        {
                            SyncToggleStates();
                            return AppBlocker.IsBlocked() ? "Утилиты заблокированы" : "Блокировка снята";
                        }, null);
                }
            };

            _btnNetLock = new VirtualGlassButton
            {
                Id = "netlock",
                Title = "Изоляция: только MC / AnyDesk",
                Subtitle = "",
                RightTag = "",
                Icon = IconKind.Wifi,
                Variant = BtnVariant.Glass,
                OnClick = delegate
                {
                    RunAsync(_btnNetLock, "Настройка фильтров WFP…", NetLockdown.Toggle,
                        delegate
                        {
                            SyncToggleStates();
                            return NetLockdown.IsLocked() ? "WFP-изоляция активна" : "Интернет разблокирован";
                        }, null);
                }
            };

            _btnSelectFiles = new VirtualGlassButton
            {
                Id = "select_files",
                Title = "Выбрать файлы…",
                Subtitle = "",
                RightTag = "",
                Icon = IconKind.Plus,
                Variant = BtnVariant.Glass,
                OnClick = OnSelectFilesClicked
            };

            _btnAddFolder = new VirtualGlassButton
            {
                Id = "add_folder",
                Title = "Добавить папку",
                Subtitle = "",
                RightTag = "",
                Icon = IconKind.CloudAdd,
                Variant = BtnVariant.Blue,
                OnClick = OnAddFolderClicked
            };

            _btnShred = new VirtualGlassButton
            {
                Id = "shred",
                Title = "Уничтожить",
                Subtitle = "",
                RightTag = "",
                Icon = IconKind.Flame,
                Variant = BtnVariant.Red,
                OnClick = OnShredClicked
            };

            _btnScanBrowsers = new VirtualGlassButton
            {
                Id = "scan_browsers",
                Title = "Сканировать",
                Subtitle = "",
                RightTag = "",
                Icon = IconKind.FolderSync,
                Variant = BtnVariant.Blue,
                OnClick = delegate
                {
                    _statusText = "Сканирование установленных браузеров…";
                    _activeBrowsers = BrowserScanner.DetectInstalledBrowsers();
                    ComputeLayout();
                    _statusText = string.Format("Обнаружено браузеров: {0}", _activeBrowsers.Count);
                    Invalidate();
                }
            };

            _btnCleanAllBrowsers = new VirtualGlassButton
            {
                Id = "clean_all_browsers",
                Title = "Очистить все",
                Subtitle = "",
                RightTag = "",
                Icon = IconKind.Flame,
                Variant = BtnVariant.Red,
                OnClick = RunCleanAllBrowsersAsync
            };

            _btnOpenBHV = new VirtualGlassButton
            {
                Id = "open_bhv",
                Title = "Открыть BHV",
                Subtitle = "",
                RightTag = "",
                Icon = IconKind.Shield,
                Variant = BtnVariant.Glass,
                OnClick = OnOpenBHVClicked
            };

            _buttons.AddRange(new VirtualGlassButton[]
            {
                _btnPanic, _btnClip, _btnExplorer, _btnBags, _btnLastActivity, _btnExecutedPrograms, _btnWinPrefetch, _btnBlock, _btnNetLock,
                _btnSelectFiles, _btnAddFolder, _btnShred,
                _btnScanBrowsers, _btnCleanAllBrowsers, _btnOpenBHV
            });
        }

        private void SyncToggleStates()
        {
            bool blocked = AppBlocker.IsBlocked();
            _btnBlock.Title = blocked ? "Разблокировать Hacker / Informer" : "Блокировать Hacker / Informer";
            _btnBlock.RightTag = "";
            _btnBlock.IsActiveToggle = blocked;

            bool netLocked = NetLockdown.IsLocked();
            _btnNetLock.Title = netLocked ? "Разблокировать интернет для всех" : "Изоляция: только MC / AnyDesk";
            _btnNetLock.RightTag = "";
            _btnNetLock.IsActiveToggle = netLocked;

            _btnPanic.RightTag = "";
            Invalidate();
        }

        private void ComputeLayout()
        {
            if (_btnPanic == null) return;
            int pad = 20;
            int gap = 20;
            int totalW = Math.Max(800, ClientSize.Width);
            int totalH = Math.Max(640, ClientSize.Height);

            int leftW = (int)((totalW - pad * 2 - gap) * 0.43f);
            int rightW = (totalW - pad * 2 - gap) - leftW;
            int cardH = totalH - pad * 2;

            _leftCardRect = new RectangleF(pad, pad, leftW, cardH);
            _rightCardRect = new RectangleF(pad + leftW + gap, pad, rightW, cardH);

            // Left Card Controls Layout
            float lx = _leftCardRect.X + 22f;
            float lw = _leftCardRect.Width - 44f;
            float ly = _leftCardRect.Y + 76f;

            _btnPanic.Bounds = new RectangleF(lx, ly, lw, 60f);
            ly += 60f + 16f; // Divider space

            float stdH = 39f;
            float stdGap = 6f;

            _btnClip.Bounds = new RectangleF(lx, ly, lw, stdH);
            ly += stdH + stdGap;

            _btnExplorer.Bounds = new RectangleF(lx, ly, lw, stdH);
            ly += stdH + stdGap;

            _btnBags.Bounds = new RectangleF(lx, ly, lw, stdH);
            ly += stdH + stdGap;

            _btnLastActivity.Bounds = new RectangleF(lx, ly, lw, stdH);
            ly += stdH + stdGap;

            _btnExecutedPrograms.Bounds = new RectangleF(lx, ly, lw, stdH);
            ly += stdH + stdGap;

            _btnWinPrefetch.Bounds = new RectangleF(lx, ly, lw, stdH);
            ly += stdH + 12f; // Second divider space

            _btnBlock.Bounds = new RectangleF(lx, ly, lw, stdH);
            ly += stdH + stdGap;

            _btnNetLock.Bounds = new RectangleF(lx, ly, lw, stdH);

            // Right Card Controls Layout
            float rx = _rightCardRect.X + 22f;
            float rw = _rightCardRect.Width - 44f;
            float ry = _rightCardRect.Y + 74f;
            float bottomBtnH = 44f;
            float dropH = _rightCardRect.Bottom - ry - bottomBtnH - 34f;

            _dropZoneRect = new RectangleF(rx, ry, rw, dropH);
            _clearListLinkRect = new RectangleF(_rightCardRect.Right - 132f, _rightCardRect.Y + 44f, 110f, 20f);

            float btnY = _dropZoneRect.Bottom + 14f;
            float btnGap = 12f;
            float btnW = (rw - btnGap * 2f) / 3f;

            float tabW = 120f;
            float tabH = 26f;
            float tabY = _rightCardRect.Y + 18f;
            float tabX = _rightCardRect.Right - (tabW * 2f + 24f);
            _tabBrowsersRect = new RectangleF(tabX, tabY, tabW, tabH);
            _tabShredderRect = new RectangleF(tabX + tabW + 6f, tabY, tabW, tabH);

            if (_activeTab == RightTabMode.Browsers)
            {
                _btnScanBrowsers.Bounds = new RectangleF(rx, btnY, btnW, bottomBtnH);
                _btnCleanAllBrowsers.Bounds = new RectangleF(rx + btnW + btnGap, btnY, btnW, bottomBtnH);
                _btnOpenBHV.Bounds = new RectangleF(rx + (btnW + btnGap) * 2f, btnY, btnW, bottomBtnH);

                _btnSelectFiles.Bounds = RectangleF.Empty;
                _btnAddFolder.Bounds = RectangleF.Empty;
                _btnShred.Bounds = RectangleF.Empty;

                float browX = _dropZoneRect.X + 14f;
                float browW = _dropZoneRect.Width - 28f;
                float browY = _dropZoneRect.Y + 14f - _scrollOffset;
                float browH = 62f;
                float browGap = 10f;

                for (int i = 0; i < _activeBrowsers.Count; i++)
                {
                    BrowserItem b = _activeBrowsers[i];
                    b.RowBounds = new RectangleF(browX, browY, browW, browH);
                    b.ActionBtnBounds = new RectangleF(browX + browW - 110f, browY + (browH - 34f) * 0.5f, 98f, 34f);
                    b.IndicatorBounds = new RectangleF(b.ActionBtnBounds.Left - 132f, browY + (browH - 26f) * 0.5f, 122f, 26f);
                    browY += browH + browGap;
                }
            }
            else
            {
                _btnSelectFiles.Bounds = new RectangleF(rx, btnY, btnW, bottomBtnH);
                _btnAddFolder.Bounds = new RectangleF(rx + btnW + btnGap, btnY, btnW, bottomBtnH);
                _btnShred.Bounds = new RectangleF(rx + (btnW + btnGap) * 2f, btnY, btnW, bottomBtnH);

                _btnScanBrowsers.Bounds = RectangleF.Empty;
                _btnCleanAllBrowsers.Bounds = RectangleF.Empty;
                _btnOpenBHV.Bounds = RectangleF.Empty;

                float rowX = _dropZoneRect.X + 14f;
                float rowW = _dropZoneRect.Width - 28f;
                float rowY = _dropZoneRect.Y + 14f - _scrollOffset;
                float rowH = 54f;
                float rowGap = 8f;

                for (int i = 0; i < _files.Count; i++)
                {
                    _files[i].RowBounds = new RectangleF(rowX, rowY, rowW, rowH);
                    _files[i].DeleteBtnBounds = new RectangleF(rowX + rowW - 38f, rowY + (rowH - 28f) * 0.5f, 28f, 28f);
                    rowY += rowH + rowGap;
                }
            }
        }

        private void RebuildBackdrop()
        {
            try
            {
                int w = Math.Max(1, ClientSize.Width);
                int h = Math.Max(1, ClientSize.Height);
                if (_backdrop != null) _backdrop.Dispose();
                _backdrop = new Bitmap(w, h, PixelFormat.Format32bppArgb);

                using (Graphics g = Graphics.FromImage(_backdrop))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;

                    // Deep iOS 18 Dark Indigo-to-Black Gradient
                    using (LinearGradientBrush bg = new LinearGradientBrush(
                        new Rectangle(0, 0, w, h),
                        Color.FromArgb(13, 16, 36),
                        Color.FromArgb(5, 6, 13),
                        LinearGradientMode.Vertical))
                    {
                        g.FillRectangle(bg, 0, 0, w, h);
                    }

                    // Soft Ambient Blurred Color Blobs (Blue Top-Left, Purple Right, Cyan Bottom)
                    DrawAmbientBlob(g, new RectangleF(-100f, -80f, 520f, 480f), Color.FromArgb(46, 10, 132, 255));
                    DrawAmbientBlob(g, new RectangleF(w - 440f, 40f, 520f, 520f), Color.FromArgb(42, 175, 82, 222));
                    DrawAmbientBlob(g, new RectangleF(w * 0.25f, h - 300f, 480f, 360f), Color.FromArgb(25, 100, 210, 255));
                }
            }
            catch { }
        }

        private static void DrawAmbientBlob(Graphics g, RectangleF bounds, Color center)
        {
            try
            {
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddEllipse(bounds);
                    using (PathGradientBrush pgb = new PathGradientBrush(gp))
                    {
                        pgb.CenterColor = center;
                        pgb.SurroundColors = new Color[] { Color.FromArgb(0, center.R, center.G, center.B) };
                        g.FillPath(pgb, gp);
                    }
                }
            }
            catch { }
        }

        private void OnFrameTick(object sender, EventArgs e)
        {
            bool dirty = false;
            float dt = 0.016f;

            for (int i = 0; i < _buttons.Count; i++)
            {
                VirtualGlassButton b = _buttons[i];

                // Spring Scale (damping 17, stiffness 275)
                float sf = (b.TargetScale - b.Scale) * 275f - b.ScaleVel * 17f;
                b.ScaleVel += sf * dt;
                b.Scale += b.ScaleVel * dt;
                if (Math.Abs(b.TargetScale - b.Scale) > 0.0008f || Math.Abs(b.ScaleVel) > 0.004f)
                    dirty = true;
                else { b.Scale = b.TargetScale; b.ScaleVel = 0f; }

                // Spring Hover Lift Y (translateY -2px)
                float lf = (b.TargetLiftY - b.LiftY) * 240f - b.LiftVel * 18f;
                b.LiftVel += lf * dt;
                b.LiftY += b.LiftVel * dt;
                if (Math.Abs(b.TargetLiftY - b.LiftY) > 0.01f || Math.Abs(b.LiftVel) > 0.01f)
                    dirty = true;
                else { b.LiftY = b.TargetLiftY; b.LiftVel = 0f; }

                // Hover brightness mix
                if (Math.Abs(b.HoverMix - b.TargetHover) > 0.008f)
                {
                    b.HoverMix += (b.TargetHover - b.HoverMix) * 0.22f;
                    dirty = true;
                }
                else b.HoverMix = b.TargetHover;

                if (b.IsLoading)
                {
                    b.ShimmerPos += 0.032f;
                    if (b.ShimmerPos > 1.4f) b.ShimmerPos = -0.4f;
                    dirty = true;
                }

                if (b.ResetTicks > 0)
                {
                    b.ResetTicks--;
                    if (b.ResetTicks == 0)
                    {
                        b.IsSuccess = false;
                        b.ProgressPct = -1f;
                    }
                    dirty = true;
                }
            }

            if (_isDragOver)
            {
                _dragPulsePhase += 0.16f;
                dirty = true;
            }

            if (_dropSuccessFlash > 0f)
            {
                _dropSuccessFlash = Math.Max(0f, _dropSuccessFlash - 0.05f);
                dirty = true;
            }

            if (dirty) Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (ClientSize.Width > 0 && ClientSize.Height > 0)
            {
                ComputeLayout();
                RebuildBackdrop();
                Invalidate();
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int on = 1;
                if (NativeMethods.DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0)
                    NativeMethods.DwmSetWindowAttribute(Handle, 19, ref on, 4);
            }
            catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try { AllowDrop = true; } catch { }
            try
            {
                WindowState = FormWindowState.Normal;
                Activate();
                BringToFront();
            }
            catch { }
        }

        // =====================================================================
        // UNIFIED GDI+ RENDERING (100% ARTIFACT-FREE & LEAK-FREE)
        // =====================================================================
        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                if (_backdrop != null)
                    g.DrawImageUnscaled(_backdrop, 0, 0);
                else
                    g.Clear(Color.FromArgb(8, 10, 22));

                DrawLeftCard(g);
                DrawRightCard(g);
            }
            catch { }
        }

        private void DrawGlassSurface(Graphics g, RectangleF rect, float radius, Color topFill, Color botFill, Color borderCol, float borderW, int topSpecularAlpha)
        {
            if (rect.Width <= 2f || rect.Height <= 2f) return;
            using (GraphicsPath path = CreateRoundRect(rect, radius))
            {
                using (LinearGradientBrush fill = new LinearGradientBrush(rect, topFill, botFill, LinearGradientMode.Vertical))
                {
                    g.FillPath(fill, path);
                }

                if (topSpecularAlpha > 0 && rect.Width > radius * 1.6f)
                {
                    using (Pen spec = new Pen(Color.FromArgb(topSpecularAlpha, 255, 255, 255), 1f))
                    {
                        g.DrawLine(spec, rect.X + radius * 0.7f, rect.Y + 1f, rect.Right - radius * 0.7f, rect.Y + 1f);
                    }
                }

                using (Pen border = new Pen(borderCol, borderW))
                {
                    g.DrawPath(border, path);
                }
            }
        }

        private void DrawLeftCard(Graphics g)
        {
            // Outer Left Glass Card (8-12% white opacity)
            DrawGlassSurface(
                g,
                _leftCardRect,
                22f,
                Color.FromArgb(24, 255, 255, 255),
                Color.FromArgb(16, 255, 255, 255),
                Color.FromArgb(40, 255, 255, 255),
                1f,
                42
            );

            // Header
            float hx = _leftCardRect.X + 22f;
            float hy = _leftCardRect.Y + 22f;
            TextRenderer.DrawText(g, "Системная очистка и защита", _fontCardTitle,
                new Point((int)hx, (int)hy), Color.White, TextFormatFlags.NoPadding);

            // Dividers
            float div1Y = _btnPanic.Bounds.Bottom + 8f;
            DrawHairlineDivider(g, hx, div1Y, _leftCardRect.Width - 44f);

            float div2Y = _btnWinPrefetch.Bounds.Bottom + 6f;
            DrawHairlineDivider(g, hx, div2Y, _leftCardRect.Width - 44f);

            // Render Left Buttons
            DrawVirtualButton(g, _btnPanic);
            DrawVirtualButton(g, _btnClip);
            DrawVirtualButton(g, _btnExplorer);
            DrawVirtualButton(g, _btnBags);
            DrawVirtualButton(g, _btnLastActivity);
            DrawVirtualButton(g, _btnExecutedPrograms);
            DrawVirtualButton(g, _btnWinPrefetch);
            DrawVirtualButton(g, _btnBlock);
            DrawVirtualButton(g, _btnNetLock);

            // Footer Status Line
            float footY = _leftCardRect.Bottom - 36f;
            DrawHairlineDivider(g, hx, footY - 8f, _leftCardRect.Width - 44f);

            Rectangle statusRect = new Rectangle((int)hx, (int)footY, (int)(_leftCardRect.Width - 44f), 20);
            Color stColor = _statusWarn ? Color.FromArgb(255, 110, 100) : Color.FromArgb(165, 245, 245, 247);
            TextRenderer.DrawText(g, _statusText, _fontFooter, statusRect, stColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private void DrawRightCard(Graphics g)
        {
            // Outer Right Glass Card
            DrawGlassSurface(
                g,
                _rightCardRect,
                22f,
                Color.FromArgb(24, 255, 255, 255),
                Color.FromArgb(16, 255, 255, 255),
                Color.FromArgb(40, 255, 255, 255),
                1f,
                42
            );

            float hx = _rightCardRect.X + 22f;
            float hy = _rightCardRect.Y + 22f;

            // Render Segmented Tab Pills
            DrawTabPill(g, _tabBrowsersRect, "Браузеры (" + _activeBrowsers.Count + ")", _activeTab == RightTabMode.Browsers, _hoverTabBrowsers, IconKind.Globe);
            DrawTabPill(g, _tabShredderRect, "Шредер (" + _files.Count + ")", _activeTab == RightTabMode.Shredder, _hoverTabShredder, IconKind.Flame);

            if (_activeTab == RightTabMode.Browsers)
            {
                TextRenderer.DrawText(g, "Аудит браузеров", _fontCardTitle,
                    new Point((int)hx, (int)hy), Color.White, TextFormatFlags.NoPadding);

                DrawGlassSurface(g, _dropZoneRect, 18f,
                    Color.FromArgb(16, 255, 255, 255),
                    Color.FromArgb(12, 255, 255, 255),
                    Color.FromArgb(36, 255, 255, 255), 1f, 25);

                GraphicsState dzState = g.Save();
                Rectangle dzClipRect = new Rectangle(
                    (int)_dropZoneRect.X + 2,
                    (int)_dropZoneRect.Y + 2,
                    Math.Max(1, (int)_dropZoneRect.Width - 4),
                    Math.Max(1, (int)_dropZoneRect.Height - 4));
                g.SetClip(dzClipRect);

                if (_activeBrowsers.Count == 0)
                {
                    float cx = _dropZoneRect.X + _dropZoneRect.Width * 0.5f;
                    float cy = _dropZoneRect.Y + _dropZoneRect.Height * 0.5f - 8f;

                    RectangleF iconBadge = new RectangleF(cx - 24f, cy - 36f, 48f, 48f);
                    DrawGlassSurface(g, iconBadge, 14f,
                        Color.FromArgb(26, 255, 255, 255),
                        Color.FromArgb(16, 255, 255, 255),
                        Color.FromArgb(45, 255, 255, 255), 1f, 30);
                    DrawVectorIcon(g, IconKind.Globe, new RectangleF(cx - 10f, cy - 22f, 20f, 20f), Color.FromArgb(10, 132, 255));

                    Rectangle hint1 = new Rectangle((int)_dropZoneRect.X + 20, (int)(cy + 22f), (int)_dropZoneRect.Width - 40, 24);
                    TextRenderer.DrawText(g, "Установленные браузеры не обнаружены", _fontBtnTitle, hint1,
                        Color.FromArgb(230, 245, 245, 247), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                else
                {
                    for (int i = 0; i < _activeBrowsers.Count; i++)
                    {
                        DrawBrowserRow(g, _activeBrowsers[i]);
                    }
                }

                g.Restore(dzState);

                DrawVirtualButton(g, _btnScanBrowsers);
                DrawVirtualButton(g, _btnCleanAllBrowsers);
                DrawVirtualButton(g, _btnOpenBHV);
            }
            else
            {
                TextRenderer.DrawText(g, "Криптографический шредер", _fontCardTitle,
                    new Point((int)hx, (int)hy), Color.White, TextFormatFlags.NoPadding);

                if (_files.Count > 0)
                {
                    Color linkCol = _hoverClearLink ? Color.FromArgb(255, 120, 110) : Color.FromArgb(100, 210, 255);
                    TextRenderer.DrawText(g, "Очистить список", _fontFooter,
                        Rectangle.Round(_clearListLinkRect), linkCol,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }

                Color dropTop = Color.FromArgb(16, 255, 255, 255);
                Color dropBot = Color.FromArgb(12, 255, 255, 255);
                Color dropBorder = Color.FromArgb(36, 255, 255, 255);
                float dropBorderW = 1f;

                if (_dropSuccessFlash > 0.01f)
                {
                    dropTop = Color.FromArgb((int)(20 + _dropSuccessFlash * 55), 48, 209, 88);
                    dropBot = Color.FromArgb((int)(12 + _dropSuccessFlash * 30), 48, 209, 88);
                    dropBorder = Color.FromArgb((int)(90 + _dropSuccessFlash * 165), 48, 209, 88);
                    dropBorderW = 1.8f;
                }
                else if (_isDragOver)
                {
                    int pulse = 135 + (int)(Math.Sin(_dragPulsePhase) * 90);
                    dropTop = Color.FromArgb(35, 10, 132, 255);
                    dropBot = Color.FromArgb(20, 10, 132, 255);
                    dropBorder = Color.FromArgb(pulse, 10, 132, 255);
                    dropBorderW = 1.8f;
                }

                DrawGlassSurface(g, _dropZoneRect, 18f, dropTop, dropBot, dropBorder, dropBorderW, 25);

                GraphicsState dzState = g.Save();
                Rectangle dzClipRect = new Rectangle(
                    (int)_dropZoneRect.X + 2,
                    (int)_dropZoneRect.Y + 2,
                    Math.Max(1, (int)_dropZoneRect.Width - 4),
                    Math.Max(1, (int)_dropZoneRect.Height - 4));
                g.SetClip(dzClipRect);

                if (_files.Count == 0)
                {
                    float cx = _dropZoneRect.X + _dropZoneRect.Width * 0.5f;
                    float cy = _dropZoneRect.Y + _dropZoneRect.Height * 0.5f - 8f;

                    RectangleF iconBadge = new RectangleF(cx - 24f, cy - 36f, 48f, 48f);
                    DrawGlassSurface(g, iconBadge, 14f,
                        Color.FromArgb(26, 255, 255, 255),
                        Color.FromArgb(16, 255, 255, 255),
                        Color.FromArgb(45, 255, 255, 255), 1f, 30);
                    DrawVectorIcon(g, IconKind.CloudAdd, new RectangleF(cx - 10f, cy - 22f, 20f, 20f), Color.FromArgb(10, 132, 255));

                    Rectangle hint1 = new Rectangle((int)_dropZoneRect.X + 20, (int)(cy + 22f), (int)_dropZoneRect.Width - 40, 24);
                    TextRenderer.DrawText(g, "Перетащите файлы или папки сюда", _fontBtnTitle, hint1,
                        Color.FromArgb(230, 245, 245, 247), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                else
                {
                    for (int i = 0; i < _files.Count; i++)
                    {
                        DrawFileRow(g, _files[i]);
                    }
                }

                g.Restore(dzState);

                DrawVirtualButton(g, _btnSelectFiles);
                DrawVirtualButton(g, _btnAddFolder);
                DrawVirtualButton(g, _btnShred);
            }
        }

        private void DrawTabPill(Graphics g, RectangleF r, string text, bool active, bool hover, IconKind icon)
        {
            Color topFill = active
                ? Color.FromArgb(55, 10, 132, 255)
                : (hover ? Color.FromArgb(32, 255, 255, 255) : Color.FromArgb(18, 255, 255, 255));
            Color botFill = active
                ? Color.FromArgb(30, 10, 132, 255)
                : (hover ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(12, 255, 255, 255));
            Color borderCol = active
                ? Color.FromArgb(130, 10, 132, 255)
                : (hover ? Color.FromArgb(55, 255, 255, 255) : Color.FromArgb(34, 255, 255, 255));

            DrawGlassSurface(g, r, 9f, topFill, botFill, borderCol, 1f, active ? 25 : 0);

            Color iconCol = active ? Color.FromArgb(100, 210, 255) : Color.FromArgb(160, 245, 245, 247);
            DrawVectorIcon(g, icon, new RectangleF(r.X + 8f, r.Y + (r.Height - 14f) * 0.5f, 14f, 14f), iconCol);

            Color textCol = active ? Color.White : Color.FromArgb(185, 245, 245, 247);
            Rectangle textRect = new Rectangle((int)(r.X + 26f), (int)r.Y, (int)(r.Width - 30f), (int)r.Height);
            TextRenderer.DrawText(g, text, _fontBtnSub, textRect, textCol,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        private void DrawBrowserRow(Graphics g, BrowserItem item)
        {
            RectangleF r = item.RowBounds;
            if (r.Bottom < _dropZoneRect.Y || r.Top > _dropZoneRect.Bottom - 36f) return;

            int bgAlpha = 20 + (int)(item.HoverMix * 14f);
            DrawGlassSurface(
                g,
                r,
                14f,
                Color.FromArgb(bgAlpha + 6, 255, 255, 255),
                Color.FromArgb(bgAlpha, 255, 255, 255),
                Color.FromArgb(34 + (int)(item.HoverMix * 20f), 255, 255, 255),
                1f,
                22
            );

            // Left Icon Badge
            RectangleF badge = new RectangleF(r.X + 12f, r.Y + (r.Height - 34f) * 0.5f, 34f, 34f);
            Color badgeTint = item.IsClean ? Color.FromArgb(48, 209, 88) : Color.FromArgb(100, 210, 255);
            DrawGlassSurface(g, badge, 10f,
                Color.FromArgb(24, 255, 255, 255),
                Color.FromArgb(16, 255, 255, 255),
                Color.FromArgb(38, 255, 255, 255), 1f, 0);
            DrawVectorIcon(g, IconKind.Globe, new RectangleF(badge.X + 9f, badge.Y + 9f, 16f, 16f), badgeTint);

            // Browser Name (vertically centered)
            int textX = (int)(badge.Right + 12f);
            int textW = (int)(item.IndicatorBounds.Left - textX - 10f);

            Rectangle titleRect = new Rectangle(textX, (int)r.Y, textW, (int)r.Height);
            TextRenderer.DrawText(g, item.Name, _fontBtnTitle, titleRect, Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // Indicator Badge (Green or Red glowing LED pill)
            RectangleF indR = item.IndicatorBounds;
            Color indTop, indBot, indBorder, ledColor;
            string indText;

            if (item.IsClean)
            {
                indTop = Color.FromArgb(35, 48, 209, 88);
                indBot = Color.FromArgb(18, 48, 209, 88);
                indBorder = Color.FromArgb(95, 48, 209, 88);
                ledColor = Color.FromArgb(48, 209, 88);
                indText = "ЧИСТ";
            }
            else
            {
                indTop = Color.FromArgb(40, 255, 69, 58);
                indBot = Color.FromArgb(22, 255, 69, 58);
                indBorder = Color.FromArgb(110, 255, 69, 58);
                ledColor = Color.FromArgb(255, 69, 58);
                indText = "ЕСТЬ ИСТОРИЯ";
            }

            DrawGlassSurface(g, indR, 8f, indTop, indBot, indBorder, 1f, 0);

            using (SolidBrush glowBr = new SolidBrush(Color.FromArgb(60, ledColor.R, ledColor.G, ledColor.B)))
            {
                g.FillEllipse(glowBr, indR.X + 6f, indR.Y + 8f, 10f, 10f);
            }
            using (SolidBrush dotBr = new SolidBrush(ledColor))
            {
                g.FillEllipse(dotBr, indR.X + 8f, indR.Y + 10f, 6f, 6f);
            }

            Rectangle indTextRect = new Rectangle((int)(indR.X + 20f), (int)indR.Y, (int)(indR.Width - 24f), (int)indR.Height);
            TextRenderer.DrawText(g, indText, _fontMonoTag, indTextRect, ledColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            // Clean Action Button
            RectangleF btnR = item.ActionBtnBounds;
            Color btnTop, btnBot, btnBorder, btnTextCol;
            string btnLabel;

            if (item.IsCleaning)
            {
                btnTop = Color.FromArgb(45, 10, 132, 255);
                btnBot = Color.FromArgb(25, 10, 132, 255);
                btnBorder = Color.FromArgb(110, 10, 132, 255);
                btnTextCol = Color.White;
                btnLabel = "Зачистка…";
            }
            else if (item.IsClean)
            {
                int hAlpha = (int)(item.BtnHoverMix * 25f);
                btnTop = Color.FromArgb(25 + hAlpha, 48, 209, 88);
                btnBot = Color.FromArgb(15 + hAlpha, 48, 209, 88);
                btnBorder = Color.FromArgb(60 + hAlpha, 48, 209, 88);
                btnTextCol = Color.FromArgb(48, 209, 88);
                btnLabel = "Очистить";
            }
            else
            {
                int hAlpha = (int)(item.BtnHoverMix * 40f);
                btnTop = Color.FromArgb(60 + hAlpha, 255, 69, 58);
                btnBot = Color.FromArgb(35 + hAlpha, 255, 69, 58);
                btnBorder = Color.FromArgb(120 + hAlpha, 255, 69, 58);
                btnTextCol = Color.White;
                btnLabel = "Очистить";
            }

            DrawGlassSurface(g, btnR, 10f, btnTop, btnBot, btnBorder, 1f, 15);
            Rectangle btnTextRect = new Rectangle((int)btnR.X, (int)btnR.Y, (int)btnR.Width, (int)btnR.Height);
            TextRenderer.DrawText(g, btnLabel, _fontBtnTitle, btnTextRect, btnTextCol,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        private void DrawFileRow(Graphics g, ShredEntry item)
        {
            RectangleF r = item.RowBounds;
            if (r.Bottom < _dropZoneRect.Y || r.Top > _dropZoneRect.Bottom - 36f) return;

            int bgAlpha = 18 + (int)(item.HoverMix * 12f);
            DrawGlassSurface(
                g,
                r,
                14f,
                Color.FromArgb(bgAlpha + 6, 255, 255, 255),
                Color.FromArgb(bgAlpha, 255, 255, 255),
                Color.FromArgb(32 + (int)(item.HoverMix * 18f), 255, 255, 255),
                1f,
                22
            );

            // Left Icon Badge
            RectangleF badge = new RectangleF(r.X + 11f, r.Y + (r.Height - 32f) * 0.5f, 32f, 32f);
            DrawGlassSurface(g, badge, 10f,
                Color.FromArgb(24, 255, 255, 255),
                Color.FromArgb(16, 255, 255, 255),
                Color.FromArgb(38, 255, 255, 255), 1f, 0);
            DrawVectorIcon(g, IconKind.FileDoc, new RectangleF(badge.X + 8f, badge.Y + 8f, 16f, 16f), Color.FromArgb(100, 210, 255));

            // Path & Meta
            int textX = (int)(badge.Right + 12f);
            int textW = (int)(item.DeleteBtnBounds.Left - textX - 8f);

            Rectangle pathRect = new Rectangle(textX, (int)(r.Y + 8f), textW, 19);
            TextRenderer.DrawText(g, item.Path, _fontFilePath, pathRect, Color.FromArgb(240, 245, 245, 247),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis | TextFormatFlags.NoPrefix);

            string meta = FormatSize(item.SizeBytes);
            Rectangle metaRect = new Rectangle(textX, (int)(r.Y + 28f), textW, 18);
            TextRenderer.DrawText(g, meta, _fontFileMeta, metaRect, Color.FromArgb(135, 245, 245, 247),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // Delete Trash Button
            if (item.DeleteHoverMix > 0.05f)
            {
                using (GraphicsPath delBg = CreateRoundRect(item.DeleteBtnBounds, 8f))
                using (SolidBrush br = new SolidBrush(Color.FromArgb((int)(item.DeleteHoverMix * 45f), 255, 69, 58)))
                {
                    g.FillPath(br, delBg);
                }
            }
            Color trashCol = item.DeleteHoverMix > 0.1f
                ? Color.FromArgb(255, 69, 58)
                : Color.FromArgb(130, 245, 245, 247);
            DrawVectorIcon(g, IconKind.Trash,
                new RectangleF(item.DeleteBtnBounds.X + 6f, item.DeleteBtnBounds.Y + 6f, 16f, 16f), trashCol);
        }

        private void DrawVirtualButton(Graphics g, VirtualGlassButton b)
        {
            if (b == null || b.Bounds.Width <= 2f || b.Bounds.Height <= 2f) return;
            RectangleF baseR = b.Bounds;
            float w = baseR.Width * b.Scale;
            float h = baseR.Height * b.Scale;
            float x = baseR.X + (baseR.Width - w) * 0.5f;
            float y = baseR.Y + (baseR.Height - h) * 0.5f + b.LiftY;
            RectangleF rect = new RectangleF(x, y, w, h);
            float radius = b.IsLarge ? 18f : 16f;

            // Soft Hover Shadow
            int shAlpha = (int)(20 + b.HoverMix * 32f);
            using (GraphicsPath shPath = CreateRoundRect(new RectangleF(x + 1f, y + 3f - b.LiftY * 0.5f, w - 2f, h), radius))
            using (SolidBrush shBr = new SolidBrush(Color.FromArgb(shAlpha, 0, 0, 0)))
            {
                g.FillPath(shBr, shPath);
            }

            // Glass Fill Colors matching Screenshot
            Color topFill, botFill, borderCol;
            int boost = (int)(b.HoverMix * 26f);

            if (b.IsSuccess)
            {
                topFill = Color.FromArgb(115, 48, 209, 88);
                botFill = Color.FromArgb(65, 48, 209, 88);
                borderCol = Color.FromArgb(130, 92, 235, 128);
            }
            else if (b.Variant == BtnVariant.Red)
            {
                topFill = Color.FromArgb(Math.Min(255, 118 + boost), 255, 69, 58);
                botFill = Color.FromArgb(Math.Min(255, 64 + boost), 255, 69, 58);
                borderCol = Color.FromArgb(Math.Min(255, 95 + boost), 255, 120, 112);
            }
            else if (b.Variant == BtnVariant.Blue || b.IsActiveToggle)
            {
                topFill = Color.FromArgb(Math.Min(255, 108 + boost), 10, 132, 255);
                botFill = Color.FromArgb(Math.Min(255, 62 + boost), 10, 132, 255);
                borderCol = Color.FromArgb(Math.Min(255, 92 + boost), 100, 210, 255);
            }
            else
            {
                int aTop = 24 + (int)(b.HoverMix * 15f);
                int aBot = 17 + (int)(b.HoverMix * 11f);
                topFill = Color.FromArgb(aTop, 255, 255, 255);
                botFill = Color.FromArgb(aBot, 255, 255, 255);
                borderCol = Color.FromArgb(42 + (int)(b.HoverMix * 24f), 255, 255, 255);
            }

            using (GraphicsPath path = CreateRoundRect(rect, radius))
            {
                using (LinearGradientBrush fill = new LinearGradientBrush(rect, topFill, botFill, LinearGradientMode.Vertical))
                {
                    g.FillPath(fill, path);
                }

                // Determinate progress fill + Shimmer highlight beam
                if (b.IsLoading)
                {
                    GraphicsState loadState = g.Save();
                    g.SetClip(path, CombineMode.Intersect);

                    if (b.ProgressPct >= 0f)
                    {
                        float pw = rect.Width * (Math.Min(100f, b.ProgressPct) / 100f);
                        if (pw > 1f)
                        {
                            using (SolidBrush pb = new SolidBrush(Color.FromArgb(90, 10, 132, 255)))
                            {
                                g.FillRectangle(pb, rect.X, rect.Y, pw, rect.Height);
                            }
                        }
                    }

                    float bw = Math.Max(4f, rect.Width * 0.45f);
                    float bx = rect.X + b.ShimmerPos * rect.Width;
                    RectangleF beamRect = new RectangleF(bx, rect.Y, bw, Math.Max(2f, rect.Height));
                    using (LinearGradientBrush shimmer = new LinearGradientBrush(
                        beamRect,
                        Color.Transparent,
                        Color.FromArgb(60, 255, 255, 255),
                        LinearGradientMode.Horizontal))
                    {
                        ColorBlend cb = new ColorBlend
                        {
                            Colors = new Color[] { Color.Transparent, Color.FromArgb(65, 255, 255, 255), Color.Transparent },
                            Positions = new float[] { 0f, 0.5f, 1f }
                        };
                        shimmer.InterpolationColors = cb;
                        g.FillRectangle(shimmer, beamRect);
                    }

                    g.Restore(loadState);
                }

                // Top specular inset line
                if (rect.Width > radius * 1.5f)
                {
                    using (Pen spec = new Pen(Color.FromArgb(46 + (int)(b.HoverMix * 20f), 255, 255, 255), 1f))
                    {
                        g.DrawLine(spec, rect.X + radius * 0.65f, rect.Y + 1f, rect.Right - radius * 0.65f, rect.Y + 1f);
                    }
                }

                using (Pen border = new Pen(borderCol, 1f))
                {
                    g.DrawPath(border, path);
                }
            }

            // Icon + Typography
            Color iconCol = b.IsSuccess
                ? Color.FromArgb(48, 209, 88)
                : (b.Variant == BtnVariant.Red
                    ? Color.FromArgb(255, 95, 88)
                    : (b.Variant == BtnVariant.Blue || b.IsActiveToggle
                        ? Color.FromArgb(100, 210, 255)
                        : Color.FromArgb(225, 245, 245, 247)));

            RectangleF iconRect = new RectangleF(rect.X + 16f, rect.Y + (rect.Height - 18f) * 0.5f, 18f, 18f);
            DrawVectorIcon(g, b.IsSuccess ? IconKind.CheckCircle : b.Icon, iconRect, iconCol);

            string mainTitle = b.IsLoading ? b.LoadingText : (b.IsSuccess ? b.SuccessText : b.Title);
            bool hasSubtitle = !string.IsNullOrEmpty(b.Subtitle);
            int rightReserve = string.IsNullOrEmpty(b.RightTag) ? 14 : 96;
            int textLeft = (int)(rect.X + 46f);
            int textWidth = Math.Max(40, (int)(rect.Width - 46f - rightReserve));

            Font tf = b.IsLarge ? _fontPanicTitle : _fontBtnTitle;

            if (hasSubtitle)
            {
                Rectangle titleRect = new Rectangle(textLeft, (int)(rect.Y + (b.IsLarge ? 11f : 8f)), textWidth, 22);
                TextRenderer.DrawText(g, mainTitle, tf, titleRect, Color.White,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                Rectangle subRect = new Rectangle(textLeft, (int)(rect.Y + (b.IsLarge ? 34f : 29f)), textWidth, 18);
                TextRenderer.DrawText(g, b.Subtitle, _fontBtnSub, subRect, Color.FromArgb(155, 245, 245, 247),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            else
            {
                Rectangle titleRect = new Rectangle(textLeft, (int)rect.Y, textWidth, (int)rect.Height);
                TextRenderer.DrawText(g, mainTitle, tf, titleRect, Color.White,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            if (!string.IsNullOrEmpty(b.RightTag))
            {
                Rectangle tagRect = new Rectangle((int)(rect.Right - 104f), (int)rect.Y, 90, (int)rect.Height);
                Color tagCol = b.IsActiveToggle ? Color.FromArgb(100, 210, 255) : Color.FromArgb(170, 245, 245, 247);
                TextRenderer.DrawText(g, b.RightTag, _fontMonoTag, tagRect, tagCol,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        private static void DrawHairlineDivider(Graphics g, float x, float y, float w)
        {
            if (w <= 4f) return;
            using (Pen p = new Pen(Color.FromArgb(26, 255, 255, 255), 1f))
            {
                g.DrawLine(p, x, y, x + w, y);
            }
        }

        private static GraphicsPath CreateRoundRect(RectangleF r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (r.Width <= 0f || r.Height <= 0f) return path;
            float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
            path.AddArc(r.X, r.Y, d, d, 180f, 90f);
            path.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            path.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        private static void DrawVectorIcon(Graphics g, IconKind kind, RectangleF box, Color color)
        {
            float cx = box.X + box.Width * 0.5f;
            float cy = box.Y + box.Height * 0.5f;

            using (Pen p = new Pen(color, 1.6f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;

                switch (kind)
                {
                    case IconKind.Bolt:
                        g.DrawPolygon(p, new PointF[]
                        {
                            new PointF(cx + 1.2f, cy - 7f),
                            new PointF(cx - 4.5f, cy + 0.5f),
                            new PointF(cx + 0.2f, cy + 0.5f),
                            new PointF(cx - 1.2f, cy + 7f),
                            new PointF(cx + 4.5f, cy - 0.5f),
                            new PointF(cx - 0.2f, cy - 0.5f)
                        });
                        break;

                    case IconKind.Clipboard:
                        g.DrawRectangle(p, cx - 4.5f, cy - 4.5f, 9f, 10.5f);
                        g.DrawLine(p, cx - 2.2f, cy - 6.2f, cx + 2.2f, cy - 6.2f);
                        g.DrawLine(p, cx - 2f, cy - 0.8f, cx + 2f, cy + 3.2f);
                        g.DrawLine(p, cx + 2f, cy - 0.8f, cx - 2f, cy + 3.2f);
                        break;

                    case IconKind.FolderSync:
                        g.DrawPolygon(p, new PointF[]
                        {
                            new PointF(cx - 6f, cy - 4.5f),
                            new PointF(cx - 1.5f, cy - 4.5f),
                            new PointF(cx + 0.2f, cy - 2.5f),
                            new PointF(cx + 6f, cy - 2.5f),
                            new PointF(cx + 6f, cy + 5f),
                            new PointF(cx - 6f, cy + 5f)
                        });
                        break;

                    case IconKind.Layers:
                        g.DrawPolygon(p, new PointF[]
                        {
                            new PointF(cx, cy - 5.8f),
                            new PointF(cx + 6.2f, cy - 2.2f),
                            new PointF(cx, cy + 1.4f),
                            new PointF(cx - 6.2f, cy - 2.2f)
                        });
                        g.DrawLines(p, new PointF[] { new PointF(cx - 5.8f, cy + 1.2f), new PointF(cx, cy + 4.5f), new PointF(cx + 5.8f, cy + 1.2f) });
                        break;

                    case IconKind.Shield:
                        g.DrawPolygon(p, new PointF[]
                        {
                            new PointF(cx, cy - 6.2f),
                            new PointF(cx + 5.4f, cy - 3.8f),
                            new PointF(cx + 4.4f, cy + 2.2f),
                            new PointF(cx, cy + 6.4f),
                            new PointF(cx - 4.4f, cy + 2.2f),
                            new PointF(cx - 5.4f, cy - 3.8f)
                        });
                        break;

                    case IconKind.Wifi:
                        g.DrawArc(p, cx - 6.5f, cy - 4.5f, 13f, 11f, 215f, 110f);
                        g.DrawArc(p, cx - 4.2f, cy - 1.8f, 8.4f, 8f, 215f, 110f);
                        using (SolidBrush sb = new SolidBrush(color))
                            g.FillEllipse(sb, cx - 1.3f, cy + 2.5f, 2.6f, 2.6f);
                        break;

                    case IconKind.Plus:
                        g.DrawLine(p, cx - 5f, cy, cx + 5f, cy);
                        g.DrawLine(p, cx, cy - 5f, cx, cy + 5f);
                        break;

                    case IconKind.CloudAdd:
                        g.DrawArc(p, cx - 6f, cy - 4f, 8f, 7f, 140f, 210f);
                        g.DrawArc(p, cx - 1f, cy - 5.5f, 7f, 8f, 200f, 210f);
                        g.DrawLine(p, cx - 5f, cy + 2.5f, cx + 5f, cy + 2.5f);
                        g.DrawLine(p, cx, cy - 1f, cx, cy + 5f);
                        break;

                    case IconKind.Flame:
                        g.DrawPolygon(p, new PointF[]
                        {
                            new PointF(cx, cy - 6.2f),
                            new PointF(cx + 4.8f, cy + 0.5f),
                            new PointF(cx + 2.6f, cy + 5.8f),
                            new PointF(cx - 2.6f, cy + 5.8f),
                            new PointF(cx - 4.8f, cy + 0.5f)
                        });
                        break;

                    case IconKind.FileDoc:
                        g.DrawRectangle(p, cx - 4.5f, cy - 5.5f, 9f, 11f);
                        g.DrawLine(p, cx - 2.2f, cy - 1.5f, cx + 2.2f, cy - 1.5f);
                        g.DrawLine(p, cx - 2.2f, cy + 1.5f, cx + 2.2f, cy + 1.5f);
                        break;

                    case IconKind.Trash:
                        g.DrawLine(p, cx - 5.2f, cy - 3.8f, cx + 5.2f, cy - 3.8f);
                        g.DrawLine(p, cx - 2f, cy - 5.4f, cx + 2f, cy - 5.4f);
                        g.DrawRectangle(p, cx - 3.8f, cy - 3.8f, 7.6f, 8.8f);
                        break;

                    case IconKind.CheckCircle:
                        g.DrawEllipse(p, cx - 5.8f, cy - 5.8f, 11.6f, 11.6f);
                        g.DrawLines(p, new PointF[]
                        {
                            new PointF(cx - 2.6f, cy + 0.2f),
                            new PointF(cx - 0.6f, cy + 2.4f),
                            new PointF(cx + 3.0f, cy - 1.8f)
                        });
                        break;

                    case IconKind.Globe:
                        g.DrawEllipse(p, cx - 6f, cy - 6f, 12f, 12f);
                        g.DrawEllipse(p, cx - 3.2f, cy - 6f, 6.4f, 12f);
                        g.DrawLine(p, cx - 6f, cy, cx + 6f, cy);
                        break;
                }
            }
        }

        // =====================================================================
        // MOUSE & DRAG-DROP INTERACTION
        // =====================================================================
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point pt = e.Location;
            bool anyHover = false;

            for (int i = 0; i < _buttons.Count; i++)
            {
                VirtualGlassButton b = _buttons[i];
                bool hit = !_busy && b.Bounds.Contains(pt);
                b.TargetHover = hit ? 1f : 0f;
                b.TargetLiftY = hit ? -2.0f : 0f;
                if (hit) anyHover = true;
            }

            bool tabBHit = !_busy && _tabBrowsersRect.Contains(pt);
            bool tabSHit = !_busy && _tabShredderRect.Contains(pt);
            if (tabBHit != _hoverTabBrowsers || tabSHit != _hoverTabShredder)
            {
                _hoverTabBrowsers = tabBHit;
                _hoverTabShredder = tabSHit;
                Invalidate();
            }
            if (tabBHit || tabSHit) anyHover = true;

            if (_activeTab == RightTabMode.Browsers)
            {
                for (int i = 0; i < _activeBrowsers.Count; i++)
                {
                    BrowserItem b = _activeBrowsers[i];
                    bool rHit = b.RowBounds.Contains(pt) && _dropZoneRect.Contains(pt);
                    bool bHit = rHit && b.ActionBtnBounds.Contains(pt);
                    b.HoverMix = rHit ? 1f : 0f;
                    b.BtnHoverMix = bHit ? 1f : 0f;
                    if (bHit) anyHover = true;
                }
            }
            else
            {
                bool clrHit = !_busy && _files.Count > 0 && _clearListLinkRect.Contains(pt);
                if (clrHit != _hoverClearLink) { _hoverClearLink = clrHit; Invalidate(); }
                if (clrHit) anyHover = true;

                for (int i = 0; i < _files.Count; i++)
                {
                    ShredEntry f = _files[i];
                    bool rHit = f.RowBounds.Contains(pt) && _dropZoneRect.Contains(pt);
                    bool dHit = rHit && f.DeleteBtnBounds.Contains(pt);
                    f.HoverMix = rHit ? 1f : 0f;
                    f.DeleteHoverMix = dHit ? 1f : 0f;
                    if (dHit) anyHover = true;
                }
            }

            Cursor = anyHover ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_busy || e.Button != MouseButtons.Left) return;

            for (int i = 0; i < _buttons.Count; i++)
            {
                VirtualGlassButton b = _buttons[i];
                if (b.Bounds.Contains(e.Location))
                {
                    b.TargetScale = 0.96f;
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_busy || e.Button != MouseButtons.Left) return;

            for (int i = 0; i < _buttons.Count; i++)
            {
                VirtualGlassButton b = _buttons[i];
                if (b.TargetScale < 1.0f)
                {
                    b.Scale = 1.02f;
                    b.ScaleVel = 0.4f;
                    b.TargetScale = 1.0f;
                    if (b.Bounds.Contains(e.Location) && b.OnClick != null)
                        b.OnClick();
                }
            }

            if (_tabBrowsersRect.Contains(e.Location))
            {
                _activeTab = RightTabMode.Browsers;
                _scrollOffset = 0;
                ComputeLayout();
                Invalidate();
                return;
            }
            if (_tabShredderRect.Contains(e.Location))
            {
                _activeTab = RightTabMode.Shredder;
                _scrollOffset = 0;
                ComputeLayout();
                Invalidate();
                return;
            }

            if (_activeTab == RightTabMode.Browsers)
            {
                for (int i = 0; i < _activeBrowsers.Count; i++)
                {
                    BrowserItem b = _activeBrowsers[i];
                    if (b.ActionBtnBounds.Contains(e.Location) && _dropZoneRect.Contains(e.Location))
                    {
                        RunCleanBrowserAsync(b);
                        return;
                    }
                }
            }
            else
            {
                if (_files.Count > 0 && _clearListLinkRect.Contains(e.Location))
                {
                    _files.Clear();
                    _scrollOffset = 0;
                    SyncToggleStates();
                    _statusText = "Список шредера очищен";
                    Invalidate();
                    return;
                }

                for (int i = _files.Count - 1; i >= 0; i--)
                {
                    if (_files[i].DeleteBtnBounds.Contains(e.Location) && _dropZoneRect.Contains(e.Location))
                    {
                        _files.RemoveAt(i);
                        ComputeLayout();
                        SyncToggleStates();
                        Invalidate();
                        break;
                    }
                }
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int count = _activeTab == RightTabMode.Browsers ? _activeBrowsers.Count : _files.Count;
            int h = _activeTab == RightTabMode.Browsers ? 72 : 62;
            if (count > 3 && _dropZoneRect.Contains(e.Location))
            {
                int maxScroll = Math.Max(0, count * h - (int)(_dropZoneRect.Height - 54f));
                _scrollOffset = Math.Max(0, Math.Min(maxScroll, _scrollOffset - (e.Delta > 0 ? 36 : -36)));
                ComputeLayout();
                Invalidate();
            }
        }

        protected override void OnDragEnter(DragEventArgs drgevent)
        {
            base.OnDragEnter(drgevent);
            if (_busy) { drgevent.Effect = DragDropEffects.None; return; }
            if (drgevent.Data.GetDataPresent(DataFormats.FileDrop))
            {
                _isDragOver = true;
                drgevent.Effect = DragDropEffects.Copy;
                Invalidate();
            }
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            _isDragOver = false;
            Invalidate();
        }

        protected override void OnDragDrop(DragEventArgs drgevent)
        {
            base.OnDragDrop(drgevent);
            _isDragOver = false;
            if (_busy) return;
            string[] dropped = drgevent.Data.GetData(DataFormats.FileDrop) as string[];
            if (dropped != null && dropped.Length > 0)
            {
                AddFilePaths(dropped);
            }
        }

        private void AddFilePaths(IEnumerable<string> paths)
        {
            int added = 0;
            foreach (string raw in paths)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                bool exists = false;
                for (int i = 0; i < _files.Count; i++)
                {
                    if (string.Equals(_files[i].Path, raw, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (exists) continue;

                long bytes = 0;
                bool isDir = Directory.Exists(raw);
                if (!isDir && File.Exists(raw))
                {
                    try { bytes = new FileInfo(raw).Length; } catch { }
                }
                else if (isDir)
                {
                    try
                    {
                        foreach (string f in Directory.GetFiles(raw, "*", SearchOption.TopDirectoryOnly))
                        {
                            try { bytes += new FileInfo(f).Length; } catch { }
                        }
                    }
                    catch { }
                }

                _files.Add(new ShredEntry
                {
                    Path = raw,
                    SizeBytes = bytes,
                    IsDirectory = isDir
                });
                added++;
            }

            if (added > 0)
            {
                _dropSuccessFlash = 1.0f;
                ComputeLayout();
                SyncToggleStates();
                _statusWarn = false;
                _statusText = "Добавлено в очередь шредера: " + added + " объ.";
                Invalidate();
            }
        }

        private void OnSelectFilesClicked()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Multiselect = true;
                dlg.CheckFileExists = true;
                dlg.Title = "Выберите файлы для уничтожения";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    AddFilePaths(dlg.FileNames);
            }
        }

        private void RunCleanBrowserAsync(BrowserItem b)
        {
            if (_busy) return;
            _busy = true;
            b.IsCleaning = true;
            _statusText = string.Format("Зачистка {0}: закрытие процессов и удаление истории…", b.Name);
            Invalidate();

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    BrowserScanner.CleanBrowser(b);
                    BeginInvoke(new Action(delegate
                    {
                        b.IsCleaning = false;
                        _busy = false;
                        _statusText = string.Format("{0} зачищен · BrowsingHistoryView: 0 записей", b.Name);
                        _statusWarn = false;
                        Invalidate();
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(delegate
                    {
                        b.IsCleaning = false;
                        _busy = false;
                        _statusText = "Ошибка зачистки: " + ex.Message;
                        _statusWarn = true;
                        Invalidate();
                    }));
                }
            });
        }

        private void RunCleanAllBrowsersAsync()
        {
            if (_busy) return;
            _busy = true;
            _statusText = "Глубокая зачистка всех обнаруженных браузеров…";
            Invalidate();

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    foreach (var b in _activeBrowsers)
                    {
                        b.IsCleaning = true;
                        BeginInvoke(new Action(delegate { Invalidate(); }));
                        BrowserScanner.CleanBrowser(b);
                        b.IsCleaning = false;
                    }
                    BrowserScanner.CleanWindowsWebCache();
                    AntiForensicTracer.WipeAllTraces();

                    BeginInvoke(new Action(delegate
                    {
                        _busy = false;
                        _statusText = "Все браузеры зачищены · BrowsingHistoryView чист";
                        _statusWarn = false;
                        Invalidate();
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(delegate
                    {
                        _busy = false;
                        _statusText = "Ошибка зачистки: " + ex.Message;
                        _statusWarn = true;
                        Invalidate();
                    }));
                }
            });
        }

        private void OnOpenBHVClicked()
        {
            string bhvPath = @"c:\Users\romal\Downloads\browsinghistoryview\BrowsingHistoryView.exe";
            if (!File.Exists(bhvPath))
            {
                bhvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BrowsingHistoryView.exe");
            }
            if (File.Exists(bhvPath))
            {
                try { Process.Start(bhvPath); } catch { }
            }
            else
            {
                MessageBox.Show(this, "Файл BrowsingHistoryView.exe не найден по пути:\n" + bhvPath, "WinCleaner", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void OnAddFolderClicked()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Выберите папку для криптографического уничтожения";
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
                    AddFilePaths(new string[] { dlg.SelectedPath });
            }
        }

        private void OnPanicClicked()
        {
            List<string> paths = new List<string>();
            for (int i = 0; i < _files.Count; i++) paths.Add(_files[i].Path);
            int shredded = 0;

            RunAsync(_btnPanic, "PANIC — Выполнение зачистки…",
                delegate
                {
                    CleanResult res = new CleanResult();
                    if (paths.Count > 0)
                    {
                        CleanResult sr = Shredder.Shred(paths);
                        shredded = sr.Files;
                        res.Merge(sr);
                    }
                    res.Merge(Cleaner.CleanAll());
                    foreach (var b in _activeBrowsers)
                    {
                        try { BrowserScanner.CleanBrowser(b); } catch { }
                    }
                    AntiForensicTracer.WipeAllTraces();
                    return res;
                },
                delegate(CleanResult r) { return "PANIC выполнен · уничтожено файлов: " + shredded; },
                delegate
                {
                    for (int i = _files.Count - 1; i >= 0; i--)
                    {
                        string p = _files[i].Path;
                        if (!File.Exists(p) && !Directory.Exists(p)) _files.RemoveAt(i);
                    }
                    ComputeLayout();
                    SyncToggleStates();
                });
        }

        private void OnShredClicked()
        {
            if (_files.Count == 0)
            {
                _statusWarn = true;
                _statusText = "Список пуст — перетащите файлы или нажмите «Выбрать файлы…»";
                Invalidate();
                return;
            }

            DialogResult ask = MessageBox.Show(this,
                string.Format("Безвозвратно уничтожить объектов: {0}?\n\nДанные будут перезаписаны в 3 прохода (0x00, 0xFF, Crypto RNG) и удалены минуя Корзину.", _files.Count),
                "Подтверждение уничтожения", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (ask != DialogResult.Yes) return;

            List<string> paths = new List<string>();
            for (int i = 0; i < _files.Count; i++) paths.Add(_files[i].Path);

            RunAsync(_btnShred, "Уничтожение…",
                delegate { return Shredder.Shred(paths); },
                delegate(CleanResult r) { return "Уничтожено файлов: " + r.Files; },
                delegate
                {
                    for (int i = _files.Count - 1; i >= 0; i--)
                    {
                        string p = _files[i].Path;
                        if (!File.Exists(p) && !Directory.Exists(p)) _files.RemoveAt(i);
                    }
                    ComputeLayout();
                    SyncToggleStates();
                });
        }

        private void RunAsync(VirtualGlassButton btn, string busyText, Func<CleanResult> work, Func<CleanResult, string> doneText, Action after)
        {
            if (_busy) return;
            _busy = true;
            btn.IsLoading = true;
            btn.LoadingText = busyText;
            btn.ProgressPct = -1f;
            _statusWarn = false;
            _statusText = busyText;
            Invalidate();

            Stopwatch sw = Stopwatch.StartNew();
            ThreadPool.QueueUserWorkItem(delegate
            {
                CleanResult res;
                try { res = work(); }
                catch (Exception ex)
                {
                    res = new CleanResult();
                    res.Errors.Add(ex.Message);
                }

                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        _busy = false;
                        btn.IsLoading = false;
                        if (after != null) after();

                        string msg = doneText(res);
                        btn.IsSuccess = (res.Errors.Count == 0);
                        btn.SuccessText = "✓ " + msg;
                        btn.ResetTicks = 55;
                        btn.ScaleVel += 0.65f;

                        _statusWarn = res.Errors.Count > 0;
                        _statusText = msg + string.Format(" · {0:0.#} с", sw.Elapsed.TotalSeconds);
                        Invalidate();
                    }));
                }
                catch { }
            });
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0 Б";
            string[] units = new string[] { "Б", "КБ", "МБ", "ГБ" };
            int idx = (int)Math.Min(3, Math.Floor(Math.Log(bytes) / Math.Log(1024)));
            double val = bytes / Math.Pow(1024, idx);
            return string.Format("{0:0.#} {1}", val, units[idx]);
        }
    }

    public static class Program
    {
        public static bool IsAdministrator()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    System.Security.Principal.WindowsPrincipal principal = new System.Security.Principal.WindowsPrincipal(id);
                    return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        [STAThread]
        public static int Main(string[] args)
        {


            WinCleaner.Licensing.AntiDebug.StartWatchdog();

            bool headless = false;
            bool skipElevate = false;
            if (args != null)
            {
                foreach (string arg in args)
                {
                    if (string.IsNullOrEmpty(arg)) continue;
                    string a = arg.Trim().TrimStart('-', '/').ToLowerInvariant();
                    if (a == "all" || a == "silent" || a == "s" || a == "q")
                        headless = true;
                    if (a == "elevated" || a == "ui" || a == "noelevate")
                        skipElevate = true;
                }
            }

            if (!IsAdministrator() && !skipElevate)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = Application.ExecutablePath;
                    psi.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
                    psi.Arguments = "--elevated " + (args != null ? string.Join(" ", args) : "");
                    psi.Verb = "runas";
                    psi.UseShellExecute = true;
                    Process child = Process.Start(psi);
                    if (child != null)
                    {
                        return 0;
                    }
                }
                catch
                {
                    // If UAC was declined or unavailable, continue and open MainForm anyway
                }
            }

            if (headless)
            {
                if (!WinCleaner.Licensing.LicenseGuard.ValidateStartup())
                {
                    Console.Error.WriteLine("[ОШИБКА ЛИЦЕНЗИИ] " + WinCleaner.Licensing.LicenseManager.LastError);
                    return 2;
                }
                CleanResult r = Cleaner.CleanAll();
                return r.Errors.Count > 0 ? 1 : 0;
            }

            if (!WinCleaner.Licensing.LicenseGuard.ValidateStartup())
            {
                using (WinCleaner.Licensing.ActivationForm af = new WinCleaner.Licensing.ActivationForm())
                {
                    if (af.ShowDialog() != DialogResult.OK || !WinCleaner.Licensing.LicenseGuard.IsOperational())
                    {
                        return 0;
                    }
                }
            }



            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "WinCleaner Startup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return 0;
        }
    }
}
