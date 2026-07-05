using BodyFlux.Network;
using Xunit;

namespace BodyFlux.Tests.Network;

public class PeerIdentityTests
{
    [Fact]
    public void Of_IsDeterministic_ForSameNameAndKey()
    {
        var a = PeerIdentity.Of("Warrior of Light", "group-key");
        var b = PeerIdentity.Of("Warrior of Light", "group-key");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Of_DiffersAcrossDifferentSyncKeys()
    {
        var a = PeerIdentity.Of("Warrior of Light", "key-one");
        var b = PeerIdentity.Of("Warrior of Light", "key-two");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Of_DiffersAcrossDifferentNames()
    {
        var a = PeerIdentity.Of("Alice Alisaie", "group-key");
        var b = PeerIdentity.Of("Bob Bobbington", "group-key");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Of_ReturnsLowercaseHex()
    {
        var id = PeerIdentity.Of("Someone", "key");
        Assert.Matches("^[0-9a-f]+$", id);
        Assert.Equal(64, id.Length); // SHA-256 -> 32 bytes -> 64 hex chars
    }

    [Fact]
    public void Short_TruncatesLongIdToEightChars()
    {
        var id = PeerIdentity.Of("Someone", "key");
        Assert.Equal(8, PeerIdentity.Short(id).Length);
        Assert.Equal(id[..8], PeerIdentity.Short(id));
    }

    [Fact]
    public void Short_LeavesShortIdUnchanged()
    {
        Assert.Equal("abc", PeerIdentity.Short("abc"));
        Assert.Equal("12345678", PeerIdentity.Short("12345678"));
    }
}
