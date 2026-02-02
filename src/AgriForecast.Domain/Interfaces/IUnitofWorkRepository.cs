namespace AgriForecast.Domain.Interfaces;

public interface IUnitofWorkRepository : IDisposable
{
    Task CommitAsync();
}