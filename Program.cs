using System;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClipBridge
{
    static class Program
    {
        private const string MutexName = "ClipBridge.v1.";
        private const string ShowEventName = "ClipBridge.v1.Show.";
        private static Mutex _mutex;
        private static EventWaitHandle _showEvent;

        [STAThread]
        static void Main(string[] args)
        {
            bool forceSilent = HasArg(args, "/silent") || HasArg(args, "/background") || HasArg(args, "-silent");
            bool forceVisible = HasArg(args, "/show") || HasArg(args, "-show") || !forceSilent;

            bool created;
            _mutex = new Mutex(true, MutexName + Environment.UserName, out created);
            if (!created)
            {
                if (forceVisible) SignalExistingInstance();
                return;
            }

            bool eventCreated;
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName + Environment.UserName, out eventCreated);

            BridgeConfig config = BridgeConfig.Load();
            ApplyStartupSetting(config.StartWithWindows);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(config, forceVisible, forceSilent, _showEvent));

            try { _showEvent.Close(); } catch { }
            try { _mutex.ReleaseMutex(); } catch { }
            try { _mutex.Close(); } catch { }
        }

        private static void SignalExistingInstance()
        {
            try
            {
                using (EventWaitHandle e = EventWaitHandle.OpenExisting(ShowEventName + Environment.UserName)) e.Set();
            }
            catch { }
        }

        public static void ApplyStartupSetting(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true))
                {
                    if (key == null) return;
                    if (enabled)
                        key.SetValue("ClipBridge", "\"" + Application.ExecutablePath + "\" /silent");
                    else
                        key.DeleteValue("ClipBridge", false);
                }
            }
            catch { }
        }

        private static bool HasArg(string[] args, string value)
        {
            for (int i = 0; i < args.Length; i++)
                if (String.Equals(args[i], value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
