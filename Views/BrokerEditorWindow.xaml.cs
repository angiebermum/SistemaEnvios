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

    public BrokerEditorWindow(Broker? broker, EmailValidationService validationService)
    {
        InitializeComponent();
        _validationService = validationService;
        EditedBroker = broker?.Clone() ?? new Broker();
        Assistants = new ObservableCollection<BrokerAssistant>(EditedBroker.Assistants.Select(value => value.Clone()));
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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var requiresReview = ReviewCheckBox.IsChecked == true;
        var errors = _validationService.ValidateBroker(
            NameTextBox.Text,
            AddressesTextBox.Text,
            Assistants,
            requiresReview,
            ReviewNoteTextBox.Text,
            out var addresses);
        if (errors.Count > 0)
        {
            MessageBox.Show(string.Join(Environment.NewLine, errors.Select(error => $"• {error}")),
                "Datos inválidos", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        EditedBroker.Name = NameTextBox.Text.Trim();
        EditedBroker.PrimaryEmailAddresses = addresses;
        EditedBroker.Assistants = Assistants.Select(value => value.Clone()).ToList();
        EditedBroker.IsActive = ActiveCheckBox.IsChecked == true;
        EditedBroker.RequiresReview = requiresReview;
        EditedBroker.ReviewNote = requiresReview ? ReviewNoteTextBox.Text.Trim() : null;
        DialogResult = true;
    }

    private void WarnSelectAssistant() => MessageBox.Show(
        "Seleccione un asistente en la tabla.",
        "Asistentes", MessageBoxButton.OK, MessageBoxImage.Information);
}
