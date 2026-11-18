using System;
using System.IO;
using Microsoft.Win32;

sealed class AppSettings
{
    const string SettingsKey = @"Software\CampusConnector";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "CampusConnector";
    public string ProfileName;
    public Guid Adapter;
    public bool Monitoring;
    public bool AutoPortal = true;
    public bool AutomaticLogin = true;

    public static AppSettings Load()
    {
        var config = new AppSettings();
        using (var key = Registry.CurrentUser.OpenSubKey(SettingsKey))
        {
            var data = key == null ? null : key.GetValue("Configuration") as string[];
            if (data != null && (data.Length == 4 || data.Length == 5))
            {
                config.ProfileName = data[0];
                Guid.TryParse(data[1], out config.Adapter);
                config.Monitoring = data[2] == "1";
                config.AutoPortal = data[3] == "1";
                config.AutomaticLogin = data.Length == 4 || data[4] == "1";
                return config;
            }
        }
        var oldPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CampusConnector", "settings.txt");
        if (File.Exists(oldPath))
            using (var reader = File.OpenText(oldPath)) config.ProfileName = reader.ReadLine();
        return config;
    }

    public void Save()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(SettingsKey))
            key.SetValue("Configuration", new[] { ProfileName ?? "", Adapter.ToString(),
                Monitoring ? "1" : "0", AutoPortal ? "1" : "0", AutomaticLogin ? "1" : "0" }, RegistryValueKind.MultiString);
    }

    public static string StartupCommand(string exe) { return "\"" + exe + "\" --startup"; }

    public static bool StartsWithWindows(string exe)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            return key != null && string.Equals(key.GetValue(RunValue) as string,
                StartupCommand(exe), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetStartup(string exe, bool enabled)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enabled) key.SetValue(RunValue, StartupCommand(exe), RegistryValueKind.String);
            else key.DeleteValue(RunValue, false);
        }
    }
}
