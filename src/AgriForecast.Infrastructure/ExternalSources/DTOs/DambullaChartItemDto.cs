using System.Text.Json.Serialization;

namespace AgriForecast.Infrastructure.ExternalSources.DTOs;

public sealed class DambullaChartItemDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("product_id")]
    public int ProductId { get; set; }

    [JsonPropertyName("min_price")]
    public decimal MinPrice { get; set; }

    [JsonPropertyName("max_price")]
    public decimal MaxPrice { get; set; }

    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    //API uses "Product" (capital P)
    [JsonPropertyName("Product")]
    public DambullaProductDto? Product { get; set; }
}
public sealed class DambullaProductDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}


