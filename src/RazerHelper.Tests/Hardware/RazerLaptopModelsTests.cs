using RazerHelper.Core.Hardware;

namespace RazerHelper.Tests.Hardware;

public class RazerLaptopModelsTests
{
    [Fact]
    public void IsNotEmpty()
    {
        Assert.NotEmpty(RazerLaptopModels.Known);
    }

    [Fact]
    public void HasNoDuplicateProductIds()
    {
        var productIds = RazerLaptopModels.Known.Select(model => model.ProductId);

        Assert.Equal(productIds.Distinct().Count(), productIds.Count());
    }

    [Fact]
    public void ListsTheVerifiedBlade16_2023()
    {
        Assert.Contains(
            RazerLaptopModels.Known,
            model => model is { ProductId: 0x029F, Name: "Razer Blade 16 (2023)", Verified: true });
    }

    [Fact]
    public void ListsTheUnverifiedBlade14_2022_WithItsFanRange()
    {
        Assert.Contains(
            RazerLaptopModels.Known,
            model => model is { ProductId: 0x028C, Name: "Razer Blade 14 (2022)", Verified: false, MinFanRpm: 3500, MaxFanRpm: 5000 });
    }

    [Fact]
    public void EveryFanRangeIsASensibleOne()
    {
        foreach (var model in RazerLaptopModels.Known)
        {
            Assert.InRange(model.MinFanRpm, 1000, 4000);
            Assert.Equal(5000, model.MaxFanRpm);
            Assert.True(model.MinFanRpm % 100 == 0);
        }
    }
}
