using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace ECS.CommissionsMailer.Views;

public static class AppDialog
{
    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon)
    {
        var dialog = new AppDialogWindow(messageBoxText, caption, button, icon);
        var owner = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive && window.IsVisible);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.ShowDialog();
        return dialog.Result;
    }
}

public partial class AppDialogWindow : Window
{
    private MessageBoxResult _primaryResult = MessageBoxResult.OK;
    private MessageBoxResult _secondaryResult = MessageBoxResult.Cancel;
    private MessageBoxResult _tertiaryResult = MessageBoxResult.Cancel;
    private MessageBoxResult _closeResult = MessageBoxResult.Cancel;
    private MessageBoxResult _result = MessageBoxResult.None;

    public AppDialogWindow(string message, string caption, MessageBoxButton buttons, MessageBoxImage icon)
    {
        InitializeComponent();
        Title = caption;
        TitleTextBlock.Text = caption;
        MessageTextBlock.Text = message;
        ConfigureIcon(icon);
        ConfigureButtons(buttons);
        Closing += AppDialogWindow_Closing;
    }

    public MessageBoxResult Result => _result == MessageBoxResult.None ? _closeResult : _result;

    private void ConfigureIcon(MessageBoxImage icon)
    {
        var (glyph, brushKey, background) = icon switch
        {
            MessageBoxImage.Error => ("\uEA39", "ErrorBrush", Color.FromRgb(252, 232, 232)),
            MessageBoxImage.Warning => ("\uE7BA", "AccentDarkBrush", Color.FromRgb(253, 246, 222)),
            MessageBoxImage.Question => ("\uE897", "PrimaryBrush", Color.FromRgb(234, 243, 251)),
            _ => ("\uE946", "PrimaryBrush", Color.FromRgb(234, 243, 251))
        };
        var accent = (Brush)FindResource(brushKey);
        IconText.Text = glyph;
        IconText.Foreground = accent;
        IconBorder.Background = new SolidColorBrush(background);
        AccentBar.Background = accent;
    }

    private void ConfigureButtons(MessageBoxButton buttons)
    {
        switch (buttons)
        {
            case MessageBoxButton.OKCancel:
                ConfigureSecondary("Cancelar", MessageBoxResult.Cancel, isCancel: true);
                ConfigurePrimary("Aceptar", MessageBoxResult.OK);
                _closeResult = MessageBoxResult.Cancel;
                break;
            case MessageBoxButton.YesNo:
                ConfigureSecondary("No", MessageBoxResult.No, isCancel: true);
                ConfigurePrimary("Sí", MessageBoxResult.Yes);
                _closeResult = MessageBoxResult.No;
                break;
            case MessageBoxButton.YesNoCancel:
                ConfigureTertiary("Cancelar", MessageBoxResult.Cancel, isCancel: true);
                ConfigureSecondary("No", MessageBoxResult.No);
                ConfigurePrimary("Sí", MessageBoxResult.Yes);
                _closeResult = MessageBoxResult.Cancel;
                break;
            default:
                ConfigurePrimary("Aceptar", MessageBoxResult.OK);
                _closeResult = MessageBoxResult.OK;
                break;
        }
    }

    private void ConfigurePrimary(string text, MessageBoxResult result)
    {
        PrimaryButton.Content = text;
        PrimaryButton.IsDefault = true;
        _primaryResult = result;
    }

    private void ConfigureSecondary(string text, MessageBoxResult result, bool isCancel = false)
    {
        SecondaryButton.Content = text;
        SecondaryButton.Visibility = Visibility.Visible;
        SecondaryButton.IsCancel = isCancel;
        _secondaryResult = result;
    }

    private void ConfigureTertiary(string text, MessageBoxResult result, bool isCancel)
    {
        TertiaryButton.Content = text;
        TertiaryButton.Visibility = Visibility.Visible;
        TertiaryButton.IsCancel = isCancel;
        _tertiaryResult = result;
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e) => CloseWith(_primaryResult);
    private void SecondaryButton_Click(object sender, RoutedEventArgs e) => CloseWith(_secondaryResult);
    private void TertiaryButton_Click(object sender, RoutedEventArgs e) => CloseWith(_tertiaryResult);

    private void CloseWith(MessageBoxResult result)
    {
        _result = result;
        Close();
    }

    private void AppDialogWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_result == MessageBoxResult.None)
        {
            _result = _closeResult;
        }
    }
}
