using System;
using System.IO;
using System.Text.Json;

namespace DH.Client.App.Services;

internal sealed class AppUserSettings
{
    public string StoragePath { get; set; } = "";

    public string SdkConfigPath { get; set; } = "";
}

internal static class AppUserSettingsStore
{
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DH",
            "DH.Client.App",
            "user-settings.json");

    public static AppUserSettings Load()
    {
        lock (SyncRoot)
        {
            try
            {
                string path = SettingsPath;
                if (!File.Exists(path))
                {
                    return new AppUserSettings();
                }

                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppUserSettings>(json) ?? new AppUserSettings();
            }
            catch
            {
                return new AppUserSettings();
            }
        }
    }

    public static void Save(AppUserSettings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        lock (SyncRoot)
        {
            string path = SettingsPath;
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(path, json);
        }
    }
}
