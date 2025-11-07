using System.Buffers;
using System.Text;
using NATS.Client.Core;
using Newtonsoft.Json;

namespace K4os.Orleans.Clustering.NATS.vPrev2;

internal class MembershipEntrySerializer: INatsSerializer<MembershipEntry>
{
    public static readonly MembershipEntrySerializer Default = new();

    public void Serialize(IBufferWriter<byte> bufferWriter, MembershipEntry value) => 
        bufferWriter.Write(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value)));

    public MembershipEntry? Deserialize(in ReadOnlySequence<byte> buffer) => 
        buffer.IsEmpty ? null : JsonConvert.DeserializeObject<MembershipEntry>(Encoding.UTF8.GetString(buffer));

    public INatsSerializer<MembershipEntry> CombineWith(INatsSerializer<MembershipEntry> next) => this;
}
