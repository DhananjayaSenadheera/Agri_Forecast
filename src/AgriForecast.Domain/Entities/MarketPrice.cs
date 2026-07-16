namespace AgriForecast.Domain.Entities;

public class MarketPrice
{
    public Guid Id { get; set; }

    public Guid? CropId { get; set; } // resolved via CommodityAliases (ProductId -> CropId) during ingestion
    public Guid? EconomicCenterId { get; set; }

    public int ExternalProductId { get; set; }
    public string ExternalProductName { get; set; } = "";

    public DateOnly PriceDate { get; set; }

    public decimal MinPrice { get; set; }
    public decimal MaxPrice { get; set; }

    public string Source { get; set; } = "";

    public DateTime RetrievedAtUtc { get; set; }
}