using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace EnvironmentBuilder.Infrastructure.Imports;

public sealed class NdjsonReader
{
    public async Task<long> ValidateAndIndexAsync(Stream input, Stream index, CancellationToken cancellationToken)
    {
        byte[] readBuffer = new byte[65536];
        byte[] line = new byte[65536];
        byte[] offsetBytes = new byte[8];
        int length = 0;
        long offset = 0, recordStart = 0, records = 0;
        int count;
        while ((count = await input.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            for (int i = 0; i < count; i++)
            {
                if (length == line.Length) { throw new IOException("NDJSON record exceeds 64 KiB including its terminator."); }
                byte next = readBuffer[i];
                line[length++] = next;
                offset++;
                if (next != '\n') { continue; }
                ValidateLine(line.AsMemory(0, length - 1));
                BinaryPrimitives.WriteInt64LittleEndian(offsetBytes, recordStart);
                await index.WriteAsync(offsetBytes, cancellationToken).ConfigureAwait(false);
                records++;
                recordStart = offset;
                length = 0;
            }
        }
        if (length != 0)
        {
            ValidateLine(line.AsMemory(0, length));
            BinaryPrimitives.WriteInt64LittleEndian(offsetBytes, recordStart);
            await index.WriteAsync(offsetBytes, cancellationToken).ConfigureAwait(false);
            records++;
        }
        return records;
    }

    private static void ValidateLine(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length != 0 && bytes.Span[^1] == '\r') { bytes = bytes[..^1]; }
        if (bytes.IsEmpty) { throw new IOException("Blank NDJSON lines are forbidden."); }
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes.Span);
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) { throw new IOException("Each NDJSON record must be an object."); }
        }
        catch (JsonException) { throw new IOException("Invalid NDJSON object."); }
        catch (DecoderFallbackException) { throw new IOException("Invalid NDJSON UTF-8."); }
    }
}
