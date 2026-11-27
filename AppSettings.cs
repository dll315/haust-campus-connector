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
    public bool StartMinimized = true;
    public bool HideOnClose = true;

    public static AppSettings Load()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(SettingsKey))
        {
            var data = key == null ? null : key.GetValue("Configuration") as string[];
            var saved = FromConfiguration(data);
            if (saved != null) return saved;
        }
        var config = new AppSettings();
        var oldPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CampusConnector", "settings.txt");
        if (File.Exists(oldPath))
            using (var reader = File.OpenText(oldPath)) config.ProfileName = reader.ReadLine();
        return config;
    }

    internal static AppSettings FromConfiguration(string[] data)
    {
        if (data == null || data.Length < 4 || data.Length > 7) return null;
        Guid adapter;
        Guid.TryParse(data[1], out adapter);
        return new AppSettings
        {
            ProfileName = data[0], Adapter = adapter,
            Monitoring = data[2] == "1", AutoPortal = data[3] == "1",
            AutomaticLogin = data.Length < 5 || data[4] == "1",
            StartMinimized = data.Length < 6 || data[5] == "1",
            HideOnClose = data.Length < 7 || data[6] == "1"
        };
    }

    internal string[] ToConfiguration()
    {
        return new[] { ProfileName ?? "", Adapter.ToString(), Monitoring ? "1" : "0",
            AutoPortal ? "1" : "0", AutomaticLogin ? "1" : "0", StartMinimized ? "1" : "0", HideOnClose ? "1" : "0" };
    }

    public void Save()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(SettingsKey))
            key.SetValue("Configuration", ToConfiguration(), RegistryValueKind.MultiString);
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
