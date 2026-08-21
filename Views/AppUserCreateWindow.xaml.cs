using System.Windows;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;
using ECS.CommissionsMailer.Services;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class AppUserCreateWindow : Window
{
    public AppUserCreateWindow()
    {
        InitializeComponent();
        RoleComboBox.ItemsSource = Enum.GetValues<AppUserRole>();
        RoleComboBox.SelectedItem = AppUserRole.Operator;
        Loaded += (_, _) => UidTextBox.Focus();
    }

    public AppUser? NewUser { get; private set; }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        var uid = UidTextBox.Text.Trim();
        var displayName = DisplayNameTextBox.Text.Trim();
        var errors = new List<string>();
        if (uid.Length is < 1 or > 128 || uid.Contains('/'))
            errors.Add("El UID debe tener entre 1 y 128 caracteres y no puede contener '/'.");
        if (string.IsNullOrWhiteSpace(displayName))
            errors.Add("El nombre para mostrar es obligatorio.");
        if (!EmailValidationService.TryNormalizeAddress(EmailTextBox.Text, out var email))
            errors.Add("Indique una dirección de correo válida.");
        if (RoleComboBox.SelectedItem is not AppUserRole)
            errors.Add("Seleccione el rol admin u operator.");

        if (errors.Count > 0)
        {
            MessageBox.Show(string.Join(Environment.NewLine, errors.Select(error => $"• {error}")),
                "Datos inválidos", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        NewUser = new AppUser
        {
            Uid = uid,
            Email = email,
            DisplayName = displayName,
            Role = (AppUserRole)RoleComboBox.SelectedItem,
            IsActive = ActiveCheckBox.IsChecked == true,
            CanUseCommissions = CommissionsCheckBox.IsChecked == true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        DialogResult = true;
    }
}
