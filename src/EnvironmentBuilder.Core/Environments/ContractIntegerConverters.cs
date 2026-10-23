using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnvironmentBuilder.Core.Environments;

internal sealed class ContractInt32Converter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !ContractInteger.TryRead(Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan), out long value) || value < int.MinValue || value > int.MaxValue) { throw new JsonException("Invalid Int32."); }
        return (int)value;
    }
    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}
internal sealed class ContractInt64Converter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !ContractInteger.TryRead(Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan), out long value)) { throw new JsonException("Invalid Int64."); }
        return (long)value;
    }
    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}
