using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MagiCorp.RememberServerPassword
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "magicorp.valheim.rememberserverpassword";
        public const string Name = "Remember Server Password";
        public const string Version = "1.1.1";
        private const string QuickConnectObjectName = "MagiCorp_RememberServerPassword_QuickConnect";

        internal static Plugin Instance;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> AutoSubmit;
        internal static ConfigEntry<bool> RememberPasswords;
        internal static ConfigEntry<bool> ShowQuickConnectButton;
        internal static ConfigEntry<string> LastServer;
        internal static ConfigEntry<bool> ForgetAllPasswords;
        internal static ConfigEntry<bool> ForgetLastServer;
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
            ShowQuickConnectButton = Config.Bind("Quick Connect", "ShowQuickConnectButton", true, "Add a Quick Connect button to the Valheim main menu for the last successfully joined server.");
            LastServer = Config.Bind("Quick Connect", "LastServer", "", "Last successfully joined server identity. Managed automatically, but may be cleared manually.");
            ForgetAllPasswords = Config.Bind("Maintenance", "ForgetAllPasswords", false, "Set true and restart the game to erase all remembered passwords. It resets to false automatically.");
            ForgetLastServer = Config.Bind("Maintenance", "ForgetLastServer", false, "Set true and restart the game to clear the Quick Connect destination. It resets to false automatically.");
            VerboseLogging = Config.Bind("General", "VerboseLogging", false, "Log server identity matching details. Passwords are never logged.");

            StorePath = Path.Combine(Paths.ConfigPath, "MagiCorp.RememberServerPassword.dat");
            KeyPath = Path.Combine(Paths.ConfigPath, "MagiCorp.RememberServerPassword.key");

            bool configChanged = false;
            if (ForgetAllPasswords.Value)
            {
                Passwords.Clear();
                TryDelete(StorePath);
                TryDelete(KeyPath);
                ForgetAllPasswords.Value = false;
                configChanged = true;
                Logger.LogInfo("All remembered server passwords were erased.");
            }

            if (ForgetLastServer.Value)
            {
                LastServer.Value = "";
                ForgetLastServer.Value = false;
                configChanged = true;
                Logger.LogInfo("Quick Connect last server was cleared.");
            }

            if (configChanged)
                Config.Save();

            LoadStore();
            new Harmony(Guid).PatchAll();
            Logger.LogInfo(Name + " " + Version + " loaded. Remembered servers: " + Passwords.Count);
        }

        internal static string GetCurrentServerKey()
        {
            try
            {
                string host = AccessTools.Field(typeof(ZNet), "m_serverHost")?.GetValue(null) as string;
                object portObject = AccessTools.Field(typeof(ZNet), "m_serverHostPort")?.GetValue(null);
                object steamObject = AccessTools.Field(typeof(ZNet), "m_serverSteamID")?.GetValue(null);
                string playFabId = AccessTools.Field(typeof(ZNet), "m_serverPlayFabPlayerId")?.GetValue(null) as string;

                int port = portObject is int p ? p : 0;
                ulong steamId = steamObject is ulong s ? s : 0UL;

                if (!string.IsNullOrWhiteSpace(host) && port > 0)
                    return FormatHostKey(host, port);

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

        private static string FormatHostKey(string host, int port)
        {
            host = host.Trim().ToLowerInvariant();
            if (host.IndexOf(':') >= 0 && !(host.StartsWith("[") && host.EndsWith("]")))
                host = "[" + host + "]";
            return "host/" + host + ":" + port;
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

        internal static void RecordSuccessfulConnection()
        {
            string serverKey = GetCurrentServerKey();
            if (!string.IsNullOrEmpty(serverKey) && LastServer.Value != serverKey)
            {
                LastServer.Value = serverKey;
                Instance.Config.Save();
                if (VerboseLogging.Value)
                    LogInfo("Quick Connect destination updated to " + serverKey);
            }

            CommitPendingPassword();
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

        internal static void EnsureQuickConnectButton(FejdStartup startup)
        {
            if (!ShowQuickConnectButton.Value || startup == null)
                return;

            try
            {
                GameObject menuList = AccessTools.Field(typeof(FejdStartup), "m_menuList")?.GetValue(startup) as GameObject;
                if (menuList == null)
                    return;

                Button quickButton = null;
                Button[] allButtons = menuList.GetComponentsInChildren<Button>(true);
                Button template = null;
                foreach (Button candidate in allButtons)
                {
                    if (candidate == null) continue;
                    if (candidate.gameObject.name == QuickConnectObjectName)
                        quickButton = candidate;
                    else if (template == null && candidate.gameObject.activeSelf)
                        template = candidate;
                }

                if (quickButton == null)
                {
                    if (template == null)
                    {
                        LogWarning("Could not add Quick Connect button because the main menu button template was not found.");
                        return;
                    }

                    GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
                    clone.name = QuickConnectObjectName;
                    quickButton = clone.GetComponent<Button>();
                    if (quickButton == null)
                    {
                        UnityEngine.Object.Destroy(clone);
                        return;
                    }

                    quickButton.onClick.RemoveAllListeners();
                    quickButton.onClick.AddListener(() => QuickConnect(startup));
                    PlaceQuickConnectButton(quickButton, template);
                }

                bool available = TryBuildJoinData(LastServer.Value, out _);
                quickButton.interactable = available;
                TMP_Text text = quickButton.GetComponentInChildren<TMP_Text>(true);
                if (text != null)
                    text.text = available ? "Quick Connect: " + DisplayServer(LastServer.Value) : "Quick Connect: No recent server";
                if (available)
                    LinkQuickConnectNavigation(startup, quickButton);
            }
            catch (Exception ex)
            {
                LogWarning("Could not create Quick Connect button: " + ex.Message);
            }
        }

        private static void PlaceQuickConnectButton(Button button, Button template)
        {
            RectTransform rect = button.transform as RectTransform;
            RectTransform parent = button.transform.parent as RectTransform;
            if (rect == null || parent == null)
                return;

            LayoutGroup layout = parent.GetComponent<LayoutGroup>();
            if (layout != null)
            {
                button.transform.SetSiblingIndex(Math.Min(template.transform.GetSiblingIndex() + 1, parent.childCount - 1));
                LayoutRebuilder.ForceRebuildLayoutImmediate(parent);
                return;
            }

            float lowestY = template.transform is RectTransform templateRect ? templateRect.anchoredPosition.y : rect.anchoredPosition.y;
            float height = Math.Max(rect.rect.height, 35f);
            foreach (Button siblingButton in parent.GetComponentsInChildren<Button>(true))
            {
                if (siblingButton == button || siblingButton.transform.parent != parent)
                    continue;
                if (siblingButton.transform is RectTransform siblingRect)
                    lowestY = Math.Min(lowestY, siblingRect.anchoredPosition.y);
            }

            rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, lowestY - height - 8f);
        }

        // The main menu buttons form an explicit up/down navigation chain, and FejdStartup caches
        // m_menuButtons in Awake for arrow-key navigation. A cloned button is in neither, so the
        // gamepad and arrow keys skip it. Splice it in after the button visually above it.
        private static void LinkQuickConnectNavigation(FejdStartup startup, Button button)
        {
            var buttonsField = AccessTools.Field(typeof(FejdStartup), "m_menuButtons");
            Button[] menuButtons = buttonsField?.GetValue(startup) as Button[];
            if (menuButtons == null || Array.IndexOf(menuButtons, button) >= 0)
                return;

            float buttonY = button.transform.position.y;
            int aboveIndex = -1;
            for (int i = 0; i < menuButtons.Length; i++)
            {
                if (menuButtons[i] == null)
                    continue;
                float y = menuButtons[i].transform.position.y;
                if (y > buttonY && (aboveIndex < 0 || y < menuButtons[aboveIndex].transform.position.y))
                    aboveIndex = i;
            }
            Button above = aboveIndex >= 0 ? menuButtons[aboveIndex] : null;

            var ordered = new List<Button>(menuButtons);
            ordered.Insert(aboveIndex + 1, button);
            buttonsField.SetValue(startup, ordered.ToArray());

            Navigation navigation = button.navigation;
            if (navigation.mode != Navigation.Mode.Explicit)
                return;

            // Take over whatever the button above pointed down to, so a wrap-around chain stays intact.
            Selectable next = above != null
                ? above.navigation.selectOnDown
                : (aboveIndex + 2 < ordered.Count ? ordered[aboveIndex + 2] : null);
            navigation.selectOnUp = above;
            navigation.selectOnDown = next;
            button.navigation = navigation;

            if (above != null)
            {
                Navigation aboveNavigation = above.navigation;
                aboveNavigation.selectOnDown = button;
                above.navigation = aboveNavigation;
            }
            if (next != null && next.navigation.selectOnUp == above)
            {
                Navigation nextNavigation = next.navigation;
                nextNavigation.selectOnUp = button;
                next.navigation = nextNavigation;
            }
        }

        private static void QuickConnect(FejdStartup startup)
        {
            if (!TryBuildJoinData(LastServer.Value, out ServerJoinData joinData))
            {
                LogWarning("Quick Connect has no valid last server.");
                return;
            }

            try
            {
                FejdStartup.ServerPassword = string.Empty;
                AccessTools.Method(typeof(FejdStartup), "ProceedJoinRequest")?.Invoke(startup, new object[] { joinData });
                LogInfo("Quick Connect requested for " + DisplayServer(LastServer.Value));
            }
            catch (Exception ex)
            {
                LogError("Quick Connect failed: " + ex.GetBaseException().Message);
            }
        }

        private static bool TryBuildJoinData(string serverKey, out ServerJoinData joinData)
        {
            joinData = ServerJoinData.None;
            if (string.IsNullOrWhiteSpace(serverKey))
                return false;

            try
            {
                if (serverKey.StartsWith("steam/", StringComparison.OrdinalIgnoreCase))
                {
                    if (!ulong.TryParse(serverKey.Substring(6), out ulong steamId) || steamId == 0UL)
                        return false;
                    joinData = new ServerJoinData(new ServerJoinDataSteamUser(steamId));
                    return joinData.IsValid;
                }

                if (serverKey.StartsWith("playfab/", StringComparison.OrdinalIgnoreCase))
                {
                    string playerId = serverKey.Substring(8);
                    if (string.IsNullOrWhiteSpace(playerId))
                        return false;
                    joinData = new ServerJoinData(new ServerJoinDataPlayFabUser(playerId));
                    return joinData.IsValid;
                }

                if (serverKey.StartsWith("host/", StringComparison.OrdinalIgnoreCase))
                {
                    string endpoint = serverKey.Substring(5);
                    string host;
                    string portText;
                    if (endpoint.StartsWith("[", StringComparison.Ordinal))
                    {
                        int close = endpoint.LastIndexOf("]:", StringComparison.Ordinal);
                        if (close <= 1) return false;
                        host = endpoint.Substring(1, close - 1);
                        portText = endpoint.Substring(close + 2);
                    }
                    else
                    {
                        int colon = endpoint.LastIndexOf(':');
                        if (colon <= 0) return false;
                        host = endpoint.Substring(0, colon);
                        portText = endpoint.Substring(colon + 1);
                    }

                    if (!ushort.TryParse(portText, out ushort port) || port == 0 || string.IsNullOrWhiteSpace(host))
                        return false;
                    joinData = new ServerJoinData(new ServerJoinDataDedicated(host, port));
                    return joinData.IsValid;
                }
            }
            catch (Exception ex)
            {
                LogWarning("Could not parse last server identity: " + ex.Message);
            }

            return false;
        }

        private static string DisplayServer(string serverKey)
        {
            if (string.IsNullOrWhiteSpace(serverKey)) return "No recent server";
            int slash = serverKey.IndexOf('/');
            string value = slash >= 0 && slash + 1 < serverKey.Length ? serverKey.Substring(slash + 1) : serverKey;
            return value.Length <= 42 ? value : value.Substring(0, 39) + "...";
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

    [HarmonyPatch(typeof(FejdStartup), "SetupGui")]
    internal static class MainMenuPatch
    {
        private static void Postfix(FejdStartup __instance)
        {
            Plugin.EnsureQuickConnectButton(__instance);
        }
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
                Plugin.RecordSuccessfulConnection();
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
