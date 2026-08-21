using System.Reflection;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;

namespace ECS.CommissionsMailer.Tests;

public sealed class OutlookAccountSelectionTests
{
    [Fact]
    public void SingleAccount_IsSelectedAutomatically()
    {
        var preferences = new MemoryPreferenceStore();
        var manager = new OutlookAccountSelectionManager(preferences);

        var state = manager.Refresh(["only@essential.test"]);

        Assert.Equal("only@essential.test", state.SelectedAccountEmail);
        Assert.Equal(["only@essential.test"], state.AvailableAccounts);
        Assert.Equal("only@essential.test", preferences.SelectedAccountEmail);
    }

    [Fact]
    public void MultipleAccounts_AreAllReturnedWithoutSilentSelection()
    {
        var manager = new OutlookAccountSelectionManager(new MemoryPreferenceStore());

        var state = manager.Refresh(["first@essential.test", "second@essential.test"]);

        Assert.Equal(["first@essential.test", "second@essential.test"], state.AvailableAccounts);
        Assert.Null(state.SelectedAccountEmail);
        Assert.True(state.RequiresExplicitSelection);
    }

    [Fact]
    public void SelectingAccountB_AssignsExactAccountToSendUsingAccount()
    {
        var manager = new OutlookAccountSelectionManager(new MemoryPreferenceStore());
        manager.Refresh(["a@essential.test", "b@essential.test"]);
        var selectedEmail = manager.Select("b@essential.test");
        var accountA = new FakeAccount("a@essential.test");
        var accountB = new FakeAccount("b@essential.test");
        var mailItem = new FakeMailItem();

        OutlookSendingAccountAssignment.AssignAndVerify(
            accountB,
            selectedEmail,
            account => mailItem.SendUsingAccount = account,
            () => mailItem.SendUsingAccount,
            account => account.EmailAddress,
            _ => { });

        Assert.Same(accountB, mailItem.SendUsingAccount);
        Assert.NotSame(accountA, mailItem.SendUsingAccount);
    }

    [Fact]
    public void SendUsingAccountReadBackMismatch_BlocksSending()
    {
        var selectedAccount = new FakeAccount("b@essential.test");
        var unexpectedAccount = new FakeAccount("a@essential.test");
        var mailItem = new FakeMailItem();

        var exception = Assert.Throws<OutlookIntegrationException>(() =>
            OutlookSendingAccountAssignment.AssignAndVerify(
                selectedAccount,
                selectedAccount.EmailAddress,
                account => mailItem.SendUsingAccount = account,
                () => unexpectedAccount,
                account => account.EmailAddress,
                _ => { }));

        Assert.Equal(OutlookFailureReason.SendingAccountUnavailable, exception.Reason);
        Assert.Contains("no confirmó", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectedAccount_RemainsAllowedWhenItIsStillAvailable()
    {
        var manager = new OutlookAccountSelectionManager(new MemoryPreferenceStore());
        manager.Refresh(["a@essential.test", "b@essential.test"]);
        manager.Select("b@essential.test");

        var selected = manager.RequireAvailableSelection(["a@essential.test", "b@essential.test"]);

        Assert.Equal("b@essential.test", selected);
    }

    [Fact]
    public void SelectedAccountDisappears_BlocksWithoutFallingBack()
    {
        var preferences = new MemoryPreferenceStore();
        var manager = new OutlookAccountSelectionManager(preferences);
        manager.Refresh(["a@essential.test", "b@essential.test"]);
        manager.Select("b@essential.test");

        var exception = Assert.Throws<OutlookIntegrationException>(() =>
            manager.RequireAvailableSelection(["a@essential.test"]));

        Assert.Equal(OutlookFailureReason.SendingAccountUnavailable, exception.Reason);
        Assert.Contains("b@essential.test", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("b@essential.test", manager.Snapshot().SelectedAccountEmail);
        Assert.Equal("b@essential.test", preferences.SelectedAccountEmail);
    }

    [Fact]
    public void MultipleAccountsWithoutPreviousSelection_BlocksSending()
    {
        var manager = new OutlookAccountSelectionManager(new MemoryPreferenceStore());
        manager.Refresh(["a@essential.test", "b@essential.test"]);

        var exception = Assert.Throws<OutlookIntegrationException>(() =>
            manager.RequireAvailableSelection(["a@essential.test", "b@essential.test"]));

        Assert.Equal(OutlookFailureReason.SendingAccountUnavailable, exception.Reason);
        Assert.Contains("Seleccione explícitamente", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingPersistedAccount_WithMultipleAccountsRequiresANewChoice()
    {
        var preferences = new MemoryPreferenceStore("missing@essential.test");
        var manager = new OutlookAccountSelectionManager(preferences);

        var state = manager.Refresh(["a@essential.test", "b@essential.test"]);

        Assert.Null(state.SelectedAccountEmail);
        Assert.True(state.RequiresExplicitSelection);
        Assert.Equal("missing@essential.test", preferences.SelectedAccountEmail);
    }

    [Fact]
    public void MissingPersistedAccount_WithOneAvailableAccountSelectsTheOnlyAccount()
    {
        var preferences = new MemoryPreferenceStore("missing@essential.test");
        var manager = new OutlookAccountSelectionManager(preferences);

        var state = manager.Refresh(["only@essential.test"]);

        Assert.Equal("only@essential.test", state.SelectedAccountEmail);
        Assert.Equal("only@essential.test", preferences.SelectedAccountEmail);
    }

    [Fact]
    public void PersistedSelection_IsRestoredAfterRestartWhenAccountStillExists()
    {
        using var scope = new TestDirectory();
        var paths = new AppDataPaths(scope.Path);
        var logger = new FileLogger(paths);
        var firstStore = new OutlookAccountPreferenceService(paths, logger);
        var firstRun = new OutlookAccountSelectionManager(firstStore);
        firstRun.Refresh(["a@essential.test", "b@essential.test"]);
        firstRun.Select("b@essential.test");

        var restarted = new OutlookAccountSelectionManager(
            new OutlookAccountPreferenceService(paths, logger));
        var restored = restarted.Refresh(["a@essential.test", "b@essential.test"]);

        Assert.Equal("b@essential.test", restored.SelectedAccountEmail);
    }

    [Fact]
    public void SelectionPreference_IsLocalOnlyAndIsNotPartOfFirestoreSettings()
    {
        using var scope = new TestDirectory();
        var paths = new AppDataPaths(scope.Path);
        var logger = new FileLogger(paths);
        var store = new OutlookAccountPreferenceService(paths, logger);

        store.SaveSelectedAccountEmail("local-outlook@essential.test");
        var firestoreFields = new FirestoreSettingsMapper().ToFields(AppConfiguration.CreateDefault());

        Assert.True(File.Exists(paths.OutlookPreferencesFile));
        Assert.StartsWith(paths.RootDirectory, paths.OutlookPreferencesFile, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("local-outlook@essential.test", store.LoadSelectedAccountEmail());
        Assert.DoesNotContain(firestoreFields.Keys, key =>
            key.Contains("outlook", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("sender", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("remitente", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FirebaseIdentity_DoesNotParticipateInOutlookAccountSelection()
    {
        var manager = new OutlookAccountSelectionManager(new MemoryPreferenceStore());
        manager.Refresh(["contabilidad@essential.test", "jonathan@essential.test"]);

        var selected = manager.Select("jonathan@essential.test");
        var constructorDependencies = new[]
            {
                typeof(OutlookAccountSelectionManager),
                typeof(OutlookAccountPreferenceService),
                typeof(OutlookEmailService)
            }
            .SelectMany(type => type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name)
            .ToArray();

        Assert.Equal("jonathan@essential.test", selected);
        Assert.DoesNotContain(constructorDependencies, dependency =>
            dependency.Contains("Firebase", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class MemoryPreferenceStore(string? selectedAccountEmail = null) : IOutlookAccountPreferenceStore
    {
        public string? SelectedAccountEmail { get; private set; } = selectedAccountEmail;

        public string? LoadSelectedAccountEmail() => SelectedAccountEmail;

        public void SaveSelectedAccountEmail(string emailAddress) => SelectedAccountEmail = emailAddress;
    }

    private sealed record FakeAccount(string EmailAddress);

    private sealed class FakeMailItem
    {
        public FakeAccount? SendUsingAccount { get; set; }
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ECSCommissionsMailer-OutlookTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
