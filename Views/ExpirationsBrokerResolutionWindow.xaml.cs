using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class ExpirationsBrokerResolutionWindow : Window
{
    internal readonly ExpirationsBrokerResolutionDialogState State;

    public ExpirationsBrokerResolutionWindow(
        ExpirationsPendingIssue issue,
        IEnumerable<ExpirationsBrokerCatalogItem> catalog)
    {
        State = new ExpirationsBrokerResolutionDialogState(issue, catalog);
        InitializeComponent();
        DataContext = State;
    }

    public Guid? SelectedBrokerId { get; private set; }
    public ExpirationsAssociationKind SelectedKind { get; private set; }
    public ExpirationsDestinationGroup SelectedDestinationGroup { get; private set; }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (State.SelectedBroker is null)
            return;
        if (State.Issue.Status is (
                ExpirationsBrokerResolutionStatus.Unresolved or
                ExpirationsBrokerResolutionStatus.Ambiguous) &&
            MessageBox.Show(
                State.ConfirmationSummary +
                "\n\nEsta asociación se utilizará en ambos tipos de Vencimientos.\n\n¿Desea continuar?",
                "Confirmar asociación",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        SelectedBrokerId = State.SelectedBroker.BrokerId;
        SelectedKind = State.SelectedKind.Value;
        SelectedDestinationGroup = State.SelectedDestinationGroup!.Value;
        DialogResult = true;
    }
}

internal sealed class ExpirationsBrokerResolutionDialogState : INotifyPropertyChanged
{
    private ExpirationsBrokerChoice? _selectedBroker;
    private ExpirationsAssociationKindOption _selectedKind;
    private ExpirationsDestinationGroupOption? _selectedDestinationGroup;
    private readonly ExpirationsDestinationRoutingPolicy _destinationPolicy;

    public ExpirationsBrokerResolutionDialogState(
        ExpirationsPendingIssue issue,
        IEnumerable<ExpirationsBrokerCatalogItem> catalog)
    {
        Issue = issue ?? throw new ArgumentNullException(nameof(issue));
        ArgumentNullException.ThrowIfNull(catalog);
        var catalogItems = catalog.ToList();
        _destinationPolicy = new ExpirationsDestinationRoutingPolicy(catalogItems);
        Brokers = catalogItems
            .Where(item => item.IsActive)
            .GroupBy(item => item.BrokerId)
            .Select(group => group.First())
            .Select(item => new ExpirationsBrokerChoice(
                item.BrokerId,
                item.Name,
                item.PrimaryEmailAddresses.FirstOrDefault(email => !string.IsNullOrWhiteSpace(email)) ?? string.Empty))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.BrokerId)
            .ToList();
        KindOptions =
        [
            new ExpirationsAssociationKindOption(ExpirationsAssociationKind.Name, "Nombre"),
            new ExpirationsAssociationKindOption(ExpirationsAssociationKind.Alias, "Alias / nombre alternativo"),
            new ExpirationsAssociationKindOption(ExpirationsAssociationKind.Code, "Código")
        ];
        var suggestedKind = SuggestCode(issue.NormalizedValue)
            ? ExpirationsAssociationKind.Code
            : ExpirationsAssociationKind.Alias;
        _selectedKind = KindOptions.Single(option => option.Value == suggestedKind);
    }

    public ExpirationsPendingIssue Issue { get; }
    public IReadOnlyList<ExpirationsBrokerChoice> Brokers { get; }
    public IReadOnlyList<ExpirationsAssociationKindOption> KindOptions { get; }
    public IReadOnlyList<ExpirationsDestinationGroupOption> DestinationGroupOptions { get; private set; } = [];
    public Visibility DestinationGroupVisibility => DestinationGroupOptions.Count > 1
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility AmbiguityWarningVisibility =>
        Issue.Status == ExpirationsBrokerResolutionStatus.Ambiguous
            ? Visibility.Visible
            : Visibility.Collapsed;
    public Visibility AssociationTypeVisibility =>
        Issue.Status is (
                ExpirationsBrokerResolutionStatus.Unresolved or
                ExpirationsBrokerResolutionStatus.Ambiguous)
            ? Visibility.Visible
            : Visibility.Collapsed;
    public string ConfirmButtonText => "Confirmar asociación";
    public string AssociationHelpText => SelectedKind.Value switch
    {
        ExpirationsAssociationKind.Name => "Nombre: nombre con el que se identifica al corredor.",
        ExpirationsAssociationKind.Alias => "Alias: variante o forma alternativa en que aparece el corredor.",
        ExpirationsAssociationKind.Code => "Código: código único asignado al corredor.",
        _ => string.Empty
    };
    public string ConfirmationSummary => SelectedBroker is null
        ? $"Valor que se asociará: {Issue.RawValue}\nSeleccione un corredor."
        : $"Valor que se asociará: {Issue.RawValue}\n" +
          $"Corredor: {SelectedBroker.DisplayText}\n" +
          $"Archivo destino: {SelectedDestinationGroup?.DisplayName ?? "Seleccione un archivo"}\n" +
          $"Tipo: {SelectedKind.DisplayName}";
    public bool CanConfirm => SelectedBroker is not null && SelectedDestinationGroup is not null;

    public ExpirationsBrokerChoice? SelectedBroker
    {
        get => _selectedBroker;
        set
        {
            if (Equals(_selectedBroker, value)) return;
            _selectedBroker = value;
            DestinationGroupOptions = value is null
                ? []
                : _destinationPolicy.AvailableGroups(value.BrokerId)
                    .Select(group => new ExpirationsDestinationGroupOption(
                        group,
                        ExpirationsDestinationGroups.DisplayName(group)))
                    .ToList();
            SelectedDestinationGroup = DestinationGroupOptions.Count == 1
                ? DestinationGroupOptions[0]
                : null;
            Notify();
            Notify(nameof(DestinationGroupOptions));
            Notify(nameof(DestinationGroupVisibility));
            Notify(nameof(CanConfirm));
            Notify(nameof(ConfirmationSummary));
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
            Notify(nameof(CanConfirm));
            Notify(nameof(ConfirmationSummary));
        }
    }

    public ExpirationsAssociationKindOption SelectedKind
    {
        get => _selectedKind;
        set
        {
            if (Equals(_selectedKind, value)) return;
            _selectedKind = value;
            Notify();
            Notify(nameof(AssociationHelpText));
            Notify(nameof(ConfirmationSummary));
        }
    }

    internal static bool SuggestCode(string normalizedValue) =>
        normalizedValue.Length is > 0 and <= 12 &&
        !normalizedValue.Contains(' ') &&
        normalizedValue.All(char.IsLetterOrDigit);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
