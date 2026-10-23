namespace EnvironmentBuilder.Core.Environments;

public sealed class DocumentException(string fieldPath, string code) : Exception($"{code}: {fieldPath}")
{
    public string FieldPath { get; } = fieldPath;
    public string Code { get; } = code;
}
