using System.Windows;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;
using ECS.CommissionsMailer.Services;
using ECS.CommissionsMailer.Views;

namespace ECS.CommissionsMailer.Tests;

public sealed class ModuleAccessRoutingTests
{
    [Fact]
    public void CommissionsOnlyRoutesDirectlyWithoutSelector()
    {
        var user = User(canUseCommissions: true, canUseExpirations: false);

        var resolution = ModuleAccessResolver.Resolve(user);

        Assert.False(resolution.RequiresSelection);
        Assert.Equal(ApplicationModule.Commissions, resolution.DirectModule);
        Assert.True(resolution.UsesCommissionsRuntime);
    }

    [Fact]
    public void ExpirationsOnlyRoutesDirectlyWithoutCommissionsRuntime()
    {
        var user = User(canUseCommissions: false, canUseExpirations: true);

        var resolution = ModuleAccessResolver.Resolve(user);

        Assert.False(resolution.RequiresSelection);
        Assert.Equal(ApplicationModule.Expirations, resolution.DirectModule);
        Assert.False(resolution.UsesCommissionsRuntime);
    }

    [Fact]
    public void BothPermissionsRequireModuleSelection()
    {
        var resolution = ModuleAccessResolver.Resolve(User(
            canUseCommissions: true,
            canUseExpirations: true));

        Assert.True(resolution.RequiresSelection);
        Assert.Null(resolution.DirectModule);
        Assert.False(resolution.UsesCommissionsRuntime);
    }

    [Fact]
    public void ActiveUserWithoutModulesIsDenied()
    {
        var error = Assert.Throws<AppUserAuthorizationException>(() => ModuleAccessResolver.Resolve(User(
            canUseCommissions: false,
            canUseExpirations: false)));

        Assert.Contains("ningún módulo", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InactiveUserIsDeniedRegardlessOfPermissions()
    {
        var error = Assert.Throws<AppUserAuthorizationException>(() => ModuleAccessResolver.Resolve(User(
            isActive: false,
            canUseCommissions: true,
            canUseExpirations: true)));

        Assert.Contains("inactivo", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommissionsSelectionDemandsPermissionAndUsesCommissionsRuntime()
    {
        var selected = ModuleAccessResolver.ValidateSelection(
            User(canUseCommissions: true, canUseExpirations: true),
            ApplicationModule.Commissions);

        Assert.Equal(ApplicationModule.Commissions, selected);
        Assert.True(ModuleAccessResolver.UsesCommissionsRuntime(selected!.Value));
    }

    [Fact]
    public void ExpirationsSelectionDemandsPermissionWithoutCommissionsRuntime()
    {
        var selected = ModuleAccessResolver.ValidateSelection(
            User(canUseCommissions: true, canUseExpirations: true),
            ApplicationModule.Expirations);

        Assert.Equal(ApplicationModule.Expirations, selected);
        Assert.False(ModuleAccessResolver.UsesCommissionsRuntime(selected!.Value));
    }

    [Fact]
    public void ExpirationsOnlyAdminCanAccessShellAndAdministration()
    {
        var user = User(
            role: AppUserRole.Admin,
            canUseCommissions: false,
            canUseExpirations: true);

        var state = new ExpirationsWindowState(user);

        AppUserAuthorization.DemandAdmin(user);
        Assert.Equal(Visibility.Visible, state.AdminAccessVisibility);
    }

    [Fact]
    public void ExpirationsOperatorDoesNotSeeAdministration()
    {
        var state = new ExpirationsWindowState(User(
            role: AppUserRole.Operator,
            canUseCommissions: false,
            canUseExpirations: true));

        Assert.Equal(Visibility.Collapsed, state.AdminAccessVisibility);
    }

    [Fact]
    public void JsonOnlyContinuesRoutingToCommissions()
    {
        Assert.Equal(ApplicationModule.Commissions, ModuleAccessResolver.ResolveJsonOnly());
        Assert.True(ModuleAccessResolver.UsesCommissionsRuntime(ModuleAccessResolver.ResolveJsonOnly()));
    }

    [Fact]
    public void ClosingSelectorWithoutSelectionDoesNotOpenAModule()
    {
        var selected = ModuleAccessResolver.ValidateSelection(
            User(canUseCommissions: true, canUseExpirations: true),
            selectedModule: null);

        Assert.Null(selected);
    }

    [Fact]
    public void ExpirationsLogoutRequestsControlledRestartWithoutCommissionsRoute()
    {
        var user = User(canUseCommissions: false, canUseExpirations: true);
        var state = new ExpirationsWindowState(user);
        var requestCount = 0;
        state.LogoutRequested += (_, _) => requestCount++;

        state.RequestLogout();

        Assert.Equal(1, requestCount);
        Assert.False(ModuleAccessResolver.Resolve(user).UsesCommissionsRuntime);
    }

    private static AppUser User(
        AppUserRole role = AppUserRole.Operator,
        bool isActive = true,
        bool canUseCommissions = false,
        bool canUseExpirations = false) => new()
    {
        Uid = "uid",
        Email = "user@example.test",
        DisplayName = "User",
        Role = role,
        IsActive = isActive,
        CanUseCommissions = canUseCommissions,
        CanUseExpirations = canUseExpirations,
        CreatedAtUtc = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
        UpdatedAtUtc = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero)
    };
}
