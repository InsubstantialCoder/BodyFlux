using BodyFlux.Morph;
using Xunit;

namespace BodyFlux.Tests.Morph;

public class EasingHelperTests
{
    [Theory]
    [InlineData(EasingMode.Linear)]
    [InlineData(EasingMode.EaseIn)]
    [InlineData(EasingMode.EaseOut)]
    [InlineData(EasingMode.EaseInOut)]
    public void Apply_ClampsToBoundaries(EasingMode mode)
    {
        Assert.Equal(0f, EasingHelper.Apply(0f, mode), precision: 5);
        Assert.Equal(1f, EasingHelper.Apply(1f, mode), precision: 5);
    }

    [Fact]
    public void Apply_Linear_IsIdentity()
    {
        Assert.Equal(0.37f, EasingHelper.Apply(0.37f, EasingMode.Linear), precision: 5);
    }

    [Fact]
    public void Apply_EaseIn_IsSlowerThanLinearAtMidpoint()
    {
        Assert.True(EasingHelper.Apply(0.5f, EasingMode.EaseIn) < 0.5f);
    }

    [Fact]
    public void Apply_EaseOut_IsFasterThanLinearAtMidpoint()
    {
        Assert.True(EasingHelper.Apply(0.5f, EasingMode.EaseOut) > 0.5f);
    }

    [Fact]
    public void Apply_EaseInOut_IsSymmetricAroundMidpoint()
    {
        var a = EasingHelper.Apply(0.25f, EasingMode.EaseInOut);
        var b = EasingHelper.Apply(0.75f, EasingMode.EaseInOut);
        Assert.Equal(1f, a + b, precision: 4);
    }
}
