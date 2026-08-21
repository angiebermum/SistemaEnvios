using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;
using ECS.CommissionsMailer.Views;

namespace ECS.CommissionsMailer.Tests;

public sealed class UserAdministrationAccessTests
{
    [Fact]
    public void ChangingOnlyExpirationsPreservesCommissionsPermission()
    {
        var existing = User(canUseCommissions: true, canUseExpirations: false);

        var updated = UserAdministrationWindow.BuildUpdatedUser(
            existing,
            existing.Role,
            existing.IsActive,
            existing.CanUseCommissions,
            canUseExpirations: true,
            updatedAtUtc: new DateTimeOffset(2026, 8, 21, 1, 0, 0, TimeSpan.Zero));

        Assert.True(updated.CanUseCommissions);
        Assert.True(updated.CanUseExpirations);
        AssertPreservedIdentity(existing, updated);
    }

    [Fact]
    public void ChangingOnlyCommissionsPreservesExpirationsPermission()
    {
        var existing = User(canUseCommissions: false, canUseExpirations: true);

        var updated = UserAdministrationWindow.BuildUpdatedUser(
            existing,
            existing.Role,
            existing.IsActive,
            canUseCommissions: true,
            canUseExpirations: existing.CanUseExpirations,
            updatedAtUtc: new DateTimeOffset(2026, 8, 21, 2, 0, 0, TimeSpan.Zero));

        Assert.True(updated.CanUseCommissions);
        Assert.True(updated.CanUseExpirations);
        AssertPreservedIdentity(existing, updated);
    }

    private static AppUser User(bool canUseCommissions, bool canUseExpirations) => new()
    {
        Uid = "uid",
        Email = "user@example.test",
        DisplayName = "User",
        Role = AppUserRole.Operator,
        IsActive = true,
        CanUseCommissions = canUseCommissions,
        CanUseExpirations = canUseExpirations,
        CreatedAtUtc = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
        UpdatedAtUtc = new DateTimeOffset(2026, 8, 10, 1, 0, 0, TimeSpan.Zero)
    };

    private static void AssertPreservedIdentity(AppUser existing, AppUser updated)
    {
        Assert.Equal(existing.Uid, updated.Uid);
        Assert.Equal(existing.Email, updated.Email);
        Assert.Equal(existing.DisplayName, updated.DisplayName);
        Assert.Equal(existing.CreatedAtUtc, updated.CreatedAtUtc);
    }
}
