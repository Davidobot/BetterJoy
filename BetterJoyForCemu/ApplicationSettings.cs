using System;
using System.Collections.Generic;
using System.Configuration;

namespace BetterJoyForCemu {
    // One write path for application-wide settings. The legacy Settings table and the new
    // Global profile pane both edit the redirected AppData/ProgramData config established by
    // EntryPoint, so moving an option between those UIs never changes where it is stored.
    internal static class ApplicationSettings {
        private static readonly object WriteLock = new object();

        private static readonly HashSet<string> GlobalOptionKeys =
            new HashSet<string>(StringComparer.Ordinal) {
                "StartInTray", "HideStatus", "MotionServer", "PassiveScan",
                "AutoAddControllers", "BlockAutoAddUSB", "AllowCalibration",
                "UseHidHide", "UnhideOnExit", "AutoCalDebugLogging",
                "DualSenseDebugLogging", "DualShock4DebugLogging", "DebugLogging", "GyroMouseDebugLogging",
                "GyroStickDebugLogging", "UseViiperForDualSenseMicrophone",
                "OpenRgbServerMode", "OpenRgbServerCachedColor", "OpenRgbServerCachedModeState",
            };

        public static bool IsGlobalOption(string key) {
            return GlobalOptionKeys.Contains(key);
        }

        public static bool BoolValue(string key) {
            bool value;
            return Boolean.TryParse(ConfigurationManager.AppSettings[key], out value) && value;
        }

        public static string StringValue(string key, string defaultValue) {
            string value = ConfigurationManager.AppSettings[key];
            return String.IsNullOrEmpty(value) ? defaultValue : value;
        }

        public static void SetValue(string key, string value) {
            SetValues(new Dictionary<string, string> { { key, value } });
        }

        // Saves a related group in one config transaction. Besides avoiding redundant disk and
        // config-watcher churn, the shared lock prevents independent socket/UI threads from
        // opening the same application config, changing different keys, then racing to save
        // stale copies over one another.
        public static void SetValues(IDictionary<string, string> values) {
            lock (WriteLock) {
                Configuration config = ConfigurationManager.OpenExeConfiguration(
                    ConfigurationUserLevel.None);
                foreach (KeyValuePair<string, string> value in values) {
                    KeyValueConfigurationElement setting = config.AppSettings.Settings[value.Key];
                    if (setting == null)
                        throw new ConfigurationErrorsException("Missing app setting: " + value.Key);
                    setting.Value = value.Value;
                }

                config.Save(ConfigurationSaveMode.Modified);
                ConfigurationManager.RefreshSection(config.AppSettings.SectionInformation.Name);
            }
        }
    }
}
