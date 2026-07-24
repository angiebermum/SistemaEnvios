using System.Windows;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class AssistantEditorWindow : Window
{
    public AssistantEditorWindow(BrokerAssistant? assistant)
    {
        InitializeComponent();
        EditedAssistant = assistant?.Clone() ?? new BrokerAssistant();
        Title = assistant is null ? "Agregar asistente" : "Editar asistente";
        NameTextBox.Text = EditedAssistant.Name;
        EmailTextBox.Text = EditedAssistant.Email;
        ActiveCheckBox.IsChecked = EditedAssistant.IsActive;
        Loaded += (_, _) => NameTextBox.Focus();
    }

    public BrokerAssistant EditedAssistant { get; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            errors.Add("El nombre del asistente es obligatorio.");
        }

        if (!EmailValidationService.TryNormalizeAddress(EmailTextBox.Text, out var email))
        {
            errors.Add("Indique una dirección de correo válida para el asistente.");
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(string.Join(Environment.NewLine, errors.Select(error => $"• {error}")),
                "Datos inválidos", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        EditedAssistant.Name = NameTextBox.Text.Trim();
        EditedAssistant.Email = email;
        EditedAssistant.IsActive = ActiveCheckBox.IsChecked == true;
        DialogResult = true;
    }
}
