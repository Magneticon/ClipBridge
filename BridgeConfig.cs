using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ClipBridge
{
    internal sealed class ClipboardGroup
    {
        public string Name;
        public string Key;
        public string Id;
        public string Auth;
    }

    internal sealed class BridgeConfig
    {
        public string LocalMachineId { get; private set; }
        public string ComputerName { get; private set; }
        public int Port { get; set; }
        public bool AutoDiscovery { get; set; }
        public int DiscoveryIntervalMilliseconds { get; set; }
        public bool Silent { get; set; }
        public bool ShowTrayIcon { get; set; }
        public bool StartWithWindows { get; set; }
        public bool SyncText { get; set; }
        public bool SyncImages { get; set; }
        public bool SyncFiles { get; set; }
        public int MaxManifestEntries { get; private set; }
        public int FileReadChunkKilobytes { get; private set; }
        public int PollMilliseconds { get; private set; }
        public int MaxImageMegabytes { get; private set; }
        public int RemoteRequestTimeoutMilliseconds { get; private set; }
        public int FileSessionRetentionMinutes { get; private set; }
        public List<ClipboardGroup> Groups { get; private set; }
        public string GroupsText { get; private set; }

        public static BridgeConfig Load()
        {
            BridgeConfig c = new BridgeConfig();
            c.Port = GetInt("Port", 8080);
            c.AutoDiscovery = GetBool("AutoDiscovery", true);
            c.DiscoveryIntervalMilliseconds = Math.Max(500, GetInt("DiscoveryIntervalMilliseconds", 2000));
            c.Silent = GetBool("Silent", true);
            c.ShowTrayIcon = GetBool("ShowTrayIcon", true);
            c.StartWithWindows = GetBool("StartWithWindows", false);
            c.SyncText = GetBool("SyncText", true);
            c.SyncImages = GetBool("SyncImages", true);
            c.SyncFiles = GetBool("SyncFiles", true);
            c.MaxManifestEntries = Math.Max(1000, GetInt("MaxManifestEntries", 200000));
            c.FileReadChunkKilobytes = Math.Max(64, Math.Min(4096, GetInt("FileReadChunkKilobytes", 1024)));
            c.PollMilliseconds = Math.Max(100, GetInt("PollMilliseconds", 250));
            c.MaxImageMegabytes = Math.Max(1, GetInt("MaxImageMegabytes", 100));
            c.RemoteRequestTimeoutMilliseconds = Math.Max(1000, GetInt("RemoteRequestTimeoutMilliseconds", 15000));
            c.FileSessionRetentionMinutes = Math.Max(60, GetInt("FileSessionRetentionMinutes", 1440));
            c.ComputerName = Environment.MachineName;
            c.LocalMachineId = LoadOrCreateMachineId();
            c.SetGroups(Get("Groups", "Main:ClipBridge-Change-Me"));
            return c;
        }

        public void SetGroups(string text)
        {
            List<ClipboardGroup> parsed = ParseGroups(text);
            if (parsed.Count == 0) throw new ArgumentException("At least one valid group is required. Use GroupName:GroupKey.");
            if (parsed.Count > 64) throw new ArgumentException("A maximum of 64 groups is supported.");
            Groups = parsed;
            GroupsText = NormalizeGroups(parsed);
        }

        public void SaveUserEditableSettings()
        {
            Configuration cfg = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            SetAppSetting(cfg, "Port", Port.ToString());
            SetAppSetting(cfg, "AutoDiscovery", AutoDiscovery.ToString().ToLowerInvariant());
            SetAppSetting(cfg, "Groups", GroupsText);
            SetAppSetting(cfg, "Silent", Silent.ToString().ToLowerInvariant());
            SetAppSetting(cfg, "ShowTrayIcon", ShowTrayIcon.ToString().ToLowerInvariant());
            SetAppSetting(cfg, "StartWithWindows", StartWithWindows.ToString().ToLowerInvariant());
            SetAppSetting(cfg, "SyncText", SyncText.ToString().ToLowerInvariant());
            SetAppSetting(cfg, "SyncImages", SyncImages.ToString().ToLowerInvariant());
            SetAppSetting(cfg, "SyncFiles", SyncFiles.ToString().ToLowerInvariant());
            cfg.Save(ConfigurationSaveMode.Modified);
            ConfigurationManager.RefreshSection("appSettings");
        }

        public static string ExecutableConfigPath
        {
            get
            {
                try { return ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None).FilePath; }
                catch { return AppDomain.CurrentDomain.SetupInformation.ConfigurationFile; }
            }
        }

        private static void SetAppSetting(Configuration cfg, string key, string value)
        {
            KeyValueConfigurationCollection s = cfg.AppSettings.Settings;
            if (s[key] == null) s.Add(key, value); else s[key].Value = value;
        }

        private static List<ClipboardGroup> ParseGroups(string text)
        {
            List<ClipboardGroup> groups = new List<ClipboardGroup>();
            string normalized = (text ?? String.Empty).Replace("\r", "").Replace("\n", ";");
            string[] items = normalized.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in items)
            {
                int p = raw.IndexOf(':');
                if (p <= 0 || p >= raw.Length - 1) continue;
                string name = raw.Substring(0, p).Trim();
                string key = raw.Substring(p + 1).Trim();
                if (name.Length == 0 || key.Length == 0) continue;
                ClipboardGroup g = new ClipboardGroup();
                g.Name = name;
                g.Key = key;
                g.Id = HashHex("ClipBridgeGroup\0" + name);
                if (ids.Contains(g.Id)) continue;
                ids.Add(g.Id);
                g.Auth = HashHex("ClipBridgeAuth\0" + g.Id + "\0" + key);
                groups.Add(g);
            }
            return groups;
        }

        private static string NormalizeGroups(List<ClipboardGroup> groups)
        {
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < groups.Count; i++)
            {
                if (i != 0) b.Append(';');
                b.Append(groups[i].Name); b.Append(':'); b.Append(groups[i].Key);
            }
            return b.ToString();
        }

        private static string HashHex(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] b = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                StringBuilder s = new StringBuilder(b.Length * 2);
                for (int i = 0; i < b.Length; i++) s.Append(b[i].ToString("x2"));
                return s.ToString();
            }
        }

        private static string LoadOrCreateMachineId()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClipBridge.machine");
            try
            {
                if (File.Exists(path))
                {
                    string v = File.ReadAllText(path).Trim();
                    Guid parsed;
                    if (Guid.TryParse(v, out parsed)) return parsed.ToString("N");
                }
                string id = Guid.NewGuid().ToString("N");
                File.WriteAllText(path, id);
                return id;
            }
            catch { return Guid.NewGuid().ToString("N"); }
        }

        private static string Get(string key, string fallback)
        {
            string value = ConfigurationManager.AppSettings[key];
            return String.IsNullOrEmpty(value) ? fallback : value;
        }

        private static int GetInt(string key, int fallback)
        {
            int value;
            return Int32.TryParse(Get(key, fallback.ToString()), out value) ? value : fallback;
        }

        private static bool GetBool(string key, bool fallback)
        {
            bool value;
            return Boolean.TryParse(Get(key, fallback.ToString()), out value) ? value : fallback;
        }
    }
}
