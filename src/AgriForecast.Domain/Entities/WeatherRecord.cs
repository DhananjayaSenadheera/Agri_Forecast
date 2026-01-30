namespace AgriForecast.Domain.Entities;

public class WeatherRecord
{
    public Guid Id { get; private set; }

    public DateTime Month { get; private set; }   // YYYY-MM-01
    public decimal AvgTemperature { get; private set; }
    public decimal TotalRainfall { get; private set; }

    private WeatherRecord() { } 
    public WeatherRecord(
        DateTime month,
        decimal avgTemperature,
        decimal totalRainfall)
    {
        if (month.Day != 1)
            throw new ArgumentException("Month must be the first day of the month", nameof(month));

        if (avgTemperature < -10 || avgTemperature > 60)
            throw new ArgumentException("Average temperature value is not realistic", nameof(avgTemperature));

        if (totalRainfall < 0)
            throw new ArgumentException("Total rainfall cannot be negative", nameof(totalRainfall));

        Month = month;
        AvgTemperature = avgTemperature;
        TotalRainfall = totalRainfall;
    }
}