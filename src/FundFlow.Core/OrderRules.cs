namespace FundFlow.Core;

public static class UnitCalculator
{
    public const int UnitDecimals = 4;
    public const int AmountDecimals = 2;

    /// <summary>Subscription: units = amount / NAV, truncated (never give out more units than paid for).</summary>
    public static decimal UnitsForAmount(decimal amount, decimal nav)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive");
        if (nav <= 0) throw new ArgumentOutOfRangeException(nameof(nav), "NAV must be positive");
        return Math.Round(amount / nav, UnitDecimals, MidpointRounding.ToZero);
    }

    /// <summary>Redemption: amount = units * NAV, truncated to satang.</summary>
    public static decimal AmountForUnits(decimal units, decimal nav)
    {
        if (units <= 0) throw new ArgumentOutOfRangeException(nameof(units), "Units must be positive");
        if (nav <= 0) throw new ArgumentOutOfRangeException(nameof(nav), "NAV must be positive");
        return Math.Round(units * nav, AmountDecimals, MidpointRounding.ToZero);
    }
}

public static class OrderValidator
{
    /// <returns>An error message, or null when the order is valid.</returns>
    public static string? Validate(OrderType type, decimal? amount, decimal? units)
    {
        return type switch
        {
            OrderType.Subscription when amount is null or <= 0 => "Subscription requires a positive amount",
            OrderType.Subscription when units is not null => "Subscription is placed by amount, not units",
            OrderType.Redemption when units is null or <= 0 => "Redemption requires positive units",
            OrderType.Redemption when amount is not null => "Redemption is placed by units, not amount",
            _ => null
        };
    }
}
