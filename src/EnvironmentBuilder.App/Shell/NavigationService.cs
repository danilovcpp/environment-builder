using CommunityToolkit.Mvvm.ComponentModel;

namespace EnvironmentBuilder.App.Shell;

public sealed record NavigationAddress(string Route, Guid? EnvironmentId = null, Guid? DraftId = null);
public sealed class NavigationService : ObservableObject
{
    private NavigationAddress current = new("environments");
    public NavigationAddress Current { get => current; private set => SetProperty(ref current, value); }
    public void Navigate(NavigationAddress address)
    {
        if (address.Route is not ("environments" or "editor" or "preview" or "seedpacks" or "settings" or "diagnostics")) { throw new ArgumentException("Unknown route.", nameof(address)); }
        Current = address;
    }
}
