using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ECS.CommissionsMailer.Models;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class DeductionEditorWindow : Window
{
    public DeductionEditorWindow(BrokerDeduction? deduction)
    {
        InitializeComponent();
        EditedDeduction = deduction?.Clone() ?? new BrokerDeduction();
        Title = deduction is null ? "Agregar rebajo" : "Editar rebajo";
        DescriptionTextBox.Text = EditedDeduction.Description;
        AmountTextBox.Text = EditedDeduction.Amount.ToString("0.00", CultureInfo.CurrentCulture);
        CurrencyComboBox.SelectedIndex = EditedDeduction.Currency == DeductionCurrency.CRC ? 0 : 1;
        ApplicationTypeComboBox.SelectedIndex =
            EditedDeduction.ApplicationType == DeductionApplicationType.GrossCommission ? 0 : 1;
        Loaded += (_, _) => DescriptionTextBox.Focus();
    }

    public BrokerDeduction EditedDeduction { get; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        var description = DescriptionTextBox.Text.Trim();
        if (description.Length == 0)
        {
            errors.Add("La descripción es obligatoria.");
        }

        if (!TryParseAmount(AmountTextBox.Text, out var amount) || amount < 0)
        {
            errors.Add("El monto debe ser un número decimal mayor o igual que cero.");
        }

        var currency = DeductionCurrency.CRC;
        if (CurrencyComboBox.SelectedItem is not ComboBoxItem currencyItem ||
            !Enum.TryParse<DeductionCurrency>(currencyItem.Tag?.ToString(), out currency))
        {
            errors.Add("Seleccione una moneda.");
        }

        var applicationType = DeductionApplicationType.GrossCommission;
        if (ApplicationTypeComboBox.SelectedItem is not ComboBoxItem typeItem ||
            !Enum.TryParse<DeductionApplicationType>(typeItem.Tag?.ToString(), out applicationType))
        {
            errors.Add("Seleccione un tipo de aplicación.");
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(string.Join(Environment.NewLine, errors.Select(value => $"• {value}")),
                "Rebajo inválido", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        EditedDeduction.Description = description;
        EditedDeduction.Amount = amount;
        EditedDeduction.Currency = currency;
        EditedDeduction.ApplicationType = applicationType;
        DialogResult = true;
    }

    private static bool TryParseAmount(string text, out decimal amount)
    {
        var cultures = new[]
        {
            CultureInfo.CurrentCulture,
            CultureInfo.GetCultureInfo("es-CR"),
            CultureInfo.InvariantCulture
        };
        foreach (var culture in cultures.DistinctBy(value => value.Name))
        {
            if (decimal.TryParse(text, NumberStyles.Number, culture, out amount))
            {
                return true;
            }
        }

        amount = 0m;
        return false;
    }
}
