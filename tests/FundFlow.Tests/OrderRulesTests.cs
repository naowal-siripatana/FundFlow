using FundFlow.Core;

namespace FundFlow.Tests;

public class UnitCalculatorTests
{
    [Fact]
    public void Subscription_units_are_truncated_not_rounded()
    {
        // 1000 / 15.2345 = 65.64048...  -> rounding would give 65.6405, truncation gives 65.6404
        Assert.Equal(65.6404m, UnitCalculator.UnitsForAmount(1000m, 15.2345m));
    }

    [Fact]
    public void Redemption_amount_is_truncated_to_two_decimals()
    {
        // 100.5 * 10.8721 = 1092.64605 -> rounding would give 1092.65, truncation gives 1092.64
        Assert.Equal(1092.64m, UnitCalculator.AmountForUnits(100.5m, 10.8721m));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(100, 0)]
    public void Invalid_inputs_throw(decimal amount, decimal nav)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UnitCalculator.UnitsForAmount(amount, nav));
    }
}

public class OrderValidatorTests
{
    [Fact]
    public void Valid_subscription_passes() =>
        Assert.Null(OrderValidator.Validate(OrderType.Subscription, 1000m, null));

    [Fact]
    public void Valid_redemption_passes() =>
        Assert.Null(OrderValidator.Validate(OrderType.Redemption, null, 50m));

    [Fact]
    public void Subscription_by_units_is_rejected() =>
        Assert.NotNull(OrderValidator.Validate(OrderType.Subscription, 1000m, 10m));

    [Fact]
    public void Redemption_without_units_is_rejected() =>
        Assert.NotNull(OrderValidator.Validate(OrderType.Redemption, null, null));

    [Fact]
    public void Subscription_with_zero_amount_is_rejected() =>
        Assert.NotNull(OrderValidator.Validate(OrderType.Subscription, 0m, null));
}
