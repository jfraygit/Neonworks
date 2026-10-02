using Nightshare.Core.Session;
using Xunit;

namespace Nightshare.Tests
{
    /// <summary>
    /// Splitting an endpoint the menu lets a player type.
    /// <para>
    /// Worth pinning because every failure here is quiet. A port left in the address gives
    /// a connection attempt to a host that does not exist, which on screen is identical to
    /// the other player not listening yet.
    /// </para>
    /// </summary>
    public class EndpointTextTests
    {
        [Theory]
        [InlineData("127.0.0.1:7777", "127.0.0.1", 7777)]
        [InlineData("192.168.1.50:50000", "192.168.1.50", 50000)]
        [InlineData("nightshare.example.com:7777", "nightshare.example.com", 7777)]
        public void SplitsAnOrdinaryEndpoint(string endpoint, string address, int port)
        {
            Assert.Equal(address, EndpointText.Address(endpoint));
            Assert.Equal(port, EndpointText.Port(endpoint));
        }

        /// <summary>
        /// A bare address must survive. Someone typing just an address has still told us
        /// something, and throwing it away for want of a port would discard the only part
        /// that cannot be guessed.
        /// </summary>
        [Fact]
        public void AnAddressWithNoPortKeepsTheAddressAndDefaultsThePort()
        {
            Assert.Equal("192.168.1.50", EndpointText.Address("192.168.1.50"));
            Assert.Equal(EndpointText.DefaultPort, EndpointText.Port("192.168.1.50"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void NothingAtAllIsNotACrash(string endpoint)
        {
            Assert.Equal("", EndpointText.Address(endpoint));
            Assert.Equal(EndpointText.DefaultPort, EndpointText.Port(endpoint));
        }

        [Theory]
        [InlineData("127.0.0.1:")]
        [InlineData("127.0.0.1:abc")]
        [InlineData("127.0.0.1:0")]
        [InlineData("127.0.0.1:70000")]
        [InlineData("127.0.0.1:-1")]
        public void AnUnusablePortFallsBackRatherThanBeingUsed(string endpoint)
        {
            Assert.Equal("127.0.0.1", EndpointText.Address(endpoint));
            Assert.Equal(EndpointText.DefaultPort, EndpointText.Port(endpoint));
        }

        /// <summary>
        /// Only the last colon can be the separator. An IPv6 literal is mostly colons, and
        /// splitting on the first one would hand back a fragment of the address.
        /// </summary>
        [Fact]
        public void TheLastColonIsTheSeparator()
        {
            Assert.Equal("fe80::1", EndpointText.Address("fe80::1:7777"));
            Assert.Equal(7777, EndpointText.Port("fe80::1:7777"));
        }

        [Fact]
        public void FormatRoundTrips()
        {
            var endpoint = EndpointText.Format("10.0.0.7", 25000);

            Assert.Equal("10.0.0.7:25000", endpoint);
            Assert.Equal("10.0.0.7", EndpointText.Address(endpoint));
            Assert.Equal(25000, EndpointText.Port(endpoint));
        }

        /// <summary>
        /// The case that broke hosting: a half-typed address the framework happily accepts.
        /// <c>IPAddress.Parse("7")</c> returns 0.0.0.7, which bound and failed with an error
        /// that reached the player as the Host button doing nothing.
        /// </summary>
        [Theory]
        [InlineData("7")]
        [InlineData("127")]
        [InlineData("127.0")]
        [InlineData("127.0.0")]
        [InlineData("127.0.0.")]
        [InlineData("127.0.0.1.5")]
        [InlineData("999.1.1.1")]
        [InlineData("")]
        [InlineData("   ")]
        public void AHalfTypedAddressIsNotConnectable(string address)
        {
            Assert.False(EndpointText.LooksConnectable(address));
        }

        [Theory]
        [InlineData("127.0.0.1")]
        [InlineData("0.0.0.0")]
        [InlineData("192.168.1.255")]
        [InlineData("255.255.255.255")]
        public void ACompleteAddressIsConnectable(string address)
        {
            Assert.True(EndpointText.LooksConnectable(address));
        }

        /// <summary>
        /// A name cannot be validated without DNS, and that is a connection's job. Letting
        /// them through is the difference between supporting names and not.
        /// </summary>
        [Theory]
        [InlineData("localhost")]
        [InlineData("nightshare.example.com")]
        [InlineData("my-pc")]
        public void AHostNameIsLetThrough(string address)
        {
            Assert.True(EndpointText.LooksConnectable(address));
        }

        [Fact]
        public void FormatRefusesToProduceSomethingUnconnectable()
        {
            Assert.Equal($"127.0.0.1:{EndpointText.DefaultPort}", EndpointText.Format("", 0));
            Assert.Equal($"10.0.0.7:{EndpointText.DefaultPort}", EndpointText.Format("10.0.0.7", 99999));
        }
    }
}
