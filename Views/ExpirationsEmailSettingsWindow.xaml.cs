using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class ExpirationsEmailSettingsWindow : Window
{
    private readonly IExpirationsEmailSettingsService _service;
    internal readonly ExpirationsEmailSettingsState State;

    public ExpirationsEmailSettingsWindow(
        IExpirationsEmailSettingsService service,
        ExpirationsProcess process)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        State = new ExpirationsEmailSettingsState(process);
        InitializeComponent();
        DataContext = State;
    }

    public bool HasSavedChanges { get; private set; }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (State.IsLoaded)
            return;
        State.SetBusy(true, "Cargando configuración...");
        try
        {
            State.Load(await _service.LoadAsync(State.Process));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"No fue posible cargar la configuración.\n\n{ex.Message}",
                "Correo de Vencimientos",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            State.SetBusy(false);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        State.SetBusy(true, "Guardando configuración...");
        try
        {
            var result = await _service.SaveAsync(
                State.Snapshot,
                State.Subject,
                State.Message,
                State.CommonCcText);
            if (result.Outcome == ExpirationsEmailSettingsSaveOutcome.ValidationFailed)
            {
                MessageBox.Show(result.Message, "Revise la configuración", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (result.Outcome == ExpirationsEmailSettingsSaveOutcome.ConcurrencyConflict)
            {
                State.Load(result.Snapshot);
                MessageBox.Show(result.Message, "Configuración actualizada", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            State.Load(result.Snapshot);
            HasSavedChanges = true;
            MessageBox.Show(result.Message, "Correo de Vencimientos", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"No fue posible guardar la configuración.\n\n{ex.Message}",
                "Correo de Vencimientos",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            State.SetBusy(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}

internal sealed class ExpirationsEmailSettingsState : INotifyPropertyChanged
{
    private string _subject = string.Empty;
    private string _message = string.Empty;
    private string _commonCcText = string.Empty;
    private bool _isBusy;
    private string _statusText = "La configuración aún no se ha cargado.";

    public ExpirationsEmailSettingsState(ExpirationsProcess process)
    {
        Process = process;
        Snapshot = new ExpirationsEmailSettingsSnapshot(process, new ExpirationsProcessSettings(), null);
    }

    public ExpirationsProcess Process { get; }
    public ExpirationsEmailSettingsSnapshot Snapshot { get; private set; }
    public bool IsLoaded { get; private set; }
    public string ProcessText => Process switch
    {
        ExpirationsProcess.PreviousMonth => "Pendientes del mes anterior",
        ExpirationsProcess.NextMonth => "Vencimientos del mes siguiente",
        ExpirationsProcess.Cancellations => "Cancelaciones",
        _ => throw new ArgumentOutOfRangeException()
    };
    public bool CanEdit => !_isBusy;
    public bool CanSave => !_isBusy && IsLoaded;
    public string StatusText => _statusText;

    public string Subject
    {
        get => _subject;
        set => SetField(ref _subject, value ?? string.Empty);
    }

    public string Message
    {
        get => _message;
        set => SetField(ref _message, value ?? string.Empty);
    }

    public string CommonCcText
    {
        get => _commonCcText;
        set => SetField(ref _commonCcText, value ?? string.Empty);
    }

    public void Load(ExpirationsEmailSettingsSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _subject = snapshot.Settings.DefaultSubject;
        _message = snapshot.Settings.DefaultMessage;
        _commonCcText = string.Join(Environment.NewLine, snapshot.Settings.CommonCcAddresses);
        IsLoaded = true;
        _statusText = snapshot.Exists
            ? "Configuración cargada."
            : "No existe configuración guardada para este proceso.";
        Notify(string.Empty);
    }

    public void SetBusy(bool value, string? status = null)
    {
        _isBusy = value;
        if (!string.IsNullOrWhiteSpace(status))
            _statusText = status;
        Notify(string.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
            return;
        field = value;
        Notify(propertyName);
    }

    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
