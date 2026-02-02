using AgriForecast.Application.Requests.Crop.DTOs;
using AgriForecast.Domain.Entities;
using AutoMapper;

namespace AgriForecast.Application.Mapper;

public class ProfileMapper : Profile
{
    public ProfileMapper()
    {
        //Crop
        CreateMap<Crop_CreateDto, Crop>()
            .ForMember(desc => desc.Id, opt => opt.MapFrom(src => Guid.NewGuid()))
            .ForMember(desc => desc.Name, opt => opt.MapFrom(src => src.Name))
            .ForMember(desc => desc.CreatedAt, opt => opt.MapFrom(src => DateTime.UtcNow))
            .ForMember(desc => desc.UpdatedAt, opt => opt.MapFrom(src => DateTime.UtcNow));

    }
}