using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class ExpirationsAssistantEditorWindow : Window
{
    internal readonly ExpirationsAssistantEditorState State;

    public ExpirationsAssistantEditorWindow(
        ExpirationsAssistantValidationService validation,
        ExpirationsAssistant? assistant = null)
    {
        State = new ExpirationsAssistantEditorState(validation, assistant);
        InitializeComponent();
        DataContext = State;
    }

    public ExpirationsAssistant? Assistant { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var result = State.Validate();
        if (!result.IsValid || result.Assistant is null)
        {
            MessageBox.Show(
                string.Join(Environment.NewLine, result.Errors),
                "Revise el asistente",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        Assistant = result.Assistant;
        DialogResult = true;
    }
}

internal sealed class ExpirationsAssistantEditorState : INotifyPropertyChanged
{
    private readonly ExpirationsAssistantValidationService _validation;
    private readonly Guid? _existingId;
    private string _name;
    private string _email;
    private bool _isActive;

    public ExpirationsAssistantEditorState(
        ExpirationsAssistantValidationService validation,
        ExpirationsAssistant? assistant = null)
    {
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
        _existingId = assistant?.Id;
        _name = assistant?.Name ?? string.Empty;
        _email = assistant?.Email ?? string.Empty;
        _isActive = assistant?.IsActive ?? true;
    }

    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value ?? string.Empty; Notify(); }
    }

    public string Email
    {
        get => _email;
        set { if (_email == value) return; _email = value ?? string.Empty; Notify(); }
    }

    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive == value) return; _isActive = value; Notify(); }
    }

    public ExpirationsAssistantEditResult Validate() => _existingId is { } id
        ? _validation.Edit(id, Name, Email, IsActive)
        : _validation.CreateNew(Name, Email, IsActive);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
