using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EnvironmentBuilder.App.Components;

namespace EnvironmentBuilder.App.Shell;

public sealed record DialogChoice(string Id, string Label);
public sealed record DialogRequest(string Title, string Description, IReadOnlyList<DialogChoice> Choices, string CancelChoice);

// WPF interaction boundary; ViewModels receive decisions and never own controls.
public sealed class DialogService
{
    public async Task<string> ShowAsync(DialogRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            IInputElement? previousFocus = Keyboard.FocusedElement;
            string choice = request.CancelChoice;
            var panel = new StackPanel();
            var frame = new Border { Child = panel };
            frame.SetResourceReference(Border.PaddingProperty, "Padding.Dialog");
            var description = new TextBlock { Text = request.Description, TextWrapping = TextWrapping.Wrap };
            description.SetResourceReference(FrameworkElement.MarginProperty, "Margin.Section");
            panel.Children.Add(description);
            var buttons = new SpacedWrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var window = new Window
            {
                Title = request.Title,
                Content = frame,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            window.SetResourceReference(FrameworkElement.WidthProperty, "Size.Dialog");
            window.SetResourceReference(Control.BackgroundProperty, "Brush.Panel");
            window.SetResourceReference(Control.ForegroundProperty, "Brush.TextPrimary");
            foreach (DialogChoice item in request.Choices)
            {
                var button = new Button { Content = item.Label, IsCancel = item.Id == request.CancelChoice };
                button.Click += (_, _) => { choice = item.Id; window.Close(); };
                buttons.Children.Add(button);
            }
            panel.Children.Add(buttons);
            try { window.ShowDialog(); return choice; }
            finally { if (previousFocus is not null) { Keyboard.Focus(previousFocus); } }
        });
    }
}
