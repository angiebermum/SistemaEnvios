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
        Assert.Equal(2, state.VisibleItems.Count);
        Assert.Contains(state.VisibleItems, item => item.StatusText == "Inactivo");
        Assert.Equal("Ocultar inactivos", state.InactiveButtonText);
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
        state.SetBusy(true);
        Assert.False(state.CanConfigureBrokers);
    }

    private static ExpirationsBrokerConfigurationItem Configuration(
        string name,
        IReadOnlyList<string> emails,
        IReadOnlyList<ExpirationsAssistant>? assistants = null,
        ExpirationsNextMonthGenerationMode mode = ExpirationsNextMonthGenerationMode.Standard) => new()
    {
        BrokerId = BrokerId,
        Name = name,
        PrimaryEmailAddresses = emails,
        IsActive = true,
        NextMonthGenerationMode = mode,
        Assistants = assistants ?? [],
        HasExplicitProfile = assistants is not null,
        ProfileUpdateTime = assistants is null ? null : "version"
    };

    private static ExpirationsAssistant Assistant(string name, string email, bool active = true) => new()
    {
        Id = Guid.NewGuid(), Name = name, Email = email, IsActive = active
    };
}
