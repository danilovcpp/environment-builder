using System.IO;
using System.Text.Json;
using EnvironmentBuilder.App.Shell;

namespace EnvironmentBuilder.UnitTests.ViewModels;

public sealed class AppPreferencesTests
{
    [Fact]
    public void Preferences_round_trip_and_replace_the_previous_file()
    {
        string directory = Path.Combine(Path.GetTempPath(), "EnvironmentBuilder-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        try
        {
            var store = new PreferencesStore(path);
            Assert.Equal(new AppPreferences(), store.Read());
            store.Save(new("dark", "services", false));
            Assert.Equal(new AppPreferences("dark", "services", false), new PreferencesStore(path).Read());
            store.Save(new("light", "seedpacks", true));
            Assert.Equal(new AppPreferences("light", "seedpacks", true), store.Read());
            Assert.Single(Directory.GetFiles(directory));
            File.WriteAllText(path, "{\"Theme\":\"invalid\",\"StartPage\":\"editor\",\"ShowDescriptions\":false}");
            Assert.Equal(new AppPreferences("system", "environments", false), store.Read());
            File.WriteAllText(path, "{broken");
            Assert.Throws<JsonException>(() => store.Read());
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }
}
