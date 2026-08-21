using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;

namespace ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;

public enum AppUserRole
{
    Admin,
    Operator
}

public sealed class AppUser
{
    public string Uid { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public AppUserRole Role { get; set; } = AppUserRole.Operator;
    public bool IsActive { get; set; }
    public bool CanUseCommissions { get; set; }
    public bool CanUseExpirations { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class AppUserAuthorizationException(string safeMessage) : UnauthorizedAccessException(safeMessage);

public sealed class AppUserMapper : IFirestoreEntityMapper<AppUser>
{
    public IReadOnlyDictionary<string, FirestoreRestValue> ToFields(AppUser value) =>
        new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["uid"] = FirestoreRestValue.String(value.Uid),
            ["email"] = FirestoreRestValue.String(value.Email),
            ["displayName"] = FirestoreRestValue.String(value.DisplayName),
            ["role"] = FirestoreRestValue.String(value.Role == AppUserRole.Admin ? "admin" : "operator"),
            ["isActive"] = FirestoreRestValue.Boolean(value.IsActive),
            ["canUseCommissions"] = FirestoreRestValue.Boolean(value.CanUseCommissions),
            ["canUseExpirations"] = FirestoreRestValue.Boolean(value.CanUseExpirations),
            ["createdAtUtc"] = FirestoreRestValue.Timestamp(value.CreatedAtUtc),
            ["updatedAtUtc"] = FirestoreRestValue.Timestamp(value.UpdatedAtUtc)
        };

    public AppUser FromFields(IReadOnlyDictionary<string, FirestoreRestValue> fields)
    {
        var role = fields.Required("role").RequireString("role");
        return new AppUser
        {
            Uid = fields.Required("uid").RequireString("uid"),
            Email = fields.Required("email").RequireString("email"),
            DisplayName = fields.Required("displayName").RequireString("displayName"),
            Role = role switch
            {
                "admin" => AppUserRole.Admin,
                "operator" => AppUserRole.Operator,
                _ => throw new InvalidDataException("El perfil appUsers contiene un role no permitido.")
            },
            IsActive = fields.Required("isActive").RequireBoolean("isActive"),
            CanUseCommissions = fields.Required("canUseCommissions").RequireBoolean("canUseCommissions"),
            CanUseExpirations = fields.Optional("canUseExpirations")?.RequireBoolean("canUseExpirations") ?? false,
            CreatedAtUtc = fields.Required("createdAtUtc").RequireTimestamp("createdAtUtc"),
            UpdatedAtUtc = fields.Required("updatedAtUtc").RequireTimestamp("updatedAtUtc")
        };
    }
}

public interface IAppUserRepository
{
    Task<FirestoreStoredDocument<AppUser>?> GetAsync(string uid, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FirestoreStoredDocument<AppUser>>> ListAsync(CancellationToken cancellationToken = default);
    Task<FirestoreStoredDocument<AppUser>> CreateAsync(AppUser value, CancellationToken cancellationToken = default);
    Task<FirestoreStoredDocument<AppUser>> UpdateAsync(AppUser value, string expectedUpdateTime, CancellationToken cancellationToken = default);
}

public sealed class AppUserRepository(IFirestoreRestClient client) : IAppUserRepository
{
    private readonly FirestoreCollectionRepository<AppUser> _repository =
        new(client, new AppUserMapper(), string.Empty, "appUsers");

    public Task<FirestoreStoredDocument<AppUser>?> GetAsync(string uid, CancellationToken cancellationToken = default) =>
        _repository.GetAsync(uid, cancellationToken);
    public Task<IReadOnlyList<FirestoreStoredDocument<AppUser>>> ListAsync(CancellationToken cancellationToken = default) =>
        _repository.ListAsync(cancellationToken: cancellationToken);
    public Task<FirestoreStoredDocument<AppUser>> CreateAsync(AppUser value, CancellationToken cancellationToken = default) =>
        _repository.CreateAsync(value.Uid, value, cancellationToken);
    public Task<FirestoreStoredDocument<AppUser>> UpdateAsync(AppUser value, string expectedUpdateTime, CancellationToken cancellationToken = default) =>
        _repository.UpdateAsync(value.Uid, value, expectedUpdateTime, cancellationToken);
}

public static class AppUserAuthorization
{
    public static void DemandActiveUser(AppUser? user)
    {
        if (user is null)
            throw new AppUserAuthorizationException("No existe un perfil de acceso para esta cuenta.");
        if (!user.IsActive)
            throw new AppUserAuthorizationException("Este perfil de la aplicación está inactivo.");
    }

    public static void DemandCommissionsAccess(AppUser? user)
    {
        DemandActiveUser(user);
        if (!user!.CanUseCommissions)
            throw new AppUserAuthorizationException("Este perfil no tiene permiso para usar Comisiones.");
    }

    public static void DemandExpirationsAccess(AppUser? user)
    {
        DemandActiveUser(user);
        if (!user!.CanUseExpirations)
            throw new AppUserAuthorizationException("Este perfil no tiene permiso para usar Vencimientos.");
    }

    public static void DemandAnyModuleAccess(AppUser? user)
    {
        DemandActiveUser(user);
        if (!user!.CanUseCommissions && !user.CanUseExpirations)
            throw new AppUserAuthorizationException("Este perfil no tiene permiso para usar ningún módulo de ECS.");
    }

    public static void DemandAdmin(AppUser? user)
    {
        DemandActiveUser(user);
        if (user!.Role != AppUserRole.Admin)
            throw new AppUserAuthorizationException("Esta operación requiere el rol admin.");
    }
}
