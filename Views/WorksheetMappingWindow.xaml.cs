using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class WorksheetMappingWindow : Window
{
    private readonly EmailValidationService _emailValidationService;
    private readonly WorksheetBrokerMappingService _mappingService;
    private readonly IReadOnlyList<Broker> _originalBrokers;

    public WorksheetMappingWindow(
        IEnumerable<string> missingWorksheetNames,
        IEnumerable<Broker> brokers,
        EmailValidationService emailValidationService,
        WorksheetBrokerMappingService mappingService)
    {
        InitializeComponent();
        _emailValidationService = emailValidationService;
        _mappingService = mappingService;
        _originalBrokers = brokers.ToList();
        foreach (var broker in _originalBrokers.OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            AvailableBrokers.Add(broker);
        }

        foreach (var name in missingWorksheetNames)
        {
            Rows.Add(new WorksheetMappingRow { WorksheetName = name });
        }

        DataContext = this;
    }

    public ObservableCollection<WorksheetMappingRow> Rows { get; } = [];
    public ObservableCollection<Broker> AvailableBrokers { get; } = [];
    public List<Broker> CreatedBrokers { get; } = [];
    public List<WorksheetBrokerAssignment> Assignments { get; } = [];

    private void CreateBroker_Click(object sender, RoutedEventArgs e)
    {
        var editor = new BrokerEditorWindow(
            null,
            _emailValidationService,
            AvailableBrokers,
            _mappingService)
        {
            Owner = this
        };
        if (editor.ShowDialog() != true)
        {
            return;
        }

        CreatedBrokers.Add(editor.EditedBroker);
        AvailableBrokers.Add(editor.EditedBroker);
        var firstUnassigned = Rows.FirstOrDefault(value => value.SelectedBroker is null);
        if (firstUnassigned is not null)
        {
            firstUnassigned.SelectedBroker = editor.EditedBroker;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var unassigned = Rows.Where(value => value.SelectedBroker is null).Select(value => value.WorksheetName).ToList();
        if (unassigned.Count > 0)
        {
            MessageBox.Show(
                "Debe asociar todas las pestañas:\n\n" +
                string.Join(Environment.NewLine, unassigned.Select(value => $"• {value}")),
                "Asociaciones incompletas", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        foreach (var row in Rows)
        {
            var broker = row.SelectedBroker!;
            if (!broker.AssociatedWorksheetNames.Contains(row.WorksheetName, StringComparer.OrdinalIgnoreCase))
            {
                broker.AssociatedWorksheetNames.Add(row.WorksheetName.Trim());
            }

            Assignments.Add(new WorksheetBrokerAssignment(row.WorksheetName, broker));
        }

        var errors = _mappingService.ValidateConfiguration(AvailableBrokers);
        if (errors.Count > 0)
        {
            foreach (var assignment in Assignments)
            {
                assignment.Broker.AssociatedWorksheetNames.RemoveAll(value =>
                    string.Equals(value, assignment.WorksheetName.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            Assignments.Clear();
            MessageBox.Show(string.Join(Environment.NewLine, errors.Select(value => $"• {value}")),
                "Asociaciones inválidas", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}

public sealed class WorksheetMappingRow : INotifyPropertyChanged
{
    private Broker? _selectedBroker;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string WorksheetName { get; init; } = string.Empty;

    public Broker? SelectedBroker
    {
        get => _selectedBroker;
        set
        {
            if (ReferenceEquals(_selectedBroker, value))
            {
                return;
            }

            _selectedBroker = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedBroker)));
        }
    }
}
