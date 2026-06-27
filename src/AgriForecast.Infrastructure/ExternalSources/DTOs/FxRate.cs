namespace AgriForecast.Infrastructure.ExternalSources.DTOs;

// Provider-agnostic FX reading: the rate for the given date (e.g. 1 USD = Rate LKR).
public sealed record FxRate(DateOnly Date, decimal Rate);
