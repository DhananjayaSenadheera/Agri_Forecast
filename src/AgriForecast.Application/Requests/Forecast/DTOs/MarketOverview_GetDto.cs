namespace AgriForecast.Application.Requests.Forecast.DTOs;

// Response for GET /api/forecast/market-overview. Contract is LOCKED (the farmer
// app is built against it in parallel): field names + camelCase JSON must not drift.
// All dates are yyyy-MM-dd strings; money is decimal (existing DTO idiom).
public class MarketOverview_GetDto
{
    // Latest observation date across MarketPrices (yyyy-MM-dd), or null when empty.
    public string? AsOf { get; set; }
    public int WindowDays { get; set; }
    public int MarketsWithData { get; set; }
    public int CropsWithData { get; set; }
    public List<MarketMover_GetDto> Movers { get; set; } = new();
    public List<MarketLatestPrice_GetDto> LatestPrices { get; set; } = new();
}

public class MarketMover_GetDto
{
    public Guid CropId { get; set; }
    public string CropName { get; set; } = string.Empty;
    public string MarketName { get; set; } = string.Empty;
    public decimal LatestPrice { get; set; }
    public decimal PreviousPrice { get; set; }
    public decimal ChangePct { get; set; }
    // Plain string "up" | "down" — NOT an enum (this API has no JsonStringEnumConverter,
    // an enum would serialize as an int and break the frontend).
    public string Direction { get; set; } = string.Empty;
}

public class MarketLatestPrice_GetDto
{
    public Guid CropId { get; set; }
    public string CropName { get; set; } = string.Empty;
    public string MarketName { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal MinPrice { get; set; }
    public decimal MaxPrice { get; set; }
    public List<MarketSparkPoint_GetDto> Spark { get; set; } = new();
}

public class MarketSparkPoint_GetDto
{
    public string Date { get; set; } = string.Empty;
    public decimal Price { get; set; }
}
