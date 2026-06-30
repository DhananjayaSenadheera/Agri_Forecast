using AgriForecast.Application.Requests.Crop.DTOs;
using AgriForecast.Application.Requests.EcconomicCenter.DTOs;
using AgriForecast.Application.Requests.PolicyFlag.DTOs;
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
            .ForMember(desc => desc.ExternalProductId, opt => opt.MapFrom(src => src.ExternalProductId))
            .ForMember(desc => desc.Source, opt => opt.MapFrom(src => src.Source))
            .ForMember(desc => desc.CreatedAt, opt => opt.MapFrom(src => DateTime.UtcNow))
            .ForMember(desc => desc.UpdatedAt, opt => opt.MapFrom(src => DateTime.UtcNow));

        CreateMap<Crop_UpdateDto, Crop>()
            .ForMember(desc => desc.Id, opt => opt.MapFrom(src => src.Id))
            .ForMember(desc => desc.Name, opt => opt.MapFrom(src => src.Name))
            // Only overwrite the mapping fields when the update actually supplies them.
            .ForMember(desc => desc.ExternalProductId, opt =>
            {
                opt.PreCondition(src => src.ExternalProductId.HasValue);
                opt.MapFrom(src => src.ExternalProductId);
            })
            .ForMember(desc => desc.Source, opt =>
            {
                opt.PreCondition(src => src.Source != null);
                opt.MapFrom(src => src.Source);
            })
            .ForMember(desc => desc.UpdatedAt, opt => opt.MapFrom(src => DateTime.UtcNow));

        CreateMap<Crop ,Crop_GetDto>()
            .ForMember(desc => desc.Name, opt => opt.MapFrom(src => src.Name))
            .ForMember(desc => desc.ExternalProductId, opt => opt.MapFrom(src => src.ExternalProductId))
            .ForMember(desc => desc.Source, opt => opt.MapFrom(src => src.Source))
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
    
            CreateMap<EconomicCenter, Eco_GetDto>()
                .ForMember(desc => desc.Name, opt => opt.MapFrom(src => src.Name))
                .ForMember(desc => desc.Location, opt => opt.MapFrom(src => src.Location))
                .ForMember(desc => desc.Description, opt => opt.MapFrom(src => src.Description))
                .ForMember(desc => desc.CreatedAt, opt => opt.MapFrom(src => src.CreatedAt))
                .ForMember(desc => desc.UpdatedAt, opt => opt.MapFrom(src => src.UpdatedAt))
                .ForMember(desc => desc.Id, opt => opt.MapFrom(src => src.Id));

        //Policy Flag
        // Dates normalised to date-only so storage carries no hidden time (leakage guard);
        // EffectiveTo stays nullable. CreatedAtUtc is set here (audit only).
        CreateMap<PolicyFlag_CreateDto, PolicyFlag>()
            .ForMember(desc => desc.Id, opt => opt.MapFrom(src => Guid.NewGuid()))
            .ForMember(desc => desc.PolicyType, opt => opt.MapFrom(src => src.PolicyType))
            .ForMember(desc => desc.Title, opt => opt.MapFrom(src => src.Title))
            .ForMember(desc => desc.Description, opt => opt.MapFrom(src => src.Description))
            .ForMember(desc => desc.EffectiveFrom, opt => opt.MapFrom(src => src.EffectiveFrom.Date))
            .ForMember(desc => desc.EffectiveTo, opt => opt.MapFrom(src => src.EffectiveTo.HasValue ? src.EffectiveTo.Value.Date : (DateTime?)null))
            .ForMember(desc => desc.Direction, opt => opt.MapFrom(src => src.Direction))
            .ForMember(desc => desc.Source, opt => opt.MapFrom(src => src.Source))
            .ForMember(desc => desc.ReferenceUrl, opt => opt.MapFrom(src => src.ReferenceUrl))
            .ForMember(desc => desc.CreatedAtUtc, opt => opt.MapFrom(src => DateTime.UtcNow));

        CreateMap<PolicyFlag, PolicyFlag_GetDto>();
    }
}