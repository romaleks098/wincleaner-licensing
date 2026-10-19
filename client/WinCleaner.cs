using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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
            catch
            {
                return false;
            }
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
            CleanResult r = new CleanResult();
            bool willBlock = !IsBlocked();

            KillTargets();

            if (willBlock)
                StartWatchdog();
            else
                StopWatchdog();

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
                                try
                                {
                                    ifeo.DeleteSubKeyTree(t, false);
                                    r.RegKeys++;
                                }
                                catch { }
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
                catch (Exception ex)
                {
                    r.Errors.Add("IFEO: " + ex.Message);
                }
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

        public static void StopWatchdog()
        {
            _watchdogActive = false;
        }

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
                        try { procs[j].Kill(); }
                        catch { }
                        finally
                        {
                            try { procs[j].Dispose(); } catch { }
                        }
                    }
                }
                catch { }
            }
        }
    }

    public static class NetLockdown
    {
        const string Prefix = "WinCleaner_NetLock_";
        const string RegKeyPath = @"Software\WinCleaner";
        const string RegValueName = "NetLocked";
        static volatile bool _cachedState = false;

        public static bool IsLocked()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", "advfirewall show allprofiles firewallpolicy");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string outStr = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(2000);
                    if (!string.IsNullOrEmpty(outStr) && outStr.IndexOf("BlockOutbound", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _cachedState = true;
                        return true;
                    }
                }
            }
            catch { }

            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegKeyPath, false))
                {
                    if (k != null)
                    {
                        object val = k.GetValue(RegValueName);
                        if (val != null) _cachedState = Convert.ToInt32(val) == 1;
                    }
                }
            }
            catch { }

            return _cachedState;
        }

        public static CleanResult Toggle()
        {
            CleanResult r = new CleanResult();
            bool willLock = !IsLocked();

            try
            {
                List<string> cmds = new List<string>();

                // 1. Delete all existing rules
                cmds.Add(string.Format("advfirewall firewall delete rule name=\"{0}DNS\"", Prefix));
                cmds.Add(string.Format("advfirewall firewall delete rule name=\"{0}DHCP\"", Prefix));
                cmds.Add(string.Format("advfirewall firewall delete rule name=\"{0}NTP\"", Prefix));
                cmds.Add(string.Format("advfirewall firewall delete rule name=\"{0}Loopback\"", Prefix));
                cmds.Add(string.Format("advfirewall firewall delete rule name=\"{0}Ports\"", Prefix));
                cmds.Add(string.Format("advfirewall firewall delete rule name=\"{0}App\"", Prefix));

                if (willLock)
                {
                    // 2. Allow vital network protocols
                    cmds.Add(string.Format("advfirewall firewall add rule name=\"{0}DNS\" dir=out action=allow protocol=UDP remoteport=53", Prefix));
                    cmds.Add(string.Format("advfirewall firewall add rule name=\"{0}DNS\" dir=out action=allow protocol=TCP remoteport=53", Prefix));
                    cmds.Add(string.Format("advfirewall firewall add rule name=\"{0}DHCP\" dir=out action=allow protocol=UDP remoteport=67-68", Prefix));
                    cmds.Add(string.Format("advfirewall firewall add rule name=\"{0}DHCP\" dir=in action=allow protocol=UDP localport=67-68", Prefix));
                    cmds.Add(string.Format("advfirewall firewall add rule name=\"{0}NTP\" dir=out action=allow protocol=UDP remoteport=123", Prefix));

                    // 3. Allow Localhost / Loopback (Minecraft internal server)
                    cmds.Add(string.Format("advfirewall firewall add rule name=\"{0}Loopback\" dir=out action=allow remoteip=127.0.0.1,::1", Prefix));
                    cmds.Add(string.Format("advfirewall firewall add rule name=\"{0}Loopback\" dir=in action=allow remoteip=127.0.0.1,::1", Prefix));

                    // 4. AnyDesk fallback port rules
                    cmds.Add(string.Format("advfirewall firewall add rule name=\"{0}Ports\" dir=out action=allow protocol=TCP remoteport=7070,6568", Prefix));
                    cmds.Add(string.Format("advfirewall firewall add rule name=\"{0}Ports\" dir=out action=allow protocol=UDP remoteport=7070,6568", Prefix));
                    cmds.Add(string.Format("advfirewall firewall add rule name=\"{0}Ports\" dir=in action=allow protocol=TCP localport=7070,6568", Prefix));
                    cmds.Add(string.Format("advfirewall firewall add rule name=\"{0}Ports\" dir=in action=allow protocol=UDP localport=7070,6568", Prefix));

                    // 5. Allowed Application paths (Minecraft, AnyDesk, WinCleaner)
                    List<string> targets = FindAllowedExecutables();
                    foreach (string path in targets)
                    {
                        cmds.Add(string.Format("advfirewall firewall add rule name=\"{0}App\" dir=out action=allow program=\"{1}\" enable=yes", Prefix, path));
                        cmds.Add(string.Format("advfirewall firewall add rule name=\"{0}App\" dir=in action=allow program=\"{1}\" enable=yes", Prefix, path));
                    }

                    // 6. Set policy: block all outbound & inbound except allowed
                    cmds.Add("advfirewall set allprofiles firewallpolicy blockinbound,blockoutbound");
                    r.RegKeys += targets.Count;
                }
                else
                {
                    // Unlock: allow all outbound
                    cmds.Add("advfirewall set allprofiles firewallpolicy blockinbound,allowoutbound");
                    r.RegKeys++;
                }

                string err = ExecuteBatchNetsh(cmds);
                if (!string.IsNullOrEmpty(err))
                {
                    r.Errors.Add("Брандмауэр: " + err);
                }

                _cachedState = willLock;
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegKeyPath))
                    {
                        if (k != null)
                            k.SetValue(RegValueName, willLock ? 1 : 0, RegistryValueKind.DWord);
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                r.Errors.Add("Сетевая изоляция: " + ex.Message);
            }

            return r;
        }

        static string ExecuteBatchNetsh(IEnumerable<string> commands)
        {
            string tmpFile = Path.Combine(Path.GetTempPath(), "wc_netsh_" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                // CRITICAL: UTF8 WITHOUT BOM so netsh parses commands properly
                File.WriteAllLines(tmpFile, commands, new UTF8Encoding(false));

                ProcessStartInfo psi = new ProcessStartInfo("netsh", "-f \"" + tmpFile + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                using (Process p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(7000);

                    if (p.ExitCode != 0)
                    {
                        string combined = (stdout + " " + stderr).Trim();
                        if (combined.IndexOf("повышение прав", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            combined.IndexOf("administrator", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // Try elevated retry via runas
                            try
                            {
                                ProcessStartInfo elevated = new ProcessStartInfo("netsh", "-f \"" + tmpFile + "\"");
                                elevated.Verb = "runas";
                                elevated.UseShellExecute = true;
                                elevated.WindowStyle = ProcessWindowStyle.Hidden;
                                using (Process pElev = Process.Start(elevated))
                                {
                                    pElev.WaitForExit(7000);
                                    if (pElev.ExitCode == 0) return null;
                                }
                            }
                            catch { }
                            return "Требуются права администратора для изменения правил брандмауэра.";
                        }
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            finally
            {
                try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch { }
            }
        }

        static List<string> FindAllowedExecutables()
        {
            List<string> list = new List<string>();

            // 1. Current executable (WinCleaner)
            try
            {
                string own = Application.ExecutablePath;
                if (!string.IsNullOrEmpty(own) && File.Exists(own) && !list.Contains(own))
                    list.Add(own);
            }
            catch { }

            // 2. Running processes: AnyDesk, Minecraft, Java
            string[] procNames = new string[] { "AnyDesk", "javaw", "java", "MinecraftLauncher", "Minecraft", "Minecraft.Windows", "TLauncher", "PrismLauncher", "ModrinthApp" };
            foreach (string pn in procNames)
            {
                try
                {
                    foreach (Process p in Process.GetProcessesByName(pn))
                    {
                        try
                        {
                            if (p.MainModule != null)
                            {
                                string path = p.MainModule.FileName;
                                if (!string.IsNullOrEmpty(path) && File.Exists(path) && !list.Contains(path))
                                    list.Add(path);
                            }
                        }
                        catch { }
                        finally
                        {
                            try { p.Dispose(); } catch { }
                        }
                    }
                }
                catch { }
            }

            // 3. Fixed AnyDesk paths
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pfx = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            string[] anydeskFixed = new string[]
            {
                Path.Combine(pf, @"AnyDesk\AnyDesk.exe"),
                Path.Combine(pfx, @"AnyDesk\AnyDesk.exe"),
                Path.Combine(appData, @"AnyDesk\AnyDesk.exe"),
                Path.Combine(localApp, @"AnyDesk\AnyDesk.exe"),
                Path.Combine(userProfile, @"Downloads\AnyDesk.exe"),
                Path.Combine(userProfile, @"Desktop\AnyDesk.exe"),
                @"C:\ProgramData\AnyDesk\AnyDesk.exe"
            };
            foreach (string p in anydeskFixed)
            {
                if (File.Exists(p) && !list.Contains(p)) list.Add(p);
            }

            // 4. Fixed Minecraft Launchers
            string[] mcLaunchers = new string[]
            {
                Path.Combine(pfx, @"Minecraft Launcher\MinecraftLauncher.exe"),
                Path.Combine(pf, @"Minecraft Launcher\MinecraftLauncher.exe"),
                Path.Combine(localApp, @"Programs\launcher\Minecraft.exe"),
                Path.Combine(appData, @".minecraft\launcher.exe"),
                Path.Combine(localApp, @"Programs\PrismLauncher\prismlauncher.exe"),
                Path.Combine(localApp, @"Programs\Modrinth App\Modrinth App.exe")
            };
            foreach (string p in mcLaunchers)
            {
                if (File.Exists(p) && !list.Contains(p)) list.Add(p);
            }

            // 5. Scan directories for javaw.exe and java.exe (Minecraft runtimes)
            List<string> javaDirs = new List<string>();
            javaDirs.Add(Path.Combine(appData, @".minecraft\runtime"));
            javaDirs.Add(Path.Combine(appData, @".tlauncher"));
            javaDirs.Add(Path.Combine(userProfile, @"curseforge\minecraft\Install\runtime"));
            javaDirs.Add(Path.Combine(localApp, @"Programs\PrismLauncher\java"));
            javaDirs.Add(Path.Combine(pf, @"Java"));
            javaDirs.Add(Path.Combine(pfx, @"Java"));
            javaDirs.Add(Path.Combine(pf, @"Eclipse Adoptium"));
            javaDirs.Add(Path.Combine(pf, @"BellSoft"));
            javaDirs.Add(Path.Combine(pf, @"Zulu"));

            string envJava = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(envJava) && Directory.Exists(envJava))
                javaDirs.Add(envJava);

            foreach (string dir in javaDirs)
            {
                if (Directory.Exists(dir))
                {
                    try
                    {
                        foreach (string ext in new string[] { "javaw.exe", "java.exe" })
                        {
                            foreach (string f in Directory.GetFiles(dir, ext, SearchOption.AllDirectories))
                            {
                                if (!list.Contains(f)) list.Add(f);
                            }
                        }
                    }
                    catch { }
                }
            }

            return list;
        }
    }

    // ------------------------------------------------------------------
    //  Очистка буфера обмена, истории Проводника и Shell Bags
    // ------------------------------------------------------------------
    public static class Cleaner
    {
        const string Exp = @"Software\Microsoft\Windows\CurrentVersion\Explorer\";

        // Все места, где Windows хранит вид папок и историю посещённых каталогов
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

        // История диалогов «Открыть / Сохранить» (туда попадают выбранные через окно файлы)
        static readonly string[] DialogKeys = new string[]
        {
            Exp + @"ComDlg32\OpenSavePidlMRU",
            Exp + @"ComDlg32\LastVisitedPidlMRU",
            Exp + @"ComDlg32\LastVisitedPidlMRULegacy",
            Exp + @"ComDlg32\CIDSizeMRU"
        };

        // ---------------- Публичные операции ----------------

        // Шаги пишут свой статус сюда (окно показывает, на чём сейчас работа)
        public static Action<string> Progress;
        public static Action<int, string> DetailedProgress;

        public static void ReportProgress(string text) { Step(text); }

        static void Step(string text)
        {
            Action<string> p = Progress;
            if (p != null) { try { p(text); } catch { } }
        }

        // WinRT-класс буфера обмена не работает из MTA-потоков (пул потоков),
        // поэтому ClearHistory вызываем в отдельном STA-потоке с тайм-аутом,
        // чтобы возможное зависание системного вызова не блокировало программу.
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
            CleanResult r = new CleanResult();

            // 1. История Win + V (WinRT)
            Step("Буфер: история Win+V…");
            ClearWinVHistory(r);

            // 2. Текущее содержимое буфера
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
            // 3. Файловый кэш истории (закреплённые элементы в папке Pinned не трогаем)
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            CleanDir(Path.Combine(local, @"Microsoft\Windows\Clipboard\HistoryData"), r, true);

            return r;
        }

        public static CleanResult CleanExplorer()
        {
            return WithExplorerStopped(true, false);
        }

        public static CleanResult CleanShellBags()
        {
            return WithExplorerStopped(false, true);
        }

        public static CleanResult CleanAll()
        {
            CleanResult r = CleanClipboard();
            r.Merge(WithExplorerStopped(true, true));
            return r;
        }

        // Проводник держит эти данные в памяти и при выходе пишет обратно в реестр,
        // поэтому сначала завершаем его, потом чистим и запускаем заново.
        static CleanResult WithExplorerStopped(bool history, bool bags)
        {
            CleanResult r = new CleanResult();

            if (history)
            {
                try { NativeMethods.SHAddToRecentDocs(0, IntPtr.Zero); } catch { }
            }

            Step("Перезапуск Проводника…");
            KillExplorer();
            try
            {
                Step(history && bags ? "Проводник и Shell Bags…" : (history ? "Параметры Проводника…" : "Shell Bags…"));
                if (history) HistoryPart(r);
                if (bags) BagsPart(r);
            }
            catch (Exception ex)
            {
                r.Errors.Add(ex.Message);
            }
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

            // Ярлыки недавних файлов
            try
            {
                if (Directory.Exists(recent))
                {
                    foreach (string f in Directory.GetFiles(recent, "*.lnk", SearchOption.TopDirectoryOnly))
                        DeleteFileQuiet(f, r);
                }
            }
            catch { }

            // Jump Lists
            CleanDir(Path.Combine(recent, "AutomaticDestinations"), r, false);
            CleanDir(Path.Combine(recent, "CustomDestinations"), r, false);

            // Реестр
            ClearAll(Exp + "RecentDocs", r);
            ClearValues(Exp + "TypedPaths", r);
            ClearAll(Exp + "WordWheelQuery", r);
            ClearValues(Exp + "RunMRU", r);

            // Кэш эскизов и иконок
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

        // ---------------- Процесс explorer.exe ----------------

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

        // ---------------- Вспомогательные ----------------

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
                foreach (FileInfo f in d.GetFiles())
                    DeleteFileQuiet(f.FullName, r);

                if (recursive)
                {
                    foreach (DirectoryInfo s in d.GetDirectories())
                    {
                        try
                        {
                            CleanDir(s.FullName, r, true);
                            s.Delete();
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                r.Errors.Add(path + ": " + ex.Message);
            }
        }

        static void DeleteTree(string path, CleanResult r)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(path))
                {
                    if (k == null) return;
                }
                Registry.CurrentUser.DeleteSubKeyTree(path, false);
                r.RegKeys++;
            }
            catch (Exception ex)
            {
                r.Errors.Add(path + ": " + ex.Message);
            }
        }

        static void ClearValues(string path, CleanResult r)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(path, true))
                {
                    if (k == null) return;
                    foreach (string v in k.GetValueNames())
                    {
                        try { k.DeleteValue(v, false); r.RegKeys++; } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                r.Errors.Add(path + ": " + ex.Message);
            }
        }

        static void ClearAll(string path, CleanResult r)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(path, true))
                {
                    if (k == null) return;
                    foreach (string s in k.GetSubKeyNames())
                    {
                        try { k.DeleteSubKeyTree(s, false); r.RegKeys++; } catch { }
                    }
                    foreach (string v in k.GetValueNames())
                    {
                        try { k.DeleteValue(v, false); r.RegKeys++; } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                r.Errors.Add(path + ": " + ex.Message);
            }
        }
    }

    // ------------------------------------------------------------------
    //  Уничтожение файлов: перезапись, обнуление, переименование, удаление
    //  (минуя Корзину)
    // ------------------------------------------------------------------
    public static class Shredder
    {
        static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        public static CleanResult Shred(IList<string> paths)
        {
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
                catch (Exception ex)
                {
                    r.Errors.Add(p + ": " + ex.Message);
                }
            }

            // Файлы, выбранные через диалог, остаются в истории диалогов — убираем её
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
                    File.Delete(path); // ссылка: удаляем саму ссылку, не цель
                    r.Files++;
                    return;
                }

                File.SetAttributes(path, FileAttributes.Normal);
                long len = new FileInfo(path).Length;

                if (len > 0)
                {
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Write,
                        FileShare.None, 4096, FileOptions.WriteThrough))
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

                // Стираем метки времени
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
            catch (Exception ex)
            {
                r.Errors.Add(name + ": " + ex.Message);
            }
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
            catch (Exception ex)
            {
                r.Errors.Add(dir + ": " + ex.Message);
            }
        }

        // Несколько раз переименовываем в случайное имя, чтобы исходное имя не осталось в таблице файлов
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

        // Защита от случайного уничтожения системных и корневых путей
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

    // ------------------------------------------------------------------
    //  Кастомная кнопка ProgressButton с анимацией, ripple и шкалой
    // ------------------------------------------------------------------
    public class ProgressButton : Button, IProgress<int>
    {
        public enum ProgressState
        {
            Idle,
            Determinate,
            Indeterminate,
            Success,
            Error
        }

        private readonly System.Windows.Forms.Timer _animTimer;
        private ProgressState _state = ProgressState.Idle;

        private float _currentProgress = 0f;
        private float _targetProgress = 0f;
        private float _indeterminatePos = 0f;

        private float _hoverProgress = 0f;
        private float _pressProgress = 0f;
        private float _targetHover = 0f;
        private float _targetPress = 0f;

        private Point _ripplePoint;
        private float _rippleProgress = 1f;
        private bool _isRippling = false;

        private float _shakePhase = 0f;
        private float _shakeOffset = 0f;
        private int _resetCountdown = 0;

        private string _originalText = "Старт";
        private string _currentDisplayMessage = "";

        public Color NormalColor { get; set; }
        public Color HoverColor { get; set; }
        public Color PressedColor { get; set; }
        public Color ProgressColor { get; set; }
        public Color SuccessColor { get; set; }
        public Color ErrorColor { get; set; }
        public int CornerRadius { get; set; }
        public bool EnableRipple { get; set; }

        public bool IsBusy
        {
            get { return _state != ProgressState.Idle; }
        }

        public void StartIndeterminate()
        {
            StartIndeterminate("Выполняется…");
        }

        public void StartIndeterminate(string statusText)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(StartIndeterminate), statusText);
                return;
            }

            if (_state == ProgressState.Idle) _originalText = Text;
            _state = ProgressState.Indeterminate;
            _currentDisplayMessage = string.IsNullOrEmpty(statusText) ? "Выполняется…" : statusText;
            _indeterminatePos = 0f;
            _resetCountdown = 0;
            StartAnimation();
        }

        public void SetProgress(int percent)
        {
            SetProgress(percent, null);
        }

        public void SetProgress(int percent, string statusText)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<int, string>(SetProgress), percent, statusText);
                return;
            }

            if (_state == ProgressState.Idle) _originalText = Text;
            _state = ProgressState.Determinate;
            _targetProgress = Math.Max(0f, Math.Min(100f, (float)percent));

            if (!string.IsNullOrEmpty(statusText))
                _currentDisplayMessage = statusText;
            else
                _currentDisplayMessage = string.Format("{0}%", (int)_targetProgress);

            StartAnimation();
        }

        public void Complete()
        {
            Complete("✓ Готово");
        }

        public void Complete(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(Complete), message);
                return;
            }

            _state = ProgressState.Success;
            _targetProgress = 100f;
            _currentDisplayMessage = string.IsNullOrEmpty(message) ? "✓ Готово" : message;
            _resetCountdown = 45;
            StartAnimation();
        }

        public void SetError()
        {
            SetError("✕ Ошибка");
        }

        public void SetError(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(SetError), message);
                return;
            }

            _state = ProgressState.Error;
            _currentDisplayMessage = string.IsNullOrEmpty(message) ? "✕ Ошибка" : message;
            _shakePhase = 1f;
            _resetCountdown = 50;
            StartAnimation();
        }

        void IProgress<int>.Report(int value)
        {
            SetProgress(value, null);
        }

        public void ResetToIdle()
        {
            _state = ProgressState.Idle;
            _currentProgress = 0f;
            _targetProgress = 0f;
            _shakePhase = 0f;
            _shakeOffset = 0f;
            _resetCountdown = 0;
            Invalidate();
        }

        public ProgressButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);

            UpdateStyles();

            NormalColor = Color.FromArgb(32, 24, 50);
            HoverColor = Color.FromArgb(46, 35, 72);
            PressedColor = Color.FromArgb(64, 49, 98);
            ProgressColor = Color.FromArgb(108, 92, 231);
            SuccessColor = Color.FromArgb(0, 184, 148);
            ErrorColor = Color.FromArgb(214, 48, 49);
            ForeColor = Color.FromArgb(234, 229, 247);
            CornerRadius = 10;
            EnableRipple = true;
            Font = new Font("Segoe UI", 9f);
            Cursor = Cursors.Hand;

            _animTimer = new System.Windows.Forms.Timer();
            _animTimer.Interval = 16;
            _animTimer.Tick += OnAnimTick;
        }

        private void OnAnimTick(object sender, EventArgs e)
        {
            bool needInvalidate = false;

            if (_state == ProgressState.Idle)
            {
                if (Math.Abs(_hoverProgress - _targetHover) > 0.005f)
                {
                    _hoverProgress += (_targetHover - _hoverProgress) * 0.22f;
                    needInvalidate = true;
                }
                else
                {
                    _hoverProgress = _targetHover;
                }

                if (Math.Abs(_pressProgress - _targetPress) > 0.005f)
                {
                    _pressProgress += (_targetPress - _pressProgress) * 0.32f;
                    needInvalidate = true;
                }
                else
                {
                    _pressProgress = _targetPress;
                }
            }

            if (_isRippling)
            {
                _rippleProgress += 0.065f;
                if (_rippleProgress >= 1f)
                {
                    _rippleProgress = 1f;
                    _isRippling = false;
                }
                needInvalidate = true;
            }

            if (_state == ProgressState.Determinate || _state == ProgressState.Success)
            {
                if (Math.Abs(_currentProgress - _targetProgress) > 0.2f)
                {
                    _currentProgress += (_targetProgress - _currentProgress) * 0.25f;
                    needInvalidate = true;
                }
                else
                {
                    _currentProgress = _targetProgress;
                }
            }

            if (_state == ProgressState.Indeterminate)
            {
                _indeterminatePos += 0.035f;
                if (_indeterminatePos > 1.4f)
                {
                    _indeterminatePos = -0.4f;
                }
                needInvalidate = true;
            }

            if (_shakePhase > 0f)
            {
                _shakePhase -= 0.05f;
                if (_shakePhase < 0f) _shakePhase = 0f;

                _shakeOffset = (float)(Math.Sin((1f - _shakePhase) * Math.PI * 6.0) * (_shakePhase * 6.5f));
                needInvalidate = true;
            }
            else
            {
                _shakeOffset = 0f;
            }

            if (_resetCountdown > 0)
            {
                _resetCountdown--;
                if (_resetCountdown == 0)
                {
                    ResetToIdle();
                    return;
                }
                needInvalidate = true;
            }

            if (needInvalidate)
            {
                Invalidate();
            }
            else if (_state == ProgressState.Idle && !_isRippling && _hoverProgress == _targetHover && _pressProgress == _targetPress)
            {
                _animTimer.Stop();
            }
        }

        private void StartAnimation()
        {
            if (!_animTimer.Enabled)
            {
                _animTimer.Start();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (!IsBusy)
            {
                _targetHover = 1f;
                StartAnimation();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!IsBusy)
            {
                _targetHover = 0f;
                _targetPress = 0f;
                StartAnimation();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!IsBusy && e.Button == MouseButtons.Left)
            {
                _targetPress = 1f;
                if (EnableRipple)
                {
                    _ripplePoint = e.Location;
                    _rippleProgress = 0f;
                    _isRippling = true;
                }
                StartAnimation();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!IsBusy)
            {
                _targetPress = 0f;
                StartAnimation();
            }
        }

        protected override void OnClick(EventArgs e)
        {
            if (IsBusy) return;
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Rectangle clientRect = ClientRectangle;
            if (clientRect.Width <= 0 || clientRect.Height <= 0) return;

            g.Clear(Parent != null ? Parent.BackColor : Color.FromArgb(20, 14, 33));

            float pressEase = EaseOut(_pressProgress);
            float hoverEase = EaseOut(_hoverProgress);

            float insetX = pressEase * 2f;
            float insetY = pressEase * 1.2f;
            float offsetY = pressEase * 1.5f;

            RectangleF shadowRect = new RectangleF(
                clientRect.X + 1f + _shakeOffset,
                clientRect.Y + 2f + (hoverEase * 2f),
                clientRect.Width - 2f,
                clientRect.Height - 3f
            );

            int shadowAlpha = (int)(15 + hoverEase * 35 - pressEase * 10);
            if (shadowAlpha > 0)
            {
                using (GraphicsPath shadowPath = CreateRoundPath(shadowRect, CornerRadius))
                using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(shadowAlpha, 0, 0, 0)))
                {
                    g.FillPath(shadowBrush, shadowPath);
                }
            }

            RectangleF btnRect = new RectangleF(
                clientRect.X + insetX + _shakeOffset,
                clientRect.Y + insetY + offsetY,
                clientRect.Width - (insetX * 2f),
                clientRect.Height - (insetY * 2f) - offsetY
            );

            Color baseColor = InterpolateColor(NormalColor, HoverColor, hoverEase);
            Color finalColor = InterpolateColor(baseColor, PressedColor, pressEase);

            using (GraphicsPath btnPath = CreateRoundPath(btnRect, CornerRadius))
            {
                using (SolidBrush btnBrush = new SolidBrush(finalColor))
                {
                    g.FillPath(btnBrush, btnPath);
                }

                if (_state == ProgressState.Determinate)
                {
                    float w = btnRect.Width * (_currentProgress / 100f);
                    if (w > 0f)
                    {
                        RectangleF fillRect = new RectangleF(btnRect.X, btnRect.Y, w, btnRect.Height);
                        g.SetClip(btnPath);
                        using (SolidBrush pb = new SolidBrush(ProgressColor))
                        {
                            g.FillRectangle(pb, fillRect);
                        }
                        g.ResetClip();
                    }
                }
                else if (_state == ProgressState.Indeterminate)
                {
                    g.SetClip(btnPath);
                    float beamW = btnRect.Width * 0.45f;
                    float beamX = btnRect.X + (_indeterminatePos * btnRect.Width);

                    RectangleF beamRect = new RectangleF(beamX, btnRect.Y, beamW, btnRect.Height);
                    using (LinearGradientBrush lgb = new LinearGradientBrush(
                        new PointF(beamRect.Left, beamRect.Top),
                        new PointF(beamRect.Right, beamRect.Top),
                        Color.Transparent,
                        ProgressColor))
                    {
                        ColorBlend blend = new ColorBlend();
                        blend.Colors = new Color[] { Color.Transparent, ProgressColor, Color.Transparent };
                        blend.Positions = new float[] { 0f, 0.5f, 1f };
                        lgb.InterpolationColors = blend;
                        g.FillRectangle(lgb, beamRect);
                    }
                    g.ResetClip();
                }
                else if (_state == ProgressState.Success)
                {
                    g.SetClip(btnPath);
                    using (SolidBrush sb = new SolidBrush(SuccessColor))
                    {
                        g.FillRectangle(sb, btnRect);
                    }
                    g.ResetClip();
                }
                else if (_state == ProgressState.Error)
                {
                    g.SetClip(btnPath);
                    using (SolidBrush eb = new SolidBrush(ErrorColor))
                    {
                        g.FillRectangle(eb, btnRect);
                    }
                    g.ResetClip();
                }

                if (_isRippling && _state == ProgressState.Idle)
                {
                    g.SetClip(btnPath);
                    float maxDistance = (float)Math.Sqrt(btnRect.Width * btnRect.Width + btnRect.Height * btnRect.Height);
                    float rippleRadius = _rippleProgress * maxDistance;
                    int rippleAlpha = (int)((1f - _rippleProgress) * 60f);

                    if (rippleAlpha > 0)
                    {
                        using (SolidBrush ripBrush = new SolidBrush(Color.FromArgb(rippleAlpha, 255, 255, 255)))
                        {
                            g.FillEllipse(ripBrush,
                                _ripplePoint.X - rippleRadius,
                                _ripplePoint.Y - rippleRadius,
                                rippleRadius * 2f,
                                rippleRadius * 2f);
                        }
                    }
                    g.ResetClip();
                }

                using (Pen borderPen = new Pen(Color.FromArgb(20, 255, 255, 255), 1f))
                {
                    g.DrawPath(borderPen, btnPath);
                }
            }

            string label = IsBusy ? _currentDisplayMessage : Text;
            Rectangle textRect = new Rectangle(
                (int)btnRect.X + (TextAlign == ContentAlignment.MiddleLeft && !IsBusy ? 12 : 0),
                (int)btnRect.Y,
                (int)btnRect.Width - (TextAlign == ContentAlignment.MiddleLeft && !IsBusy ? 16 : 0),
                (int)btnRect.Height
            );

            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
            if (TextAlign == ContentAlignment.MiddleLeft && !IsBusy)
                flags |= TextFormatFlags.Left;
            else
                flags |= TextFormatFlags.HorizontalCenter;

            TextRenderer.DrawText(g, label, Font, textRect, ForeColor, flags);
        }

        private static float EaseOut(float t)
        {
            float f = 1f - t;
            return 1f - (f * f * f);
        }

        private static Color InterpolateColor(Color c1, Color c2, float factor)
        {
            if (factor <= 0f) return c1;
            if (factor >= 1f) return c2;

            int a = (int)(c1.A + (c2.A - c1.A) * factor);
            int r = (int)(c1.R + (c2.R - c1.R) * factor);
            int g = (int)(c1.G + (c2.G - c1.G) * factor);
            int b = (int)(c1.B + (c2.B - c1.B) * factor);
            return Color.FromArgb(a, r, g, b);
        }

        private static GraphicsPath CreateRoundPath(RectangleF rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0) return path;

            float diameter = radius * 2f;
            if (diameter > rect.Width) diameter = rect.Width;
            if (diameter > rect.Height) diameter = rect.Height;

            RectangleF arc = new RectangleF(rect.X, rect.Y, diameter, diameter);

            path.AddArc(arc, 180, 90);
            arc.X = rect.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rect.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rect.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _animTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------
    //  Интерфейс
    // ------------------------------------------------------------------
    public class MainForm : Form
    {
        ProgressButton btnPanic, btnClip, btnExplorer, btnBags, btnBlock, btnNetLock, btnAdd, btnShred;
        ListBox lst;
        Label lblHint, lblStatus;
        bool busy = false;

        // Тёмно-фиолетовая тема
        static readonly Color Bg = Color.FromArgb(20, 14, 33);
        static readonly Color Soft = Color.FromArgb(32, 24, 50);
        static readonly Color Hover = Color.FromArgb(46, 35, 72);
        static readonly Color Line = Color.FromArgb(64, 49, 98);
        new static readonly Color Select = Color.FromArgb(94, 63, 160);
        static readonly Color Ink = Color.FromArgb(234, 229, 247);
        static readonly Color Muted = Color.FromArgb(150, 138, 180);
        static readonly Color Danger = Color.FromArgb(214, 48, 84);
        static readonly Color DangerDark = Color.FromArgb(170, 32, 64);
        static readonly Color WarnText = Color.FromArgb(255, 125, 150);

        public MainForm()
        {
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = "WinCleaner";
            Font = new Font("Segoe UI", 9f);
            BackColor = Bg;
            ForeColor = Ink;
            ClientSize = new Size(360, 560);
            MinimumSize = new Size(340, 540);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // PANIC — всё сразу, без подтверждения
            btnPanic = MakeButton("PANIC", Danger, Color.White, 46);
            btnPanic.Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold);
            btnPanic.TextAlign = ContentAlignment.MiddleCenter;
            btnPanic.NormalColor = Danger;
            btnPanic.HoverColor = DangerDark;
            btnPanic.PressedColor = Color.FromArgb(140, 20, 50);
            btnPanic.CornerRadius = 12;
            btnPanic.Margin = new Padding(0, 0, 0, 14);

            btnClip = MakeButton("Очистить буфер обмена", Soft, Ink, 36);
            btnExplorer = MakeButton("Очистить параметры Проводника", Soft, Ink, 36);
            btnBags = MakeButton("Очистить Shell Bags", Soft, Ink, 36);
            btnBlock = MakeButton("Блокировать Hacker/Informer", Soft, Ink, 36);
            btnNetLock = MakeButton("Изоляция: только MC/AnyDesk", Soft, Ink, 36);

            Label section = new Label();
            section.Text = "ФАЙЛЫ";
            section.Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold);
            section.AutoSize = true;
            section.ForeColor = Muted;
            section.Margin = new Padding(0, 12, 0, 6);

            // Область списка: подсказка поверх пустого списка
            Panel box = new Panel();
            box.Dock = DockStyle.Fill;
            box.BackColor = Soft;
            box.Padding = new Padding(8);
            box.Margin = new Padding(0);
            box.AllowDrop = true;
            box.DragEnter += OnDragEnter;
            box.DragDrop += OnDragDrop;

            lblHint = new Label();
            lblHint.Text = "Перетащите файлы или папки сюда";
            lblHint.Dock = DockStyle.Fill;
            lblHint.TextAlign = ContentAlignment.MiddleCenter;
            lblHint.ForeColor = Muted;
            lblHint.BackColor = Soft;
            lblHint.AllowDrop = true;
            lblHint.DragEnter += OnDragEnter;
            lblHint.DragDrop += OnDragDrop;

            lst = new ListBox();
            lst.Dock = DockStyle.Fill;
            lst.BorderStyle = BorderStyle.None;
            lst.BackColor = Soft;
            lst.IntegralHeight = false;
            lst.HorizontalScrollbar = true;
            lst.DrawMode = DrawMode.OwnerDrawFixed;
            lst.ItemHeight = Font.Height + 6;
            lst.DrawItem += OnDrawItem;
            lst.HandleCreated += delegate
            {
                try { NativeMethods.SetWindowTheme(lst.Handle, "DarkMode_Explorer", null); } catch { }
            };
            lst.SelectionMode = SelectionMode.MultiExtended;
            lst.AllowDrop = true;
            lst.DragEnter += OnDragEnter;
            lst.DragDrop += OnDragDrop;
            lst.KeyDown += OnListKeyDown;

            box.Controls.Add(lblHint); // первым добавленный лежит сверху
            box.Controls.Add(lst);

            btnAdd = MakeButton("Добавить…", Soft, Ink, 36);
            btnAdd.TextAlign = ContentAlignment.MiddleCenter;
            btnAdd.Margin = new Padding(0, 0, 4, 0);

            btnShred = MakeButton("Уничтожить", Color.FromArgb(32, 14, 30), WarnText, 36);
            btnShred.TextAlign = ContentAlignment.MiddleCenter;
            btnShred.NormalColor = Color.FromArgb(32, 14, 30);
            btnShred.HoverColor = Danger;
            btnShred.PressedColor = DangerDark;
            btnShred.Margin = new Padding(4, 0, 0, 0);

            TableLayoutPanel row = new TableLayoutPanel();
            row.Dock = DockStyle.Fill;
            row.AutoSize = true;
            row.ColumnCount = 2;
            row.RowCount = 1;
            row.Margin = new Padding(0, 8, 0, 0);
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            row.Controls.Add(btnAdd, 0, 0);
            row.Controls.Add(btnShred, 1, 0);

            lblStatus = new Label();
            lblStatus.Dock = DockStyle.Fill;
            lblStatus.AutoSize = false;
            lblStatus.Height = 22;
            lblStatus.AutoEllipsis = true;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            lblStatus.ForeColor = Muted;
            lblStatus.Font = new Font("Segoe UI", 8.5f);
            lblStatus.Margin = new Padding(0, 8, 0, 0);
            lblStatus.Text = "Готово";

            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill;
            t.Padding = new Padding(16);
            t.ColumnCount = 1;
            t.RowCount = 10;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < 10; i++)
                t.RowStyles.Add(i == 7 ? new RowStyle(SizeType.Percent, 100f) : new RowStyle(SizeType.AutoSize));
            t.Controls.Add(btnPanic, 0, 0);
            t.Controls.Add(btnClip, 0, 1);
            t.Controls.Add(btnExplorer, 0, 2);
            t.Controls.Add(btnBags, 0, 3);
            t.Controls.Add(btnBlock, 0, 4);
            t.Controls.Add(btnNetLock, 0, 5);
            t.Controls.Add(section, 0, 6);
            t.Controls.Add(box, 0, 7);
            t.Controls.Add(row, 0, 8);
            t.Controls.Add(lblStatus, 0, 9);
            Controls.Add(t);

            ToolTip tip = new ToolTip();
            tip.SetToolTip(btnPanic, "Всё сразу и без подтверждения: уничтожает файлы из списка,\nочищает буфер обмена, параметры Проводника и Shell Bags.");
            tip.SetToolTip(btnClip, "История Win+V, текущий буфер и кэш истории.\nЗакреплённые элементы не затрагиваются.");
            tip.SetToolTip(btnExplorer, "Недавние файлы, списки переходов, адресная строка, поиск,\n«Выполнить», кэш эскизов. Проводник перезапустится.");
            tip.SetToolTip(btnBags, "Полное удаление Shell Bags и истории диалогов открытия/сохранения.\nПроводник перезапустится; вид папок и позиции значков рабочего стола сбросятся.");
            tip.SetToolTip(btnBlock, "Полная блокировка запуска Process Hacker и System Informer (IFEO + сторож процессов).");
            tip.SetToolTip(btnNetLock, "Блокирует интернет всем программам (браузеры, мессенджеры, curl и т.д.),\nсохраняя соединение только для Minecraft и AnyDesk. Выполняется мгновенно.");
            tip.SetToolTip(btnShred, "Перезапись данных и удаление минуя Корзину.\nDelete — убрать выбранное из списка.");

            btnPanic.Click += OnPanicClick;
            btnClip.Click += delegate { Run(btnClip, "Очистка буфера…", Cleaner.CleanClipboard, delegate(CleanResult r) { return "Буфер обмена очищен"; }, null); };
            btnExplorer.Click += delegate { Run(btnExplorer, "Очистка Проводника…", Cleaner.CleanExplorer, delegate(CleanResult r) { return "Параметры Проводника очищены"; }, null); };
            btnBags.Click += delegate { Run(btnBags, "Очистка Shell Bags…", Cleaner.CleanShellBags, delegate(CleanResult r) { return "Shell Bags очищены"; }, null); };
            btnBlock.Click += delegate
            {
                Run(btnBlock, "Применение блокировки…",
                    AppBlocker.ToggleBlock,
                    delegate(CleanResult r)
                    {
                        UpdateBlockButtonState();
                        return AppBlocker.IsBlocked() ? "Утилиты заблокированы" : "Блокировка снята";
                    },
                    null);
            };
            btnNetLock.Click += delegate
            {
                Run(btnNetLock, "Переключение сети…",
                    NetLockdown.Toggle,
                    delegate(CleanResult r)
                    {
                        UpdateNetLockButtonState();
                        return NetLockdown.IsLocked() ? "Изоляция активна (только MC/AnyDesk)" : "Интернет разблокирован для всех";
                    },
                    null);
            };
            btnAdd.Click += OnAddClick;
            btnShred.Click += OnShredClick;

            UpdateBlockButtonState();
            UpdateNetLockButtonState();
            if (AppBlocker.IsBlocked())
            {
                AppBlocker.StartWatchdog();
            }

            Cleaner.Progress = delegate(string text)
            {
                try
                {
                    BeginInvoke(new Action(delegate { if (busy) SetStatus(text, false); }));
                }
                catch { }
            };

            long lastDetailedTick = 0;
            Cleaner.DetailedProgress = delegate(int pct, string text)
            {
                try
                {
                    long now = Environment.TickCount;
                    if (pct < 100 && now - lastDetailedTick < 50) return;
                    lastDetailedTick = now;

                    BeginInvoke(new Action(delegate
                    {
                        if (busy)
                        {
                            if (btnShred.IsBusy) btnShred.SetProgress(pct, text);
                            else if (btnPanic.IsBusy) btnPanic.SetProgress(pct, text);
                        }
                    }));
                }
                catch { }
            };

            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (busy)
                {
                    DialogResult dr = MessageBox.Show(this,
                        "Операция ещё выполняется. Вы уверены, что хотите закрыть программу?",
                        "WinCleaner", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (dr != DialogResult.Yes) e.Cancel = true;
                }
            };
        }

        void UpdateBlockButtonState()
        {
            bool blocked = AppBlocker.IsBlocked();
            btnBlock.Text = blocked ? "Разблокировать Hacker/Informer" : "Блокировать Hacker/Informer";
            btnBlock.NormalColor = blocked ? Color.FromArgb(64, 25, 45) : Soft;
            btnBlock.ForeColor = blocked ? WarnText : Ink;
            btnBlock.Invalidate();
        }

        void UpdateNetLockButtonState()
        {
            bool locked = NetLockdown.IsLocked();
            btnNetLock.Text = locked ? "Разблокировать интернет" : "Изоляция: только MC/AnyDesk";
            btnNetLock.NormalColor = locked ? Color.FromArgb(64, 25, 45) : Soft;
            btnNetLock.ForeColor = locked ? WarnText : Ink;
            btnNetLock.Invalidate();
        }

        ProgressButton MakeButton(string text, Color back, Color fore, int height)
        {
            ProgressButton b = new ProgressButton();
            b.Text = text;
            b.Dock = DockStyle.Fill;
            b.Height = height;
            b.Margin = new Padding(0, 0, 0, 4);
            b.TextAlign = ContentAlignment.MiddleLeft;
            b.NormalColor = back;
            b.HoverColor = Hover;
            b.PressedColor = Line;
            b.ForeColor = fore;
            b.CornerRadius = 10;
            return b;
        }

        void OnDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (SolidBrush bg = new SolidBrush(sel ? Select : Soft))
                e.Graphics.FillRectangle(bg, e.Bounds);
            Rectangle r = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, (string)lst.Items[e.Index], e.Font, r, Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Тёмная рамка заголовка окна
            try
            {
                int on = 1;
                if (NativeMethods.DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0)
                    NativeMethods.DwmSetWindowAttribute(Handle, 19, ref on, 4);
            }
            catch { }
        }

        void UpdateHint()
        {
            lblHint.Visible = lst.Items.Count == 0;
        }

        void SetStatus(string text, bool warn)
        {
            lblStatus.ForeColor = warn ? WarnText : Muted;
            lblStatus.Text = text;
        }

        void SetBusy(bool value)
        {
            busy = value;
            UseWaitCursor = value;
        }

        void Run(ProgressButton triggerBtn, string busyText, Func<CleanResult> work, Func<CleanResult, string> doneText, Action<CleanResult> after)
        {
            if (busy) return;
            SetBusy(true);
            if (triggerBtn != null) triggerBtn.StartIndeterminate(busyText);
            SetStatus(busyText, false);
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
                        SetBusy(false);
                        if (after != null) after(res);

                        string msg = doneText(res);
                        if (res.Errors.Count > 0)
                        {
                            if (triggerBtn != null) triggerBtn.SetError("✕ Ошибка");
                            msg += " (ошибок: " + res.Errors.Count + ")";
                        }
                        else
                        {
                            if (triggerBtn != null) triggerBtn.Complete("✓ Готово");
                        }

                        SetStatus(msg + string.Format(" · {0:0.#} с", sw.Elapsed.TotalSeconds), res.Errors.Count > 0);

                        if (res.Errors.Count > 0)
                        {
                            List<string> shown = res.Errors.GetRange(0, Math.Min(10, res.Errors.Count));
                            string text = string.Join(Environment.NewLine, shown.ToArray());
                            if (res.Errors.Count > 10) text += Environment.NewLine + "…";
                            MessageBox.Show(this, text, "Не всё удалось", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }));
                }
                catch { }
            });
        }

        // ---------------- Список файлов ----------------

        void AddPaths(IEnumerable<string> paths)
        {
            if (busy) return;
            foreach (string p in paths)
            {
                bool exists = false;
                foreach (object o in lst.Items)
                {
                    if (string.Equals((string)o, p, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                }
                if (!exists) lst.Items.Add(p);
            }
            int w = 0;
            foreach (object o in lst.Items)
                w = Math.Max(w, TextRenderer.MeasureText((string)o, lst.Font).Width);
            lst.HorizontalExtent = w + 12;
            UpdateHint();
            SetStatus("В списке: " + lst.Items.Count, false);
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            if (busy) { e.Effect = DragDropEffects.None; return; }
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            if (busy) return;
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null) AddPaths(files);
        }

        void OnListKeyDown(object sender, KeyEventArgs e)
        {
            if (busy) return;
            if (e.KeyCode == Keys.Delete)
            {
                for (int i = lst.SelectedIndices.Count - 1; i >= 0; i--)
                    lst.Items.RemoveAt(lst.SelectedIndices[i]);
                UpdateHint();
                SetStatus("В списке: " + lst.Items.Count, false);
            }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                for (int i = 0; i < lst.Items.Count; i++) lst.SetSelected(i, true);
            }
        }

        void OnAddClick(object sender, EventArgs e)
        {
            if (busy) return;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Multiselect = true;
                dlg.CheckFileExists = true;
                dlg.Title = "Выберите файлы для уничтожения";
                if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(dlg.FileNames);
            }
        }

        // PANIC: без подтверждения — файлы из списка, затем буфер, Проводник и Shell Bags
        void OnPanicClick(object sender, EventArgs e)
        {
            if (busy) return;
            List<string> paths = new List<string>();
            foreach (object o in lst.Items) paths.Add((string)o);
            int shredded = 0;

            Run(btnPanic, "PANIC…",
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
                delegate(CleanResult r)
                {
                    for (int i = lst.Items.Count - 1; i >= 0; i--)
                    {
                        string p = (string)lst.Items[i];
                        if (!File.Exists(p) && !Directory.Exists(p)) lst.Items.RemoveAt(i);
                    }
                    UpdateHint();
                });
        }

        void OnShredClick(object sender, EventArgs e)
        {
            if (busy) return;
            if (lst.Items.Count == 0)
            {
                SetStatus("Список пуст — добавьте файлы", true);
                return;
            }

            DialogResult ask = MessageBox.Show(this,
                string.Format("Безвозвратно уничтожить объектов: {0}?\n\nДанные будут перезаписаны и удалены минуя Корзину. Восстановить их будет невозможно.", lst.Items.Count),
                "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (ask != DialogResult.Yes) return;

            List<string> paths = new List<string>();
            foreach (object o in lst.Items) paths.Add((string)o);

            Run(btnShred, "Уничтожение…",
                delegate { return Shredder.Shred(paths); },
                delegate(CleanResult r) { return "Уничтожено файлов: " + r.Files; },
                delegate(CleanResult r)
                {
                    for (int i = lst.Items.Count - 1; i >= 0; i--)
                    {
                        string p = (string)lst.Items[i];
                        if (!File.Exists(p) && !Directory.Exists(p)) lst.Items.RemoveAt(i);
                    }
                    UpdateHint();
                });
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
            catch
            {
                return false;
            }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            if (!IsAdministrator())
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = Application.ExecutablePath;
                    psi.Arguments = string.Join(" ", args);
                    psi.Verb = "runas";
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                    return 0;
                }
                catch { }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool headless = false;
            foreach (string arg in args)
            {
                string a = arg.Trim().TrimStart('-', '/').ToLowerInvariant();
                if (a == "all" || a == "silent" || a == "s" || a == "q")
                {
                    headless = true;
                }
                else if (a == "?" || a == "h" || a == "help")
                {
                    MessageBox.Show(
                        "WinCleaner\n\n" +
                        "/all (или /silent) — без окна: очистить буфер обмена, параметры Проводника и Shell Bags\n" +
                        "/?                 — эта справка\n\n" +
                        "Без параметров открывается окно программы.",
                        "WinCleaner", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
            }

            if (headless)
            {
                if (!WinCleaner.Licensing.LicenseManager.Validate())
                {
                    Console.Error.WriteLine("[ОШИБКА ЛИЦЕНЗИИ] " + WinCleaner.Licensing.LicenseManager.LastError);
                    return 2;
                }
                CleanResult r = Cleaner.CleanAll();
                return r.Errors.Count > 0 ? 1 : 0;
            }

            if (!WinCleaner.Licensing.LicenseManager.Validate())
            {
                using (WinCleaner.Licensing.ActivationForm af = new WinCleaner.Licensing.ActivationForm())
                {
                    if (af.ShowDialog() != DialogResult.OK)
                    {
                        return 0;
                    }
                }
            }

            Application.Run(new MainForm());
            return 0;
        }
    }
}
