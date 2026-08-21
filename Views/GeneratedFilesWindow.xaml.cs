using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;

namespace ECS.CommissionsMailer.Views;

public partial class GeneratedFilesWindow : Window, INotifyPropertyChanged
{
    private readonly GeneratedFileViewerService _viewerService;
    private readonly AssociatedWorkbookEditService _editService;
    private readonly Action<Window, BrokerSendItem> _addManualFile;
    private readonly Func<BrokerSendItem, string, AssociatedFileUnlinkResult> _unlinkFile;
    private readonly Action<BrokerSendItem, string, string> _persistReplacement;

    public GeneratedFilesWindow(
        BrokerSendItem broker,
        GeneratedFileViewerService viewerService,
        AssociatedWorkbookEditService editService,
        Action<Window, BrokerSendItem> addManualFile,
        Func<BrokerSendItem, string, AssociatedFileUnlinkResult> unlinkFile,
        Action<BrokerSendItem, string, string> persistReplacement)
    {
        InitializeComponent();
        Broker = broker;
        _viewerService = viewerService;
        _editService = editService;
        _addManualFile = addManualFile;
        _unlinkFile = unlinkFile;
        _persistReplacement = persistReplacement;
        Broker.AttachmentPaths.CollectionChanged += AttachmentPaths_CollectionChanged;
        Closed += GeneratedFilesWindow_Closed;
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public BrokerSendItem Broker { get; }
    public ObservableCollection<string> Files => Broker.AttachmentPaths;
    public string TitleText => $"Archivos asociados — {Broker.BrokerName}";
    public string FileCountText => Files.Count == 1
        ? "1 archivo asociado"
        : $"{Files.Count} archivos asociados";

    private void ViewFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string path)
        {
            return;
        }

        var result = _viewerService.OpenAssociated(path, Broker);
        GeneratedFilesGrid.Items.Refresh();
        switch (result.Status)
        {
            case GeneratedFileOpenStatus.Opened:
                return;
            case GeneratedFileOpenStatus.MissingPath:
            case GeneratedFileOpenStatus.NonAbsolutePath:
            case GeneratedFileOpenStatus.FileNotFound:
                AppDialog.Show(
                    "Este archivo no está disponible en este equipo.\n\n" +
                    "Firestore conserva únicamente los metadatos y resultados; no contiene el Excel físico. " +
                    "La asociación local se conservará hasta que decida eliminarla de la lista.",
                    "Archivo no disponible", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            case GeneratedFileOpenStatus.UnsupportedExtension:
                AppDialog.Show(
                    "No fue posible abrir el archivo.\n\n" +
                    "Solo se pueden visualizar archivos .xlsx asociados.",
                    "Archivo no compatible", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            case GeneratedFileOpenStatus.NotAssociated:
                AppDialog.Show(
                    "No fue posible abrir el archivo.\n\n" +
                    "El archivo ya no está asociado a este corredor.",
                    "Archivo no asociado", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            case GeneratedFileOpenStatus.TemporaryCopyFailed:
                AppDialog.Show(
                    "No se pudo preparar una copia temporal del archivo para visualizarlo.\n\n" +
                    "El archivo original no fue modificado.",
                    "No se pudo preparar la vista", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            default:
                AppDialog.Show(
                    "No fue posible abrir el archivo.\n\n" +
                    "Verifique que exista una aplicación instalada para visualizar archivos de Excel.",
                    "No se pudo abrir", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
        }
    }

    private void EditFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string path)
        {
            return;
        }

        if (!AssociatedFileAssociationService.IsAssociated(Broker, path))
        {
            AppDialog.Show(
                "El archivo ya no está asociado a este corredor.",
                "Archivo no asociado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var start = _editService.BeginEdit(path);
        if (!start.Succeeded)
        {
            ShowEditStartError(start);
            return;
        }

        var session = start.Session!;
        while (true)
        {
            var checkChoice = AppDialog.Show(
                "El archivo se abrió para edición.\n\n" +
                "Realice los cambios en Excel, guarde el archivo y ciérrelo. " +
                "Luego presione “Comprobar cambios”.",
                "Editar archivo asociado",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information,
                "Comprobar cambios",
                "Cancelar edición");
            if (checkChoice != MessageBoxResult.Yes)
            {
                _editService.Cancel(session);
                return;
            }

            var check = _editService.CheckChanges(session);
            if (check.Status == WorkbookChangeCheckStatus.FileLocked)
            {
                AppDialog.Show(
                    "No se puede comprobar el archivo porque continúa abierto en Excel.\n\n" +
                    "Guarde los cambios, cierre el archivo e intente nuevamente.",
                    "Archivo todavía abierto", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            if (check.Status == WorkbookChangeCheckStatus.NoChanges)
            {
                _editService.Cancel(session);
                AppDialog.Show(
                    "No se detectaron cambios en el archivo.\n\n" +
                    "El archivo asociado no fue reemplazado.",
                    "Sin cambios", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!check.HasChanges)
            {
                ShowChangeCheckError(check);
                _editService.Cancel(session);
                return;
            }

            var replaceChoice = AppDialog.Show(
                "Se detectaron cambios en el archivo editado.\n\n" +
                "¿Desea reemplazar el archivo actualmente asociado por esta nueva versión?\n\n" +
                "La ruta, el nombre y la asociación con el corredor se conservarán.",
                "Confirmar reemplazo",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                "Sí, reemplazar",
                "No, conservar original");
            if (replaceChoice != MessageBoxResult.Yes)
            {
                _editService.Cancel(session);
                return;
            }

            var replacement = _editService.Replace(
                session,
                sha256 => _persistReplacement(Broker, path, sha256));
            if (replacement.Status == WorkbookReplaceStatus.FileLocked)
            {
                AppDialog.Show(
                    "No se puede reemplazar el archivo porque la copia editada o el archivo original " +
                    "continúa abierto.\n\nCierre el archivo e intente nuevamente.",
                    "Archivo todavía abierto", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            if (replacement.Succeeded)
            {
                GeneratedFilesGrid.Items.Refresh();
                AppDialog.Show(
                    "El archivo asociado fue reemplazado correctamente.\n\n" +
                    "Se conservaron la ruta, el nombre y la asociación con el corredor.",
                    "Archivo reemplazado", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            AppDialog.Show(
                "No se pudo reemplazar el archivo.\n\n" +
                (replacement.Status == WorkbookReplaceStatus.RecoveryFailed
                    ? "No fue posible confirmar la recuperación automática. Revise el registro de la aplicación."
                    : "El archivo anterior se conservó sin cambios.") +
                SafeDetail(replacement.ErrorMessage),
                "No se pudo reemplazar", MessageBoxButton.OK, MessageBoxImage.Error);
            _editService.Cancel(session);
            return;
        }
    }

    private void UnlinkFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string path)
        {
            return;
        }

        var choice = AppDialog.Show(
            "¿Desea eliminar este archivo de la lista del corredor?\n\n" +
            "El archivo dejará de estar asociado y no se adjuntará al correo.\n\n" +
            "El archivo físico permanecerá en su ubicación actual y no será eliminado.",
            "Eliminar de la lista",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            "Eliminar de la lista",
            "Cancelar");
        if (choice != MessageBoxResult.Yes)
        {
            return;
        }

        var result = _unlinkFile(Broker, path);
        GeneratedFilesGrid.Items.Refresh();
        if (result.Status == AssociatedFileUnlinkStatus.PersistenceFailed)
        {
            AppDialog.Show(
                "No fue posible guardar el cambio.\n\n" +
                "La asociación y el archivo físico se conservaron." +
                SafeDetail(result.ErrorMessage),
                "Error al guardar", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else if (result.Status == AssociatedFileUnlinkStatus.NotAssociated)
        {
            AppDialog.Show(
                "El archivo ya no está asociado a este corredor.",
                "Archivo no asociado", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void AddManualFile_Click(object sender, RoutedEventArgs e)
    {
        _addManualFile(this, Broker);
        GeneratedFilesGrid.Items.Refresh();
    }

    private static void ShowEditStartError(WorkbookEditStartResult result)
    {
        var (message, title, icon) = result.Status switch
        {
            WorkbookEditStartStatus.OriginalNotFound => (
                "No se encontró el archivo original. La asociación se conservará.",
                "Archivo no encontrado",
                MessageBoxImage.Warning),
            WorkbookEditStartStatus.UnsupportedExtension => (
                "Solo se pueden editar archivos asociados con extensión .xlsx.",
                "Archivo no compatible",
                MessageBoxImage.Warning),
            WorkbookEditStartStatus.FileLocked => (
                "El archivo original está abierto o bloqueado. Ciérrelo e intente nuevamente.",
                "Archivo abierto",
                MessageBoxImage.Warning),
            WorkbookEditStartStatus.InvalidWorkbook => (
                "El archivo asociado no es un libro .xlsx válido o está dañado.",
                "Archivo inválido",
                MessageBoxImage.Error),
            _ => (
                "No fue posible crear y abrir la copia temporal." + SafeDetail(result.ErrorMessage),
                "No se pudo editar",
                MessageBoxImage.Error)
        };
        AppDialog.Show(message, title, MessageBoxButton.OK, icon);
    }

    private static void ShowChangeCheckError(WorkbookChangeCheckResult result)
    {
        var message = result.Status switch
        {
            WorkbookChangeCheckStatus.TemporaryFileNotFound =>
                "No se encontró la copia temporal editada.",
            WorkbookChangeCheckStatus.OriginalNotFound =>
                "No se encontró el archivo original. La asociación se conservará.",
            WorkbookChangeCheckStatus.UnsupportedExtension =>
                "La copia temporal o el archivo original ya no tiene extensión .xlsx.",
            WorkbookChangeCheckStatus.InvalidWorkbook =>
                "La copia editada no es un libro .xlsx válido o está dañada.",
            WorkbookChangeCheckStatus.OriginalChanged =>
                "El archivo original cambió mientras se editaba la copia. No se reemplazará para evitar perder cambios.",
            _ => "No fue posible comprobar la copia editada." + SafeDetail(result.ErrorMessage)
        };
        AppDialog.Show(
            message,
            "No se pudieron comprobar los cambios",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void AttachmentPaths_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(FileCountText));
        GeneratedFilesGrid.Items.Refresh();
    }

    private void GeneratedFilesWindow_Closed(object? sender, EventArgs e) =>
        Broker.AttachmentPaths.CollectionChanged -= AttachmentPaths_CollectionChanged;

    private static string SafeDetail(string detail) =>
        string.IsNullOrWhiteSpace(detail) ? string.Empty : $"\n\nDetalle: {detail}";

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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

public sealed class AssociatedFileOriginConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 ||
            values[0] is not string path ||
            values[1] is not IEnumerable<string> generatedPaths)
        {
            return "Manual";
        }

        return generatedPaths.Any(candidate => PathsEqual(candidate, path))
            ? "Generado"
            : "Manual";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public sealed class AssociatedFileModifiedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            return value is string path && File.Exists(path)
                ? File.GetLastWriteTime(path).ToString("dd/MM/yyyy HH:mm", culture)
                : "—";
        }
        catch
        {
            return "—";
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
