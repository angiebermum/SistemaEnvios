using System.Windows;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class WorksheetNameEditorWindow : Window
{
    public WorksheetNameEditorWindow(string? worksheetName)
    {
        InitializeComponent();
        WorksheetNameTextBox.Text = worksheetName ?? string.Empty;
        Loaded += (_, _) =>
        {
            WorksheetNameTextBox.Focus();
            WorksheetNameTextBox.SelectAll();
        };
    }

    public string WorksheetName { get; private set; } = string.Empty;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var value = WorksheetNameTextBox.Text.Trim();
        if (value.Length == 0)
        {
            MessageBox.Show("El nombre de la pestaña es obligatorio.",
                "Pestaña asociada", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (value.Length > 31 || value.Any(character => "[]:*?/\\".Contains(character)))
        {
            MessageBox.Show("El nombre no cumple las reglas de nombres de pestaña de Excel.",
                "Pestaña asociada", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        WorksheetName = value;
        DialogResult = true;
    }
}
