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

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern uint GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

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
            Exp + @"ComDlg32\LastVisitedPidlMRU",
            Exp + @"ComDlg32\LastVisitedPidlMRULegacy",
            Exp + @"ComDlg32\CIDSizeMRU"
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
            CleanResult r = CleanClipboard();
            r.Merge(WithExplorerStopped(true, true));
            return r;
        }

        static CleanResult WithExplorerStopped(bool history, bool bags)
        {
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
        CheckCircle
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

    public class MainForm : Form
    {
        private Bitmap _backdrop;
        private readonly List<VirtualGlassButton> _buttons = new List<VirtualGlassButton>();
        private readonly List<ShredEntry> _files = new List<ShredEntry>();

        private VirtualGlassButton _btnPanic;
        private VirtualGlassButton _btnClip;
        private VirtualGlassButton _btnExplorer;
        private VirtualGlassButton _btnBags;
        private VirtualGlassButton _btnBlock;
        private VirtualGlassButton _btnNetLock;
        private VirtualGlassButton _btnSelectFiles;
        private VirtualGlassButton _btnAddFolder;
        private VirtualGlassButton _btnShred;

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

            Text = "WinCleaner — iOS 18 Glass";
            BackColor = Color.FromArgb(8, 10, 22);
            ForeColor = Color.FromArgb(245, 245, 247);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1040, 620);
            MinimumSize = new Size(920, 580);

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
                Subtitle = "Шредер списка + Буфер Win+V + Проводник + Shell Bags",
                RightTag = "0 объ.",
                Icon = IconKind.Bolt,
                Variant = BtnVariant.Red,
                IsLarge = true,
                OnClick = OnPanicClicked
            };

            _btnClip = new VirtualGlassButton
            {
                Id = "clip",
                Title = "Очистить буфер обмена",
                Subtitle = "История Win+V, текущее содержимое и папка HistoryData",
                RightTag = "WinRT + User32",
                Icon = IconKind.Clipboard,
                Variant = BtnVariant.Glass,
                OnClick = delegate
                {
                    RunAsync(_btnClip, "Очистка Win+V и HistoryData…", Cleaner.CleanClipboard,
                        delegate { return "Буфер обмена очищен"; }, null);
                }
            };

            _btnExplorer = new VirtualGlassButton
            {
                Id = "explorer",
                Title = "Очистить параметры Проводника",
                Subtitle = "Недавние файлы, TypedPaths, WordWheelQuery, RunMRU",
                RightTag = "Explorer MRU",
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
                Subtitle = "Сброс Shell\\Bags, BagMRU и истории диалогов OpenSavePidlMRU",
                RightTag = "14 веток",
                Icon = IconKind.Layers,
                Variant = BtnVariant.Glass,
                OnClick = delegate
                {
                    RunAsync(_btnBags, "Удаление веток Shell Bags…", Cleaner.CleanShellBags,
                        delegate { return "Реестр Shell Bags очищен"; }, null);
                }
            };

            _btnBlock = new VirtualGlassButton
            {
                Id = "block",
                Title = "Блокировать Hacker / Informer",
                Subtitle = "Перехват запуска IFEO (systray.exe) + фоновый сторож процессов",
                RightTag = "ВЫКЛ",
                Icon = IconKind.Shield,
                Variant = BtnVariant.Glass,
                OnClick = delegate
                {
                    RunAsync(_btnBlock, "Применение правил IFEO…", AppBlocker.ToggleBlock,
                        delegate
                        {
                            SyncToggleStates();
                            return AppBlocker.IsBlocked() ? "Утилиты заблокированы (IFEO)" : "Блокировка снята";
                        }, null);
                }
            };

            _btnNetLock = new VirtualGlassButton
            {
                Id = "netlock",
                Title = "Изоляция: только MC / AnyDesk",
                Subtitle = "Прямые фильтры ядра WFP (fwpuclnt.dll) и сброс TCP-таблицы",
                RightTag = "ОТКРЫТО",
                Icon = IconKind.Wifi,
                Variant = BtnVariant.Glass,
                OnClick = delegate
                {
                    RunAsync(_btnNetLock, "Настройка фильтров ядра WFP…", NetLockdown.Toggle,
                        delegate
                        {
                            SyncToggleStates();
                            return NetLockdown.IsLocked() ? "WFP-изоляция активна (только MC/AnyDesk)" : "Интернет разблокирован для всех";
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

            _buttons.AddRange(new VirtualGlassButton[]
            {
                _btnPanic, _btnClip, _btnExplorer, _btnBags, _btnBlock, _btnNetLock,
                _btnSelectFiles, _btnAddFolder, _btnShred
            });
        }

        private void SyncToggleStates()
        {
            bool blocked = AppBlocker.IsBlocked();
            _btnBlock.Title = blocked ? "Разблокировать Hacker / Informer" : "Блокировать Hacker / Informer";
            _btnBlock.RightTag = blocked ? "АКТИВНО" : "ВЫКЛ";
            _btnBlock.IsActiveToggle = blocked;

            bool netLocked = NetLockdown.IsLocked();
            _btnNetLock.Title = netLocked ? "Разблокировать интернет для всех" : "Изоляция: только MC / AnyDesk";
            _btnNetLock.RightTag = netLocked ? "WFP LOCK" : "ОТКРЫТО";
            _btnNetLock.IsActiveToggle = netLocked;

            _btnPanic.RightTag = _files.Count + " объ.";
            Invalidate();
        }

        private void ComputeLayout()
        {
            if (_btnPanic == null) return;
            int pad = 20;
            int gap = 20;
            int totalW = Math.Max(800, ClientSize.Width);
            int totalH = Math.Max(560, ClientSize.Height);

            int leftW = (int)((totalW - pad * 2 - gap) * 0.43f);
            int rightW = (totalW - pad * 2 - gap) - leftW;
            int cardH = totalH - pad * 2;

            _leftCardRect = new RectangleF(pad, pad, leftW, cardH);
            _rightCardRect = new RectangleF(pad + leftW + gap, pad, rightW, cardH);

            // Left Card Controls Layout
            float lx = _leftCardRect.X + 22f;
            float lw = _leftCardRect.Width - 44f;
            float ly = _leftCardRect.Y + 76f;

            _btnPanic.Bounds = new RectangleF(lx, ly, lw, 64f);
            ly += 64f + 22f; // Divider space

            float stdH = 54f;
            float stdGap = 9f;

            _btnClip.Bounds = new RectangleF(lx, ly, lw, stdH);
            ly += stdH + stdGap;

            _btnExplorer.Bounds = new RectangleF(lx, ly, lw, stdH);
            ly += stdH + stdGap;

            _btnBags.Bounds = new RectangleF(lx, ly, lw, stdH);
            ly += stdH + 22f; // Second divider space

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

            _btnSelectFiles.Bounds = new RectangleF(rx, btnY, btnW, bottomBtnH);
            _btnAddFolder.Bounds = new RectangleF(rx + btnW + btnGap, btnY, btnW, bottomBtnH);
            _btnShred.Bounds = new RectangleF(rx + (btnW + btnGap) * 2f, btnY, btnW, bottomBtnH);

            // File rows layout inside drop zone
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

            // Header & Subtitle
            float hx = _leftCardRect.X + 22f;
            float hy = _leftCardRect.Y + 20f;
            TextRenderer.DrawText(g, "Системная очистка и защита", _fontCardTitle,
                new Point((int)hx, (int)hy), Color.White, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "Мгновенное удаление следов активности и блокировка анализаторов", _fontCardSub,
                new Point((int)hx, (int)(hy + 27f)), Color.FromArgb(155, 245, 245, 247), TextFormatFlags.NoPadding);

            // Dividers
            float div1Y = _btnPanic.Bounds.Bottom + 11f;
            DrawHairlineDivider(g, hx, div1Y, _leftCardRect.Width - 44f);

            float div2Y = _btnBags.Bounds.Bottom + 11f;
            DrawHairlineDivider(g, hx, div2Y, _leftCardRect.Width - 44f);

            // Render Left Buttons
            DrawVirtualButton(g, _btnPanic);
            DrawVirtualButton(g, _btnClip);
            DrawVirtualButton(g, _btnExplorer);
            DrawVirtualButton(g, _btnBags);
            DrawVirtualButton(g, _btnBlock);
            DrawVirtualButton(g, _btnNetLock);

            // Footer Status Line
            float footY = _leftCardRect.Bottom - 36f;
            DrawHairlineDivider(g, hx, footY - 8f, _leftCardRect.Width - 44f);

            Rectangle statusRect = new Rectangle((int)hx, (int)footY, (int)(_leftCardRect.Width - 105f), 20);
            Color stColor = _statusWarn ? Color.FromArgb(255, 110, 100) : Color.FromArgb(165, 245, 245, 247);
            TextRenderer.DrawText(g, _statusText, _fontFooter, statusRect, stColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            Rectangle uiTagRect = new Rectangle((int)(_leftCardRect.Right - 82f), (int)footY, 60, 20);
            TextRenderer.DrawText(g, "iOS 18 UI", _fontMonoTag, uiTagRect, Color.FromArgb(115, 245, 245, 247),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
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
            float hy = _rightCardRect.Y + 20f;
            TextRenderer.DrawText(g, "Криптографический шредер (3 прохода DoD)", _fontCardTitle,
                new Point((int)hx, (int)hy), Color.White, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "Перезапись 0x00 · 0xFF · Crypto RNG · Сброс меток 2000-01-01 · Тройное переименование", _fontCardSub,
                new Point((int)hx, (int)(hy + 27f)), Color.FromArgb(155, 245, 245, 247), TextFormatFlags.NoPadding);

            // Top-right Total Count & Size + "Очистить список"
            long totalBytes = 0;
            for (int i = 0; i < _files.Count; i++) totalBytes += _files[i].SizeBytes;
            string summary = string.Format("{0} объ. · {1}", _files.Count, FormatSize(totalBytes));

            Rectangle sumRect = new Rectangle((int)(_rightCardRect.Right - 210f), (int)hy, 188, 20);
            TextRenderer.DrawText(g, summary, _fontMonoTag, sumRect, Color.FromArgb(200, 245, 245, 247),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

            if (_files.Count > 0)
            {
                Color linkCol = _hoverClearLink ? Color.FromArgb(100, 210, 255) : Color.FromArgb(10, 132, 255);
                TextRenderer.DrawText(g, "Очистить список", _fontFooter,
                    Rectangle.Round(_clearListLinkRect), linkCol,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }

            // Inner Drop Zone Container
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

            // Clip file items inside the drop zone without leaking Region handles
            GraphicsState dzState = g.Save();
            Rectangle dzClipRect = new Rectangle(
                (int)_dropZoneRect.X + 2,
                (int)_dropZoneRect.Y + 2,
                Math.Max(1, (int)_dropZoneRect.Width - 4),
                Math.Max(1, (int)_dropZoneRect.Height - 40));
            g.SetClip(dzClipRect);

            if (_files.Count == 0)
            {
                // Empty state hint
                float cx = _dropZoneRect.X + _dropZoneRect.Width * 0.5f;
                float cy = _dropZoneRect.Y + (_dropZoneRect.Height - 36f) * 0.5f - 12f;

                RectangleF iconBadge = new RectangleF(cx - 24f, cy - 36f, 48f, 48f);
                DrawGlassSurface(g, iconBadge, 14f,
                    Color.FromArgb(26, 255, 255, 255),
                    Color.FromArgb(16, 255, 255, 255),
                    Color.FromArgb(45, 255, 255, 255), 1f, 30);
                DrawVectorIcon(g, IconKind.CloudAdd, new RectangleF(cx - 10f, cy - 22f, 20f, 20f), Color.FromArgb(10, 132, 255));

                Rectangle hint1 = new Rectangle((int)_dropZoneRect.X + 20, (int)(cy + 22f), (int)_dropZoneRect.Width - 40, 22);
                TextRenderer.DrawText(g, "Перетащите файлы или папки сюда", _fontBtnTitle, hint1,
                    Color.FromArgb(230, 245, 245, 247), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                Rectangle hint2 = new Rectangle((int)_dropZoneRect.X + 20, (int)(cy + 44f), (int)_dropZoneRect.Width - 40, 20);
                TextRenderer.DrawText(g, "Системные каталоги Windows и Program Files автоматически защищены от удаления", _fontBtnSub, hint2,
                    Color.FromArgb(135, 245, 245, 247), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else
            {
                for (int i = 0; i < _files.Count; i++)
                {
                    DrawFileRow(g, _files[i]);
                }
            }

            g.Restore(dzState);

            // Bottom Footer inside Drop Zone
            float dzFootY = _dropZoneRect.Bottom - 34f;
            DrawHairlineDivider(g, _dropZoneRect.X + 14f, dzFootY - 4f, _dropZoneRect.Width - 28f);

            DrawVectorIcon(g, IconKind.CheckCircle, new RectangleF(_dropZoneRect.X + 16f, dzFootY + 5f, 14f, 14f), Color.FromArgb(48, 209, 88));
            Rectangle dzFootLeft = new Rectangle((int)(_dropZoneRect.X + 36f), (int)dzFootY, (int)(_dropZoneRect.Width - 180f), 24);
            TextRenderer.DrawText(g, "Очистка ComDlg32 MRU и ярлыков Recent (.lnk) после прохода", _fontBtnSub, dzFootLeft,
                Color.FromArgb(150, 245, 245, 247), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            Rectangle dzFootRight = new Rectangle((int)(_dropZoneRect.Right - 150f), (int)dzFootY, 136, 24);
            TextRenderer.DrawText(g, "WriteThrough · 64 КБ", _fontFileMeta, dzFootRight,
                Color.FromArgb(135, 245, 245, 247), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

            // Bottom 3 Action Buttons
            DrawVirtualButton(g, _btnSelectFiles);
            DrawVirtualButton(g, _btnAddFolder);
            DrawVirtualButton(g, _btnShred);
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

            string meta = string.Format("{0}  ·  3 прохода (0x00, 0xFF, RNG)", FormatSize(item.SizeBytes));
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

            if (hasSubtitle)
            {
                Font tf = b.IsLarge ? _fontPanicTitle : _fontBtnTitle;
                Rectangle titleRect = new Rectangle(textLeft, (int)(rect.Y + (b.IsLarge ? 11f : 8f)), textWidth, 22);
                TextRenderer.DrawText(g, mainTitle, tf, titleRect, Color.White,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                Rectangle subRect = new Rectangle(textLeft, (int)(rect.Y + (b.IsLarge ? 34f : 29f)), textWidth, 18);
                TextRenderer.DrawText(g, b.Subtitle, _fontBtnSub, subRect, Color.FromArgb(155, 245, 245, 247),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            else
            {
                Rectangle titleRect = new Rectangle(textLeft - 4, (int)rect.Y, (int)(rect.Width - 48f), (int)rect.Height);
                TextRenderer.DrawText(g, mainTitle, _fontBtnTitle, titleRect, Color.White,
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

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_files.Count > 3 && _dropZoneRect.Contains(e.Location))
            {
                int maxScroll = Math.Max(0, _files.Count * 62 - (int)(_dropZoneRect.Height - 54f));
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

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                MessageBox.Show(e.Exception.ToString(), "WinCleaner UI Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

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
