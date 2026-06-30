namespace AgriForecast.Domain.Enums;

// Coarse human judgement of a policy's expected impact on harvest-time price.
// This is the signal the ML layer as-of-joins on; it is deliberately simple.
public enum PolicyDirection
{
    Bearish = -1,   // expected to push prices down
    Neutral = 0,
    Bullish = 1     // expected to push prices up
}
