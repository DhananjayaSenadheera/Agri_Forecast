using AgriForecast.Application.Requests.Crop.DTOs;
using AgriForecast.Application.Requests.EcconomicCenter.DTOs;
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
        
        CreateMap<Crop_UpdateDto, Crop>()
            .ForMember(desc => desc.Id, opt => opt.MapFrom(src => src.Id))
            .ForMember(desc => desc.Name, opt => opt.MapFrom(src => src.Name))
            .ForMember(desc => desc.UpdatedAt, opt => opt.MapFrom(src => DateTime.UtcNow));
        
        CreateMap<Crop ,Crop_GetDto>()
            .ForMember(desc => desc.Name, opt => opt.MapFrom(src => src.Name))
            .ForMember(desc => desc.CreatedAt, opt => opt.MapFrom(src => src.CreatedAt))
            .ForMember(desc => desc.UpdatedAt, opt => opt.MapFrom(src => src.UpdatedAt))
            .ForMember(desc => desc.Id, opt => opt.MapFrom(src => src.Id));
        
        CreateMap<Crop_DeleteDto, Crop>()
            .ForMember(desc => desc.Id, opt => opt.MapFrom(src => src.Id));
        
        //Economic Center
        CreateMap<Eco_CreateDto , EconomicCenter>()
            .ForMember(desc => desc.Id, opt => opt.MapFrom(src => Guid.NewGuid()))
            .ForMember(desc => desc.Name, opt => opt.MapFrom(src => src.Name))
            .ForMember(desc => desc.CreatedAt, opt => opt.MapFrom(src => DateTime.UtcNow))
            .ForMember(desc => desc.UpdatedAt, opt => opt.MapFrom(src => DateTime.UtcNow));
        
        CreateMap<Eco_DeleteDto, EconomicCenter>()
            .ForMember(desc => desc.Id, opt => opt.MapFrom(src => Guid.NewGuid()));
        
        CreateMap<Eco_UpdateDto, EconomicCenter>()
            .ForMember(desc => desc.Id, opt => opt.MapFrom(src => src.Id))
            .ForMember(desc => desc.Name, opt => opt.MapFrom(src => src.Name))
            .ForMember(desc => desc.Location, opt => opt.MapFrom(src => src.Location))
            .ForMember(desc => desc.Description, opt => opt.MapFrom(src => src.Description))
            .ForMember(desc => desc.UpdatedAt, opt => opt.MapFrom(src => DateTime.UtcNow));
        
    }
}