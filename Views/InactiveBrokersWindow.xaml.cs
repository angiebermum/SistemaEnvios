using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Views;

public partial class InactiveBrokersWindow : Window
{
    public InactiveBrokersWindow(ObservableCollection<Broker> inactiveBrokers)
    {
        InitializeComponent();
        InactiveBrokers = inactiveBrokers;
        DataContext = this;
    }

    public ObservableCollection<Broker> InactiveBrokers { get; }
    public event Action<Broker>? ActivationRequested;

    private void ActivateBroker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle || toggle.CommandParameter is not Broker broker)
        {
            return;
        }

        if (toggle.IsChecked != true)
        {
            return;
        }

        if (AppDialog.Show(
                $"¿Desea activar a '{broker.IdentityText}'?\n\nEl corredor volverá a aparecer en la vista principal.",
                "Activar corredor", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            return;
        }

        ActivationRequested?.Invoke(broker);
    }
}
