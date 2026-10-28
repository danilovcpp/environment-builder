using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace EnvironmentBuilder.App.Components;

// Presentation placeholders never write to the store or invoke runtime operations.
public static class PlaceholderActions
{
    public static ICommand Explain { get; } = new RelayCommand(() => MessageBox.Show(
        "Эта возможность пока доступна только для предварительного просмотра. Изменения не сохранены.",
        "Предварительный просмотр", MessageBoxButton.OK, MessageBoxImage.Information));
}
