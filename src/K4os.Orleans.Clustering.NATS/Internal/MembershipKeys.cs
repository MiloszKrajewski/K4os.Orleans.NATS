using K4os.Orleans.NATS;

namespace K4os.Orleans.Clustering.NATS.Internal;

internal static class MembershipKeys
{
    public static string BucketName(string serviceId) =>
        $"orleans-{serviceId.ToNatsId()}";

    public static string ClusterPrefix(string clusterId) =>
        clusterId.ToNatsId();

    public static string DirectoryKey(string cId) =>
        $"{cId}.struct.membership";

    public static string SiloBlobKey(string cId, string encodedAddr, string guidEncoded) =>
        $"{cId}.struct.silo.{encodedAddr}.{guidEncoded}";

    public static string SiloKeyPrefix(string cId, string encodedAddr) =>
        $"{cId}.struct.silo.{encodedAddr}";

    public static string AliveKey(string cId, string encodedAddr) =>
        $"{cId}.alive.{encodedAddr}";

    public static string EncodeAddr(SiloAddress address) =>
        address.ToParsableString().ToNatsId();

    public static SiloAddress DecodeAddr(string encoded) =>
        SiloAddress.FromParsableString(encoded.FromNatsId());
}
