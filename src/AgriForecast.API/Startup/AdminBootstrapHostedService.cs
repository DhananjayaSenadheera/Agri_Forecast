using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Interfaces;

namespace AgriForecast.API.Startup;

/// <summary>
/// One-time, config-driven admin bootstrap (API-9). On startup, if <c>Auth:BootstrapAdminUsername</c>
/// is set AND no user currently holds the Admin role, the named EXISTING user is promoted to Admin
/// and the promotion is logged (username only — no secrets). It is a no-op when the config key is
/// absent/blank, when an admin already exists, or when the named user does not exist.
///
/// <para>Rationale (owner-approved default): a seeded admin with baked-in credentials in the repo is
/// a standing breach. Promoting an owner-registered account leaves NO secret material in git — the
/// account's password is created through the normal registration flow. There is deliberately no
/// migration seeding an admin and no admin credential in any committed appsettings file.</para>
///
/// <para>Update path: register the intended admin through <c>POST /api/auth/register</c> (creates a
/// Farmer), set <c>Auth:BootstrapAdminUsername</c> to that username via user-secrets / the
/// <c>Auth__BootstrapAdminUsername</c> environment variable (NOT committed appsettings.json), and
/// restart the API once. After promotion the key can be removed; the guard makes a second run a
/// no-op anyway.</para>
/// </summary>
public class AdminBootstrapHostedService : IHostedService
{
    public const string ConfigKey = "Auth:BootstrapAdminUsername";

    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminBootstrapHostedService> _logger;

    public AdminBootstrapHostedService(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<AdminBootstrapHostedService> logger)
    {
        _services = services;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var username = _configuration[ConfigKey]?.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            // No bootstrap configured — nothing to do (the common steady-state).
            return;
        }

        // Repositories are scoped; a hosted service is a singleton, so open a DI scope.
        using var scope = _services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitofWorkRepository>();

        try
        {
            var adminCount = await users.CountByRoleAsync(UserRoles.Admin);
            if (adminCount > 0)
            {
                // An admin already exists — never auto-promote a second one.
                _logger.LogInformation(
                    "Admin bootstrap skipped: an admin already exists ({AdminCount}).", adminCount);
                return;
            }

            var user = await users.GetByUsernameAsync(username);
            if (user is null)
            {
                // Fail loud but non-fatal: the operator named a user that has not registered yet.
                _logger.LogWarning(
                    "Admin bootstrap: configured username was not found; no promotion performed. " +
                    "Register that account first, then restart.");
                return;
            }

            if (string.Equals(user.Role, UserRoles.Admin, StringComparison.OrdinalIgnoreCase))
            {
                // Defensive: adminCount==0 above means this branch is unreachable in practice.
                _logger.LogInformation("Admin bootstrap: configured user is already an admin; no change.");
                return;
            }

            user.Role = UserRoles.Admin;
            user.UpdatedAt = DateTime.UtcNow;
            await users.UpdateAsync(user);
            await unitOfWork.CommitAsync();

            _logger.LogInformation("Admin bootstrap: promoted user {Username} to Admin.", user.Username);
        }
        catch (Exception ex)
        {
            // Bootstrap must never crash the app on startup (e.g. DB not reachable yet). Log and
            // continue — no stack trace leaks to any client (this runs before the HTTP pipeline).
            _logger.LogError(ex, "Admin bootstrap failed; continuing startup without promotion.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
