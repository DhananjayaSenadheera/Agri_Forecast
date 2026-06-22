using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.Forecast.DTOs;

public class MonthlyForecast_GetDto
{
    public Guid CropId { get; set; }
    public string CropName { get; set; } = string.Empty;
    public DateTime Month { get; set; }
    public decimal AveragePrice { get; set; }
    public decimal MinPrice { get; set; }
    public decimal MaxPrice { get; set; }
    public int DataPoints { get; set; }
    public PriceTrend Trend { get; set; }
    public ForecastConfidence Confidence { get; set; }
}
