using System.Windows;
using System.Windows.Controls;
namespace EnvironmentBuilder.App.Features.Environments;

public partial class ComponentEditor : UserControl
{
    public ComponentEditor() { InitializeComponent(); }
    public void RevealTab(string header)
    {
        SettingsExpander.IsExpanded = true;
        ComponentTabs.SelectedItem = ComponentTabs.Items.OfType<TabItem>().FirstOrDefault(tab => tab.Header as string == header);
        UpdateLayout();
    }
    public FrameworkElement? FindField(string tag) => tag switch
    {
        "parameters" => ParametersTable,
        "ports" => PortsTable,
        "storage" => StorageTable,
        "environment" => VariablesTable,
        "source" => DataContext is ComponentEditorViewModel { IsBuild: true } ? FolderInput : SourceInput,
        _ => null
    };
}
