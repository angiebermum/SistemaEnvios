using System.Windows;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;
using ECS.CommissionsMailer.Services;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class ModuleSelectionWindow : Window
{
    public ModuleSelectionWindow(AppUser currentUser)
    {
        AppUserAuthorization.DemandAnyModuleAccess(currentUser);
        InitializeComponent();
        SignedInUserText = $"{currentUser.DisplayName} · {currentUser.Email}";
        DataContext = this;
    }

    public string SignedInUserText { get; }
    public ApplicationModule? SelectedModule { get; private set; }
    public bool LogoutRequested { get; private set; }

    private void Commissions_Click(object sender, RoutedEventArgs e) =>
        SelectModule(ApplicationModule.Commissions);

    private void Expirations_Click(object sender, RoutedEventArgs e) =>
        SelectModule(ApplicationModule.Expirations);

    private void SelectModule(ApplicationModule module)
    {
        SelectedModule = module;
        DialogResult = true;
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "¿Desea cerrar la sesión de Firebase en este equipo?",
                "Cerrar sesión",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        LogoutRequested = true;
        Close();
    }
}
