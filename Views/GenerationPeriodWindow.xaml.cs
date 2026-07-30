using System.Windows;
using ECS.CommissionsMailer.Services;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class GenerationPeriodWindow : Window
{
    private readonly DesktopOutputDirectoryService _outputService;
    private readonly FileNameSanitizer _sanitizer;
    private readonly string _sampleBroker;
    private readonly string _sampleWorksheet;

    public GenerationPeriodWindow(
        int worksheetCount,
        int brokerCount,
        string sampleBroker,
        string sampleWorksheet,
        DesktopOutputDirectoryService outputService,
        FileNameSanitizer sanitizer)
    {
        InitializeComponent();
        _outputService = outputService;
        _sanitizer = sanitizer;
        _sampleBroker = sampleBroker;
        _sampleWorksheet = sampleWorksheet;
        WorksheetCountTextBlock.Text = $"{worksheetCount} pestaña(s) / {worksheetCount} archivo(s)";
        BrokerCountTextBlock.Text = $"{brokerCount} corredor(es)";
        Loaded += (_, _) => PeriodTextBox.Focus();
        UpdatePreview();
    }

    public string Period { get; private set; } = string.Empty;
    public string OutputDirectory { get; private set; } = string.Empty;
    public bool OutputDirectoryExists { get; private set; }

    private void PeriodTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        UpdatePreview();

    private void UpdatePreview()
    {
        if (!IsInitialized)
        {
            return;
        }

        var period = PeriodTextBox.Text.Trim();
        if (period.Length == 0 || _sanitizer.ValidatePeriod(PeriodTextBox.Text).Count > 0)
        {
            FolderPreviewTextBlock.Text = "Ingrese un periodo válido.";
            FilePreviewTextBlock.Text = "Detalle de pago - …";
            FolderStateTextBlock.Text = "Pendiente";
            return;
        }

        var output = _outputService.GetOutputDirectory(period);
        FolderPreviewTextBlock.Text = output;
        FilePreviewTextBlock.Text = _sanitizer.CreatePaymentFileName(_sampleBroker, _sampleWorksheet, period);
        FolderStateTextBlock.Text = Directory.Exists(output)
            ? "La carpeta ya existe; se solicitará confirmación para reemplazar."
            : "Carpeta nueva";
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        var errors = _sanitizer.ValidatePeriod(PeriodTextBox.Text);
        if (errors.Count > 0)
        {
            MessageBox.Show(string.Join(Environment.NewLine, errors.Select(value => $"• {value}")),
                "Periodo inválido", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Period = PeriodTextBox.Text.Trim();
        OutputDirectory = _outputService.GetOutputDirectory(Period);
        OutputDirectoryExists = Directory.Exists(OutputDirectory);
        DialogResult = true;
    }
}
