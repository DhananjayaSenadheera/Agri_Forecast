using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.Crop.DTOs;

public class BestCrop_GetDto
{
    public Guid CropId { get; set; }
    public string CropName { get; set; } = string.Empty;
    public string CropCode { get; set; } = string.Empty;
    public decimal AveragePrice { get; set; }
    public PriceTrend Trend { get; set; }
    public ForecastConfidence Confidence { get; set; }
    public RecommendationLevel RecommendationLevel { get; set; }
}
