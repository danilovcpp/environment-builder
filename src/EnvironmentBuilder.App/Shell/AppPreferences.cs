using System.IO;
using System.Text.Json;

namespace EnvironmentBuilder.App.Shell;

public sealed record AppPreferences(string Theme = "system", string StartPage = "environments", bool ShowDescriptions = true)
{
    public AppPreferences Validate() => this with
    {
        Theme = Theme is "system" or "light" or "dark" ? Theme : "system",
        StartPage = StartPage is "environments" or "seedpacks" or "services" or "diagnostics" ? StartPage : "environments"
    };
}

public sealed class PreferencesStore(string path)
{
    public AppPreferences Read() => File.Exists(path)
        ? (JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(path)) ?? new()).Validate()
        : new();

    public void Save(AppPreferences preferences)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences.Validate(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }
}
