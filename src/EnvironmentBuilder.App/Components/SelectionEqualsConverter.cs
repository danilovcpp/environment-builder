using System.Globalization;
using System.Windows.Data;

namespace EnvironmentBuilder.App.Components;

/// <summary>Displays a command-driven option using the shared checked/unchecked style.</summary>
public sealed class SelectionEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Equals(value, parameter);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
