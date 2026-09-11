using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace MagiCorp.RememberServerPassword
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "magicorp.valheim.rememberserverpassword";
        public const string Name = "Remember Server Password";
        public const string Version = "1.0.0";

        internal static Plugin Instance;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> AutoSubmit;
        internal static ConfigEntry<bool> RememberPasswords;
        internal static ConfigEntry<bool> ForgetAllPasswords;
        internal static ConfigEntry<bool> VerboseLogging;

        private static readonly Dictionary<string, string> Passwords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("MagiCorp.RememberServerPassword.v1");
        private static string StorePath;
        private static string KeyPath;
        private static string PendingServerKey;
        private static string PendingPassword;
        private static string AutoAttemptServerKey;

        private void Awake()
        {
            Instance = this;
            Enabled = Config.Bind("General", "Enabled", true, "Enable remembered server passwords.");
            AutoSubmit = Config.Bind("General", "AutoSubmit", true, "Automatically submit a remembered password when a known server requests one.");
            RememberPasswords = Config.Bind("General", "RememberPasswords", true, "Remember manually entered passwords after a successful connection.");
            ForgetAllPasswords = Config.Bind("General", "ForgetAllPasswords", false, "Set true and restart the game to erase all remembered passwords. It resets to false automatically.");
            VerboseLogging = Config.Bind("General", "VerboseLogging", false, "Log server identity matching details. Passwords are never logged.");

            StorePath = Path.Combine(Paths.ConfigPath, "MagiCorp.RememberServerPassword.dat");
            KeyPath = Path.Combine(Paths.ConfigPath, "MagiCorp.RememberServerPassword.key");

            if (ForgetAllPasswords.Value)
            {
                TryDelete(StorePath);
                TryDelete(KeyPath);
                ForgetAllPasswords.Value = false;
                Config.Save();
                Logger.LogInfo("All remembered server passwords were erased.");
            }

            LoadStore();
            new Harmony(Guid).PatchAll();
            Logger.LogInfo(Name + " " + Version + " loaded. Remembered servers: " + Passwords.Count);
        }

        internal static string GetCurrentServerKey()
        {
            try
            {
                string host = AccessTools.Field(typeof(ZNet), "m_serverHost").GetValue(null) as string;
                int port = (int)AccessTools.Field(typeof(ZNet), "m_serverHostPort").GetValue(null);
                ulong steamId = (ulong)AccessTools.Field(typeof(ZNet), "m_serverSteamID").GetValue(null);
                string playFabId = AccessTools.Field(typeof(ZNet), "m_serverPlayFabPlayerId").GetValue(null) as string;

                if (!string.IsNullOrWhiteSpace(host) && port > 0)
                    return "host/" + host.Trim().ToLowerInvariant() + ":" + port;

                if (steamId != 0UL)
                    return "steam/" + steamId;

                if (!string.IsNullOrWhiteSpace(playFabId))
                    return "playfab/" + playFabId.Trim().ToLowerInvariant();
            }
            catch (Exception ex)
            {
                LogWarning("Could not identify current server: " + ex.Message);
            }

            return null;
        }

        internal static bool TryGetPassword(string serverKey, out string password)
        {
            password = null;
            return Enabled.Value && AutoSubmit.Value && !string.IsNullOrEmpty(serverKey) &&
                   Passwords.TryGetValue(serverKey, out password) && !string.IsNullOrEmpty(password);
        }

        internal static void CapturePendingPassword(string serverKey, string password)
        {
            if (!Enabled.Value || !RememberPasswords.Value || string.IsNullOrEmpty(serverKey) || string.IsNullOrEmpty(password))
                return;

            PendingServerKey = serverKey;
            PendingPassword = password;
            if (VerboseLogging.Value)
                LogInfo("Password entry pending validation for " + serverKey);
        }

        internal static void MarkAutoAttempt(string serverKey)
        {
            AutoAttemptServerKey = serverKey;
            if (VerboseLogging.Value)
                LogInfo("Auto-submitting remembered password for " + serverKey);
        }

        internal static void CommitPendingPassword()
        {
            AutoAttemptServerKey = null;
            FejdStartup.ServerPassword = string.Empty;
            if (string.IsNullOrEmpty(PendingServerKey) || string.IsNullOrEmpty(PendingPassword))
                return;

            bool changed = !Passwords.TryGetValue(PendingServerKey, out string existing) || existing != PendingPassword;
            Passwords[PendingServerKey] = PendingPassword;
            if (changed)
            {
                SaveStore();
                LogInfo("Remembered password for " + PendingServerKey);
            }

            PendingServerKey = null;
            PendingPassword = null;
        }

        internal static void HandleWrongPassword()
        {
            string serverKey = GetCurrentServerKey();
            PendingServerKey = null;
            PendingPassword = null;

            if (!string.IsNullOrEmpty(AutoAttemptServerKey) && string.Equals(AutoAttemptServerKey, serverKey, StringComparison.OrdinalIgnoreCase))
            {
                if (Passwords.Remove(serverKey))
                {
                    SaveStore();
                    LogWarning("Stored password was rejected and removed for " + serverKey + ". The next join will prompt normally.");
                }
            }

            AutoAttemptServerKey = null;
            FejdStartup.ServerPassword = string.Empty;
        }

        internal static void ClearTransientState()
        {
            PendingServerKey = null;
            PendingPassword = null;
            AutoAttemptServerKey = null;
        }

        private static void LoadStore()
        {
            Passwords.Clear();
            if (!File.Exists(StorePath))
                return;

            try
            {
                foreach (string line in File.ReadAllLines(StorePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string[] parts = line.Split(new[] { '\t' }, 2);
                    if (parts.Length != 2) continue;

                    string serverKey = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
                    string password = Decrypt(parts[1]);
                    if (!string.IsNullOrEmpty(serverKey) && password != null)
                        Passwords[serverKey] = password;
                }
            }
            catch (Exception ex)
            {
                LogWarning("Could not read password store: " + ex.Message);
            }
        }

        private static void SaveStore()
        {
            try
            {
                var lines = new List<string>();
                foreach (KeyValuePair<string, string> pair in Passwords)
                {
                    string key = Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Key));
                    lines.Add(key + "\t" + Encrypt(pair.Value));
                }
                File.WriteAllLines(StorePath, lines.ToArray());
            }
            catch (Exception ex)
            {
                LogError("Could not save password store: " + ex.Message);
            }
        }

        private static string Encrypt(string value)
        {
            byte[] clear = Encoding.UTF8.GetBytes(value);
            try
            {
                byte[] encrypted = ProtectedData.Protect(clear, DpapiEntropy, DataProtectionScope.CurrentUser);
                return "D:" + Convert.ToBase64String(encrypted);
            }
            catch (PlatformNotSupportedException)
            {
                return EncryptPortable(clear);
            }
            catch (CryptographicException)
            {
                return EncryptPortable(clear);
            }
        }

        private static string Decrypt(string value)
        {
            if (value.StartsWith("D:", StringComparison.Ordinal))
            {
                byte[] encrypted = Convert.FromBase64String(value.Substring(2));
                try
                {
                    return Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, DpapiEntropy, DataProtectionScope.CurrentUser));
                }
                catch (PlatformNotSupportedException)
                {
                    return null;
                }
                catch (CryptographicException)
                {
                    return null;
                }
            }

            if (value.StartsWith("A:", StringComparison.Ordinal))
                return DecryptPortable(value.Substring(2));

            return null;
        }

        private static string EncryptPortable(byte[] clear)
        {
            byte[] key = GetOrCreatePortableKey();
            using (Aes aes = Aes.Create())
            {
                aes.Key = key;
                aes.GenerateIV();
                using (ICryptoTransform encryptor = aes.CreateEncryptor())
                {
                    byte[] cipher = encryptor.TransformFinalBlock(clear, 0, clear.Length);
                    byte[] payload = new byte[aes.IV.Length + cipher.Length];
                    Buffer.BlockCopy(aes.IV, 0, payload, 0, aes.IV.Length);
                    Buffer.BlockCopy(cipher, 0, payload, aes.IV.Length, cipher.Length);
                    return "A:" + Convert.ToBase64String(payload);
                }
            }
        }

        private static string DecryptPortable(string value)
        {
            try
            {
                byte[] key = GetOrCreatePortableKey();
                byte[] payload = Convert.FromBase64String(value);
                using (Aes aes = Aes.Create())
                {
                    int ivLength = aes.BlockSize / 8;
                    if (payload.Length <= ivLength) return null;
                    byte[] iv = new byte[ivLength];
                    byte[] cipher = new byte[payload.Length - ivLength];
                    Buffer.BlockCopy(payload, 0, iv, 0, ivLength);
                    Buffer.BlockCopy(payload, ivLength, cipher, 0, cipher.Length);
                    aes.Key = key;
                    aes.IV = iv;
                    using (ICryptoTransform decryptor = aes.CreateDecryptor())
                    {
                        byte[] clear = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
                        return Encoding.UTF8.GetString(clear);
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static byte[] GetOrCreatePortableKey()
        {
            if (File.Exists(KeyPath))
                return Convert.FromBase64String(File.ReadAllText(KeyPath).Trim());

            byte[] key = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                rng.GetBytes(key);
            File.WriteAllText(KeyPath, Convert.ToBase64String(key));
            LogWarning("OS protected storage was unavailable. Using a per-install AES key in the BepInEx config directory instead.");
            return key;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { LogWarning("Could not delete " + path + ": " + ex.Message); }
        }

        private static void LogInfo(string message) { if (Instance != null) Instance.Logger.LogInfo(message); }
        private static void LogWarning(string message) { if (Instance != null) Instance.Logger.LogWarning(message); }
        private static void LogError(string message) { if (Instance != null) Instance.Logger.LogError(message); }
    }

    [HarmonyPatch(typeof(ZNet), "RPC_ClientHandshake")]
    internal static class ClientHandshakePatch
    {
        private static void Prefix()
        {
            string serverKey = Plugin.GetCurrentServerKey();
            if (Plugin.TryGetPassword(serverKey, out string password))
            {
                FejdStartup.ServerPassword = password;
                Plugin.MarkAutoAttempt(serverKey);
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), "OnPasswordEntered")]
    internal static class PasswordEnteredPatch
    {
        private static void Prefix(string __0)
        {
            Plugin.CapturePendingPassword(Plugin.GetCurrentServerKey(), __0);
        }
    }

    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
    internal static class PeerInfoPatch
    {
        private static void Postfix(ZNet __instance)
        {
            if (!__instance.IsServer())
                Plugin.CommitPendingPassword();
        }
    }

    [HarmonyPatch(typeof(ZNet), "RPC_Error")]
    internal static class RpcErrorPatch
    {
        private static void Postfix(int __1)
        {
            if ((ZNet.ConnectionStatus)__1 == ZNet.ConnectionStatus.ErrorPassword)
                Plugin.HandleWrongPassword();
        }
    }

    [HarmonyPatch(typeof(ZNet), "ResetServerHost")]
    internal static class ResetServerHostPatch
    {
        private static void Prefix()
        {
            Plugin.ClearTransientState();
        }
    }
}
