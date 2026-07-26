namespace AgriForecast.Domain.Enums;

// Coarse expected impact of a policy on harvest-time price; the signal the ML layer as-of-joins on.
public enum PolicyDirection
{
    Bearish = -1,   // expected to push prices down
    Neutral = 0,
    Bullish = 1     // expected to push prices up
}
