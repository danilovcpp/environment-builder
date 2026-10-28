using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.App.Features.Environments;

public abstract class EditorRow : ObservableObject
{
    public event EventHandler? Changed;
    protected override void OnPropertyChanged(PropertyChangedEventArgs e) { base.OnPropertyChanged(e); Changed?.Invoke(this, EventArgs.Empty); }
    protected void ChildChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}

public sealed class EditorRows<T> : ObservableCollection<T> where T : EditorRow
{
    public event EventHandler? Changed;
    private void ItemChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
    protected override void InsertItem(int index, T item) { item.Changed += ItemChanged; base.InsertItem(index, item); ItemChanged(this, EventArgs.Empty); }
    protected override void RemoveItem(int index) { this[index].Changed -= ItemChanged; base.RemoveItem(index); ItemChanged(this, EventArgs.Empty); }
    protected override void SetItem(int index, T item) { this[index].Changed -= ItemChanged; item.Changed += ItemChanged; base.SetItem(index, item); ItemChanged(this, EventArgs.Empty); }
    protected override void ClearItems() { foreach (T item in this) { item.Changed -= ItemChanged; } base.ClearItems(); ItemChanged(this, EventArgs.Empty); }
}

public sealed partial class ValueEditor : EditorRow
{
    [ObservableProperty] private string key = "";
    [ObservableProperty] private string text = "";
    [ObservableProperty] private bool useSecret;
    public string TypeHint { get; init; } = "string";
    public bool Required { get; init; }
    public bool DeclaredSecret { get; init; }
    public bool IsDeclared { get; init; }
    [ObservableProperty] private bool included = true;
    public Value Build() => UseSecret ? new Value { SecretRef = Text } : new Value { Literal = Text };
    public static ValueEditor From(string key, Value value) => new() { Key = key, Text = value.SecretRef ?? value.Literal ?? "", UseSecret = value.SecretRef is not null };
}

public sealed partial class PortEditor : EditorRow
{
    [ObservableProperty] private string id = "port";
    [ObservableProperty] private string containerPort = "8080";
    [ObservableProperty] private string hostPort = "";
    [ObservableProperty] private string protocol = "tcp";
    [ObservableProperty] private string access = "loopback";
    [ObservableProperty] private string purpose = "application";
    public static string[] Protocols { get; } = ["tcp", "udp"];
    public static string[] AccessScopes { get; } = ["loopback", "network"];
    public static string[] Purposes { get; } = ["application", "control"];
    public Port Build() => new(Id, Integer(ContainerPort, "/ports/containerPort"), Protocol, string.IsNullOrEmpty(HostPort) ? null : Integer(HostPort, "/ports/hostPort"), Access, Purpose);
    public static int Integer(string text, string path) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : throw new DocumentException(path, "invalid-port-number");
    public static PortEditor From(Port port) => new() { Id = port.Id, ContainerPort = port.ContainerPort.ToString(CultureInfo.InvariantCulture), HostPort = port.HostPort?.ToString(CultureInfo.InvariantCulture) ?? "", Protocol = port.Protocol, Access = port.Access, Purpose = port.Purpose };
}

public sealed partial class StorageEditor : EditorRow
{
    [ObservableProperty] private string id = "data";
    [ObservableProperty] private string kind = "managed";
    [ObservableProperty] private string targetPath = "/data";
    [ObservableProperty] private string pathRef = "local-path://input";
    public static string[] Kinds { get; } = ["managed", "readOnlyInput"];
    public Storage Build() => Kind == "managed" ? new ManagedStorage(Id, TargetPath) : new ReadOnlyInputStorage(Id, TargetPath, PathRef);
    public static StorageEditor From(Storage storage) => new() { Id = storage.Id, TargetPath = storage.TargetPath, Kind = storage is ManagedStorage ? "managed" : "readOnlyInput", PathRef = storage is ReadOnlyInputStorage input ? input.PathRef : "local-path://input" };
}

public sealed partial class DependencyEditor : EditorRow
{
    [ObservableProperty] private Guid dependentId;
    [ObservableProperty] private Guid requiredId;
    [ObservableProperty] private string? requirementId;
    [ObservableProperty] private string condition = "running";
    [ObservableProperty] private string reason = "Дополнительная связь";
    public static string[] Conditions { get; } = ["running", "healthy", "ready"];
    public bool IsRequirement => RequirementId is not null;
    public ComponentDependency Build() => new(DependentId, RequiredId, RequirementId, Condition, Reason);
    public static DependencyEditor From(ComponentDependency edge) => new() { DependentId = edge.DependentId, RequiredId = edge.RequiredId, RequirementId = edge.RequirementId, Condition = edge.Condition, Reason = edge.Reason };
}

public sealed record ValidationMessage(string FieldPath, string Message);
public sealed record ComponentChoice(Guid Id, string Label);
public sealed partial class DefinitionChoice(ComponentDefinition definition) : ObservableObject
{
    public ComponentDefinition Definition { get; } = definition;
    [ObservableProperty] private bool isSelected;
    public string Label => $"{Definition.Name} · {Definition.Version} · {Definition.ComponentType}";
    public string SourceLabel => Definition.DefaultSource is ImageSource image ? image.Reference : "Локальная сборка";
}

internal static class EditorJson
{
    internal static Dictionary<string, string> Strings(string text, string path)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { throw new JsonException(); }
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            { if (p.Value.ValueKind != JsonValueKind.String || !values.TryAdd(p.Name, p.Value.GetString()!)) { throw new JsonException(); } }
            return values;
        }
        catch (JsonException) { throw new DocumentException(path, "invalid-string-map"); }
    }
}
