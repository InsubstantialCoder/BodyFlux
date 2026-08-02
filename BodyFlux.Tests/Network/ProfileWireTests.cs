using System;
using BodyFlux.Network;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BodyFlux.Tests.Network;

public class ProfileWireTests
{
    [Fact]
    public void Minimize_StripsToIdNameBones()
    {
        var full = new JObject
        {
            ["ID"]              = "11111111-1111-1111-1111-111111111111",
            ["Name"]            = "MyRealProfileName",
            ["CharacterName"]   = "Some Character",
            ["Bones"]           = new JObject { ["j_kao"] = new JObject { ["marker"] = "x" } },
        }.ToString();

        var minimized = JObject.Parse(ProfileWire.Minimize(full));

        Assert.Equal("00000000-0000-0000-0000-000000000000", minimized["ID"]!.Value<string>());
        Assert.Equal("BodyFlux", minimized["Name"]!.Value<string>());
        Assert.False(minimized.ContainsKey("CharacterName"));
        Assert.Equal("x", minimized["Bones"]!["j_kao"]!["marker"]!.Value<string>());
    }

    [Fact]
    public void Minimize_MissingBonesNode_DefaultsToEmptyObject()
    {
        var minimized = JObject.Parse(ProfileWire.Minimize("{}"));
        Assert.NotNull(minimized["Bones"]);
        Assert.Empty(((JObject)minimized["Bones"]!).Properties());
    }

    [Fact]
    public void Minimize_MalformedJson_ReturnsInputUnchanged()
    {
        const string malformed = "{ not json";
        Assert.Equal(malformed, ProfileWire.Minimize(malformed));
    }

    [Fact]
    public void Pack_Unpack_RoundTrips()
    {
        const string json = """{"Bones":{"j_kao":{"Scaling":{"X":1.5,"Y":1.5,"Z":1.5}}}}""";

        var packed   = ProfileWire.Pack(json);
        var unpacked = ProfileWire.Unpack(packed);

        Assert.Equal(json, unpacked);
    }

    [Fact]
    public void Pack_ProducesBase64String()
    {
        var packed = ProfileWire.Pack("{}");
        // Should not throw — asserts it really is valid base64.
        Convert.FromBase64String(packed);
    }

    [Fact]
    public void TryDecode_UnpacksPackedField()
    {
        const string json = """{"Bones":{"j_kao":{"Scaling":{"X":1.5,"Y":1.5,"Z":1.5}}}}""";

        Assert.True(ProfileWire.TryDecode(ProfileWire.Pack(json), out var decoded));
        Assert.Equal(json, decoded);
    }

    [Fact]
    public void TryDecode_PassesPlainJsonThrough()
    {
        const string json = """{"Bones":{}}""";

        Assert.True(ProfileWire.TryDecode(json, out var decoded));
        Assert.Equal(json, decoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 and not json")]
    public void TryDecode_RejectsUndecodableField(string? field)
    {
        Assert.False(ProfileWire.TryDecode(field, out _));
    }

    [Fact]
    public void TryDecode_RejectsBase64ThatIsNotAProfile()
    {
        // Valid base64, but not DEFLATE'd JSON — must be dropped, not handed on as a profile.
        var junk = Convert.ToBase64String("hello world"u8.ToArray());
        Assert.False(ProfileWire.TryDecode(junk, out _));
    }
}
