using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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
            BigInteger xx = Mod((y * y - 1) * Inv(D * y * y + 1, Q), Q);
            BigInteger x = BigInteger.ModPow(xx, (Q + 3) / 8, Q);

            if (Mod(x * x - xx, Q) != 0)
            {
                x = Mod(x * I, Q);
            }

            if (Mod(x, 2) != sign)
            {
                x = Q - x;
            }

            return new Point(x, y);
        }

        private static BigInteger HashToBigInt(byte[] data)
        {
            using (SHA512 sha512 = SHA512.Create())
            {
                byte[] hash = sha512.ComputeHash(data);
                byte[] buf = new byte[hash.Length + 1];
                Array.Copy(hash, 0, buf, 0, hash.Length);
                buf[hash.Length] = 0;
                return new BigInteger(buf);
            }
        }

        public static bool Verify(byte[] signature, byte[] message, byte[] publicKey)
        {
            if (signature == null || signature.Length != 64) return false;
            if (publicKey == null || publicKey.Length != 32) return false;
            if (message == null) return false;

            try
            {
                BigInteger s = DecodeInt(signature, 32, 32);
                if (s >= L) return false;

                Point a = DecodePoint(publicKey, 0);
                Point r = DecodePoint(signature, 0);

                byte[] kInput = new byte[32 + 32 + message.Length];
                Array.Copy(signature, 0, kInput, 0, 32);
                Array.Copy(publicKey, 0, kInput, 32, 32);
                Array.Copy(message, 0, kInput, 64, message.Length);

                BigInteger k = Mod(HashToBigInt(kInput), L);

                Point p1 = ScalarMult(BasePoint, s);
                Point p2 = Edwards(r, ScalarMult(a, k));

                return p1.X == p2.X && p1.Y == p2.Y;
            }
            catch
            {
                return false;
            }
        }
    }
    #endregion

    #region Hardware ID Generator
    public static class HardwareId
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GetVolumeInformation(
            string rootPathName,
            StringBuilder volumeNameBuffer,
            int volumeNameSize,
            out uint volumeSerialNumber,
            out uint maximumComponentLength,
            out uint fileSystemFlags,
            StringBuilder fileSystemNameBuffer,
            int nFileSystemNameSize);

        public static string GetVolumeSerial()
        {
            try
            {
                string sysDrive = Path.GetPathRoot(Environment.SystemDirectory);
                if (string.IsNullOrEmpty(sysDrive)) sysDrive = "C:\\";

                uint serialNumber;
                uint maxCompLen;
                uint flags;
                if (GetVolumeInformation(sysDrive, null, 0, out serialNumber, out maxCompLen, out flags, null, 0))
                {
                    return serialNumber.ToString("X8");
                }
            }
            catch { }
            return "00000000";
        }

        public static string GetMachineGuid()
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
                using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    if (rk != null)
                    {
                        object val = rk.GetValue("MachineGuid");
                        if (val != null) return val.ToString().Trim();
                    }
                }
            }
            catch { }

            return "NO-GUID";
        }

        public static string GetCpuInfo()
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

    #region License Manager & Storage
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

    public static class LicenseManager
    {
        // Default Server Endpoint - can be customized via environment or config
        public static string ServerUrl = "https://wincleaner-license.onrender.com";

        // Telegram Bot link for purchase / management
        public static string TelegramBotUrl = "https://t.me/WinCleanerLicense_Bot";

        // Server Ed25519 Public Key (32 bytes hex)
        public static readonly string PublicKeyHex = "acba32615e13044a3d15823b07ed600e9384c34b79eab06aa88f8c08fada9457";

        // Maximum offline duration permitted without contacting the server
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
                // Explicitly enable TLS 1.3 (12288), TLS 1.2 (3072), TLS 1.1 (768), TLS 1.0 (192)
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

            // Check if online validation is needed (grace period expired or opportunistic update)
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

                // Simple JSON parser for expected response fields
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
            catch
            {
                // Network unavailable - keep offline grace state
            }
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
            this.BackColor = Color.FromArgb(24, 24, 37); // Dark theme matching WinCleaner
            this.ForeColor = Color.FromArgb(205, 214, 244);
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            // Title
            Label lblTitle = new Label();
            lblTitle.Text = "Требуется активация WinCleaner";
            lblTitle.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
            lblTitle.ForeColor = Color.FromArgb(137, 180, 250);
            lblTitle.Location = new Point(20, 16);
            lblTitle.Size = new Size(480, 28);
            this.Controls.Add(lblTitle);

            // HWID Label
            lblHwidHint = new Label();
            lblHwidHint.Text = "Ваш аппаратный идентификатор (HWID):";
            lblHwidHint.Location = new Point(20, 56);
            lblHwidHint.Size = new Size(480, 20);
            lblHwidHint.ForeColor = Color.FromArgb(166, 173, 200);
            this.Controls.Add(lblHwidHint);

            // HWID TextBox
            txtHwid = new TextBox();
            txtHwid.ReadOnly = true;
            txtHwid.Location = new Point(20, 80);
            txtHwid.Size = new Size(380, 26);
            txtHwid.BackColor = Color.FromArgb(17, 17, 27);
            txtHwid.ForeColor = Color.FromArgb(148, 226, 213);
            txtHwid.Font = new Font("Consolas", 9f);
            txtHwid.Text = HardwareId.GenerateHwid();
            this.Controls.Add(txtHwid);

            // Copy HWID Button
            btnCopyHwid = new Button();
            btnCopyHwid.Text = "📋 Копировать";
            btnCopyHwid.Location = new Point(408, 79);
            btnCopyHwid.Size = new Size(92, 28);
            btnCopyHwid.FlatStyle = FlatStyle.Flat;
            btnCopyHwid.BackColor = Color.FromArgb(49, 50, 68);
            btnCopyHwid.ForeColor = Color.FromArgb(205, 214, 244);
            btnCopyHwid.Cursor = Cursors.Hand;
            btnCopyHwid.Click += delegate {
                Clipboard.SetText(txtHwid.Text);
                MessageBox.Show(this, "HWID скопирован в буфер обмена!", "WinCleaner", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            this.Controls.Add(btnCopyHwid);

            // License Key Label
            lblKeyHint = new Label();
            lblKeyHint.Text = "Введите лицензионный ключ (WINC-XXXX-XXXX-XXXX):";
            lblKeyHint.Location = new Point(20, 120);
            lblKeyHint.Size = new Size(480, 20);
            lblKeyHint.ForeColor = Color.FromArgb(166, 173, 200);
            this.Controls.Add(lblKeyHint);

            // License Key TextBox
            txtKey = new TextBox();
            txtKey.Location = new Point(20, 144);
            txtKey.Size = new Size(480, 26);
            txtKey.BackColor = Color.FromArgb(17, 17, 27);
            txtKey.ForeColor = Color.White;
            txtKey.Font = new Font("Consolas", 10.5f, FontStyle.Bold);
            this.Controls.Add(txtKey);

            // Status Label
            lblStatus = new Label();
            lblStatus.Location = new Point(20, 180);
            lblStatus.Size = new Size(480, 42);
            lblStatus.ForeColor = Color.FromArgb(243, 139, 168);
            if (!string.IsNullOrEmpty(LicenseManager.LastError))
            {
                lblStatus.Text = LicenseManager.LastError;
            }
            this.Controls.Add(lblStatus);

            // Telegram Button
            btnTelegram = new Button();
            btnTelegram.Text = "✈ Купить в Telegram";
            btnTelegram.Location = new Point(20, 245);
            btnTelegram.Size = new Size(180, 36);
            btnTelegram.FlatStyle = FlatStyle.Flat;
            btnTelegram.BackColor = Color.FromArgb(49, 50, 68);
            btnTelegram.ForeColor = Color.FromArgb(137, 180, 250);
            btnTelegram.Cursor = Cursors.Hand;
            btnTelegram.Click += delegate {
                try { System.Diagnostics.Process.Start(LicenseManager.TelegramBotUrl); } catch { }
            };
            this.Controls.Add(btnTelegram);

            // Activate Button
            btnActivate = new Button();
            btnActivate.Text = "⚡ Активировать";
            btnActivate.Location = new Point(340, 245);
            btnActivate.Size = new Size(160, 36);
            btnActivate.FlatStyle = FlatStyle.Flat;
            btnActivate.BackColor = Color.FromArgb(137, 180, 250);
            btnActivate.ForeColor = Color.FromArgb(17, 17, 27);
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
                lblStatus.ForeColor = Color.FromArgb(243, 139, 168);
                lblStatus.Text = "Пожалуйста, введите ключ.";
                return;
            }

            btnActivate.Enabled = false;
            btnActivate.Text = "Проверка...";
            lblStatus.ForeColor = Color.FromArgb(137, 180, 250);
            lblStatus.Text = "Связь с сервером лицензирования...";
            this.Refresh();

            bool ok = LicenseManager.Activate(key);
            if (ok)
            {
                lblStatus.ForeColor = Color.FromArgb(166, 227, 161);
                lblStatus.Text = "Лицензия успешно активирована до: " + LicenseManager.CurrentLicense.ExpiresAt.ToLocalTime().ToString("dd.MM.yyyy");
                IsSuccess = true;
                MessageBox.Show(this, "Лицензия успешно активирована!\n\nДействует до: " + LicenseManager.CurrentLicense.ExpiresAt.ToLocalTime().ToString("dd.MM.yyyy"), "Успех", MessageBoxButtons.OK, MessageBoxIcon.Information);
                this.DialogResult = DialogResult.OK;
                this.Close();
            }
            else
            {
                lblStatus.ForeColor = Color.FromArgb(243, 139, 168);
                lblStatus.Text = LicenseManager.LastError ?? "Ошибка активации ключа.";
                btnActivate.Enabled = true;
                btnActivate.Text = "⚡ Активировать";
            }
        }
    }
    #endregion
}
