using AgriForecast.Application.Requests.Market.DTOs;
using MarketEntity = AgriForecast.Domain.Entities.Market;

namespace AgriForecast.Application.Mapper;

// Hand-written static mapper. MarketCode is stamped by the handler after construction.
public static class MarketMapper
{
    public static MarketEntity ToEntity(this Market_CreateDto src)
    {
        return MarketEntity.CreateNew(
            name: src.Name,
            district: src.District,
            marketType: src.MarketType,
            isEconomicCenter: src.IsEconomicCenter);
    }
}
