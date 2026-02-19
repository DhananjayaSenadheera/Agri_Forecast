namespace AgriForecast.Domain.Entities;

public class MarketPrice
{
    public Guid Id { get; set; }

    public int? CropId { get; set; } // optional until mapping implemented
    public int? EconomicCenterId { get; set; }

    public int ExternalProductId { get; set; }
    public string ExternalProductName { get; set; } = "";

    public DateOnly PriceDate { get; set; }

    public decimal MinPrice { get; set; }
    public decimal MaxPrice { get; set; }

    public string Source { get; set; } = "";

    public DateTime RetrievedAtUtc { get; set; }
}