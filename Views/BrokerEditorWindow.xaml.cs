using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class BrokerEditorWindow : Window
{
    private readonly EmailValidationService _validationService;
    private readonly WorksheetBrokerMappingService _mappingService;
    private readonly IReadOnlyList<Broker> _allBrokers;

    public BrokerEditorWindow(
        Broker? broker,
        EmailValidationService validationService,
        IEnumerable<Broker>? allBrokers = null,
        WorksheetBrokerMappingService? mappingService = null)
    {
        InitializeComponent();
        _validationService = validationService;
        _mappingService = mappingService ?? new WorksheetBrokerMappingService();
        _allBrokers = (allBrokers ?? (broker is null ? [] : [broker])).ToList();
        EditedBroker = broker?.Clone() ?? new Broker();
        Assistants = new ObservableCollection<BrokerAssistant>(EditedBroker.Assistants.Select(value => value.Clone()));
        WorksheetNames = new ObservableCollection<string>(EditedBroker.AssociatedWorksheetNames);
        Deductions = new ObservableCollection<BrokerDeduction>(
            EditedBroker.Deductions.OrderBy(value => value.DisplayOrder).Select(value => value.Clone()));
        Title = broker is null ? "Agregar corredor" : "Editar corredor";
        NameTextBox.Text = EditedBroker.Name;
        AddressesTextBox.Text = string.Join(Environment.NewLine, EditedBroker.PrimaryEmailAddresses);
        ActiveCheckBox.IsChecked = EditedBroker.IsActive;
        ReviewCheckBox.IsChecked = EditedBroker.RequiresReview;
        ReviewNoteTextBox.Text = EditedBroker.ReviewNote;
        DataContext = this;
        Loaded += (_, _) => NameTextBox.Focus();
    }

    public Broker EditedBroker { get; }
    public ObservableCollection<BrokerAssistant> Assistants { get; }
    public ObservableCollection<string> WorksheetNames { get; }
    public ObservableCollection<BrokerDeduction> Deductions { get; }
    public BrokerAssistant? SelectedAssistant { get; set; }

    private void AddAssistant_Click(object sender, RoutedEventArgs e)
    {
        var editor = new AssistantEditorWindow(null) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            Assistants.Add(editor.EditedAssistant);
            AssistantsGrid.SelectedItem = editor.EditedAssistant;
        }
    }

    private void EditAssistant_Click(object sender, RoutedEventArgs e)
    {
        if (AssistantsGrid.SelectedItem is not BrokerAssistant assistant)
        {
            WarnSelectAssistant();
            return;
        }

        var editor = new AssistantEditorWindow(assistant) { Owner = this };
        if (editor.ShowDialog() != true)
        {
            return;
        }

        var index = Assistants.IndexOf(assistant);
        Assistants[index] = editor.EditedAssistant;
        AssistantsGrid.SelectedIndex = index;
    }

    private void ToggleAssistant_Click(object sender, RoutedEventArgs e)
    {
        if (AssistantsGrid.SelectedItem is not BrokerAssistant assistant)
        {
            (sender as ToggleButton)?.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            WarnSelectAssistant();
            return;
        }

        var replacement = assistant.Clone();
        replacement.IsActive = (sender as ToggleButton)?.IsChecked == true;
        var index = Assistants.IndexOf(assistant);
        Assistants[index] = replacement;
        AssistantsGrid.SelectedIndex = index;
    }

    private void RemoveAssistant_Click(object sender, RoutedEventArgs e)
    {
        if (AssistantsGrid.SelectedItem is not BrokerAssistant assistant)
        {
            WarnSelectAssistant();
            return;
        }

        if (MessageBox.Show($"¿Desea quitar a '{assistant.Name}' de este corredor?",
                "Quitar asistente", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            Assistants.Remove(assistant);
        }
    }

    private void AddWorksheet_Click(object sender, RoutedEventArgs e)
    {
        var editor = new WorksheetNameEditorWindow(null) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            if (WorksheetNames.Contains(editor.WorksheetName, StringComparer.OrdinalIgnoreCase))
            {
                ShowDuplicateWorksheet(editor.WorksheetName);
                return;
            }

            WorksheetNames.Add(editor.WorksheetName);
            WorksheetsGrid.SelectedItem = editor.WorksheetName;
        }
    }

    private void EditWorksheet_Click(object sender, RoutedEventArgs e)
    {
        if (WorksheetsGrid.SelectedItem is not string selected)
        {
            WarnSelectWorksheet();
            return;
        }

        var editor = new WorksheetNameEditorWindow(selected) { Owner = this };
        if (editor.ShowDialog() != true)
        {
            return;
        }

        if (WorksheetNames.Any(value =>
                !string.Equals(value, selected, StringComparison.Ordinal) &&
                string.Equals(value.Trim(), editor.WorksheetName, StringComparison.OrdinalIgnoreCase)))
        {
            ShowDuplicateWorksheet(editor.WorksheetName);
            return;
        }

        var index = WorksheetNames.IndexOf(selected);
        WorksheetNames[index] = editor.WorksheetName;
        WorksheetsGrid.SelectedIndex = index;
    }

    private void RemoveWorksheet_Click(object sender, RoutedEventArgs e)
    {
        if (WorksheetsGrid.SelectedItem is not string selected)
        {
            WarnSelectWorksheet();
            return;
        }

        if (MessageBox.Show($"¿Desea eliminar la asociación con la pestaña '{selected}'?",
                "Eliminar asociación", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            WorksheetNames.Remove(selected);
        }
    }

    private void AddDeduction_Click(object sender, RoutedEventArgs e)
    {
        var editor = new DeductionEditorWindow(null) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            editor.EditedDeduction.DisplayOrder = Deductions.Count == 0
                ? 0
                : Deductions.Max(value => value.DisplayOrder) + 1;
            Deductions.Add(editor.EditedDeduction);
            DeductionsGrid.SelectedItem = editor.EditedDeduction;
        }
    }

    private void EditDeduction_Click(object sender, RoutedEventArgs e)
    {
        if (DeductionsGrid.SelectedItem is not BrokerDeduction deduction)
        {
            WarnSelectDeduction();
            return;
        }

        var editor = new DeductionEditorWindow(deduction) { Owner = this };
        if (editor.ShowDialog() != true)
        {
            return;
        }

        var index = Deductions.IndexOf(deduction);
        Deductions[index] = editor.EditedDeduction;
        DeductionsGrid.SelectedIndex = index;
    }

    private void RemoveDeduction_Click(object sender, RoutedEventArgs e)
    {
        if (DeductionsGrid.SelectedItem is not BrokerDeduction deduction)
        {
            WarnSelectDeduction();
            return;
        }

        if (MessageBox.Show($"¿Desea eliminar el rebajo '{deduction.Description}'?",
                "Eliminar rebajo", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            Deductions.Remove(deduction);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var requiresReview = ReviewCheckBox.IsChecked == true;
        var errors = _validationService.ValidateBroker(
            NameTextBox.Text,
            AddressesTextBox.Text,
            Assistants,
            requiresReview,
            ReviewNoteTextBox.Text,
            out var addresses,
            requirePrimaryEmail: false);

        var candidate = EditedBroker.Clone();
        candidate.Name = NameTextBox.Text.Trim();
        candidate.AssociatedWorksheetNames = WorksheetNames.Select(value => value.Trim()).ToList();
        candidate.Deductions = Deductions.Select(value => value.Clone()).ToList();
        errors.AddRange(_mappingService.ValidateWorksheetNames(candidate, _allBrokers));
        errors.AddRange(_mappingService.ValidateDeductions(candidate));
        if (errors.Count > 0)
        {
            MessageBox.Show(
                string.Join(Environment.NewLine, errors.Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(error => $"• {error}")),
                "Datos inválidos", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        EditedBroker.Name = candidate.Name;
        EditedBroker.PrimaryEmailAddresses = addresses;
        EditedBroker.Assistants = Assistants.Select(value => value.Clone()).ToList();
        EditedBroker.AssociatedWorksheetNames = candidate.AssociatedWorksheetNames;
        EditedBroker.Deductions = candidate.Deductions;
        EditedBroker.IsActive = ActiveCheckBox.IsChecked == true;
        EditedBroker.RequiresReview = requiresReview;
        EditedBroker.ReviewNote = requiresReview ? ReviewNoteTextBox.Text.Trim() : null;
        DialogResult = true;
    }

    private void WarnSelectAssistant() => MessageBox.Show(
        "Seleccione un asistente en la tabla.",
        "Asistentes", MessageBoxButton.OK, MessageBoxImage.Information);

    private void WarnSelectWorksheet() => MessageBox.Show(
        "Seleccione una pestaña en la tabla.",
        "Pestañas asociadas", MessageBoxButton.OK, MessageBoxImage.Information);

    private void WarnSelectDeduction() => MessageBox.Show(
        "Seleccione un rebajo en la tabla.",
        "Rebajos", MessageBoxButton.OK, MessageBoxImage.Information);

    private void ShowDuplicateWorksheet(string worksheetName) => MessageBox.Show(
        $"La pestaña '{worksheetName}' ya está agregada a este corredor.",
        "Pestaña duplicada", MessageBoxButton.OK, MessageBoxImage.Warning);
}
