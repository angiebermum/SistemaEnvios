using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsEmailSettingsService
{
    Task<ExpirationsEmailSettingsSnapshot> LoadAsync(
        ExpirationsProcess process,
        CancellationToken cancellationToken = default);
    Task<ExpirationsEmailSettingsSaveResult> SaveAsync(
        ExpirationsEmailSettingsSnapshot snapshot,
        string? subject,
        string? message,
        string? commonCcText,
        CancellationToken cancellationToken = default);
}

public sealed class ExpirationsEmailSettingsService : IExpirationsEmailSettingsService
{
    private readonly IExpirationsProcessSettingsRepository _repository;
    private readonly EmailValidationService _emailValidation;
    private readonly TimeProvider _timeProvider;

    public ExpirationsEmailSettingsService(
        IExpirationsProcessSettingsRepository repository,
        EmailValidationService? emailValidation = null,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _emailValidation = emailValidation ?? new EmailValidationService();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ExpirationsEmailSettingsSnapshot> LoadAsync(
        ExpirationsProcess process,
        CancellationToken cancellationToken = default)
    {
        var stored = await _repository.GetAsync(process, cancellationToken);
        return stored is null
            ? new ExpirationsEmailSettingsSnapshot(process, new ExpirationsProcessSettings(), null)
            : new ExpirationsEmailSettingsSnapshot(process, Copy(stored.Value), stored.UpdateTime);
    }

    public async Task<ExpirationsEmailSettingsSaveResult> SaveAsync(
        ExpirationsEmailSettingsSnapshot snapshot,
        string? subject,
        string? message,
        string? commonCcText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var errors = new List<string>();
        var normalizedSubject = subject?.Trim() ?? string.Empty;
        var normalizedMessage = message?.Trim() ?? string.Empty;
        if (normalizedSubject.Length == 0)
            errors.Add("El asunto es obligatorio.");
        if (normalizedMessage.Length == 0)
            errors.Add("El mensaje es obligatorio.");

        _emailValidation.TryParseAddresses(
            commonCcText,
            required: false,
            out var commonCc,
            out var ccErrors);
        errors.AddRange(ccErrors.Select(error => $"CC generales: {error}"));
        if (errors.Count > 0)
        {
            return new ExpirationsEmailSettingsSaveResult
            {
                Outcome = ExpirationsEmailSettingsSaveOutcome.ValidationFailed,
                Snapshot = snapshot,
                Errors = errors,
                Message = string.Join(Environment.NewLine, errors)
            };
        }

        var settings = new ExpirationsProcessSettings
        {
            DefaultSubject = normalizedSubject,
            DefaultMessage = normalizedMessage,
            CommonCcAddresses = commonCc,
            UpdatedAtUtc = _timeProvider.GetUtcNow()
        };

        try
        {
            var stored = snapshot.Exists
                ? await _repository.UpdateAsync(
                    snapshot.Process,
                    settings,
                    snapshot.UpdateTime!,
                    cancellationToken)
                : await _repository.CreateAsync(snapshot.Process, settings, cancellationToken);
            return new ExpirationsEmailSettingsSaveResult
            {
                Outcome = snapshot.Exists
                    ? ExpirationsEmailSettingsSaveOutcome.Updated
                    : ExpirationsEmailSettingsSaveOutcome.Created,
                Snapshot = new ExpirationsEmailSettingsSnapshot(
                    snapshot.Process,
                    Copy(stored.Value),
                    stored.UpdateTime),
                Message = "La configuración de correo fue guardada."
            };
        }
        catch (FirestoreConcurrencyException)
        {
            return await ConflictAsync(snapshot.Process, cancellationToken);
        }
        catch (FirestoreRestException ex) when (ex.Kind == FirestoreFailureKind.Conflict)
        {
            return await ConflictAsync(snapshot.Process, cancellationToken);
        }
    }

    private async Task<ExpirationsEmailSettingsSaveResult> ConflictAsync(
        ExpirationsProcess process,
        CancellationToken cancellationToken)
    {
        var reloaded = await LoadAsync(process, cancellationToken);
        return new ExpirationsEmailSettingsSaveResult
        {
            Outcome = ExpirationsEmailSettingsSaveOutcome.ConcurrencyConflict,
            Snapshot = reloaded,
            Message = "La configuración cambió desde otro equipo. Se recargaron los datos para evitar sobrescribir cambios."
        };
    }

    private static ExpirationsProcessSettings Copy(ExpirationsProcessSettings source) => new()
    {
        DefaultSubject = source.DefaultSubject,
        DefaultMessage = source.DefaultMessage,
        CommonCcAddresses = source.CommonCcAddresses.ToList(),
        UpdatedAtUtc = source.UpdatedAtUtc
    };
}
