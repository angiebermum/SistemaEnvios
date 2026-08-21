using System.Windows;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class ExpirationsWindow : Window
{
    private readonly IAppUserRepository _appUsers;
    private readonly ExpirationsWindowState _state;

    public ExpirationsWindow(AppUser currentUser, IAppUserRepository appUsers)
    {
        ArgumentNullException.ThrowIfNull(appUsers);
        _state = new ExpirationsWindowState(currentUser);
        _appUsers = appUsers;
        InitializeComponent();
        DataContext = _state;
    }

    public event EventHandler? LogoutRequested
    {
        add => _state.LogoutRequested += value;
        remove => _state.LogoutRequested -= value;
    }

    private void ManageAccess_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppUserAuthorization.DemandAdmin(_state.CurrentUser);
            new UserAdministrationWindow(_appUsers, _state.CurrentUser) { Owner = this }.ShowDialog();
        }
        catch (AppUserAuthorizationException ex)
        {
            MessageBox.Show(ex.Message, "Administración de accesos", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "¿Desea cerrar la sesión de Firebase en este equipo?",
                "Cerrar sesión",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _state.RequestLogout();
        Close();
    }
}

internal sealed class ExpirationsWindowState
{
    public ExpirationsWindowState(AppUser currentUser)
    {
        AppUserAuthorization.DemandExpirationsAccess(currentUser);
        CurrentUser = currentUser;
    }

    public AppUser CurrentUser { get; }
    public string SignedInUserText => $"{CurrentUser.DisplayName} · {CurrentUser.Email}";
    public Visibility AdminAccessVisibility =>
        CurrentUser.IsActive && CurrentUser.Role == AppUserRole.Admin
            ? Visibility.Visible
            : Visibility.Collapsed;

    public event EventHandler? LogoutRequested;

    public void RequestLogout() => LogoutRequested?.Invoke(this, EventArgs.Empty);
}
