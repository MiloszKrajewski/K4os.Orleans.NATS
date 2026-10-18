using Orleans;
using Orleans.Runtime;

namespace K4os.Orleans.NATS.Tests.Internal;

internal class TestLocalSiloDetails : ILocalSiloDetails
{
    public TestLocalSiloDetails(SiloAddress siloAddress, string? name = null, string? clusterId = null)
    {
        SiloAddress = siloAddress;
        Name = name ?? "test-silo";
        ClusterId = clusterId ?? "test";
        DnsHostName = "localhost";
        GatewayAddress = siloAddress;
    }

    public string Name { get; }
    public string ClusterId { get; }
    public string DnsHostName { get; }
    public SiloAddress SiloAddress { get; }
    public SiloAddress GatewayAddress { get; }
}
