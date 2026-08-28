using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;
using ECS.CommissionsMailer.Services.Expirations;
using ECS.CommissionsMailer.Views;

namespace ECS.CommissionsMailer.Tests;

public sealed class ExpirationsAssistantAndConfigurationUiTests
{
    private static readonly Guid BrokerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly ExpirationsAssistantValidationService _validation = new();

    [Theory]
    [InlineData("", "assistant@example.test", "nombre")]
    [InlineData("Asistente", "", "obligatorio")]
    [InlineData("Asistente", "correo inválido", "válido")]
    public void AssistantRequiredFieldsAndEmailAreValidated(string name, string email, string expected)
    {
        var result = _validation.CreateNew(name, email);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidNewAssistantIsNormalizedActiveAndGetsNewId()
    {
        var result = _validation.CreateNew("  Ana Luisa  ", "  ana@example.test  ");

        var assistant = Assert.IsType<ExpirationsAssistant>(result.Assistant);
        Assert.NotEqual(Guid.Empty, assistant.Id);
        Assert.Equal("Ana Luisa", assistant.Name);
        Assert.Equal("ana@example.test", assistant.Email);
        Assert.True(assistant.IsActive);
    }

    [Fact]
    public void EditingAssistantPreservesId()
    {
        var id = Guid.NewGuid();
        var result = _validation.Edit(id, "Nombre nuevo", "new@example.test", false);

        Assert.Equal(id, Assert.IsType<ExpirationsAssistant>(result.Assistant).Id);
        Assert.False(result.Assistant!.IsActive);
    }

    [Fact]
    public void DuplicateAssistantEmailsAreRejectedCaseInsensitively()
    {
        var result = _validation.ValidateAndNormalize(
            [],
            [Assistant("Uno", "same@example.test"), Assistant("Dos", "SAME@example.test")]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("ya pertenece", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AssistantCannotRepeatAnyPrimaryEmailCaseInsensitively()
    {
        var result = _validation.ValidateAndNormalize(
            ["broker@example.test", "other@example.test"],
            [Assistant("Asistente", "BROKER@example.test")]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error == "Este correo ya pertenece al corredor o a otro asistente.");
    }

    [Fact]
    public void ManagementSearchFiltersLocallyByNameAndAnyEmail()
    {
        var state = new ExpirationsBrokerManagementState();
        state.SetItems([
            Configuration("Arturo Quesada", ["arturo@example.test"]),
            Configuration("Jerrika", ["jerrika@example.test", "office@example.test"])
        ]);

        state.SearchText = "quesada";
        Assert.Equal("Arturo Quesada", Assert.Single(state.VisibleItems).Name);
        state.SearchText = "OFFICE@";
        Assert.Equal("Jerrika", Assert.Single(state.VisibleItems).Name);
        Assert.False(state.CanConfigure);
        state.SelectedItem = state.VisibleItems[0];
        Assert.True(state.CanConfigure);
    }

    [Fact]
    public void ManagementSeparatesActiveAndInactiveExpirationsProfiles()
    {
        var state = new ExpirationsBrokerManagementState();
        state.SetItems([
            Configuration("Activo", ["active@example.test"]),
            new ExpirationsBrokerConfigurationItem
            {
                BrokerId = Guid.NewGuid(),
                Name = "Inactivo",
                PrimaryEmailAddresses = ["inactive@example.test"],
                IsActive = false
            }
        ]);

        Assert.Equal("Activo", Assert.Single(state.VisibleItems).Name);
        Assert.Equal(1, state.InactiveCount);
        Assert.Equal("Ver inactivos (1)", state.InactiveButtonText);
        state.ToggleInactiveVisibility();
        Assert.Equal("Inactivo", Assert.Single(state.VisibleItems).Name);
        Assert.All(state.VisibleItems, item => Assert.False(item.IsActive));
        Assert.Equal("Ver todos", state.InactiveButtonText);
        state.ToggleInactiveVisibility();
        Assert.Equal("Activo", Assert.Single(state.VisibleItems).Name);
        Assert.All(state.VisibleItems, item => Assert.True(item.IsActive));
        Assert.Equal("Ver inactivos (1)", state.InactiveButtonText);
        state.SetExclusionCount(3);
        Assert.Equal("Ver excluidos (3)", state.ExclusionsButtonText);
    }

    [Theory]
    [InlineData(ExpirationsProcess.PreviousMonth, 1, 2)]
    [InlineData(ExpirationsProcess.NextMonth, 1, 2)]
    [InlineData(ExpirationsProcess.Cancellations, 2, 3)]
    public void ManagementAssistantCountersUseOnlyTheSelectedProcessList(
        ExpirationsProcess process,
        int expectedActive,
        int expectedTotal)
    {
        var configuration = new ExpirationsBrokerConfigurationItem
        {
            BrokerId = BrokerId,
            Name = "Corredor",
            Assistants =
            [
                Assistant("Regular activo", "regular-active@example.test", true),
                Assistant("Regular inactivo", "regular-inactive@example.test", false)
            ],
            CancellationAssistants =
            [
                Assistant("Cancelación uno", "cancellation-one@example.test", true),
                Assistant("Cancelación dos", "cancellation-two@example.test", true),
                Assistant("Cancelación inactivo", "cancellation-inactive@example.test", false)
            ]
        };
        var state = new ExpirationsBrokerManagementState(process);

        state.SetItems([configuration]);

        var visible = Assert.Single(state.VisibleItems);
        Assert.Equal(expectedActive, visible.ActiveAssistantCount);
        Assert.Equal(expectedTotal, visible.TotalAssistantCount);
    }

    [Fact]
    public void ProfileStateTracksCountsLocalChangesToggleAndDiscard()
    {
        var active = Assistant("Ana", "ana@example.test", true);
        var inactive = Assistant("Beatriz", "beatriz@example.test", false);
        var configuration = Configuration("Jerrika", ["jerrika@example.test"], [active, inactive]);
        var state = new ExpirationsBrokerProfileState(configuration, _validation);

        Assert.Equal(1, state.ActiveAssistantCount);
        Assert.Equal(2, state.TotalAssistantCount);
        state.SelectedAssistant = state.Assistants.Single(value => value.Id == inactive.Id);
        state.ToggleSelectedAssistant();
        Assert.Equal(2, state.ActiveAssistantCount);
        Assert.True(state.HasUnsavedChanges);
        Assert.Equal(inactive.Id, state.SelectedAssistant!.Id);
        state.ToggleSelectedAssistant();
        Assert.Equal(inactive.Id, state.SelectedAssistant!.Id);
        Assert.False(state.SelectedAssistant.IsActive);
        state.ToggleSelectedAssistant();
        Assert.Equal(inactive.Id, state.SelectedAssistant!.Id);
        Assert.True(state.SelectedAssistant.IsActive);

        state.DiscardChanges();
        Assert.Equal(1, state.ActiveAssistantCount);
        Assert.False(state.HasUnsavedChanges);
        Assert.Contains(state.Assistants, value => value.Id == inactive.Id && !value.IsActive);
    }

    [Fact]
    public void ProfileStateAddEditAndPersistPreserveAssistantIdentity()
    {
        var state = new ExpirationsBrokerProfileState(
            Configuration("Arturo", ["arturo@example.test"]),
            _validation);
        var added = Assert.IsType<ExpirationsAssistant>(
            _validation.CreateNew("Asistente", "assistant@example.test").Assistant);
        Assert.Empty(state.AddOrReplaceAssistant(added));
        Assert.True(state.HasUnsavedChanges);
        var edited = Assert.IsType<ExpirationsAssistant>(
            _validation.Edit(added.Id, "Asistente editado", "edited@example.test", false).Assistant);
        Assert.Empty(state.AddOrReplaceAssistant(edited));
        Assert.Equal(added.Id, Assert.Single(state.Assistants).Id);
        Assert.False(state.Assistants[0].IsActive);

        var persisted = state.BuildConfiguration();
        state.MarkPersisted(persisted);
        Assert.False(state.HasUnsavedChanges);
        Assert.Equal(0, state.ActiveAssistantCount);
        Assert.Equal(1, state.TotalAssistantCount);
    }

    [Fact]
    public void ProfileStateShowsBusinessLabelsAndPersistsSelectedNextMonthMode()
    {
        var state = new ExpirationsBrokerProfileState(
            Configuration("Corredor", ["broker@example.test"]),
            _validation);

        Assert.Equal(["Estándar", "Especial — dos archivos ordenados"],
            state.NextMonthGenerationModeOptions.Select(option => option.DisplayName));
        state.SelectedNextMonthGenerationModeOption = state.NextMonthGenerationModeOptions.Single(option =>
            option.Value == ExpirationsNextMonthGenerationMode.SpecialDualSorted);

        Assert.True(state.HasUnsavedChanges);
        Assert.Equal(
            ExpirationsNextMonthGenerationMode.SpecialDualSorted,
            state.BuildConfiguration().NextMonthGenerationMode);
        state.DiscardChanges();
        Assert.Equal(
            ExpirationsNextMonthGenerationMode.Standard,
            state.SelectedNextMonthGenerationModeOption.Value);
    }

    [Fact]
    public void PreviousMonthShowsReadOnlyStandardAndPreservesPersistedNextMonthMode()
    {
        var state = new ExpirationsBrokerProfileState(
            Configuration(
                "Félix",
                ["felix@example.test"],
                mode: ExpirationsNextMonthGenerationMode.SpecialDualSorted),
            _validation,
            ExpirationsProcess.PreviousMonth);

        Assert.Equal(System.Windows.Visibility.Visible, state.PreviousMonthFormatVisibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, state.NextMonthFormatVisibility);
        state.SelectedNextMonthGenerationModeOption = state.NextMonthGenerationModeOptions.Single(option =>
            option.Value == ExpirationsNextMonthGenerationMode.Standard);

        Assert.Equal(
            ExpirationsNextMonthGenerationMode.SpecialDualSorted,
            state.BuildConfiguration().NextMonthGenerationMode);
    }

    [Fact]
    public void NextMonthShowsEditablePersistedSpecialModeAndRespectsStandardOverride()
    {
        var state = new ExpirationsBrokerProfileState(
            Configuration(
                "Félix",
                ["felix@example.test"],
                mode: ExpirationsNextMonthGenerationMode.SpecialDualSorted),
            _validation,
            ExpirationsProcess.NextMonth);

        Assert.Equal(System.Windows.Visibility.Collapsed, state.PreviousMonthFormatVisibility);
        Assert.Equal(System.Windows.Visibility.Visible, state.NextMonthFormatVisibility);
        Assert.Equal(
            ExpirationsNextMonthGenerationMode.SpecialDualSorted,
            state.SelectedNextMonthGenerationModeOption.Value);
        state.SelectedNextMonthGenerationModeOption = state.NextMonthGenerationModeOptions.Single(option =>
            option.Value == ExpirationsNextMonthGenerationMode.Standard);

        Assert.Equal(
            ExpirationsNextMonthGenerationMode.Standard,
            state.BuildConfiguration().NextMonthGenerationMode);
    }

    [Fact]
    public void CancellationProfileEditsOnlyCancellationAssistantsAndPreservesNormalAssistants()
    {
        var normal = Assistant("Normal", "normal@example.test");
        var cancellation = Assistant("Cancelaciones", "cancel@example.test");
        var configuration = Configuration(
            "Corredor",
            ["broker@example.test"],
            [normal],
            cancellationAssistants: [cancellation]);
        var state = new ExpirationsBrokerProfileState(
            configuration,
            _validation,
            ExpirationsProcess.Cancellations);

        Assert.Equal("Asistentes de Cancelaciones", state.AssistantsSectionTitle);
        Assert.Equal(cancellation.Id, Assert.Single(state.Assistants).Id);
        var added = Assistant("Otra cancelación", "other-cancel@example.test");
        Assert.Empty(state.AddOrReplaceAssistant(added));
        var built = state.BuildConfiguration();

        Assert.Equal(normal.Id, Assert.Single(built.Assistants).Id);
        Assert.Equal(
            new[] { cancellation.Id, added.Id }.Order(),
            built.CancellationAssistants.Select(item => item.Id).Order());
        Assert.Equal(
            ExpirationsNextMonthGenerationMode.Standard,
            built.NextMonthGenerationMode);
    }

    [Fact]
    public void AssistantDeleteIsLocalUntilBuildAndDiscardRestoresIt()
    {
        var kept = Assistant("Conservado", "kept@example.test");
        var removed = Assistant("Eliminar", "remove@example.test");
        var state = new ExpirationsBrokerProfileState(
            Configuration("Corredor", ["broker@example.test"], [kept, removed]),
            _validation);
        state.SelectedAssistant = state.Assistants.Single(value => value.Id == removed.Id);

        Assert.True(state.RemoveSelectedAssistant());
        Assert.True(state.HasUnsavedChanges);
        Assert.Null(state.SelectedAssistant);
        Assert.Equal(kept.Id, Assert.Single(state.BuildConfiguration().Assistants).Id);

        state.DiscardChanges();
        Assert.False(state.HasUnsavedChanges);
        Assert.Equal(
            new[] { kept.Id, removed.Id }.Order(),
            state.Assistants.Select(value => value.Id).Order());
    }

    [Fact]
    public void DeleteConfirmationCancellationLeavesAssistantStateUntouched()
    {
        var assistant = Assistant("Sin eliminar", "kept@example.test");
        var state = new ExpirationsBrokerProfileState(
            Configuration("Corredor", ["broker@example.test"], [assistant]),
            _validation);
        state.SelectedAssistant = state.Assistants.Single();

        // A canceled confirmation does not invoke RemoveSelectedAssistant.
        Assert.False(state.HasUnsavedChanges);
        Assert.Equal(assistant.Id, Assert.Single(state.Assistants).Id);
    }

    [Fact]
    public void AssistantEditorStateDefaultsActiveAndPreservesExistingId()
    {
        var create = new ExpirationsAssistantEditorState(_validation)
        {
            Name = "Nuevo", Email = "new@example.test"
        };
        var created = Assert.IsType<ExpirationsAssistant>(create.Validate().Assistant);
        Assert.True(created.IsActive);

        var edit = new ExpirationsAssistantEditorState(_validation, created)
        {
            Name = "Editado", IsActive = false
        };
        var edited = Assert.IsType<ExpirationsAssistant>(edit.Validate().Assistant);
        Assert.Equal(created.Id, edited.Id);
        Assert.False(edited.IsActive);
    }

    [Fact]
    public void ExpirationsOperatorCanOpenBrokerConfigurationAndBusyDisablesIt()
    {
        var state = new ExpirationsWindowState(new AppUser
        {
            Uid = "operator",
            DisplayName = "Operador",
            Email = "operator@example.test",
            Role = AppUserRole.Operator,
            IsActive = true,
            CanUseExpirations = true
        });

        Assert.True(state.CanConfigureBrokers);
        Assert.Equal(
            [ExpirationsProcess.PreviousMonth, ExpirationsProcess.NextMonth, ExpirationsProcess.Cancellations],
            state.ProcessOptions.Select(option => option.Value));
        state.SetBusy(true);
        Assert.False(state.CanConfigureBrokers);
    }

    private static ExpirationsBrokerConfigurationItem Configuration(
        string name,
        IReadOnlyList<string> emails,
        IReadOnlyList<ExpirationsAssistant>? assistants = null,
        ExpirationsNextMonthGenerationMode mode = ExpirationsNextMonthGenerationMode.Standard,
        IReadOnlyList<ExpirationsAssistant>? cancellationAssistants = null) => new()
    {
        BrokerId = BrokerId,
        Name = name,
        PrimaryEmailAddresses = emails,
        IsActive = true,
        NextMonthGenerationMode = mode,
        Assistants = assistants ?? [],
        CancellationAssistants = cancellationAssistants ?? [],
        HasExplicitProfile = assistants is not null || cancellationAssistants is not null,
        ProfileUpdateTime = assistants is null && cancellationAssistants is null ? null : "version"
    };

    private static ExpirationsAssistant Assistant(string name, string email, bool active = true) => new()
    {
        Id = Guid.NewGuid(), Name = name, Email = email, IsActive = active
    };
}
