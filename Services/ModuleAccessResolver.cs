using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;

namespace ECS.CommissionsMailer.Services;

public enum ApplicationModule
{
    Commissions,
    Expirations
}

public sealed record ModuleAccessResolution(ApplicationModule? DirectModule, bool RequiresSelection)
{
    public bool UsesCommissionsRuntime => DirectModule == ApplicationModule.Commissions;
}

public static class ModuleAccessResolver
{
    public static bool CanSwitchModules(AppUser? user) => user is
        { IsActive: true, CanUseCommissions: true, CanUseExpirations: true };

    public static ModuleAccessResolution Resolve(AppUser? user)
    {
        AppUserAuthorization.DemandAnyModuleAccess(user);
        if (user!.CanUseCommissions && user.CanUseExpirations)
            return new ModuleAccessResolution(null, RequiresSelection: true);

        return new ModuleAccessResolution(
            user.CanUseCommissions ? ApplicationModule.Commissions : ApplicationModule.Expirations,
            RequiresSelection: false);
    }

    public static ApplicationModule ResolveJsonOnly() => ApplicationModule.Commissions;

    public static ApplicationModule? ValidateSelection(AppUser user, ApplicationModule? selectedModule)
    {
        if (selectedModule is null) return null;
        DemandModuleAccess(user, selectedModule.Value);
        return selectedModule;
    }

    public static void DemandModuleAccess(AppUser user, ApplicationModule module)
    {
        if (module == ApplicationModule.Commissions)
            AppUserAuthorization.DemandCommissionsAccess(user);
        else
            AppUserAuthorization.DemandExpirationsAccess(user);
    }

    public static bool UsesCommissionsRuntime(ApplicationModule module) =>
        module == ApplicationModule.Commissions;
}
