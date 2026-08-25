using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;

namespace ECS.CommissionsMailer.Views;

public partial class ExpirationsAssociationEditorWindow : Window
{
    internal readonly ExpirationsAssociationEditorState State;

    public ExpirationsAssociationEditorWindow(
        ExpirationsBrokerConfigurationItem broker,
        ExpirationsBrokerAssociation? association = null)
    {
        State = new ExpirationsAssociationEditorState(broker, association);
        InitializeComponent();
        DataContext = State;
    }

    internal ExpirationsAssociationInput? AssociationInput { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        AssociationInput = State.BuildInput();
        if (AssociationInput is null)
            return;
        DialogResult = true;
    }
}

internal sealed record ExpirationsAssociationInput(
    ExpirationsAssociationKind Kind,
    string Value,
    ExpirationsDestinationGroup DestinationGroup);

internal sealed class ExpirationsAssociationEditorState : INotifyPropertyChanged
{
    private ExpirationsAssociationKindOption _selectedKindOption;
    private string _value;
    private ExpirationsDestinationGroupOption? _selectedDestinationGroup;

    public ExpirationsAssociationEditorState(
        ExpirationsBrokerConfigurationItem broker,
        ExpirationsBrokerAssociation? association = null)
    {
        ArgumentNullException.ThrowIfNull(broker);
        BrokerName = broker.Name;
        BrokerEmail = broker.PrimaryEmailAddresses.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ??
                      "Sin correo principal";
        TitleText = association is null ? "Agregar asociación" : "Editar asociación";
        KindOptions =
        [
            new(ExpirationsAssociationKind.Name, "Nombre"),
            new(ExpirationsAssociationKind.Alias, "Alias"),
            new(ExpirationsAssociationKind.Code, "Código")
        ];
        _selectedKindOption = KindOptions.Single(option =>
            option.Value == (association?.Kind ?? ExpirationsAssociationKind.Name));
        _value = association?.Value ?? string.Empty;
        DestinationGroupOptions = ExpirationsDestinationRoutingPolicy
            .AvailableGroupsForBrokerName(broker.Name)
            .Select(group => new ExpirationsDestinationGroupOption(
                group,
                ExpirationsDestinationGroups.DisplayName(group)))
            .ToList();
        _selectedDestinationGroup = association?.DestinationGroup is { } existing
            ? DestinationGroupOptions.FirstOrDefault(option => option.Value == existing)
            : DestinationGroupOptions.Count == 1 ? DestinationGroupOptions[0] : null;
    }

    public string TitleText { get; }
    public string BrokerName { get; }
    public string BrokerEmail { get; }
    public IReadOnlyList<ExpirationsAssociationKindOption> KindOptions { get; }
    public IReadOnlyList<ExpirationsDestinationGroupOption> DestinationGroupOptions { get; }
    public Visibility DestinationGroupVisibility => DestinationGroupOptions.Count > 1
        ? Visibility.Visible
        : Visibility.Collapsed;
    public bool CanSave => Value.Trim().Length > 0 && SelectedDestinationGroup is not null;
    public string SummaryValue => $"Valor: {(Value.Trim().Length == 0 ? "—" : Value.Trim())}";
    public string SummaryKind => $"Tipo: {SelectedKindOption.DisplayName}";
    public string SummaryBroker => $"Corredor: {BrokerName} — {BrokerEmail}";
    public string SummaryDestination =>
        $"Archivo destino: {SelectedDestinationGroup?.DisplayName ?? "Seleccione uno"}";

    public ExpirationsAssociationKindOption SelectedKindOption
    {
        get => _selectedKindOption;
        set
        {
            if (Equals(_selectedKindOption, value)) return;
            _selectedKindOption = value;
            Notify();
            Notify(nameof(SummaryKind));
        }
    }

    public ExpirationsDestinationGroupOption? SelectedDestinationGroup
    {
        get => _selectedDestinationGroup;
        set
        {
            if (Equals(_selectedDestinationGroup, value)) return;
            _selectedDestinationGroup = value;
            Notify();
            Notify(nameof(CanSave));
            Notify(nameof(SummaryDestination));
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            if (string.Equals(_value, value, StringComparison.Ordinal)) return;
            _value = value ?? string.Empty;
            Notify();
            Notify(nameof(CanSave));
            Notify(nameof(SummaryValue));
        }
    }

    public ExpirationsAssociationInput? BuildInput() => CanSave
        ? new ExpirationsAssociationInput(
            SelectedKindOption.Value,
            Value.Trim(),
            SelectedDestinationGroup!.Value)
        : null;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
