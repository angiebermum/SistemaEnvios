using System.Windows;
using System.Windows.Input;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authentication;

namespace ECS.CommissionsMailer.Views;

public partial class LoginWindow : Window
{
    private readonly IFirebaseAuthenticationService _authentication;
    private bool _busy;
    private bool _syncingPassword;

    public LoginWindow(IFirebaseAuthenticationService authentication, string? initialEmail = null)
    {
        InitializeComponent();
        _authentication = authentication;
        EmailTextBox.Text = initialEmail ?? string.Empty;
        Loaded += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(EmailTextBox.Text)) EmailTextBox.Focus();
            else PasswordBox.Focus();
        };
    }

    public FirebaseUserSession? Session { get; private set; }

    private async void SignIn_Click(object sender, RoutedEventArgs e) => await SignInAsync();

    private async void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SignInAsync();
        }
    }

    private async Task SignInAsync()
    {
        if (_busy) return;
        var email = EmailTextBox.Text.Trim();
        var password = PasswordBox.Password;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            StatusTextBlock.Text = "Ingrese el correo y la contraseña.";
            return;
        }

        SetBusy(true);
        StatusTextBlock.Text = "Validando la cuenta…";
        try
        {
            Session = await _authentication.SignInAsync(email, password);
            ClearPassword();
            DialogResult = true;
        }
        catch (FirebaseAuthenticationException ex)
        {
            ClearPassword();
            StatusTextBlock.Text = ex.Message;
            FocusPasswordInput();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ResetPassword_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var email = EmailTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(email))
        {
            StatusTextBlock.Text = "Ingrese primero el correo electrónico.";
            return;
        }

        SetBusy(true);
        try
        {
            await _authentication.SendPasswordResetAsync(email);
            StatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
            StatusTextBlock.Text = "Firebase envió las instrucciones para restablecer la contraseña.";
        }
        catch (FirebaseAuthenticationException ex)
        {
            StatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            StatusTextBlock.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        EmailTextBox.IsEnabled = !value;
        PasswordBox.IsEnabled = !value;
        VisiblePasswordTextBox.IsEnabled = !value;
        ShowPasswordCheckBox.IsEnabled = !value;
        SignInButton.IsEnabled = !value;
        ResetButton.IsEnabled = !value;
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword)
        {
            return;
        }

        _syncingPassword = true;
        try
        {
            VisiblePasswordTextBox.Text = PasswordBox.Password;
        }
        finally
        {
            _syncingPassword = false;
        }
    }

    private void VisiblePasswordTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncingPassword)
        {
            return;
        }

        _syncingPassword = true;
        try
        {
            PasswordBox.Password = VisiblePasswordTextBox.Text;
        }
        finally
        {
            _syncingPassword = false;
        }
    }

    private void ShowPasswordCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        SynchronizeVisiblePassword();
        PasswordBox.Visibility = Visibility.Collapsed;
        VisiblePasswordTextBox.Visibility = Visibility.Visible;
        FocusPasswordInput();
    }

    private void ShowPasswordCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        SynchronizeHiddenPassword();
        VisiblePasswordTextBox.Visibility = Visibility.Collapsed;
        PasswordBox.Visibility = Visibility.Visible;
        FocusPasswordInput();
    }

    private void SynchronizeVisiblePassword()
    {
        _syncingPassword = true;
        try
        {
            VisiblePasswordTextBox.Text = PasswordBox.Password;
        }
        finally
        {
            _syncingPassword = false;
        }
    }

    private void SynchronizeHiddenPassword()
    {
        _syncingPassword = true;
        try
        {
            PasswordBox.Password = VisiblePasswordTextBox.Text;
        }
        finally
        {
            _syncingPassword = false;
        }
    }

    private void ClearPassword()
    {
        _syncingPassword = true;
        try
        {
            PasswordBox.Clear();
            VisiblePasswordTextBox.Clear();
        }
        finally
        {
            _syncingPassword = false;
        }
    }

    private void FocusPasswordInput()
    {
        if (ShowPasswordCheckBox.IsChecked == true)
        {
            VisiblePasswordTextBox.Focus();
            VisiblePasswordTextBox.CaretIndex = VisiblePasswordTextBox.Text.Length;
        }
        else
        {
            PasswordBox.Focus();
        }
    }
}
