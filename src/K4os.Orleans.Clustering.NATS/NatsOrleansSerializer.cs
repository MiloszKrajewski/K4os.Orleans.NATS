using System.Buffers;
using System.Text.Json;
using NATS.Client.Core;

namespace K4os.Orleans.Clustering.NATS;

public class NatsSystemJsonSerializer<T>: INatsSerializer<T>
{
    private readonly JsonSerializerOptions _options;
    private readonly INatsSerializer<T>? _next;

    public NatsSystemJsonSerializer(
        JsonSerializerOptions? options = null,
        INatsSerializer<T>? next = null)
    {
        _options = options ?? new JsonSerializerOptions();
        _next = next;
    }
    
    public void Serialize(IBufferWriter<byte> bufferWriter, T value)
    {
        var utfWriter = new Utf8JsonWriter(bufferWriter);
        JsonSerializer.Serialize(utfWriter, value, _options);
    }

    public T? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        var utfReader = new Utf8JsonReader(buffer);
        return JsonSerializer.Deserialize<T>(ref utfReader, _options);
    }

    public INatsSerializer<T> CombineWith(INatsSerializer<T> next) =>
        new NatsSystemJsonSerializer<T>(_options, _next is null ? next : _next.CombineWith(next));
}
