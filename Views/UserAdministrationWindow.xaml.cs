using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class UserAdministrationWindow : Window
{
    private readonly IAppUserRepository _repository;
    private readonly AppUser _currentUser;
    private readonly ObservableCollection<UserRow> _users = [];
    private readonly ICollectionView _view;

    public UserAdministrationWindow(IAppUserRepository repository, AppUser currentUser)
    {
        InitializeComponent();
        AppUserAuthorization.DemandAdmin(currentUser);
        _repository = repository;
        _currentUser = currentUser;
        RoleComboBox.ItemsSource = Enum.GetValues<AppUserRole>();
        _view = CollectionViewSource.GetDefaultView(_users);
        _view.Filter = Filter;
        UsersGrid.ItemsSource = _view;
    }

    private UserRow? Selected => UsersGrid.SelectedItem as UserRow;

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();
    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var editor = new AppUserCreateWindow { Owner = this };
        if (editor.ShowDialog() != true || editor.NewUser is not { } newUser) return;
        if (MessageBox.Show(
                $"¿Registrar el perfil appUsers/{newUser.Uid} para {newUser.DisplayName} ({newUser.Email})?\n\n" +
                "Esta acción no crea ni modifica la cuenta de Firebase Authentication.",
                "Confirmar nuevo perfil", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        SetBusy(true, "Registrando perfil…");
        try
        {
            var stored = await _repository.CreateAsync(newUser);
            var row = new UserRow(stored.Value, stored.UpdateTime);
            _users.Add(row);
            _view.Refresh();
            UsersGrid.SelectedItem = row;
            UsersGrid.ScrollIntoView(row);
            StatusTextBlock.Text = "Perfil registrado.";
        }
        catch (FirestoreRestException ex) when (ex.Kind == FirestoreFailureKind.Conflict)
        {
            MessageBox.Show(
                $"Ya existe appUsers/{newUser.Uid}. Recargue la lista y modifique el perfil existente si corresponde.",
                "El perfil ya existe", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "No se registró el perfil", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadAsync()
    {
        SetBusy(true, "Cargando perfiles…");
        try
        {
            var values = await _repository.ListAsync();
            _users.Clear();
            foreach (var value in values.OrderBy(item => item.Value.DisplayName).ThenBy(item => item.Value.Email))
                _users.Add(new UserRow(value.Value, value.UpdateTime));
            StatusTextBlock.Text = $"{_users.Count} perfil(es).";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Administración de accesos", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => _view.Refresh();

    private bool Filter(object value)
    {
        if (value is not UserRow row) return false;
        var search = SearchTextBox.Text.Trim();
        return search.Length == 0 || row.User.DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
               row.User.Email.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void UsersGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var selected = Selected;
        ActiveCheckBox.IsEnabled = selected is not null;
        CommissionsCheckBox.IsEnabled = selected is not null;
        RoleComboBox.IsEnabled = selected is not null;
        SaveButton.IsEnabled = selected is not null;
        if (selected is null) return;
        ActiveCheckBox.IsChecked = selected.User.IsActive;
        CommissionsCheckBox.IsChecked = selected.User.CanUseCommissions;
        RoleComboBox.SelectedItem = selected.User.Role;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } selected || RoleComboBox.SelectedItem is not AppUserRole role) return;
        if (MessageBox.Show(
                $"¿Confirma los cambios de permisos para {selected.User.DisplayName} ({selected.User.Email})?",
                "Confirmar permisos", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var updated = new AppUser
        {
            Uid = selected.User.Uid,
            Email = selected.User.Email,
            DisplayName = selected.User.DisplayName,
            Role = role,
            IsActive = ActiveCheckBox.IsChecked == true,
            CanUseCommissions = CommissionsCheckBox.IsChecked == true,
            CreatedAtUtc = selected.User.CreatedAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        SetBusy(true, "Guardando permisos…");
        try
        {
            var stored = await _repository.UpdateAsync(updated, selected.UpdateTime);
            selected.User = stored.Value;
            selected.UpdateTime = stored.UpdateTime;
            UsersGrid.Items.Refresh();
            StatusTextBlock.Text = "Permisos actualizados.";
        }
        catch (FirestoreConcurrencyException ex)
        {
            MessageBox.Show(ex.Message, "Conflicto de concurrencia", MessageBoxButton.OK, MessageBoxImage.Warning);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "No se guardaron los permisos", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        UsersGrid.IsEnabled = !busy;
        SearchTextBox.IsEnabled = !busy;
        CreateButton.IsEnabled = !busy;
        ReloadButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy && Selected is not null;
        if (status is not null) StatusTextBlock.Text = status;
    }

    private sealed class UserRow(AppUser user, string updateTime)
    {
        public AppUser User { get; set; } = user;
        public string UpdateTime { get; set; } = updateTime;
    }
}
