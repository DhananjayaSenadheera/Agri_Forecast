namespace AgriForecast.Domain.Entities;

public class User
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "Farmer";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
