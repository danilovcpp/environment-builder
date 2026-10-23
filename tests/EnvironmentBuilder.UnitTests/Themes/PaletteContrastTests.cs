using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace EnvironmentBuilder.UnitTests.Themes;

public sealed class PaletteContrastTests
{
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Meaningful_text_and_focus_indicators_meet_contrast_on_their_surfaces(string theme)
    {
        XDocument document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Themes", $"{theme}.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        Dictionary<string, string> colors = document.Root!.Elements()
            .Where(element => element.Name.LocalName == "Color")
            .ToDictionary(element => element.Attribute(xaml + "Key")!.Value, element => element.Value);

        foreach (string surface in new[] { "Canvas", "Panel", "Input", "Selected" })
        {
            AssertContrast(colors, "TextPrimary", surface, 4.5);
            AssertContrast(colors, "TextSecondary", surface, 4.5);
        }

        foreach (string status in new[] { "Success", "Info", "Warning", "Error", "Neutral", "Purple" })
        {
            AssertContrast(colors, status + "Foreground", status + "Background", 4.5);
        }

        AssertContrast(colors, "OnAccent", "Accent", 4.5);
        AssertContrast(colors, "OnAccent", "AccentHover", 4.5);
        AssertContrast(colors, "OnAccent", "AccentPressed", 4.5);
        AssertContrast(colors, "FocusRing", "Canvas", 3.0);
        AssertContrast(colors, "FocusRing", "Panel", 3.0);

        XElement supportingBrush = document.Root.Elements().Single(element =>
            element.Attribute(xaml + "Key")?.Value == "Brush.CanvasSupportingText");
        Assert.Equal("{StaticResource Color.TextSecondary}", supportingBrush.Attribute("Color")!.Value);
    }

    private static void AssertContrast(Dictionary<string, string> colors, string foreground, string background, double minimum)
    {
        double first = Luminance(colors["Color." + foreground]);
        double second = Luminance(colors["Color." + background]);
        double ratio = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
        Assert.True(ratio >= minimum, $"{foreground} on {background}: {ratio:F2}:1; required {minimum}:1.");
    }

    private static double Luminance(string hex)
    {
        double Linear(int offset)
        {
            double channel = int.Parse(hex.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(1) + 0.7152 * Linear(3) + 0.0722 * Linear(5);
    }
}
