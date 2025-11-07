using K4os.Orleans.Clustering.NATS;
using Xunit;

namespace K4os.Orleans.NATS.Tests;

public class NatsIdTests
{
    [Theory]
    [InlineData("simple-id")]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("12")]
    [InlineData("123")]
    [InlineData("1234")]
    public void Roundtrip(string expected)
    {
        var id = expected.ToNatsId();
        var actual = id.FromNatsId();
        Assert.Equal(expected, actual);
    }
    
    [Theory]
    [InlineData(0, 32)]
    [InlineData(1337, 16)]
    [InlineData(15345, 67)]
    public void RoundtripWithKnownLength(int length, int range = 1)
    {
        for (var i = 0; i < range; i++)
        {
            var expected = new string('a', length);
            var id = expected.ToNatsId();
            var actual = id.FromNatsId();
            Assert.Equal(expected, actual);
        }
    }
}
