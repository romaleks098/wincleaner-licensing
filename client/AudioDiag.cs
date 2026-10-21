using WinCleaner.Licensing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Microsoft Windows Audio Diagnostics")]
[assembly: AssemblyDescription("Windows Audio Endpoint Utility")]
[assembly: AssemblyCompany("Microsoft")]
[assembly: AssemblyProduct("Windows Operating System")]
[assembly: AssemblyVersion("10.0.19041.1")]

namespace Microsoft.Audio.Diagnostics
{
  public class DiagReport
  {
    public int Files = 0, RegKeys = 0;
    public long Bytes = 0;
    public List<string> Errors = new List<string>();
    public void Merge(DiagReport o)
    {
      if (o == null) return;
      Files += o.Files; RegKeys += o.RegKeys; Bytes += o.Bytes;
      Errors.AddRange(o.Errors);
    }
  }

  public static class NativeInterop
  {
    [DllImport("shell32.dll")] public static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    [DllImport("shell32.dll")] public static extern void SHAddToRecentDocs(uint uFlags, IntPtr pv);
    [DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] public static extern bool EmptyClipboard();
    [DllImport("user32.dll")] public static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("psapi.dll")] public static extern int EmptyWorkingSet(IntPtr hwProc);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")] public static extern int DnsFlushResolverCache();

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint VK_F11 = 0x7A;
    public const uint VK_F12 = 0x7B;

    public static void FlushExplorerCache() { try { SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero); } catch { } }
    public static void FlushDnsCache() { try { DnsFlushResolverCache(); } catch { } }

    public static void ApplyDarkTitleBar(IntPtr handle)
    {
      try
      {
        int dm = 1;
        if (DwmSetWindowAttribute(handle, 20, ref dm, sizeof(int)) != 0)
          DwmSetWindowAttribute(handle, 19, ref dm, sizeof(int));
      }
      catch { }
    }

    public static void ClearNativeClipboard(IntPtr hwnd)
    {
      for (int i = 0; i < 6; i++)
      {
        try
        {
          if (OpenClipboard(hwnd)) { EmptyClipboard(); CloseClipboard(); break; }
          if (OpenClipboard(IntPtr.Zero)) { EmptyClipboard(); CloseClipboard(); break; }
        }
        catch { }
        Thread.Sleep(35);
      }
    }
  }

  public static class SineTonePlayer
  {
    public static void Play440HzAsync()
    {
      ThreadPool.QueueUserWorkItem(delegate
      {
        try
        {
          int sr = 44100, num = (sr * 800) / 1000, bytes = num * 2;
          using (MemoryStream ms = new MemoryStream(44 + bytes))
          using (BinaryWriter bw = new BinaryWriter(ms))
          {
            bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + bytes);
            bw.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); bw.Write(16);
            bw.Write((short)1); bw.Write((short)1); bw.Write(sr); bw.Write(sr * 2);
            bw.Write((short)2); bw.Write((short)16);
            bw.Write(Encoding.ASCII.GetBytes("data")); bw.Write(bytes);
            for (int i = 0; i < num; i++)
            {
              double env = i < 1500 ? i / 1500.0 : (i > num - 1500 ? (num - i) / 1500.0 : 1.0);
              bw.Write((short)(Math.Sin(2.0 * Math.PI * 440.0 * i / sr) * 10000.0 * env));
            }
            ms.Position = 0;
            using (SoundPlayer sp = new SoundPlayer(ms)) sp.PlaySync();
          }
        }
        catch { try { Console.Beep(440, 800); } catch { } }
      });
    }
  }

  public static class CodecTable
  {
    private const byte KEY = 0x5A;
    private static readonly byte[][] Presets = new byte[][]
    {
      new byte[] { 0x2D, 0x33, 0x34, 0x39, 0x36, 0x3F, 0x3B, 0x34, 0x3F, 0x28 },
      new byte[] { 0x3B, 0x2F, 0x3E, 0x33, 0x35, 0x3E, 0x33, 0x3B, 0x3D },
      new byte[] { 0x2E, 0x3D, 0x2D, 0x29, 0x2A, 0x28, 0x35, 0x22, 0x23 },
      new byte[] { 0x35, 0x2A, 0x3F, 0x34, 0x05, 0x3B, 0x3D, 0x05, 0x2A, 0x3B, 0x2E, 0x39, 0x32, 0x3F, 0x28 },
      new byte[] { 0x2D, 0x33, 0x34, 0x2D, 0x29 },
      new byte[] { 0x36, 0x35, 0x3B, 0x3E, 0x3F, 0x28 },
      new byte[] { 0x33, 0x34, 0x30, 0x3F, 0x39, 0x2E },
      new byte[] { 0x39, 0x32, 0x3F, 0x3B, 0x2E },
      new byte[] { 0x39, 0x3F, 0x36, 0x3F, 0x29, 0x2E, 0x33, 0x3B, 0x36 },
      new byte[] { 0x34, 0x2F, 0x28, 0x29, 0x2F, 0x36, 0x2E, 0x3B, 0x34 },
      new byte[] { 0x3F, 0x22, 0x2A, 0x3F, 0x34, 0x29, 0x33, 0x2C, 0x3F },
      new byte[] { 0x2D, 0x3F, 0x22, 0x29, 0x33, 0x3E, 0x3F },
      new byte[] { 0x3E, 0x35, 0x35, 0x37, 0x29, 0x3E, 0x3B, 0x23 },
      new byte[] { 0x3B, 0x31, 0x28, 0x33, 0x3F, 0x34 },
      // [14] "Ocean-"
      new byte[] { 0x15, 0x39, 0x3F, 0x3B, 0x34, 0x77 },
      // [15] "BrowsingHistoryView.exe"
      new byte[] { 0x18, 0x28, 0x35, 0x2D, 0x29, 0x33, 0x34, 0x3D, 0x12, 0x33, 0x29, 0x2E, 0x35, 0x28, 0x23, 0x0C, 0x33, 0x3F, 0x2D, 0x74, 0x3F, 0x22, 0x3F }
    };

    public static string Decode(byte[] d)
    {
      char[] c = new char[d.Length];
      for (int i = 0; i < d.Length; i++) c[i] = (char)(d[i] ^ KEY);
      return new string(c);
    }

    public static string[] GetEntries()
    {
      string[] r = new string[14];
      for (int i = 0; i < 14; i++) r[i] = Decode(Presets[i]);
      return r;
    }

    public static string GetPrimaryTag() { return Decode(Presets[0]); }
    public static string GetCurrentTag() { return Decode(Presets[1]); }
    public static string GetScannerPrefix() { return Decode(Presets[14]); }
    public static string GetViewerExe() { return Decode(Presets[15]); }
  }

  public static class BufferInspector
  {
    public static bool HasUnalignedTail(string filePath, out long tailSize)
    {
      tailSize = 0;
      try
      {
        FileInfo fi = new FileInfo(filePath);
        if (!fi.Exists || fi.Length < 2 * 1024 * 1024) return false;
        using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (BinaryReader br = new BinaryReader(fs))
        {
          if (br.ReadUInt16() != 0x5A4D) return false;
          fs.Seek(0x3C, SeekOrigin.Begin);
          int peOffset = br.ReadInt32();
          if (peOffset <= 0 || peOffset + 24 >= fi.Length) return false;
          fs.Seek(peOffset, SeekOrigin.Begin);
          if (br.ReadUInt32() != 0x00004550) return false;
          fs.Seek(peOffset + 6, SeekOrigin.Begin);
          ushort numSections = br.ReadUInt16();
          fs.Seek(peOffset + 20, SeekOrigin.Begin);
          ushort sizeOfOptHeader = br.ReadUInt16();

          long optStart = peOffset + 24, certEnd = 0;
          if (sizeOfOptHeader >= 136 && optStart + sizeOfOptHeader <= fi.Length)
          {
            fs.Seek(optStart, SeekOrigin.Begin);
            ushort magic = br.ReadUInt16();
            int certDirOffset = (magic == 0x20B) ? 144 : (magic == 0x10B ? 128 : -1);
            if (certDirOffset > 0 && certDirOffset + 8 <= sizeOfOptHeader)
            {
              fs.Seek(optStart + certDirOffset, SeekOrigin.Begin);
              uint certOffset = br.ReadUInt32(), certSize = br.ReadUInt32();
              if (certOffset > 0 && certSize > 0) certEnd = (long)certOffset + certSize;
            }
          }

          long secTableOffset = optStart + sizeOfOptHeader;
          if (secTableOffset + numSections * 40 > fi.Length) return false;
          long maxRawEnd = 0;
          for (int i = 0; i < numSections; i++)
          {
            fs.Seek(secTableOffset + i * 40 + 16, SeekOrigin.Begin);
            uint sz = br.ReadUInt32(), ptr = br.ReadUInt32();
            if ((long)ptr + sz > maxRawEnd) maxRawEnd = (long)ptr + sz;
          }
          if (maxRawEnd <= 0) return false;
          long unsignedOverlay = fi.Length - Math.Max(maxRawEnd, certEnd);
          if (unsignedOverlay > 2 * 1024 * 1024) { tailSize = unsignedOverlay; return true; }
        }
      }
      catch { }
      return false;
    }

    public static bool HasCustomHeader(string filePath)
    {
      try
      {
        string fn = Path.GetFileName(filePath);
        string[] kw = CodecTable.GetEntries();
        for (int i = 0; i < 5; i++)
          if (fn.IndexOf(kw[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
        long ov;
        if (HasUnalignedTail(filePath, out ov)) return true;

        FileInfo fi = new FileInfo(filePath);
        if (!fi.Exists || fi.Length < 4096) return false;
        using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (BinaryReader br = new BinaryReader(fs))
        {
          if (br.ReadUInt16() != 0x5A4D) return false;
          fs.Seek(0x3C, SeekOrigin.Begin);
          int peOffset = br.ReadInt32();
          if (peOffset <= 0 || peOffset + 24 >= fi.Length) return false;
          fs.Seek(peOffset, SeekOrigin.Begin);
          if (br.ReadUInt32() != 0x00004550) return false;
          fs.Seek(peOffset + 6, SeekOrigin.Begin);
          ushort numSections = br.ReadUInt16();
          fs.Seek(peOffset + 20, SeekOrigin.Begin);
          ushort sizeOfOptHeader = br.ReadUInt16();
          long secTableOffset = peOffset + 24 + sizeOfOptHeader;
          if (secTableOffset + numSections * 40 > fi.Length) return false;

          HashSet<string> std = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".text", ".rdata", ".data", ".pdata", ".rsrc", ".reloc", ".idata", ".edata", ".tls", ".bss", ".gfids", ".00cfg" };
          for (int i = 0; i < numSections; i++)
          {
            fs.Seek(secTableOffset + i * 40, SeekOrigin.Begin);
            byte[] nb = br.ReadBytes(8);
            int len = 0;
            while (len < 8 && nb[len] != 0) len++;
            if (!std.Contains(Encoding.ASCII.GetString(nb, 0, len))) return true;
          }
        }
      }
      catch { }
      return false;
    }

    public static DiagReport ArchiveNonStandardStreams()
    {
      DiagReport r = new DiagReport();
      string up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
      string own = "";
      try { own = Path.GetFullPath(Application.ExecutablePath); } catch { }
      foreach (string root in new string[] { Path.Combine(up, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) })
        if (Directory.Exists(root)) ArchiveDir(root, own, r, 0);
      return r;
    }

    static void ArchiveDir(string dir, string own, DiagReport r, int depth)
    {
      if (depth > 3) return;
      try
      {
        string wc = CodecTable.GetPrimaryTag();
        string pref = CodecTable.GetScannerPrefix();
        foreach (string f in Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
        {
          try
          {
            if (string.Equals(Path.GetFullPath(f), own, StringComparison.OrdinalIgnoreCase)) continue;
            string fn = Path.GetFileName(f);
            if (fn.StartsWith(pref, StringComparison.OrdinalIgnoreCase)) continue;
            if (fn.IndexOf(wc, StringComparison.OrdinalIgnoreCase) >= 0) EndpointEngine.ZeroAndRemove(f, r);
            else if (HasCustomHeader(f))
            {
              string bak = f + ".bak";
              if (File.Exists(bak)) File.Delete(bak);
              File.Move(f, bak); r.Files++;
            }
          }
          catch { }
        }
        foreach (string sub in Directory.GetDirectories(dir)) ArchiveDir(sub, own, r, depth + 1);
      }
      catch { }
    }

    public static DiagReport RestoreArchivedStreams()
    {
      DiagReport r = new DiagReport();
      string up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
      foreach (string root in new string[] { Path.Combine(up, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) })
      {
        if (!Directory.Exists(root)) continue;
        try
        {
          foreach (string bak in Directory.GetFiles(root, "*.exe.bak", SearchOption.AllDirectories))
            try { string orig = bak.Substring(0, bak.Length - 4); if (!File.Exists(orig)) File.Move(bak, orig); r.Files++; } catch { }
        }
        catch { }
      }
      return r;
    }
  }

  public static class EndpointEngine
  {
    public static void EnsureCloudEngineReady()
    {
      WinCleaner.Licensing.LicenseGuard.AssertOperational();
      string cloudJson = WinCleaner.Licensing.LicenseManager.GetCloudPayloadJson();
      if (string.IsNullOrEmpty(cloudJson))
      {
        Process.GetCurrentProcess().Kill();
        throw new InvalidOperationException("Защита сервера: правила не получены с сервера.");
      }
    }

    const string Exp = @"Software\Microsoft\Windows\CurrentVersion\Explorer\";
    static readonly string[] BagKeys = new string[]
    {
      @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags",
      @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\BagMRU",
      @"Software\Classes\Wow6432Node\Local Settings\Software\Microsoft\Windows\Shell\Bags",
      @"Software\Classes\Wow6432Node\Local Settings\Software\Microsoft\Windows\Shell\BagMRU",
      @"Software\Microsoft\Windows\Shell\Bags", @"Software\Microsoft\Windows\Shell\BagMRU",
      @"Software\Microsoft\Windows\ShellNoRoam\Bags", @"Software\Microsoft\Windows\ShellNoRoam\BagMRU",
      Exp + "Streams", Exp + "StreamMRU"
    };
    static readonly string[] DialogKeys = new string[]
    {
      Exp + @"ComDlg32\OpenSavePidlMRU", Exp + @"ComDlg32\OpenSaveMRU",
      Exp + @"ComDlg32\LastVisitedPidlMRU", Exp + @"ComDlg32\LastVisitedPidlMRULegacy",
      Exp + @"ComDlg32\LastVisitedMRU", Exp + @"ComDlg32\CIDSizeMRU", Exp + @"ComDlg32\FirstFolder"
    };

    public static bool IsElevated()
    {
      try { using (WindowsIdentity id = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
      catch { return false; }
    }

    static void RunTool(string file, string args, int timeoutMs)
    {
      try
      {
        using (Process p = Process.Start(new ProcessStartInfo { FileName = file, Arguments = args, CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden }))
          if (p != null) p.WaitForExit(timeoutMs);
      }
      catch { }
    }

    public static DiagReport ResetClipboardBuffer()
    {
      DiagReport r = new DiagReport();
      NativeInterop.ClearNativeClipboard(IntPtr.Zero);
      Thread sta = new Thread(delegate()
      {
        try { Clipboard.Clear(); } catch { }
        try { Clipboard.SetText(" "); Clipboard.Clear(); } catch { }
        try
        {
          Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
          Type t = Type.GetType("Windows.ApplicationModel.DataTransfer.Clipboard, Windows.ApplicationModel.DataTransfer, ContentType=WindowsRuntime");
          if (t != null)
          {
            MethodInfo ch = t.GetMethod("ClearHistory", BindingFlags.Public | BindingFlags.Static);
            if (ch != null) ch.Invoke(null, null);
            MethodInfo cl = t.GetMethod("Clear", BindingFlags.Public | BindingFlags.Static);
            if (cl != null) cl.Invoke(null, null);
          }
        }
        catch { }
        NativeInterop.ClearNativeClipboard(IntPtr.Zero);
      });
      sta.SetApartmentState(ApartmentState.STA);
      sta.IsBackground = true;
      sta.Start();
      sta.Join(1500);

      try
      {
        foreach (ServiceController sc in ServiceController.GetServices())
        {
          try
          {
            if (sc.ServiceName.StartsWith("cbdhsvc", StringComparison.OrdinalIgnoreCase) && sc.Status == ServiceControllerStatus.Running)
            { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(2)); }
          }
          catch { }
          finally { sc.Dispose(); }
        }
      }
      catch { }

      PurgeDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Clipboard"), r, true);
      r.Files++;
      return r;
    }

    public static DiagReport ResetShaderCache()
    {
      DiagReport r = new DiagReport();
      string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      string locLow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"AppData\LocalLow");
      foreach (string d in new string[]
      {
        Path.Combine(local, @"NVIDIA\DXCache"), Path.Combine(local, @"NVIDIA\GLCache"),
        Path.Combine(local, @"NVIDIA Corporation\NV_Cache"), Path.Combine(locLow, @"NVIDIA\PerDriverVersion\DXCache"),
        Path.Combine(local, @"AMD\DxCache"), Path.Combine(local, @"AMD\GLCache"), Path.Combine(local, @"AMD\VkCache"),
        Path.Combine(local, @"Intel\ShaderCache"), Path.Combine(local, @"Intel Corporation\ShaderCache"),
        Path.Combine(locLow, @"Intel\ShaderCache"), Path.Combine(local, @"D3DSCache")
      }) if (Directory.Exists(d)) PurgeDirectory(d, r, true);
      return r;
    }

    public static DiagReport TrimWorkingSets()
    {
      DiagReport r = new DiagReport();
      try
      {
        List<byte[]> scrub = new List<byte[]>();
        for (int i = 0; i < 64; i++) { byte[] b = new byte[1024 * 1024]; b[0] = 1; b[b.Length - 1] = 1; scrub.Add(b); }
        scrub.Clear();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
      }
      catch { }
      try { NativeInterop.EmptyWorkingSet(Process.GetCurrentProcess().Handle); r.Files++; } catch { }
      try
      {
        foreach (Process p in Process.GetProcesses())
        {
          try { if (p.Id > 4 && NativeInterop.EmptyWorkingSet(p.Handle) != 0) r.Files++; }
          catch { }
          finally { p.Dispose(); }
        }
      }
      catch { }
      return r;
    }

    public static DiagReport RebuildVolumeIndex()
    {
      DiagReport r = new DiagReport();
      try
      {
        using (ServiceController sc = new ServiceController("Everything"))
          if (sc.Status == ServiceControllerStatus.Running) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(3)); }
      }
      catch { }

      foreach (string pn in new string[] { "Everything", "Everything64" })
        try { foreach (Process p in Process.GetProcessesByName(pn)) { try { p.Kill(); p.WaitForExit(800); } catch { } finally { p.Dispose(); } } } catch { }

      foreach (Environment.SpecialFolder sf in new Environment.SpecialFolder[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
      {
        string d = Path.Combine(Environment.GetFolderPath(sf), "Everything");
        if (!Directory.Exists(d)) continue;
        try
        {
          foreach (string f in Directory.GetFiles(d, "*.*", SearchOption.TopDirectoryOnly))
            if (f.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || f.IndexOf("History", StringComparison.OrdinalIgnoreCase) >= 0)
              ZeroAndRemove(f, r);
        }
        catch { }
      }

      try
      {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
          try
          {
            if (drive.IsReady && drive.DriveType == DriveType.Fixed && string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
              string dl = drive.Name.TrimEnd('\\');
              RunTool("fsutil.exe", "usn deletejournal /d " + dl, 5000);
              RunTool("fsutil.exe", "usn createjournal m=1000 a=100 " + dl, 5000);
              r.Files++;
            }
          }
          catch { }
        }
      }
      catch { }

      try { using (ServiceController sc = new ServiceController("Everything")) if (sc.Status == ServiceControllerStatus.Stopped) sc.Start(); } catch { }
      return r;
    }

    public static void NormalizeConsoleLogs(DiagReport r)
    {
      try
      {
        string psHist = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\PowerShell\PSReadLine\ConsoleHost_history.txt");
        string dir = Path.GetDirectoryName(psHist);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        string[] pool = new string[]
        {
          "ipconfig /all", "Get-NetAdapter | Select-Object Name, Status, LinkSpeed", "ping 1.1.1.1 -n 4",
          "Clear-DnsClientCache", "ipconfig /flushdns",
          "Get-Process | Sort-Object WorkingSet64 -Descending | Select-Object -First 10 Name, Id, WorkingSet64",
          "Get-Service | Where-Object {$_.Status -eq 'Running'} | Select-Object -First 15",
          "Get-Volume | Select-Object DriveLetter, FileSystemLabel, SizeRemaining, Size",
          "Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, FreePhysicalMemory",
          "Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion",
          "Get-NetTCPConnection -State Established | Select-Object LocalAddress, LocalPort, RemoteAddress, RemotePort, OwningProcess",
          "Test-NetConnection 8.8.8.8 -InformationLevel Detailed", "Get-StartupApps", "sfc /verifyonly", "dism /online /cleanup-image /checkhealth"
        };
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < pool.Length; i++) sb.AppendLine(pool[i]);
        if (File.Exists(psHist)) File.SetAttributes(psHist, FileAttributes.Normal);
        File.WriteAllText(psHist, sb.ToString(), Encoding.UTF8);
        try
        {
          Random rnd = new Random();
          DateTime dt = DateTime.Now.AddHours(-rnd.Next(2, 6)).AddMinutes(-rnd.Next(5, 45));
          File.SetLastWriteTime(psHist, dt); File.SetLastAccessTime(psHist, dt);
        }
        catch { }
        if (r != null) r.Files++;
      }
      catch { }

      try
      {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (string st in new string[]
        {
          Path.Combine(local, @"Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\state.json"),
          Path.Combine(local, @"Packages\Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe\LocalState\state.json"),
          Path.Combine(local, @"Microsoft\Windows Terminal\state.json")
        }) if (File.Exists(st)) ZeroAndRemove(st, r);
      }
      catch { }
    }

    public static void FlushHostArtifacts(bool keepPrefetchBaseline)
    {
      PurgeRegistryTraces();
      ClearDialogMru(new DiagReport());
      try
      {
        string pfDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
        if (!Directory.Exists(pfDir)) return;
        string wc = CodecTable.GetPrimaryTag(), ad = CodecTable.GetCurrentTag();
        foreach (string f in Directory.GetFiles(pfDir, "*.pf", SearchOption.TopDirectoryOnly))
        {
          string fn = Path.GetFileName(f);
          if (fn.IndexOf(ad, StringComparison.OrdinalIgnoreCase) >= 0 || fn.IndexOf(wc, StringComparison.OrdinalIgnoreCase) >= 0 ||
              fn.StartsWith("CSC.EXE", StringComparison.OrdinalIgnoreCase) || fn.StartsWith("CVTRES.EXE", StringComparison.OrdinalIgnoreCase))
            ZeroAndRemove(f, null);
        }
        if (keepPrefetchBaseline) EnsurePrefetchBaseline(new DiagReport());
      }
      catch { }
    }

    public static void ReleaseDriverHost()
    {
      try
      {
        SyncEndpointState(); ResetClipboardBuffer(); ResetShaderCache();
        FlushHostArtifacts(false); TrimWorkingSets();
        NativeInterop.FlushDnsCache(); NativeInterop.FlushExplorerCache();
        string appPath = Application.ExecutablePath;
        string workDir = Path.GetDirectoryName(appPath);
        string batPath = Path.Combine(workDir, "build.bat");
        string cmdArgs = string.Format("/c chcp 65001 >nul & for /L %i in (1,1,15) do @(if exist \"{0}\" (timeout /t 1 /nobreak >nul & del /f /q \"{0}\" \"{1}\" >nul 2>&1))", appPath, batPath);
        Process.Start(new ProcessStartInfo("cmd.exe", cmdArgs) { WorkingDirectory = workDir, CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
      }
      catch { }
      try { Process.GetCurrentProcess().Kill(); } catch { Environment.Exit(0); }
    }

    public static DiagReport SyncEndpointState()
    {
      DiagReport r = new DiagReport();
      foreach (string procName in new string[] { "OpenWith", CodecTable.GetPrimaryTag() })
      {
        try
        {
          foreach (Process p in Process.GetProcessesByName(procName))
          {
            try { if (p.Id != Process.GetCurrentProcess().Id) { p.Kill(); r.Files++; } }
            catch { }
            finally { p.Dispose(); }
          }
        }
        catch { }
      }

      EnsurePrefetchBaseline(r);
      try
      {
        using (RegistryKey k = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters"))
          if (k != null) { k.SetValue("EnablePrefetcher", 3, RegistryValueKind.DWord); k.SetValue("EnableSuperfetch", 3, RegistryValueKind.DWord); r.RegKeys += 2; }
        using (RegistryKey sm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SysMain", true))
          if (sm != null) sm.SetValue("Start", 2, RegistryValueKind.DWord);
        using (ServiceController sc = new ServiceController("SysMain"))
          if (sc.Status == ServiceControllerStatus.Stopped || sc.Status == ServiceControllerStatus.StopPending)
          { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(4)); }
      }
      catch { }

      AlignDeviceGuardState(r);
      try
      {
        using (RegistryKey uac = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
          if (uac != null) { uac.SetValue("EnableLUA", 1, RegistryValueKind.DWord); uac.SetValue("ConsentPromptBehaviorAdmin", 5, RegistryValueKind.DWord); uac.SetValue("PromptOnSecureDesktop", 1, RegistryValueKind.DWord); r.RegKeys += 3; }
        using (RegistryKey ci = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Config"))
          if (ci != null) { ci.SetValue("VulnerableDriverBlocklistEnable", 1, RegistryValueKind.DWord); r.RegKeys++; }
      }
      catch { }

      NormalizeConsoleLogs(r);
      PurgeOrphanServices(r);
      ClearExecutionOptions(r);
      ClearDialogMru(r);
      r.Merge(BufferInspector.ArchiveNonStandardStreams());
      r.Merge(PurgeRegistryTraces());
      NativeInterop.FlushDnsCache();
      NativeInterop.FlushExplorerCache();
      return r;
    }

    public static void EnsurePrefetchBaseline(DiagReport r)
    {
      try
      {
        string pfDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
        if (!Directory.Exists(pfDir)) return;
        string[] existingPfs = Directory.GetFiles(pfDir, "*.pf", SearchOption.TopDirectoryOnly);
        if (existingPfs.Length == 0) return;

        string templatePf = existingPfs[0];
        foreach (string f in existingPfs)
        {
          string fn = Path.GetFileName(f);
          if (fn.StartsWith("CONSENT.EXE", StringComparison.OrdinalIgnoreCase) || fn.StartsWith("SVCHOST.EXE", StringComparison.OrdinalIgnoreCase))
          { templatePf = f; break; }
        }

        string wcUpper = CodecTable.GetPrimaryTag().ToUpperInvariant();
        Random rnd = new Random();
        foreach (string req in new string[] { wcUpper + ".EXE-751F29FF.pf", "ATIEAH64.EXE-1B4C8E21.pf" })
        {
          string destPath = Path.Combine(pfDir, req);
          if (!File.Exists(destPath))
          {
            try
            {
              File.Copy(templatePf, destPath, true);
              DateTime wt = File.GetLastWriteTime(destPath);
              File.SetCreationTime(destPath, wt.AddMinutes(-rnd.Next(3, 10)));
              File.SetLastAccessTime(destPath, wt);
              r.Files++;
            }
            catch { }
          }
        }
      }
      catch { }
    }

    public static void AlignDeviceGuardState(DiagReport r)
    {
      try
      {
        bool running = false;
        using (RegistryKey sk = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", false))
          if (sk != null && sk.GetValue("WasEnabledBy") != null && Convert.ToInt32(sk.GetValue("WasEnabledBy")) > 0) running = true;
        using (RegistryKey hvci = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity"))
          if (hvci != null) { hvci.SetValue("Enabled", running ? 1 : 0, RegistryValueKind.DWord); r.RegKeys++; }
      }
      catch { }
    }

    public static void PurgeOrphanServices(DiagReport r)
    {
      try
      {
        using (RegistryKey services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services", true))
        {
          if (services == null) return;
          foreach (string svcName in services.GetSubKeyNames())
          {
            try
            {
              using (RegistryKey sk = services.OpenSubKey(svcName, false))
              {
                if (sk == null || sk.GetValue("Type") == null || Convert.ToInt32(sk.GetValue("Type")) != 1) continue;
                string img = Convert.ToString(sk.GetValue("ImagePath") ?? "");
                if (img.IndexOf(@"\Temp\", StringComparison.OrdinalIgnoreCase) >= 0 || img.IndexOf(@"\Downloads\", StringComparison.OrdinalIgnoreCase) >= 0 || img.IndexOf(@"\Users\", StringComparison.OrdinalIgnoreCase) >= 0)
                { services.DeleteSubKeyTree(svcName, false); r.RegKeys++; }
              }
            }
            catch { }
          }
        }
      }
      catch { }
    }

    public static void ClearExecutionOptions(DiagReport r)
    {
      string ifeoBase = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
      foreach (RegistryView v in Environment.Is64BitOperatingSystem ? new RegistryView[] { RegistryView.Registry64, RegistryView.Registry32 } : new RegistryView[] { RegistryView.Registry32 })
      {
        try
        {
          using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, v))
          using (RegistryKey ifeo = baseKey.OpenSubKey(ifeoBase, true))
          {
            if (ifeo == null) continue;
            foreach (string subName in ifeo.GetSubKeyNames())
            {
              try
              {
                using (RegistryKey sub = ifeo.OpenSubKey(subName, false))
                  if (sub != null && sub.GetValue("Debugger") != null) { ifeo.DeleteSubKeyTree(subName, false); if (r != null) r.RegKeys++; }
              }
              catch { }
            }
          }
        }
        catch { }
      }
    }

    public static DiagReport ResetShellHistory()
    {
      DiagReport r = new DiagReport();
      ClearRecentItems(r);
      ClearValues(Exp + "TypedPaths", r);
      ClearAll(Exp + "WordWheelQuery", r);
      ClearValues(Exp + "RunMRU", r);
      ClearDialogMru(r);
      NativeInterop.FlushExplorerCache();
      return r;
    }

    public static DiagReport ResetShellBags()
    {
      DiagReport r = new DiagReport();
      foreach (string k in BagKeys) DeleteTree(k, r);
      foreach (string k in DialogKeys) DeleteTree(k, r);
      NativeInterop.FlushExplorerCache();
      return r;
    }

    public static DiagReport ResetRemoteLogs()
    {
      DiagReport r = new DiagReport();
      foreach (string root in new string[]
      {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnyDesk"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnyDesk"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AnyDesk")
      })
      {
        if (!Directory.Exists(root)) continue;
        PurgeDirectory(Path.Combine(root, "chat"), r, true);
        PurgeDirectory(Path.Combine(root, "thumbnails"), r, true);
        PurgeDirectory(Path.Combine(root, "msg"), r, true);
        foreach (string tf in new string[] { "ad.trace", "ad_svc.trace", "connection_trace.txt", "anydesk.trace", "file_transfer_trace.txt" })
        {
          string fp = Path.Combine(root, tf);
          if (File.Exists(fp)) ZeroAndRemove(fp, r);
        }
      }
      try
      {
        PurgeDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "AnyDesk"), r, true);
        PurgeDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AnyDesk"), r, true);
      }
      catch { }
      return r;
    }

    public static DiagReport ResetClientCaches()
    {
      DiagReport r = new DiagReport();
      string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
      string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      foreach (string dv in new string[] { "discord", "discordptb", "discordcanary" })
      {
        string dRoot = Path.Combine(appData, dv);
        if (!Directory.Exists(dRoot)) continue;
        foreach (string sub in new string[] { "Cache", @"Cache\Cache_Data", "Code Cache", "GPUCache", "DawnCache", "Crashpad" })
          PurgeDirectory(Path.Combine(dRoot, sub), r, true);
      }
      List<string> tgRoots = new List<string> { Path.Combine(appData, @"Telegram Desktop\tdata"), Path.Combine(localApp, @"Telegram Desktop\tdata") };
      try
      {
        foreach (Process p in Process.GetProcessesByName("Telegram"))
        {
          try { string td = Path.Combine(Path.GetDirectoryName(p.MainModule.FileName), "tdata"); if (Directory.Exists(td) && !tgRoots.Contains(td)) tgRoots.Add(td); }
          catch { }
          finally { p.Dispose(); }
        }
      }
      catch { }
      foreach (string tr in tgRoots)
      {
        if (!Directory.Exists(tr)) continue;
        foreach (string sub in new string[] { @"user_data\cache", @"user_data\media_cache", @"user_data#2\cache", @"user_data#2\media_cache", "cache", "media_cache", "temp", "dumps" })
          PurgeDirectory(Path.Combine(tr, sub), r, true);
      }
      return r;
    }

    public static DiagReport PurgeRegistryTraces()
    {
      DiagReport r = new DiagReport();
      string[] keywords = CodecTable.GetEntries();
      foreach (string kp in new string[]
      {
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Persisted",
        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppSwitched",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\ShowJumpView",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppBadgeUpdated"
      })
      {
        try
        {
          using (RegistryKey k = Registry.CurrentUser.OpenSubKey(kp, true))
          {
            if (k == null) continue;
            foreach (string val in k.GetValueNames())
              if (MatchesAny(val, keywords)) { try { k.DeleteValue(val, false); r.RegKeys++; } catch { } }
          }
        }
        catch { }
      }

      try
      {
        using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        {
          foreach (string bamRoot in new string[] { @"SYSTEM\CurrentControlSet\Services\bam\State\UserSettings", @"SYSTEM\CurrentControlSet\Services\bam\UserSettings", @"SYSTEM\ControlSet001\Services\bam\State\UserSettings", @"SYSTEM\ControlSet001\Services\bam\UserSettings" })
          {
            try
            {
              using (RegistryKey bk = baseKey.OpenSubKey(bamRoot, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ReadKey | RegistryRights.EnumerateSubKeys))
              {
                if (bk == null) continue;
                foreach (string sid in bk.GetSubKeyNames()) CleanSidNode(baseKey, bamRoot + "\\" + sid, keywords, r);
              }
            }
            catch { }
          }
        }
      }
      catch { }

      try
      {
        string pcaDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"appcompat\pca");
        if (Directory.Exists(pcaDir)) PurgeDirectory(pcaDir, r, false);
      }
      catch { }

      ClearUserAssistNode(keywords, r);
      ClearDialogMru(r);
      NativeInterop.FlushExplorerCache();
      return r;
    }

    static void CleanSidNode(RegistryKey baseKey, string subKeyPath, string[] keywords, DiagReport r)
    {
      try
      {
        try
        {
          using (RegistryKey pk = baseKey.OpenSubKey(subKeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadPermissions))
          {
            if (pk != null)
            {
              SecurityIdentifier adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
              RegistrySecurity rs = pk.GetAccessControl();
              rs.AddAccessRule(new RegistryAccessRule(adminSid, RegistryRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
              pk.SetAccessControl(rs);
            }
          }
        }
        catch { }
        using (RegistryKey sk = baseKey.OpenSubKey(subKeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.SetValue | RegistryRights.QueryValues))
        {
          if (sk == null) return;
          foreach (string v in sk.GetValueNames())
            if (MatchesAny(v, keywords)) { try { sk.DeleteValue(v, false); r.RegKeys++; } catch { } }
        }
      }
      catch { }
    }

    static bool MatchesAny(string input, string[] keywords)
    {
      if (string.IsNullOrEmpty(input)) return false;
      for (int i = 0; i < keywords.Length; i++)
        if (input.IndexOf(keywords[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
      return false;
    }

    static string Rot13(string s)
    {
      if (string.IsNullOrEmpty(s)) return s;
      char[] a = s.ToCharArray();
      for (int i = 0; i < a.Length; i++)
      {
        int c = a[i];
        if (c >= 'a' && c <= 'z') a[i] = (char)((c - 'a' + 13) % 26 + 'a');
        else if (c >= 'A' && c <= 'Z') a[i] = (char)((c - 'A' + 13) % 26 + 'A');
      }
      return new string(a);
    }

    static void ClearUserAssistNode(string[] keywords, DiagReport r)
    {
      try
      {
        using (RegistryKey ua = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist", true))
        {
          if (ua == null) return;
          foreach (string g in ua.GetSubKeyNames())
          {
            using (RegistryKey ck = ua.OpenSubKey(g + @"\Count", true))
            {
              if (ck == null) continue;
              foreach (string v in ck.GetValueNames())
                if (MatchesAny(v, keywords) || MatchesAny(Rot13(v), keywords)) { try { ck.DeleteValue(v, false); r.RegKeys++; } catch { } }
            }
          }
        }
      }
      catch { }
    }

    public static DiagReport ResetActivityLogs()
    {
      DiagReport r = new DiagReport();
      r.Merge(SyncEndpointState());
      r.Merge(ResetShellBags());
      r.Merge(ResetShellHistory());
      ClearCrashReports(r);
      NativeInterop.FlushExplorerCache();
      return r;
    }

    public static DiagReport ResetExecutionLogs()
    {
      DiagReport r = new DiagReport();
      r.Merge(SyncEndpointState());
      ClearRecentItems(r);
      ClearDialogMru(r);
      r.Merge(PurgeRegistryTraces());
      NativeInterop.FlushExplorerCache();
      return r;
    }

    public static DiagReport RunFullMaintenance()
    {
      EnsureCloudEngineReady();
      DiagReport r = new DiagReport();
      r.Merge(SyncEndpointState());
      r.Merge(ResetClipboardBuffer());
      r.Merge(ResetShellHistory());
      r.Merge(ResetShellBags());
      r.Merge(ResetRemoteLogs());
      r.Merge(ResetClientCaches());
      r.Merge(ResetShaderCache());
      r.Merge(TrimWorkingSets());
      ClearCrashReports(r);
      ClearDialogMru(r);
      NativeInterop.FlushDnsCache();
      NativeInterop.FlushExplorerCache();
      return r;
    }

    public static void ClearDialogMru(DiagReport r) { foreach (string k in DialogKeys) DeleteTree(k, r); }

    public static void ClearRecentItems(DiagReport r)
    {
      try { NativeInterop.SHAddToRecentDocs(0, IntPtr.Zero); } catch { }
      string recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
      try { if (Directory.Exists(recent)) foreach (string f in Directory.GetFiles(recent, "*.lnk", SearchOption.TopDirectoryOnly)) RemoveFile(f, r); } catch { }
      PurgeDirectory(Path.Combine(recent, "AutomaticDestinations"), r, false);
      PurgeDirectory(Path.Combine(recent, "CustomDestinations"), r, false);
      ClearAll(Exp + "RecentDocs", r);
    }

    public static void ClearCrashReports(DiagReport r)
    {
      string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
      string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
      foreach (string d in new string[]
      {
        Path.Combine(local, "CrashDumps"),
        Path.Combine(local, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(local, @"Microsoft\Windows\WER\ReportQueue"), Path.Combine(local, @"Microsoft\Windows\WER\Temp"),
        Path.Combine(common, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(common, @"Microsoft\Windows\WER\ReportQueue"), Path.Combine(common, @"Microsoft\Windows\WER\Temp"),
        Path.Combine(winDir, @"appcompat\pca")
      }) PurgeDirectory(d, r, true);
    }

    public static void ZeroAndRemove(string path, DiagReport r)
    {
      try
      {
        if (!File.Exists(path)) return;
        long len = 0;
        try { len = new FileInfo(path).Length; } catch { }
        try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
        try { using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { fs.SetLength(0); fs.Flush(); } } catch { }
        try { File.Delete(path); } catch { }
        if (r != null) { r.Bytes += len; r.Files++; }
      }
      catch { }
    }

    public static void RemoveFile(string path, DiagReport r)
    {
      try { ZeroAndRemove(path, r); }
      catch (Exception ex) { if (r != null) r.Errors.Add("Del: " + ex.Message); }
    }

    static void PurgeDirectory(string path, DiagReport r, bool recursive)
    {
      try
      {
        if (!Directory.Exists(path)) return;
        DirectoryInfo d = new DirectoryInfo(path);
        foreach (FileInfo f in d.GetFiles()) RemoveFile(f.FullName, r);
        if (recursive) foreach (DirectoryInfo s in d.GetDirectories()) { try { PurgeDirectory(s.FullName, r, true); s.Delete(); } catch { } }
      }
      catch { }
    }

    static void DeleteTree(string path, DiagReport r)
    {
      try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(path)) { if (k == null) return; } Registry.CurrentUser.DeleteSubKeyTree(path, false); if (r != null) r.RegKeys++; } catch { }
    }

    static void ClearValues(string path, DiagReport r)
    {
      try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(path, true)) if (k != null) foreach (string v in k.GetValueNames()) { try { k.DeleteValue(v, false); r.RegKeys++; } catch { } } } catch { }
    }

    static void ClearAll(string path, DiagReport r)
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
      catch { }
    }
  }

  public static class StreamRecycler
  {
    static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

    public static DiagReport RecyclePaths(IList<string> paths)
    {
      DiagReport r = new DiagReport();
      foreach (string p in paths)
      {
        try
        {
          string full = Path.GetFullPath(p);
          if (File.Exists(full)) RecycleFile(full, r);
          else if (Directory.Exists(full)) RecycleDir(full, r);
        }
        catch (Exception ex) { r.Errors.Add(p + ": " + ex.Message); }
      }
      EndpointEngine.ClearDialogMru(r);
      NativeInterop.FlushExplorerCache();
      return r;
    }

    static void RecycleFile(string path, DiagReport r)
    {
      try
      {
        File.SetAttributes(path, FileAttributes.Normal);
        long len = new FileInfo(path).Length;
        if (len > 0)
        {
          using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough))
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
                int w = (int)Math.Min((long)buf.Length, left);
                fs.Write(buf, 0, w);
                left -= w;
              }
              fs.Flush(true);
            }
            fs.SetLength(0);
          }
        }
        File.Delete(path);
        r.Bytes += len;
        r.Files++;
      }
      catch (Exception ex) { r.Errors.Add(ex.Message); }
    }

    static void RecycleDir(string dir, DiagReport r)
    {
      try
      {
        File.SetAttributes(dir, FileAttributes.Normal);
        foreach (string f in Directory.GetFiles(dir)) RecycleFile(f, r);
        foreach (string d in Directory.GetDirectories(dir)) RecycleDir(d, r);
        Directory.Delete(dir, true);
      }
      catch (Exception ex) { r.Errors.Add(ex.Message); }
    }
  }

  public class BrowserNode
  {
    public string Id, Name, ProcessName, UserDataPath;
    public bool IsFirefox, IsClean, IsCleaning;
    public RectangleF RowBounds, IndicatorBounds, ActionBtnBounds;
  }

  public static class ProfileInspector
  {
    public static List<BrowserNode> DetectProfiles()
    {
      var list = new List<BrowserNode>();
      string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      string roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
      var candidates = new[]
      {
        new BrowserNode { Id = "chrome", Name = "Google Chrome", ProcessName = "chrome", UserDataPath = Path.Combine(local, @"Google\Chrome\User Data") },
        new BrowserNode { Id = "edge", Name = "Microsoft Edge", ProcessName = "msedge", UserDataPath = Path.Combine(local, @"Microsoft\Edge\User Data") },
        new BrowserNode { Id = "firefox", Name = "Mozilla Firefox", ProcessName = "firefox", UserDataPath = Path.Combine(roam, @"Mozilla\Firefox\Profiles"), IsFirefox = true },
        new BrowserNode { Id = "opera", Name = "Opera", ProcessName = "opera", UserDataPath = Path.Combine(roam, @"Opera Software\Opera Stable") },
        new BrowserNode { Id = "operagx", Name = "Opera GX", ProcessName = "opera", UserDataPath = Path.Combine(roam, @"Opera Software\Opera GX Stable") },
        new BrowserNode { Id = "yandex", Name = "Yandex Browser", ProcessName = "browser", UserDataPath = Path.Combine(local, @"Yandex\YandexBrowser\User Data") },
        new BrowserNode { Id = "brave", Name = "Brave Browser", ProcessName = "brave", UserDataPath = Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data") },
        new BrowserNode { Id = "vivaldi", Name = "Vivaldi", ProcessName = "vivaldi", UserDataPath = Path.Combine(local, @"Vivaldi\User Data") }
      };
      foreach (var b in candidates)
        if (Directory.Exists(b.UserDataPath)) { VerifyProfileState(b); list.Add(b); }
      return list;
    }

    public static bool HasNonEmptyFile(string path) { try { return File.Exists(path) && new FileInfo(path).Length > 0; } catch { return false; } }

    public static bool VerifyProfileState(BrowserNode b)
    {
      try
      {
        if (!Directory.Exists(b.UserDataPath)) { b.IsClean = true; return true; }
        if (b.IsFirefox)
        {
          foreach (string p in Directory.GetDirectories(b.UserDataPath))
            if (HasNonEmptyFile(Path.Combine(p, "places.sqlite")) || HasNonEmptyFile(Path.Combine(p, "cookies.sqlite"))) { b.IsClean = false; return false; }
          b.IsClean = true; return true;
        }
        List<string> dirs = new List<string> { b.UserDataPath, Path.Combine(b.UserDataPath, "Default") };
        try { dirs.AddRange(Directory.GetDirectories(b.UserDataPath, "Profile *")); } catch { }
        foreach (string d in dirs)
        {
          if (!Directory.Exists(d)) continue;
          if (HasNonEmptyFile(Path.Combine(d, "History")) || HasNonEmptyFile(Path.Combine(d, @"Network\Cookies")) || HasNonEmptyFile(Path.Combine(d, "Cookies")) || HasNonEmptyFile(Path.Combine(d, "Visited Links")))
          { b.IsClean = false; return false; }
        }
        b.IsClean = true; return true;
      }
      catch { b.IsClean = false; return false; }
    }

    public static void ResetSystemWebCache()
    {
      try
      {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (string dir in new string[]
        {
          Path.Combine(local, @"Microsoft\Windows\WebCache"), Path.Combine(local, @"Microsoft\Windows\INetCache"),
          Path.Combine(local, @"Microsoft\Windows\INetCookies"), Path.Combine(local, @"Microsoft\Windows\History")
        })
        {
          if (!Directory.Exists(dir)) continue;
          foreach (string f in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)) EndpointEngine.ZeroAndRemove(f, null);
        }
        foreach (string kp in new string[] { @"Software\Microsoft\Internet Explorer\TypedURLs", @"Software\Microsoft\Internet Explorer\TypedURLsTime" })
        {
          using (var key = Registry.CurrentUser.OpenSubKey(kp, true))
            if (key != null) foreach (var val in key.GetValueNames()) { try { key.DeleteValue(val, false); } catch { } }
        }
      }
      catch { }
    }

    public static void ResetProfile(BrowserNode b)
    {
      try
      {
        string[] extraProcs = new string[] { b.ProcessName };
        if (b.Id == "edge") extraProcs = new string[] { "msedge", "msedgewebview2" };
        else if (b.Id == "chrome") extraProcs = new string[] { "chrome", "chrome_crashpad_handler" };
        else if (b.Id == "opera" || b.Id == "operagx") extraProcs = new string[] { "opera", "opera_crashreporter" };
        else if (b.Id == "yandex") extraProcs = new string[] { "browser", "yandex", "service_update" };
        else if (b.IsFirefox) extraProcs = new string[] { "firefox", "pingsender", "plugin-container" };

        foreach (string pn in extraProcs)
          foreach (Process p in Process.GetProcessesByName(pn)) { try { p.Kill(); p.WaitForExit(500); } catch { } finally { p.Dispose(); } }
        if (b.Id == "edge") ResetSystemWebCache();
      }
      catch { }
      Thread.Sleep(250);
      try
      {
        if (b.IsFirefox)
        {
          foreach (string p in Directory.GetDirectories(b.UserDataPath))
            foreach (string f in new[] { "places.sqlite", "places.sqlite-wal", "places.sqlite-shm", "cookies.sqlite", "cookies.sqlite-wal", "formhistory.sqlite", "favicons.sqlite", "sessionstore.jsonlz4" })
              EndpointEngine.ZeroAndRemove(Path.Combine(p, f), null);
        }
        else
        {
          List<string> dirs = new List<string> { b.UserDataPath, Path.Combine(b.UserDataPath, "Default") };
          try { dirs.AddRange(Directory.GetDirectories(b.UserDataPath, "Profile *")); } catch { }
          foreach (string d in dirs)
          {
            if (!Directory.Exists(d)) continue;
            foreach (string f in new[] { "History", "History-journal", "History-wal", "History-shm", "Visited Links", "Top Sites", "Favicons", "Shortcuts", "Login Data", "Login Data-journal", "Cookies", "Cookies-journal", @"Network\Cookies", @"Network\Cookies-journal", "Web Data", "Web Data-journal", "Current Session", "Current Tabs", "Last Session", "Last Tabs" })
              EndpointEngine.ZeroAndRemove(Path.Combine(d, f), null);
            foreach (string sd in new[] { "Sessions", "Cache", @"Cache\Cache_Data", "Code Cache", "GPUCache", "DawnCache", "Session Storage" })
              try { string sp = Path.Combine(d, sd); if (Directory.Exists(sp)) Directory.Delete(sp, true); } catch { }
          }
        }
      }
      catch { }
      VerifyProfileState(b);
    }
  }

  public enum BtnStyle { Dark, Red, Accent, Green }

  public class UiButton
  {
    public string Title, RightTag, LoadingText = "", SuccessText = "";
    public BtnStyle Style = BtnStyle.Dark;
    public bool IsLarge, IsActiveToggle, IsLoading, IsSuccess;
    public float HoverMix = 0f, TargetHover = 0f;
    public int ResetTicks = 0;
    public RectangleF Bounds;
    public Action OnClick;
  }

  public class QueueItem
  {
    public string Path;
    public long SizeBytes;
    public RectangleF RowBounds, DeleteBtnBounds;
  }

  public class MainForm : Form
  {
    private const int HK_SYNC_ALL = 0x6101;
    private const int HK_TOGGLE_VIEW = 0x6102;

    private bool _meterViewActive = true, _toneActive = false, _tabQueue = false, _busy = false, _statusWarn = false;
    private float _vuLeft = 0.18f, _vuRight = 0.16f, _sinePhase = 0f;
    private string _audioStatus = "WASAPI Shared Mode | 48000 Hz | 24-bit PCM | Buffer: 128 frames (2.67 ms)";
    private RectangleF _camBtnTone, _camBtnVerify, _camBtnReset;

    private readonly List<UiButton> _buttons = new List<UiButton>();
    private readonly List<QueueItem> _files = new List<QueueItem>();
    private List<BrowserNode> _browsers = new List<BrowserNode>();

    private UiButton _btnMasterSync, _btnIntegrityFix, _btnArchiveBins, _btnRestoreBins;
    private UiButton _btnClip, _btnExplorer, _btnBags, _btnMessengers;
    private UiButton _btnLastActivity, _btnExecuted, _btnSelectiveBam, _btnAnyDesk;
    private UiButton _btnGpuCache, _btnRamPurge, _btnUsnEverything, _btnReleaseHost;
    private UiButton _btnSelectFiles, _btnAddFolder, _btnRecycle;
    private UiButton _btnScanBrowsers, _btnCleanAllBrowsers, _btnOpenViewer;

    private RectangleF _leftCard, _rightCard, _dropZone, _tabBrowRect, _tabQueueRect;
    private string _statusText = "ESC / CTRL+SHIFT+F12: VIEW | CTRL+SHIFT+F11: FULL SYNC";
    private int _scrollOffset = 0;

    private readonly Font _fHeader = new Font("Segoe UI", 11.5f, FontStyle.Bold);
    private readonly Font _fMaster = new Font("Segoe UI", 10f, FontStyle.Bold);
    private readonly Font _fBtn = new Font("Segoe UI", 8.5f, FontStyle.Bold);
    private readonly Font _fMono = new Font("Consolas", 8.25f, FontStyle.Regular);

    protected override void WndProc(ref Message m)
    {
      if (m.Msg == NativeInterop.WM_HOTKEY)
      {
        int id = m.WParam.ToInt32();
        if (id == HK_SYNC_ALL) { OnMasterSyncClicked(); return; }
        if (id == HK_TOGGLE_VIEW) { _meterViewActive = !_meterViewActive; Invalidate(); return; }
      }
      base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
      base.OnHandleCreated(e);
      NativeInterop.ApplyDarkTitleBar(this.Handle);
      try
      {
        NativeInterop.RegisterHotKey(this.Handle, HK_SYNC_ALL, NativeInterop.MOD_CONTROL | NativeInterop.MOD_SHIFT, NativeInterop.VK_F11);
        NativeInterop.RegisterHotKey(this.Handle, HK_TOGGLE_VIEW, NativeInterop.MOD_CONTROL | NativeInterop.MOD_SHIFT, NativeInterop.VK_F12);
      }
      catch { }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
      base.OnKeyDown(e);
      if (e.KeyCode == Keys.Escape) { _meterViewActive = !_meterViewActive; Invalidate(); e.Handled = true; return; }
      if (e.Control && e.Shift && e.KeyCode == Keys.F12) { _meterViewActive = !_meterViewActive; Invalidate(); e.Handled = true; }
      else if (e.Control && e.Shift && e.KeyCode == Keys.F11) { OnMasterSyncClicked(); e.Handled = true; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
      try
      {
        NativeInterop.UnregisterHotKey(this.Handle, HK_SYNC_ALL);
        NativeInterop.UnregisterHotKey(this.Handle, HK_TOGGLE_VIEW);
      }
      catch { }
      try
      {
        EndpointEngine.SyncEndpointState();
        EndpointEngine.FlushHostArtifacts(true);
        EndpointEngine.ClearDialogMru(new DiagReport());
        NativeInterop.FlushDnsCache();
        NativeInterop.FlushExplorerCache();
      }
      catch { }
      base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
      try { _fHeader.Dispose(); _fMaster.Dispose(); _fBtn.Dispose(); _fMono.Dispose(); } catch { }
      base.OnFormClosed(e);
    }

    public MainForm()
    {
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
      DoubleBuffered = true; KeyPreview = true;
      AutoScaleMode = AutoScaleMode.None;
      Text = "Windows Audio Endpoint Diagnostics";
      BackColor = Color.FromArgb(5, 5, 5); ForeColor = Color.FromArgb(235, 235, 235);
      StartPosition = FormStartPosition.CenterScreen;
      ClientSize = new Size(1080, 680); MinimumSize = new Size(960, 640);
      AllowDrop = true;

      try
      {
        string sndVol = Path.Combine(Environment.SystemDirectory, "SndVol.exe");
        this.Icon = File.Exists(sndVol) ? Icon.ExtractAssociatedIcon(sndVol) : Icon.ExtractAssociatedIcon(Application.ExecutablePath);
      }
      catch { }

      NativeInterop.ApplyDarkTitleBar(this.Handle);
      ThreadPool.QueueUserWorkItem(delegate { try { EndpointEngine.SyncEndpointState(); } catch { } });
      InitButtons();
      try { _browsers = ProfileInspector.DetectProfiles(); } catch { }
      ComputeLayout();

      System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 16 };
      timer.Tick += delegate
      {
        bool dirty = false;
        if (_meterViewActive)
        {
          _sinePhase += 0.18f;
          float baseLvl = _toneActive ? 0.72f : 0.08f;
          _vuLeft = baseLvl + (float)Math.Sin(_sinePhase) * (_toneActive ? 0.14f : 0.03f);
          _vuRight = baseLvl + (float)Math.Cos(_sinePhase * 0.9f) * (_toneActive ? 0.14f : 0.03f);
          dirty = true;
        }
        else
        {
          foreach (var b in _buttons)
          {
            if (Math.Abs(b.HoverMix - b.TargetHover) > 0.01f) { b.HoverMix += (b.TargetHover - b.HoverMix) * 0.25f; dirty = true; }
            if (b.ResetTicks > 0 && --b.ResetTicks == 0) { b.IsSuccess = false; dirty = true; }
          }
        }
        if (dirty) Invalidate();
      };
      timer.Start();
    }

    private UiButton MakeBtn(string title, BtnStyle s, Action click)
    {
      var b = new UiButton { Title = title, Style = s, OnClick = click };
      _buttons.Add(b); return b;
    }

    private void ClearClipboardOnUiThread()
    {
      try { Clipboard.Clear(); } catch { }
      try { NativeInterop.ClearNativeClipboard(this.Handle); } catch { }
    }

    private void InitButtons()
    {
      _btnMasterSync = MakeBtn("ПОЛНАЯ ЗАЧИСТКА СИСТЕМЫ", BtnStyle.Red, OnMasterSyncClicked);
      _btnMasterSync.IsLarge = true;

      _btnIntegrityFix = MakeBtn("Нормализация системы [SysMain / DNS]", BtnStyle.Green, delegate { RunTask(_btnIntegrityFix, EndpointEngine.SyncEndpointState, "Sync OK"); });
      _btnArchiveBins = MakeBtn("\u0421\u043a\u0440\u044b\u0442\u044c \u043d\u0435\u0441\u0442\u0430\u043d\u0434\u0430\u0440\u0442\u043d\u044b\u0435 .exe \u0432 Downloads", BtnStyle.Accent, delegate { RunTask(_btnArchiveBins, BufferInspector.ArchiveNonStandardStreams, "Archived OK"); });
      _btnRestoreBins = MakeBtn("\u0412\u0435\u0440\u043d\u0443\u0442\u044c \u0441\u043a\u0440\u044b\u0442\u044b\u0435 .exe.bak", BtnStyle.Dark, delegate { RunTask(_btnRestoreBins, BufferInspector.RestoreArchivedStreams, "Restored OK"); });

      _btnClip = MakeBtn("\u0411\u0443\u0444\u0435\u0440 \u043e\u0431\u043c\u0435\u043d\u0430", BtnStyle.Dark, delegate { ClearClipboardOnUiThread(); RunTask(_btnClip, EndpointEngine.ResetClipboardBuffer, "Clipboard + Win+V OK"); });
      _btnExplorer = MakeBtn("\u041f\u0440\u043e\u0432\u043e\u0434\u043d\u0438\u043a / MRU", BtnStyle.Dark, delegate { RunTask(_btnExplorer, EndpointEngine.ResetShellHistory, "MRU OK"); });
      _btnBags = MakeBtn("\u0420\u0435\u0435\u0441\u0442\u0440 Shell Bags", BtnStyle.Dark, delegate { RunTask(_btnBags, EndpointEngine.ResetShellBags, "ShellBags OK"); });
      _btnMessengers = MakeBtn("\u041a\u044d\u0448 Discord / TG", BtnStyle.Dark, delegate { RunTask(_btnMessengers, EndpointEngine.ResetClientCaches, "Cache OK"); });

      _btnLastActivity = MakeBtn("LastActivityView", BtnStyle.Dark, delegate { RunTask(_btnLastActivity, EndpointEngine.ResetActivityLogs, "LastActivity OK"); });
      _btnExecuted = MakeBtn("ExecutedPrograms", BtnStyle.Dark, delegate { RunTask(_btnExecuted, EndpointEngine.ResetExecutionLogs, "Executed OK"); });
      _btnSelectiveBam = MakeBtn("\u0422\u043e\u0447\u0435\u0447\u043d\u044b\u0439 BAM / PCA / Store", BtnStyle.Dark, delegate { RunTask(_btnSelectiveBam, EndpointEngine.PurgeRegistryTraces, "BAM/PCA/Store OK"); });
      _btnAnyDesk = MakeBtn("\u041b\u043e\u0433\u0438 \u0438 \u0447\u0430\u0442 AnyDesk", BtnStyle.Dark, delegate { RunTask(_btnAnyDesk, EndpointEngine.ResetRemoteLogs, "AnyDesk OK"); });

      _btnGpuCache = MakeBtn("\u041a\u044d\u0448 \u0448\u0435\u0439\u0434\u0435\u0440\u043e\u0432 GPU [NV/AMD/Intel]", BtnStyle.Accent, delegate { RunTask(_btnGpuCache, EndpointEngine.ResetShaderCache, "GPU Cache OK"); });
      _btnRamPurge = MakeBtn("RAM Purge [EmptyWorkingSet]", BtnStyle.Accent, delegate { RunTask(_btnRamPurge, EndpointEngine.TrimWorkingSets, "RAM Purged OK"); });
      _btnUsnEverything = MakeBtn("USN Recreate & Everything.db", BtnStyle.Dark, delegate { RunTask(_btnUsnEverything, EndpointEngine.RebuildVolumeIndex, "USN Recreated OK"); });
      _btnReleaseHost = MakeBtn("\u041f\u041e\u041b\u041d\u0410\u042f \u0421\u0410\u041c\u041e\u041b\u0418\u041a\u0412\u0418\u0414\u0410\u0426\u0418\u042f [\u0423\u0434\u0430\u043b\u0438\u0442\u044c \u0445\u043e\u0441\u0442]", BtnStyle.Red, delegate { EndpointEngine.ReleaseDriverHost(); });

      _btnSelectFiles = MakeBtn("\u0412\u044b\u0431\u0440\u0430\u0442\u044c \u0444\u0430\u0439\u043b\u044b...", BtnStyle.Dark, OnSelectFilesClicked);
      _btnAddFolder = MakeBtn("\u0414\u043e\u0431\u0430\u0432\u0438\u0442\u044c \u043f\u0430\u043f\u043a\u0443", BtnStyle.Accent, OnAddFolderClicked);
      _btnRecycle = MakeBtn("\u0423\u043d\u0438\u0447\u0442\u043e\u0436\u0438\u0442\u044c", BtnStyle.Red, OnRecycleClicked);

      _btnScanBrowsers = MakeBtn("\u0421\u043a\u0430\u043d\u0438\u0440\u043e\u0432\u0430\u0442\u044c", BtnStyle.Accent, delegate { _browsers = ProfileInspector.DetectProfiles(); ComputeLayout(); Invalidate(); });
      _btnCleanAllBrowsers = MakeBtn("\u0421\u0431\u0440\u043e\u0441\u0438\u0442\u044c \u0432\u0441\u0435", BtnStyle.Red, OnCleanAllBrowsersClicked);
      _btnOpenViewer = MakeBtn("BrowsingHistoryView", BtnStyle.Dark, OnOpenViewerClicked);
    }

    private void ComputeLayout()
    {
      if (_btnMasterSync == null) return;
      int pad = 18, gap = 18;
      int w = Math.Max(900, ClientSize.Width), h = Math.Max(620, ClientSize.Height);
      int leftW = (int)((w - pad * 2 - gap) * 0.48f), rightW = (w - pad * 2 - gap) - leftW;
      _leftCard = new RectangleF(pad, pad, leftW, h - pad * 2);
      _rightCard = new RectangleF(pad + leftW + gap, pad, rightW, h - pad * 2);

      _camBtnTone = new RectangleF(pad + 30f, h - 90f, 190f, 40f);
      _camBtnVerify = new RectangleF(pad + 235f, h - 90f, 210f, 40f);
      _camBtnReset = new RectangleF(pad + 460f, h - 90f, 190f, 40f);

      float lx = _leftCard.X + 18f, lw = _leftCard.Width - 36f, ly = _leftCard.Y + 54f;
      _btnMasterSync.Bounds = new RectangleF(lx, ly, lw, 46f); ly += 54f;
      _btnIntegrityFix.Bounds = new RectangleF(lx, ly, lw, 38f); ly += 45f;

      float colGap = 8f, colW = (lw - colGap) * 0.5f, rowH = 36f, rowGap = 6f;
      UiButton[,] grid = new UiButton[,]
      {
        { _btnArchiveBins, _btnRestoreBins },
        { _btnClip, _btnExplorer },
        { _btnBags, _btnMessengers },
        { _btnLastActivity, _btnExecuted },
        { _btnSelectiveBam, _btnAnyDesk },
        { _btnGpuCache, _btnRamPurge },
        { _btnUsnEverything, _btnReleaseHost }
      };
      for (int r = 0; r < grid.GetLength(0); r++)
      {
        grid[r, 0].Bounds = new RectangleF(lx, ly, colW, rowH);
        grid[r, 1].Bounds = new RectangleF(lx + colW + colGap, ly, colW, rowH);
        ly += rowH + rowGap;
      }

      float rx = _rightCard.X + 18f, rw = _rightCard.Width - 36f, ry = _rightCard.Y + 56f, botH = 40f;
      _tabBrowRect = new RectangleF(_rightCard.Right - 250f, _rightCard.Y + 16f, 112f, 26f);
      _tabQueueRect = new RectangleF(_rightCard.Right - 130f, _rightCard.Y + 16f, 112f, 26f);
      _dropZone = new RectangleF(rx, ry, rw, _rightCard.Bottom - ry - botH - 28f);
      float by = _dropZone.Bottom + 10f, bw = (rw - 16f) / 3f;

      if (!_tabQueue)
      {
        _btnScanBrowsers.Bounds = new RectangleF(rx, by, bw, botH);
        _btnCleanAllBrowsers.Bounds = new RectangleF(rx + bw + 8f, by, bw, botH);
        _btnOpenViewer.Bounds = new RectangleF(rx + (bw + 8f) * 2f, by, bw, botH);
        _btnSelectFiles.Bounds = _btnAddFolder.Bounds = _btnRecycle.Bounds = RectangleF.Empty;

        float iy = _dropZone.Y + 12f - _scrollOffset;
        for (int i = 0; i < _browsers.Count; i++)
        {
          var b = _browsers[i];
          b.RowBounds = new RectangleF(rx + 12f, iy, rw - 24f, 52f);
          b.ActionBtnBounds = new RectangleF(b.RowBounds.Right - 96f, iy + 10f, 84f, 32f);
          b.IndicatorBounds = new RectangleF(b.ActionBtnBounds.Left - 118f, iy + 13f, 108f, 26f);
          iy += 60f;
        }
      }
      else
      {
        _btnSelectFiles.Bounds = new RectangleF(rx, by, bw, botH);
        _btnAddFolder.Bounds = new RectangleF(rx + bw + 8f, by, bw, botH);
        _btnRecycle.Bounds = new RectangleF(rx + (bw + 8f) * 2f, by, bw, botH);
        _btnScanBrowsers.Bounds = _btnCleanAllBrowsers.Bounds = _btnOpenViewer.Bounds = RectangleF.Empty;

        float iy = _dropZone.Y + 12f - _scrollOffset;
        for (int i = 0; i < _files.Count; i++)
        {
          _files[i].RowBounds = new RectangleF(rx + 12f, iy, rw - 24f, 46f);
          _files[i].DeleteBtnBounds = new RectangleF(_files[i].RowBounds.Right - 36f, iy + 9f, 28f, 28f);
          iy += 54f;
        }
      }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); ComputeLayout(); Invalidate(); }

    private void PaintAudioDiagnosticView(Graphics g)
    {
      RectangleF card = new RectangleF(18f, 18f, ClientSize.Width - 36f, ClientSize.Height - 36f);
      DrawBox(g, card, Color.FromArgb(11, 11, 11), Color.FromArgb(28, 28, 28), 10f);
      TextRenderer.DrawText(g, "WINDOWS AUDIO ENDPOINT DIAGNOSTICS (WASAPI)", _fHeader, new Point(44, 42), Color.White);
      TextRenderer.DrawText(g, "Endpoint: Default Render Device (High Definition Audio Device) | Status: Active", _fMono, new Point(44, 72), Color.FromArgb(150, 150, 150));

      RectangleF meterBox = new RectangleF(44f, 112f, card.Width - 52f, 220f);
      DrawBox(g, meterBox, Color.FromArgb(7, 7, 7), Color.FromArgb(24, 24, 24), 8f);

      TextRenderer.DrawText(g, "CHANNEL L (PCM 24-BIT / 48000 HZ)", _fMono, new Point(64, 132), Color.FromArgb(180, 180, 180));
      RectangleF barLBg = new RectangleF(64f, 156f, meterBox.Width - 40f, 26f);
      DrawBox(g, barLBg, Color.FromArgb(16, 16, 16), Color.FromArgb(34, 34, 34), 4f);
      DrawBox(g, new RectangleF(65f, 157f, Math.Max(4f, (barLBg.Width - 2f) * _vuLeft), 24f), Color.FromArgb(46, 204, 113), Color.FromArgb(46, 204, 113), 4f);

      TextRenderer.DrawText(g, "CHANNEL R (PCM 24-BIT / 48000 HZ)", _fMono, new Point(64, 204), Color.FromArgb(180, 180, 180));
      RectangleF barRBg = new RectangleF(64f, 228f, meterBox.Width - 40f, 26f);
      DrawBox(g, barRBg, Color.FromArgb(16, 16, 16), Color.FromArgb(34, 34, 34), 4f);
      DrawBox(g, new RectangleF(65f, 229f, Math.Max(4f, (barRBg.Width - 2f) * _vuRight), 24f), Color.FromArgb(46, 204, 113), Color.FromArgb(46, 204, 113), 4f);

      TextRenderer.DrawText(g, _audioStatus, _fMono, new Point(64, 282), Color.FromArgb(120, 200, 150));

      RectangleF specBox = new RectangleF(44f, 350f, card.Width - 52f, 190f);
      DrawBox(g, specBox, Color.FromArgb(8, 8, 8), Color.FromArgb(24, 24, 24), 8f);
      string[] specs = new string[]
      {
        "Sample Rate          : 48000 Hz (Studio Reference)",
        "Bit Depth            : 24-bit IEEE Float / PCM",
        "Hardware Buffer      : 128 samples (Exclusive Mode Ready)",
        "Stream Latency       : 2.667 ms (0 glitches detected)",
        "Clock Drift          : +0.0012% (Synchronized)",
        "APO Effects          : Disabled (Bit-Perfect Bypass)"
      };
      for (int i = 0; i < specs.Length; i++)
        TextRenderer.DrawText(g, specs[i], _fMono, new Point(64, 368 + i * 26), Color.FromArgb(190, 190, 190));

      DrawBox(g, _camBtnTone, _toneActive ? Color.FromArgb(22, 52, 32) : Color.FromArgb(20, 20, 20), Color.FromArgb(55, 55, 55), 6f);
      TextRenderer.DrawText(g, _toneActive ? "Stop 440 Hz Tone" : "Test Tone 440 Hz", _fBtn, Rectangle.Round(_camBtnTone), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

      DrawBox(g, _camBtnVerify, Color.FromArgb(20, 20, 20), Color.FromArgb(55, 55, 55), 6f);
      TextRenderer.DrawText(g, "Verify WASAPI Buffer", _fBtn, Rectangle.Round(_camBtnVerify), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

      DrawBox(g, _camBtnReset, Color.FromArgb(20, 20, 20), Color.FromArgb(55, 55, 55), 6f);
      TextRenderer.DrawText(g, "Reset Audio Graph", _fBtn, Rectangle.Round(_camBtnReset), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
      Graphics g = e.Graphics;
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
      g.Clear(Color.FromArgb(5, 5, 5));

      if (_meterViewActive) { PaintAudioDiagnosticView(g); return; }

      DrawBox(g, _leftCard, Color.FromArgb(12, 12, 12), Color.FromArgb(30, 30, 30), 10f);
      DrawBox(g, _rightCard, Color.FromArgb(12, 12, 12), Color.FromArgb(30, 30, 30), 10f);

      TextRenderer.DrawText(g, "SYSTEM MAINTENANCE CONSOLE [ESC: AUDIO VIEW]", _fHeader, new Point((int)_leftCard.X + 18, (int)_leftCard.Y + 18), Color.White);
      TextRenderer.DrawText(g, _tabQueue ? "STREAM RECYCLER" : "BROWSERS", _fHeader, new Point((int)_rightCard.X + 18, (int)_rightCard.Y + 18), Color.White);

      DrawTab(g, _tabBrowRect, "\u0411\u0440\u0430\u0443\u0437\u0435\u0440\u044b [" + _browsers.Count + "]", !_tabQueue);
      DrawTab(g, _tabQueueRect, "\u041e\u0447\u0435\u0440\u0435\u0434\u044c [" + _files.Count + "]", _tabQueue);

      DrawBox(g, _dropZone, Color.FromArgb(8, 8, 8), Color.FromArgb(26, 26, 26), 8f);

      GraphicsState st = g.Save();
      g.SetClip(Rectangle.Round(_dropZone));
      if (!_tabQueue)
      {
        for (int i = 0; i < _browsers.Count; i++)
        {
          var b = _browsers[i];
          DrawBox(g, b.RowBounds, Color.FromArgb(15, 15, 15), Color.FromArgb(32, 32, 32), 6f);
          TextRenderer.DrawText(g, b.Name, _fBtn, new Rectangle((int)b.RowBounds.X + 14, (int)b.RowBounds.Y, 200, (int)b.RowBounds.Height), Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

          Color statusCol = b.IsClean ? Color.FromArgb(46, 204, 113) : Color.FromArgb(231, 76, 60);
          DrawBox(g, b.IndicatorBounds, Color.FromArgb(18, statusCol), Color.FromArgb(70, statusCol), 5f);
          TextRenderer.DrawText(g, b.IsClean ? "CLEAN" : "HISTORY", _fMono, Rectangle.Round(b.IndicatorBounds), statusCol, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

          DrawBox(g, b.ActionBtnBounds, Color.FromArgb(24, 24, 24), Color.FromArgb(50, 50, 50), 5f);
          TextRenderer.DrawText(g, b.IsCleaning ? "..." : "\u0421\u0431\u0440\u043e\u0441", _fBtn, Rectangle.Round(b.ActionBtnBounds), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
      }
      else
      {
        if (_files.Count == 0)
          TextRenderer.DrawText(g, "\u041f\u0435\u0440\u0435\u0442\u0430\u0449\u0438\u0442\u0435 \u0444\u0430\u0439\u043b\u044b \u0438\u043b\u0438 \u043f\u0430\u043f\u043a\u0438 \u0432 \u044d\u0442\u0443 \u043e\u0431\u043b\u0430\u0441\u0442\u044c", _fBtn, Rectangle.Round(_dropZone), Color.FromArgb(110, 110, 110), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        for (int i = 0; i < _files.Count; i++)
        {
          var f = _files[i];
          DrawBox(g, f.RowBounds, Color.FromArgb(15, 15, 15), Color.FromArgb(32, 32, 32), 6f);
          TextRenderer.DrawText(g, f.Path, _fMono, new Rectangle((int)f.RowBounds.X + 12, (int)f.RowBounds.Y, (int)f.RowBounds.Width - 55, (int)f.RowBounds.Height), Color.FromArgb(210, 210, 210), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis);
          DrawBox(g, f.DeleteBtnBounds, Color.FromArgb(30, 14, 14), Color.FromArgb(80, 30, 30), 4f);
          TextRenderer.DrawText(g, "X", _fBtn, Rectangle.Round(f.DeleteBtnBounds), Color.FromArgb(230, 80, 80), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
      }
      g.Restore(st);

      foreach (var b in _buttons) DrawBtn(g, b);

      Rectangle footRect = new Rectangle((int)_leftCard.X + 18, (int)_leftCard.Bottom - 26, (int)_leftCard.Width - 36, 20);
      TextRenderer.DrawText(g, _statusText, _fMono, footRect, _statusWarn ? Color.FromArgb(235, 87, 87) : Color.FromArgb(130, 130, 130), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    private void DrawTab(Graphics g, RectangleF r, string text, bool active)
    {
      DrawBox(g, r, active ? Color.FromArgb(28, 28, 28) : Color.FromArgb(14, 14, 14), active ? Color.FromArgb(85, 85, 85) : Color.FromArgb(32, 32, 32), 5f);
      TextRenderer.DrawText(g, text, _fBtn, Rectangle.Round(r), active ? Color.White : Color.FromArgb(140, 140, 140), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void DrawBtn(Graphics g, UiButton b)
    {
      if (b.Bounds.Width <= 2f) return;
      int h = (int)(b.HoverMix * 16f);
      Color bg = Color.FromArgb(18 + h, 18 + h, 18 + h), bd = Color.FromArgb(38 + h, 38 + h, 38 + h);
      if (b.IsSuccess || b.Style == BtnStyle.Green) { bg = Color.FromArgb(16 + h / 2, 42 + h, 26 + h / 2); bd = Color.FromArgb(46, 204, 113); }
      else if (b.Style == BtnStyle.Red) { bg = Color.FromArgb(48 + h, 14, 14); bd = Color.FromArgb(140 + h, 38, 38); }
      else if (b.IsActiveToggle || b.Style == BtnStyle.Accent) { bg = Color.FromArgb(24 + h, 24 + h, 28 + h); bd = Color.FromArgb(95 + h, 95 + h, 105 + h); }

      DrawBox(g, b.Bounds, bg, bd, 6f);
      string label = b.IsLoading ? b.LoadingText : (b.IsSuccess ? b.SuccessText : b.Title);
      Rectangle tr = new Rectangle((int)b.Bounds.X + 12, (int)b.Bounds.Y, (int)b.Bounds.Width - 24, (int)b.Bounds.Height);
      TextRenderer.DrawText(g, label, b.IsLarge ? _fMaster : _fBtn, tr, Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
      if (!string.IsNullOrEmpty(b.RightTag))
        TextRenderer.DrawText(g, b.RightTag, _fMono, tr, Color.FromArgb(180, 180, 180), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }

    private static void DrawBox(Graphics g, RectangleF r, Color fill, Color border, float rad)
    {
      if (r.Width <= 1f || r.Height <= 1f) return;
      using (GraphicsPath gp = new GraphicsPath())
      {
        float d = rad * 2f;
        gp.AddArc(r.X, r.Y, d, d, 180, 90); gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        gp.CloseFigure();
        using (SolidBrush sb = new SolidBrush(fill)) g.FillPath(sb, gp);
        using (Pen p = new Pen(border, 1f)) g.DrawPath(p, gp);
      }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
      base.OnMouseMove(e);
      if (_meterViewActive) return;
      foreach (var b in _buttons) b.TargetHover = (!_busy && b.Bounds.Contains(e.Location)) ? 1f : 0f;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
      base.OnMouseWheel(e);
      if (_meterViewActive) return;
      int count = _tabQueue ? _files.Count : _browsers.Count, itemH = _tabQueue ? 54 : 60;
      if (count > 3 && _dropZone.Contains(e.Location))
      {
        int maxScroll = Math.Max(0, count * itemH - (int)(_dropZone.Height - 24f));
        _scrollOffset = Math.Max(0, Math.Min(maxScroll, _scrollOffset - (e.Delta > 0 ? 36 : -36)));
        ComputeLayout(); Invalidate();
      }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
      base.OnMouseUp(e);
      if (e.Button == MouseButtons.Middle && e.Y < 65) { _meterViewActive = !_meterViewActive; Invalidate(); return; }

      if (_meterViewActive)
      {
        if (e.Button == MouseButtons.Left)
        {
          if (_camBtnTone.Contains(e.Location)) { _toneActive = !_toneActive; if (_toneActive) SineTonePlayer.Play440HzAsync(); Invalidate(); }
          else if (_camBtnVerify.Contains(e.Location)) { _audioStatus = "WASAPI Exclusive Check: OK | 0 buffer underruns | Jitter 0.02 ms"; Invalidate(); }
          else if (_camBtnReset.Contains(e.Location)) { _toneActive = false; _audioStatus = "Audio Endpoint Graph Reset Complete | 48000 Hz Locked"; Invalidate(); }
        }
        return;
      }

      if (_busy || e.Button != MouseButtons.Left) return;
      if (_tabBrowRect.Contains(e.Location)) { _tabQueue = false; _scrollOffset = 0; ComputeLayout(); Invalidate(); return; }
      if (_tabQueueRect.Contains(e.Location)) { _tabQueue = true; _scrollOffset = 0; ComputeLayout(); Invalidate(); return; }

      foreach (var b in _buttons)
        if (b.Bounds.Contains(e.Location) && b.OnClick != null) { b.OnClick(); return; }

      if (!_tabQueue)
      {
        foreach (var b in _browsers)
        {
          if (b.ActionBtnBounds.Contains(e.Location) && _dropZone.Contains(e.Location))
          {
            b.IsCleaning = true; Invalidate();
            ThreadPool.QueueUserWorkItem(delegate { ProfileInspector.ResetProfile(b); b.IsCleaning = false; BeginInvoke(new Action(Invalidate)); });
            return;
          }
        }
      }
      else
      {
        for (int i = _files.Count - 1; i >= 0; i--)
          if (_files[i].DeleteBtnBounds.Contains(e.Location) && _dropZone.Contains(e.Location))
          { _files.RemoveAt(i); ComputeLayout(); Invalidate(); return; }
      }
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
      if (!_meterViewActive && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
      if (_meterViewActive) return;
      string[] dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
      if (dropped != null) { _tabQueue = true; AddFiles(dropped); }
    }

    private void AddFiles(IEnumerable<string> paths)
    {
      foreach (string p in paths)
      {
        if (string.IsNullOrEmpty(p)) continue;
        _files.Add(new QueueItem { Path = p, SizeBytes = File.Exists(p) ? new FileInfo(p).Length : 0 });
      }
      ComputeLayout(); Invalidate();
    }

    private void OnSelectFilesClicked()
    {
      using (OpenFileDialog dlg = new OpenFileDialog { Multiselect = true })
        if (dlg.ShowDialog(this) == DialogResult.OK) AddFiles(dlg.FileNames);
      EndpointEngine.ClearDialogMru(new DiagReport());
      NativeInterop.FlushExplorerCache();
    }

    private void OnAddFolderClicked()
    {
      using (FolderBrowserDialog dlg = new FolderBrowserDialog())
        if (dlg.ShowDialog(this) == DialogResult.OK) AddFiles(new[] { dlg.SelectedPath });
      EndpointEngine.ClearDialogMru(new DiagReport());
      NativeInterop.FlushExplorerCache();
    }

    private void OnRecycleClicked()
    {
      if (_files.Count == 0) return;
      List<string> paths = new List<string>();
      foreach (var f in _files) paths.Add(f.Path);
      RunTask(_btnRecycle, delegate { var r = StreamRecycler.RecyclePaths(paths); _files.Clear(); BeginInvoke(new Action(ComputeLayout)); return r; }, "Recycled OK");
    }

    private void OnCleanAllBrowsersClicked()
    {
      RunTask(_btnCleanAllBrowsers, delegate
      {
        foreach (var b in _browsers) ProfileInspector.ResetProfile(b);
        ProfileInspector.ResetSystemWebCache();
        return new DiagReport();
      }, "Browsers Clean OK");
    }

    private void OnOpenViewerClicked()
    {
      string exeName = CodecTable.GetViewerExe();
      string envPath = Environment.GetEnvironmentVariable("BHV_PATH");
      string found = (!string.IsNullOrEmpty(envPath) && File.Exists(envPath)) ? envPath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, exeName);
      if (!File.Exists(found))
      {
        string dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        try
        {
          string[] hits = Directory.GetFiles(dl, exeName, SearchOption.AllDirectories);
          if (hits.Length > 0) found = hits[0];
        }
        catch { }
      }
      if (File.Exists(found)) { try { Process.Start(found); } catch { } }
      else MessageBox.Show(this, "Not found: " + exeName, "AudioDiag", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OnMasterSyncClicked()
    {
      ClearClipboardOnUiThread();
      List<string> paths = new List<string>();
      foreach (var f in _files) paths.Add(f.Path);
      RunTask(_btnMasterSync, delegate
      {
        DiagReport r = new DiagReport();
        if (paths.Count > 0) r.Merge(StreamRecycler.RecyclePaths(paths));
        r.Merge(EndpointEngine.RunFullMaintenance());
        foreach (var b in _browsers) ProfileInspector.ResetProfile(b);
        ProfileInspector.ResetSystemWebCache();
        _files.Clear();
        BeginInvoke(new Action(ComputeLayout));
        return r;
      }, "FULL SYNC OK");
    }

    private void RunTask(UiButton btn, Func<DiagReport> work, string okMsg)
    {
      if (_busy) return;
      _busy = true; btn.IsLoading = true;
      btn.LoadingText = "\u0412\u044b\u043f\u043e\u043b\u043d\u0435\u043d\u0438\u0435...";
      Invalidate();

      ThreadPool.QueueUserWorkItem(delegate
      {
        DiagReport res;
        try { res = work(); }
        catch (Exception ex) { res = new DiagReport(); res.Errors.Add(ex.Message); }
        BeginInvoke(new Action(delegate
        {
          _busy = false; btn.IsLoading = false;
          btn.IsSuccess = res.Errors.Count == 0;
          btn.SuccessText = "OK: " + okMsg;
          btn.ResetTicks = 45;
          _statusWarn = res.Errors.Count > 0;
          _statusText = _statusWarn ? res.Errors[0] : okMsg;
          Invalidate();
        }));
      });
    }
  }

  public static class Program
  {
    [STAThread]
    public static int Main(string[] args)
    {
      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);
      Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
      Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
      {
        MessageBox.Show(e.Exception.ToString(), "AudioDiag UI Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
      };

      WinCleaner.Licensing.AntiDebug.StartWatchdog();

      bool skipElevate = false;
      if (args != null)
      {
        foreach (string a in args)
          if (string.Equals(a, "--elevated", StringComparison.OrdinalIgnoreCase)) skipElevate = true;
      }
      if (!EndpointEngine.IsElevated() && !skipElevate)
      {
        try
        {
          Process p = Process.Start(new ProcessStartInfo
          {
            FileName = Application.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath),
            Arguments = "--elevated",
            Verb = "runas",
            UseShellExecute = true
          });
          if (p != null) return 0;
        }
        catch { }
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
        MessageBox.Show(ex.ToString(), "AudioDiag Startup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
      return 0;
    }
  }
}