namespace AgriForecast.Domain.Entities;

public class CropPrice
{
    public Guid Id { get; private set; }
    public Guid CropId { get; private set; }
    public Crop Crop { get; private set; } = null!;
    public Guid EconomicCenterId { get; private set; }
    public EconomicCenter EconomicCenter { get; private set; } = null!;
    public DateTime Month { get; private set; }   // YYYY-MM-01
    public decimal AveragePrice { get; private set; }
    private CropPrice() { } 
    public CropPrice(
        Guid cropId,
        Guid economicCenterId,
        DateTime month,
        decimal averagePrice)
    {
        if (month.Day != 1)
            throw new ArgumentException("Month must be the first day of the month", nameof(month));

        if (averagePrice <= 0)
            throw new ArgumentException("Average price must be greater than zero", nameof(averagePrice));

        CropId = cropId;
        EconomicCenterId = economicCenterId;
        Month = month;
        AveragePrice = averagePrice;
    }
}