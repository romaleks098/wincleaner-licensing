using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WinCleaner.Licensing
{
    #region RFC 8032 Ed25519 Signature Verifier
    public static class Ed25519
    {
        private static readonly BigInteger Q = BigInteger.Pow(2, 255) - 19;
        private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
        private static readonly BigInteger D;
        private static readonly BigInteger I;
        private static readonly Point BasePoint;

        public struct Point
        {
            public BigInteger X;
            public BigInteger Y;

            public Point(BigInteger x, BigInteger y)
            {
                X = x;
                Y = y;
            }

            public static readonly Point Zero = new Point(0, 1);
        }

        static Ed25519()
        {
            D = Mod(-121665 * Inv(121666, Q), Q);
            I = BigInteger.ModPow(2, (Q - 1) / 4, Q);

            BigInteger by = Mod(4 * Inv(5, Q), Q);
            BigInteger bx = RecoverX(by);
            BasePoint = new Point(bx, by);
        }

        private static BigInteger Mod(BigInteger x, BigInteger m)
        {
            BigInteger r = x % m;
            return r < 0 ? r + m : r;
        }

        private static BigInteger Inv(BigInteger x, BigInteger m)
        {
            return BigInteger.ModPow(Mod(x, m), m - 2, m);
        }

        private static BigInteger RecoverX(BigInteger y)
        {
            BigInteger xx = Mod((y * y - 1) * Inv(D * y * y + 1, Q), Q);
            BigInteger x = BigInteger.ModPow(xx, (Q + 3) / 8, Q);

            if (Mod(x * x - xx, Q) != 0)
            {
                x = Mod(x * I, Q);
            }

            if (x % 2 != 0)
            {
                x = Q - x;
            }

            return x;
        }

        private static Point Edwards(Point p, Point q)
        {
            BigInteger x1 = p.X, y1 = p.Y;
            BigInteger x2 = q.X, y2 = q.Y;

            BigInteger dxy = Mod(D * x1 * x2 * y1 * y2, Q);
            BigInteger x3 = Mod((x1 * y2 + x2 * y1) * Inv(1 + dxy, Q), Q);
            BigInteger y3 = Mod((y1 * y2 + x1 * x2) * Inv(1 - dxy, Q), Q);

            return new Point(x3, y3);
        }

        private static Point ScalarMult(Point p, BigInteger e)
        {
            if (e == 0) return Point.Zero;

            Point q = ScalarMult(p, e / 2);
            q = Edwards(q, q);

            if (!e.IsEven)
            {
                q = Edwards(q, p);
            }

            return q;
        }

        private static BigInteger DecodeInt(byte[] bytes, int offset, int length)
        {
            byte[] buf = new byte[length + 1];
            Array.Copy(bytes, offset, buf, 0, length);
            buf[length] = 0;
            return new BigInteger(buf);
        }

        private static Point DecodePoint(byte[] bytes, int offset)
        {
            byte[] yBytes = new byte[32];
            Array.Copy(bytes, offset, yBytes, 0, 32);

            byte sign = (byte)((yBytes[31] >> 7) & 1);
            yBytes[31] &= 0x7F;

            BigInteger y = DecodeInt(yBytes, 0, 32);
            BigInteger x = RecoverX(y);

            if ((x.IsEven ? 0 : 1) != sign)
            {
                x = Q - x;
            }

            return new Point(x, y);
        }

        private static byte[] EncodePoint(Point p)
        {
            byte[] yBytes = p.Y.ToByteArray();
            byte[] result = new byte[32];
            Array.Copy(yBytes, 0, result, 0, Math.Min(yBytes.Length, 32));

            if (!p.X.IsEven)
            {
                result[31] |= 0x80;
            }

            return result;
        }

        private static BigInteger HashToScalar(byte[] data)
        {
            using (SHA512 sha = SHA512.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                byte[] buf = new byte[hash.Length + 1];
                Array.Copy(hash, 0, buf, 0, hash.Length);
                buf[hash.Length] = 0;
                return Mod(new BigInteger(buf), L);
            }
        }

        public static bool Verify(byte[] signature, byte[] message, byte[] publicKey)
        {
            if (signature == null || signature.Length != 64) return false;
            if (publicKey == null || publicKey.Length != 32) return false;
            if (message == null) return false;

            try
            {
                Point rPoint = DecodePoint(signature, 0);
                BigInteger s = DecodeInt(signature, 32, 32);

                if (s >= L || s < 0) return false;

                Point aPoint = DecodePoint(publicKey, 0);

                byte[] kData = new byte[32 + 32 + message.Length];
                Array.Copy(signature, 0, kData, 0, 32);
                Array.Copy(publicKey, 0, kData, 32, 32);
                Array.Copy(message, 0, kData, 64, message.Length);

                BigInteger k = HashToScalar(kData);

                Point sB = ScalarMult(BasePoint, s);
                Point rPlusKA = Edwards(rPoint, ScalarMult(aPoint, k));

                Point sB8 = ScalarMult(sB, 8);
                Point rKA8 = ScalarMult(rPlusKA, 8);

                return sB8.X == rKA8.X && sB8.Y == rKA8.Y;
            }
            catch
            {
                return false;
            }
        }
    }
    #endregion

    #region Hardware ID Generation
    public static class HardwareId
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool GetVolumeInformation(
            string lpRootPathName,
            StringBuilder lpVolumeNameBuffer,
            int nVolumeNameSize,
            out uint lpVolumeSerialNumber,
            out uint lpMaximumComponentLength,
            out uint lpFileSystemFlags,
            StringBuilder lpFileSystemNameBuffer,
            int nFileSystemNameSize);

        private static string GetMachineGuid()
        {
            try
            {
                using (RegistryKey rk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey sub = rk.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    if (sub != null)
                    {
                        object val = sub.GetValue("MachineGuid");
                        if (val != null) return val.ToString().Trim();
                    }
                }
            }
            catch { }

            try
            {
                using (RegistryKey sub = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    if (sub != null)
                    {
                        object val = sub.GetValue("MachineGuid");
                        if (val != null) return val.ToString().Trim();
                    }
                }
            }
            catch { }

            return Environment.MachineName;
        }

        private static string GetVolumeSerial()
        {
            try
            {
                string drive = Path.GetPathRoot(Environment.SystemDirectory);
                if (string.IsNullOrEmpty(drive)) drive = "C:\\";

                uint serial, maxComp, flags;
                StringBuilder volName = new StringBuilder(261);
                StringBuilder fsName = new StringBuilder(261);

                if (GetVolumeInformation(drive, volName, volName.Capacity, out serial, out maxComp, out flags, fsName, fsName.Capacity))
                {
                    return serial.ToString("X8");
                }
            }
            catch { }
            return "00000000";
        }

        private static string GetCpuInfo()
        {
            try
            {
                using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (rk != null)
                    {
                        object val = rk.GetValue("ProcessorNameString");
                        if (val != null) return val.ToString().Trim();
                    }
                }
            }
            catch { }
            return Environment.ProcessorCount.ToString();
        }

        public static string GenerateHwid()
        {
            string guid = GetMachineGuid();
            string vol = GetVolumeSerial();
            string cpu = GetCpuInfo();

            string raw = string.Format("GUID={0};VOL={1};CPU={2}", guid, vol, cpu);

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                StringBuilder sb = new StringBuilder(64);
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("X2"));
                }
                return sb.ToString();
            }
        }
    }
    #endregion

    #region Anti-Debug & Anti-Tamper Security Guard
    public static class AntiDebug
    {
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess, [MarshalAs(UnmanagedType.Bool)] ref bool isDebuggerPresent);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsDebuggerPresent();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        private static readonly string[] BlacklistedTools = new string[]
        {
            "dnspy", "dnspy-x86", "de4dot", "ilspy", "x64dbg", "x32dbg", "ollydbg",
            "ida", "ida64", "idag", "idag64", "idaw", "idaw64", "ghidra",
            "cheatengine-x86_64", "cheatengine-i386", "cheatengine",
            "scylla", "scylla_x64", "scylla_x86", "charles", "fiddler",
            "httpdebuggerui", "httpdebugger", "wireshark",
            "megadumper", "extremedumper", "simpleassemblyexplorer"
        };

        private static Thread _watchdogThread;
        private static volatile bool _watchdogStarted = false;

        public static void StartWatchdog()
        {
            if (_watchdogStarted) return;
            _watchdogStarted = true;
            _watchdogThread = new Thread(WatchdogLoop)
            {
                IsBackground = true,
                Name = "SecWatchdog",
                Priority = ThreadPriority.Lowest
            };
            _watchdogThread.Start();
        }

        private static void WatchdogLoop()
        {
            Random rng = new Random();
            while (true)
            {
                try
                {
                    if (CheckDebuggerOrTools())
                    {
                        Process.GetCurrentProcess().Kill();
                        return;
                    }
                }
                catch { }

                Thread.Sleep(rng.Next(1500, 3000));
            }
        }

        public static bool CheckDebuggerOrTools()
        {
            try
            {
                if (Debugger.IsAttached || Debugger.IsLogging())
                    return true;

                if (IsDebuggerPresent())
                    return true;

                bool remotePresent = false;
                CheckRemoteDebuggerPresent(Process.GetCurrentProcess().Handle, ref remotePresent);
                if (remotePresent)
                    return true;

                Process[] procs = Process.GetProcesses();
                for (int i = 0; i < procs.Length; i++)
                {
                    try
                    {
                        string name = procs[i].ProcessName.ToLowerInvariant();
                        for (int j = 0; j < BlacklistedTools.Length; j++)
                        {
                            if (name == BlacklistedTools[j] || name.Contains(BlacklistedTools[j]))
                            {
                                return true;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }
    }
    #endregion

    #region License Info & License Guard
    public class LicenseInfo
    {
        public string LicenseKey;
        public string Hwid;
        public DateTime ExpiresAt;
        public DateTime LastCheckedAt;
        public string SignatureBase64;

        public string ToCanonicalString()
        {
            return string.Format("{0}|{1}|{2}",
                LicenseKey.Trim(),
                Hwid.Trim().ToUpperInvariant(),
                ExpiresAt.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        }
    }

    public static class LicenseGuard
    {
        private static byte[] _sessionToken = null;
        private static int _guardState = 0;
        private const int ValidMagic = 0x5A9C3B12;

        public static bool ValidateStartup()
        {
            if (AntiDebug.CheckDebuggerOrTools())
            {
                Process.GetCurrentProcess().Kill();
                return false;
            }

            bool ok = LicenseManager.Validate();
            if (ok && LicenseManager.CurrentLicense != null)
            {
                Initialize(LicenseManager.CurrentLicense);
                AntiDebug.StartWatchdog();
                return true;
            }
            return false;
        }

        public static void Initialize(LicenseInfo lic)
        {
            if (lic == null || !LicenseManager.VerifySignature(lic))
            {
                _sessionToken = null;
                _guardState = 0;
                return;
            }

            try
            {
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] sigBytes = Convert.FromBase64String(lic.SignatureBase64);
                    byte[] hwidBytes = Encoding.UTF8.GetBytes(lic.Hwid);
                    byte[] combined = new byte[sigBytes.Length + hwidBytes.Length + 8];
                    Buffer.BlockCopy(sigBytes, 0, combined, 0, sigBytes.Length);
                    Buffer.BlockCopy(hwidBytes, 0, combined, sigBytes.Length, hwidBytes.Length);
                    byte[] salt = new byte[] { 0x57, 0x69, 0x6E, 0x43, 0x6C, 0x65, 0x61, 0x6E }; // "WinClean"
                    Buffer.BlockCopy(salt, 0, combined, sigBytes.Length + hwidBytes.Length, 8);
                    _sessionToken = sha.ComputeHash(combined);
                }
                _guardState = ValidMagic;
            }
            catch
            {
                _sessionToken = null;
                _guardState = 0;
            }
        }

        public static bool IsOperational()
        {
            if (_guardState != ValidMagic || _sessionToken == null || _sessionToken.Length != 32)
                return false;

            if (LicenseManager.CurrentLicense == null)
                return false;

            if (DateTime.UtcNow > LicenseManager.CurrentLicense.ExpiresAt)
                return false;

            return true;
        }

        public static void AssertOperational()
        {
            if (AntiDebug.CheckDebuggerOrTools())
            {
                Process.GetCurrentProcess().Kill();
                throw new UnauthorizedAccessException("Security violation");
            }

            if (!IsOperational())
            {
                Process.GetCurrentProcess().Kill();
                throw new InvalidOperationException("Операция заблокирована: требуется действующая цифровая лицензия WinCleaner.");
            }
        }

        public static byte[] DecryptPayload(byte[] cipherData)
        {
            if (_sessionToken == null || _sessionToken.Length != 32 || _guardState != ValidMagic)
            {
                Process.GetCurrentProcess().Kill();
                return new byte[0];
            }

            if (!IsOperational())
            {
                Process.GetCurrentProcess().Kill();
                return new byte[0];
            }

            try
            {
                using (RijndaelManaged aes = new RijndaelManaged())
                {
                    aes.KeySize = 256;
                    aes.BlockSize = 128;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = _sessionToken;

                    byte[] iv = new byte[16];
                    byte[] actualCipher = new byte[cipherData.Length - 16];
                    Buffer.BlockCopy(cipherData, 0, iv, 0, 16);
                    Buffer.BlockCopy(cipherData, 16, actualCipher, 0, actualCipher.Length);
                    aes.IV = iv;

                    using (ICryptoTransform decryptor = aes.CreateDecryptor())
                    {
                        return decryptor.TransformFinalBlock(actualCipher, 0, actualCipher.Length);
                    }
                }
            }
            catch
            {
                Process.GetCurrentProcess().Kill();
                return new byte[0];
            }
        }
    }
    #endregion

    #region License Manager & Storage
    public static class LicenseManager
    {
        public static string ServerUrl = "https://wincleaner-license.onrender.com";
        public static string TelegramBotUrl = "https://t.me/WinCleanerLicense_Bot";
        public static readonly string PublicKeyHex = "acba32615e13044a3d15823b07ed600e9384c34b79eab06aa88f8c08fada9457";
        public static readonly TimeSpan MaxOfflineGracePeriod = TimeSpan.FromDays(7);

        public static LicenseInfo CurrentLicense { get; private set; }
        public static string LastError { get; private set; }

        static LicenseManager()
        {
            EnsureSecurityProtocols();
        }

        public static void EnsureSecurityProtocols()
        {
            try
            {
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)12288 
                                                     | (SecurityProtocolType)3072 
                                                     | (SecurityProtocolType)768 
                                                     | (SecurityProtocolType)192;
                ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            }
            catch { }
        }

        private static string GetLicenseFilePath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCleaner");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return Path.Combine(dir, "license.dat");
        }

        private static byte[] HexToBytes(string hex)
        {
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < hex.Length; i += 2)
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            return bytes;
        }

        public static bool VerifySignature(LicenseInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.SignatureBase64)) return false;

            try
            {
                byte[] pubKey = HexToBytes(PublicKeyHex);
                byte[] sig = Convert.FromBase64String(info.SignatureBase64);
                byte[] msg = Encoding.UTF8.GetBytes(info.ToCanonicalString());

                return Ed25519.Verify(sig, msg, pubKey);
            }
            catch
            {
                return false;
            }
        }

        public static bool SaveLocalLicense(LicenseInfo info)
        {
            try
            {
                string raw = string.Format("{0}\n{1}\n{2}\n{3}\n{4}",
                    info.LicenseKey,
                    info.Hwid,
                    info.ExpiresAt.ToString("o"),
                    info.LastCheckedAt.ToString("o"),
                    info.SignatureBase64);

                byte[] plain = Encoding.UTF8.GetBytes(raw);
                byte[] cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

                File.WriteAllBytes(GetLicenseFilePath(), cipher);
                CurrentLicense = info;
                LicenseGuard.Initialize(info);
                return true;
            }
            catch (Exception ex)
            {
                LastError = "Ошибка сохранения лицензии: " + ex.Message;
                return false;
            }
        }

        public static LicenseInfo LoadLocalLicense()
        {
            try
            {
                string path = GetLicenseFilePath();
                if (!File.Exists(path)) return null;

                byte[] cipher = File.ReadAllBytes(path);
                byte[] plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
                string raw = Encoding.UTF8.GetString(plain);

                string[] lines = raw.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 5) return null;

                LicenseInfo info = new LicenseInfo();
                info.LicenseKey = lines[0].Trim();
                info.Hwid = lines[1].Trim();
                info.ExpiresAt = DateTime.Parse(lines[2].Trim()).ToUniversalTime();
                info.LastCheckedAt = DateTime.Parse(lines[3].Trim()).ToUniversalTime();
                info.SignatureBase64 = lines[4].Trim();

                // Anti-Clock-Rollback Detection:
                if (DateTime.UtcNow < info.LastCheckedAt.AddMinutes(-10))
                {
                    LastError = "Обнаружен откат системного времени назад. Проверьте дату и время на компьютере.";
                    return null;
                }

                return info;
            }
            catch
            {
                return null;
            }
        }

        public static void ClearLocalLicense()
        {
            try
            {
                string path = GetLicenseFilePath();
                if (File.Exists(path)) File.Delete(path);
                CurrentLicense = null;
            }
            catch { }
        }

        public static bool Validate()
        {
            LastError = null;
            LicenseInfo lic = LoadLocalLicense();
            if (lic == null)
            {
                if (string.IsNullOrEmpty(LastError))
                    LastError = "Лицензия не найдена на этом компьютере.";
                return false;
            }

            string currentHwid = HardwareId.GenerateHwid();
            if (!string.Equals(lic.Hwid, currentHwid, StringComparison.OrdinalIgnoreCase))
            {
                LastError = "Лицензия привязана к другому оборудованию (HWID не совпадает).";
                return false;
            }

            if (!VerifySignature(lic))
            {
                LastError = "Цифровая подпись лицензии недействительна или повреждена.";
                ClearLocalLicense();
                return false;
            }

            if (DateTime.UtcNow > lic.ExpiresAt)
            {
                LastError = "Срок действия лицензии истек " + lic.ExpiresAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
                return false;
            }

            TimeSpan timeSinceCheck = DateTime.UtcNow - lic.LastCheckedAt;
            if (timeSinceCheck > TimeSpan.FromHours(12))
            {
                bool onlineOk = TryOnlineValidate(lic);
                if (!onlineOk && timeSinceCheck > MaxOfflineGracePeriod)
                {
                    LastError = "Истек период автономной работы (7 дней). Требуется подключение к интернету для проверки лицензии.";
                    return false;
                }
            }

            CurrentLicense = lic;
            LicenseGuard.Initialize(lic);
            return true;
        }

        public static bool TryOnlineValidate(LicenseInfo lic)
        {
            try
            {
                string url = ServerUrl.TrimEnd('/') + "/api/v1/validate";
                string json = string.Format("{{\"license_key\":\"{0}\",\"hwid\":\"{1}\"}}",
                    EscapeJson(lic.LicenseKey),
                    EscapeJson(lic.Hwid));

                string resp = HttpPostJson(url, json);
                if (string.IsNullOrEmpty(resp)) return false;

                string status = ExtractJsonValue(resp, "status");
                if (status != "active")
                {
                    LastError = "Лицензия отозвана или не активна на сервере: " + status;
                    ClearLocalLicense();
                    return false;
                }

                string expStr = ExtractJsonValue(resp, "expires_at");
                string sig = ExtractJsonValue(resp, "signature");

                if (!string.IsNullOrEmpty(expStr) && !string.IsNullOrEmpty(sig))
                {
                    lic.ExpiresAt = DateTime.Parse(expStr).ToUniversalTime();
                    lic.SignatureBase64 = sig;
                    lic.LastCheckedAt = DateTime.UtcNow;

                    if (VerifySignature(lic))
                    {
                        SaveLocalLicense(lic);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static bool Activate(string licenseKey)
        {
            LastError = null;
            if (string.IsNullOrEmpty(licenseKey))
            {
                LastError = "Введите ключ лицензии.";
                return false;
            }

            string hwid = HardwareId.GenerateHwid();
            string devName = Environment.MachineName;

            try
            {
                string url = ServerUrl.TrimEnd('/') + "/api/v1/activate";
                string json = string.Format("{{\"license_key\":\"{0}\",\"hwid\":\"{1}\",\"device_name\":\"{2}\"}}",
                    EscapeJson(licenseKey.Trim()),
                    EscapeJson(hwid),
                    EscapeJson(devName));

                string resp = HttpPostJson(url, json);
                if (string.IsNullOrEmpty(resp))
                {
                    LastError = "Сервер вернул пустой ответ.";
                    return false;
                }

                string status = ExtractJsonValue(resp, "status");
                if (status != "active")
                {
                    string err = ExtractJsonValue(resp, "detail");
                    if (string.IsNullOrEmpty(err)) err = ExtractJsonValue(resp, "error");
                    LastError = !string.IsNullOrEmpty(err) ? err : "Не удалось активировать лицензию (статус: " + status + ")";
                    return false;
                }

                string expStr = ExtractJsonValue(resp, "expires_at");
                string sig = ExtractJsonValue(resp, "signature");

                if (string.IsNullOrEmpty(expStr) || string.IsNullOrEmpty(sig))
                {
                    LastError = "Некорректный ответ сервера активации (отсутствуют подпись или дата).";
                    return false;
                }

                LicenseInfo lic = new LicenseInfo();
                lic.LicenseKey = licenseKey.Trim();
                lic.Hwid = hwid;
                lic.ExpiresAt = DateTime.Parse(expStr).ToUniversalTime();
                lic.LastCheckedAt = DateTime.UtcNow;
                lic.SignatureBase64 = sig;

                if (!VerifySignature(lic))
                {
                    LastError = "Цифровая подпись ответа сервера не прошла проверку!";
                    return false;
                }

                SaveLocalLicense(lic);
                CurrentLicense = lic;
                LicenseGuard.Initialize(lic);
                return true;
            }
            catch (WebException wex)
            {
                if (wex.Response != null)
                {
                    try
                    {
                        using (StreamReader r = new StreamReader(wex.Response.GetResponseStream()))
                        {
                            string errResp = r.ReadToEnd();
                            string detail = ExtractJsonValue(errResp, "detail");
                            if (!string.IsNullOrEmpty(detail))
                            {
                                LastError = detail;
                                return false;
                            }
                        }
                    }
                    catch { }
                }
                LastError = "Ошибка сети: " + wex.Message;
                return false;
            }
            catch (Exception ex)
            {
                LastError = "Ошибка активации: " + ex.Message;
                return false;
            }
        }

        private static string HttpPostJson(string url, string json)
        {
            EnsureSecurityProtocols();

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json; charset=utf-8";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;

            byte[] body = Encoding.UTF8.GetBytes(json);
            req.ContentLength = body.Length;

            using (Stream reqStream = req.GetRequestStream())
            {
                reqStream.Write(body, 0, body.Length);
            }

            using (WebResponse resp = req.GetResponse())
            using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private static string _cachedCloudPayload = null;

        public static string GetCloudPayloadJson()
        {
            if (!string.IsNullOrEmpty(_cachedCloudPayload))
                return _cachedCloudPayload;

            _cachedCloudPayload = FetchCloudPayloadJson();
            return _cachedCloudPayload;
        }

        public static string FetchCloudPayloadJson()
        {
            if (CurrentLicense == null || !VerifySignature(CurrentLicense))
                return null;

            try
            {
                EnsureSecurityProtocols();
                string url = ServerUrl.TrimEnd('/') + "/api/v1/payload";
                string json = string.Format("{{\"license_key\":\"{0}\",\"hwid\":\"{1}\"}}",
                    EscapeJson(CurrentLicense.LicenseKey.Trim()),
                    EscapeJson(CurrentLicense.Hwid.Trim()));

                string resp = HttpPostJson(url, json);
                if (string.IsNullOrEmpty(resp)) return null;

                string encB64 = ExtractJsonValue(resp, "encrypted_payload");
                string ivB64 = ExtractJsonValue(resp, "iv");
                if (string.IsNullOrEmpty(encB64) || string.IsNullOrEmpty(ivB64))
                    return null;

                byte[] cipherBytes = Convert.FromBase64String(encB64);
                byte[] ivBytes = Convert.FromBase64String(ivB64);

                byte[] fullData = new byte[ivBytes.Length + cipherBytes.Length];
                Buffer.BlockCopy(ivBytes, 0, fullData, 0, ivBytes.Length);
                Buffer.BlockCopy(cipherBytes, 0, fullData, ivBytes.Length, cipherBytes.Length);

                byte[] dec = LicenseGuard.DecryptPayload(fullData);
                if (dec != null && dec.Length > 0)
                {
                    _cachedCloudPayload = Encoding.UTF8.GetString(dec);
                    return _cachedCloudPayload;
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
            return null;
        }

        public static string ExtractJsonValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;

            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx == -1) return null;

            int colon = json.IndexOf(':', idx + pattern.Length);
            if (colon == -1) return null;

            int start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t' || json[start] == '\r' || json[start] == '\n'))
                start++;

            if (start >= json.Length) return null;

            if (json[start] == '"')
            {
                start++;
                int end = json.IndexOf('"', start);
                if (end == -1) return null;
                return json.Substring(start, end - start);
            }
            else
            {
                int end = start;
                while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']' && json[end] != '\r' && json[end] != '\n')
                    end++;
                return json.Substring(start, end - start).Trim();
            }
        }
    }
    #endregion

    #region Activation Form UI
    public class ActivationForm : Form
    {
        private TextBox txtHwid;
        private TextBox txtKey;
        private Button btnCopyHwid;
        private Button btnActivate;
        private Button btnTelegram;
        private Label lblStatus;
        private Label lblHwidHint;
        private Label lblKeyHint;

        public bool IsSuccess { get; private set; }

        public ActivationForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "WinCleaner — Активация лицензии";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ClientSize = new Size(520, 310);
            this.BackColor = Color.FromArgb(10, 12, 24);
            this.ForeColor = Color.FromArgb(240, 240, 245);
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Label lblTitle = new Label();
            lblTitle.Text = "Требуется активация WinCleaner";
            lblTitle.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
            lblTitle.ForeColor = Color.FromArgb(140, 185, 255);
            lblTitle.Location = new Point(20, 16);
            lblTitle.Size = new Size(480, 28);
            this.Controls.Add(lblTitle);

            lblHwidHint = new Label();
            lblHwidHint.Text = "Ваш аппаратный идентификатор (HWID):";
            lblHwidHint.Location = new Point(20, 56);
            lblHwidHint.Size = new Size(480, 20);
            lblHwidHint.ForeColor = Color.FromArgb(170, 175, 195);
            this.Controls.Add(lblHwidHint);

            txtHwid = new TextBox();
            txtHwid.ReadOnly = true;
            txtHwid.Location = new Point(20, 80);
            txtHwid.Size = new Size(380, 26);
            txtHwid.BackColor = Color.FromArgb(18, 20, 36);
            txtHwid.ForeColor = Color.FromArgb(150, 225, 215);
            txtHwid.Font = new Font("Consolas", 9f);
            txtHwid.Text = HardwareId.GenerateHwid();
            this.Controls.Add(txtHwid);

            btnCopyHwid = new Button();
            btnCopyHwid.Text = "📋 Скопировать";
            btnCopyHwid.Location = new Point(408, 79);
            btnCopyHwid.Size = new Size(92, 28);
            btnCopyHwid.FlatStyle = FlatStyle.Flat;
            btnCopyHwid.BackColor = Color.FromArgb(35, 40, 65);
            btnCopyHwid.ForeColor = Color.FromArgb(220, 225, 245);
            btnCopyHwid.Cursor = Cursors.Hand;
            btnCopyHwid.Click += delegate {
                try { Clipboard.SetText(txtHwid.Text); } catch { }
                MessageBox.Show(this, "HWID скопирован в буфер обмена!", "WinCleaner", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            this.Controls.Add(btnCopyHwid);

            lblKeyHint = new Label();
            lblKeyHint.Text = "Введите лицензионный ключ (WINC-XXXX-XXXX-XXXX):";
            lblKeyHint.Location = new Point(20, 120);
            lblKeyHint.Size = new Size(480, 20);
            lblKeyHint.ForeColor = Color.FromArgb(170, 175, 195);
            this.Controls.Add(lblKeyHint);

            txtKey = new TextBox();
            txtKey.Location = new Point(20, 144);
            txtKey.Size = new Size(480, 26);
            txtKey.BackColor = Color.FromArgb(18, 20, 36);
            txtKey.ForeColor = Color.White;
            txtKey.Font = new Font("Consolas", 10.5f, FontStyle.Bold);
            this.Controls.Add(txtKey);

            lblStatus = new Label();
            lblStatus.Location = new Point(20, 180);
            lblStatus.Size = new Size(480, 42);
            lblStatus.ForeColor = Color.FromArgb(250, 130, 140);
            if (!string.IsNullOrEmpty(LicenseManager.LastError))
            {
                lblStatus.Text = LicenseManager.LastError;
            }
            this.Controls.Add(lblStatus);

            btnTelegram = new Button();
            btnTelegram.Text = "✈ Купить в Telegram";
            btnTelegram.Location = new Point(20, 245);
            btnTelegram.Size = new Size(180, 36);
            btnTelegram.FlatStyle = FlatStyle.Flat;
            btnTelegram.BackColor = Color.FromArgb(35, 40, 65);
            btnTelegram.ForeColor = Color.FromArgb(140, 185, 255);
            btnTelegram.Cursor = Cursors.Hand;
            btnTelegram.Click += delegate {
                try { Process.Start(LicenseManager.TelegramBotUrl); } catch { }
            };
            this.Controls.Add(btnTelegram);

            btnActivate = new Button();
            btnActivate.Text = "⚡ Активировать";
            btnActivate.Location = new Point(340, 245);
            btnActivate.Size = new Size(160, 36);
            btnActivate.FlatStyle = FlatStyle.Flat;
            btnActivate.BackColor = Color.FromArgb(140, 185, 255);
            btnActivate.ForeColor = Color.FromArgb(10, 12, 24);
            btnActivate.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            btnActivate.Cursor = Cursors.Hand;
            btnActivate.Click += OnActivateClick;
            this.Controls.Add(btnActivate);
        }

        private void OnActivateClick(object sender, EventArgs e)
        {
            string key = txtKey.Text.Trim();
            if (string.IsNullOrEmpty(key))
            {
                lblStatus.ForeColor = Color.FromArgb(250, 130, 140);
                lblStatus.Text = "Пожалуйста, введите лицензионный ключ.";
                return;
            }

            btnActivate.Enabled = false;
            btnActivate.Text = "Проверка...";
            lblStatus.ForeColor = Color.FromArgb(140, 185, 255);
            lblStatus.Text = "Проверка лицензии на сервере...";
            this.Refresh();

            bool ok = LicenseManager.Activate(key);
            if (ok)
            {
                lblStatus.ForeColor = Color.FromArgb(130, 230, 150);
                lblStatus.Text = "Лицензия успешно активирована до: " + LicenseManager.CurrentLicense.ExpiresAt.ToLocalTime().ToString("dd.MM.yyyy");
                IsSuccess = true;
                MessageBox.Show(this, "Лицензия успешно активирована!\n\nДействует до: " + LicenseManager.CurrentLicense.ExpiresAt.ToLocalTime().ToString("dd.MM.yyyy"), "Успех", MessageBoxButtons.OK, MessageBoxIcon.Information);
                this.DialogResult = DialogResult.OK;
                this.Close();
            }
            else
            {
                lblStatus.ForeColor = Color.FromArgb(250, 130, 140);
                lblStatus.Text = LicenseManager.LastError ?? "Ошибка активации ключа.";
                btnActivate.Enabled = true;
                btnActivate.Text = "⚡ Активировать";
            }
        }
    }
    #endregion
}
