using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;

namespace ECS.CommissionsMailer.Views;

public partial class GeneratedFilesWindow : Window
{
    private readonly BrokerSendItem _broker;
    private readonly GeneratedFileViewerService _viewerService;

    public GeneratedFilesWindow(
        BrokerSendItem broker,
        IReadOnlyList<GeneratedPaymentFile> files,
        GeneratedFileViewerService viewerService)
    {
        InitializeComponent();
        _broker = broker;
        _viewerService = viewerService;
        Files = files;
        TitleText = $"Archivos generados — {broker.BrokerName}";
        FileCountText = files.Count == 1
            ? "1 archivo asociado"
            : $"{files.Count} archivos asociados";
        DataContext = this;
    }

    public IReadOnlyList<GeneratedPaymentFile> Files { get; }
    public string TitleText { get; }
    public string FileCountText { get; }

    private void ViewFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GeneratedPaymentFile file)
        {
            return;
        }

        var result = _viewerService.Open(file, _broker);
        GeneratedFilesGrid.Items.Refresh();
        switch (result.Status)
        {
            case GeneratedFileOpenStatus.Opened:
                return;
            case GeneratedFileOpenStatus.MissingPath:
            case GeneratedFileOpenStatus.NonAbsolutePath:
            case GeneratedFileOpenStatus.FileNotFound:
                AppDialog.Show(
                    "No se encontró el archivo generado.\n\n" +
                    "Es posible que haya sido movido, eliminado o renombrado.",
                    "Archivo no encontrado", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            case GeneratedFileOpenStatus.UnsupportedExtension:
                AppDialog.Show(
                    "No fue posible abrir el archivo.\n\n" +
                    "Solo se pueden visualizar archivos generados con extensión .xlsx.",
                    "Archivo no compatible", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            case GeneratedFileOpenStatus.NotAssociated:
                AppDialog.Show(
                    "No fue posible abrir el archivo.\n\n" +
                    "El archivo ya no está asociado a este corredor.",
                    "Archivo no asociado", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            default:
                AppDialog.Show(
                    "No fue posible abrir el archivo.\n\n" +
                    "Verifique que exista una aplicación instalada para visualizar archivos de Excel.",
                    "No se pudo abrir", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
        }
    }
}

public sealed class GeneratedFileNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value as string;
        return string.IsNullOrWhiteSpace(path) ? "(sin nombre)" : Path.GetFileName(path);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class GeneratedFileAvailabilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        GeneratedFileViewerService.GetAvailability(value as string) == GeneratedFileAvailability.Available
            ? "Disponible"
            : "No encontrado";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
