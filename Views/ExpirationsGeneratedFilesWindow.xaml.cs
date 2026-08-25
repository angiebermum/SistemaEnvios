using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services;
using ECS.CommissionsMailer.Services.Expirations;
using Microsoft.Win32;

namespace ECS.CommissionsMailer.Views;

public partial class ExpirationsGeneratedFilesWindow : Window, INotifyPropertyChanged
{
    private readonly string _brokerName;
    private readonly Func<IReadOnlyList<ExpirationsGeneratedFile>> _loadFiles;
    private readonly GeneratedFileViewerService _viewerService;
    private readonly AssociatedWorkbookEditService _editService;
    private readonly Action<ExpirationsGeneratedFile, string> _persistReplacement;
    private readonly Action<ExpirationsGeneratedFile> _unlinkFile;
    private readonly Func<string, ExpirationsManualFileAddResult> _addManualFile;
    private readonly Func<string, ExpirationsAttachmentSlotKey?, ExpirationsManualFileAddResult>? _addManualFileWithTarget;
    private readonly IReadOnlyList<ExpirationsRequiredAttachmentSlot> _requiredSlots;
    private readonly Func<Task> _afterMutation;
    private readonly bool _isParticipant;

    public ExpirationsGeneratedFilesWindow(
        string brokerName,
        Func<IReadOnlyList<ExpirationsGeneratedFile>> loadFiles,
        GeneratedFileViewerService viewerService,
        AssociatedWorkbookEditService editService,
        Action<ExpirationsGeneratedFile, string> persistReplacement,
        Action<ExpirationsGeneratedFile> unlinkFile,
        Func<string, ExpirationsManualFileAddResult> addManualFile,
        Func<Task> afterMutation,
        bool isParticipant = true,
        Func<string, ExpirationsAttachmentSlotKey?, ExpirationsManualFileAddResult>? addManualFileWithTarget = null,
        IReadOnlyList<ExpirationsRequiredAttachmentSlot>? requiredSlots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerName);
        _brokerName = brokerName;
        _loadFiles = loadFiles ?? throw new ArgumentNullException(nameof(loadFiles));
        _viewerService = viewerService ?? throw new ArgumentNullException(nameof(viewerService));
        _editService = editService ?? throw new ArgumentNullException(nameof(editService));
        _persistReplacement = persistReplacement ?? throw new ArgumentNullException(nameof(persistReplacement));
        _unlinkFile = unlinkFile ?? throw new ArgumentNullException(nameof(unlinkFile));
        _addManualFile = addManualFile ?? throw new ArgumentNullException(nameof(addManualFile));
        _addManualFileWithTarget = addManualFileWithTarget;
        _requiredSlots = requiredSlots ?? [];
        _afterMutation = afterMutation ?? throw new ArgumentNullException(nameof(afterMutation));
        _isParticipant = isParticipant;
        InitializeComponent();
        DataContext = this;
        RefreshFiles();
    }

    public ObservableCollection<ExpirationsGeneratedFileDisplay> Files { get; } = [];
    public ObservableCollection<ExpirationsManualTargetOption> ManualTargetOptions { get; } = [];
    public ExpirationsManualTargetOption? SelectedManualTarget { get; set; }
    public string TitleText => $"Archivos asociados — {_brokerName}";
    public string FileCountText => Files.Count == 1 ? "1 archivo asociado" : $"{Files.Count} archivos asociados";
    public string EmptyFilesText => _isParticipant
        ? "No hay archivos asociados a este corredor."
        : "Este corredor no participa en el reporte actual. Puede agregar archivos manuales para un envío puntual.";
    public Visibility EmptyFilesVisibility => Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ViewFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ExpirationsGeneratedFileDisplay item)
            return;
        var result = _viewerService.OpenAssociated(
            item.Path,
            _brokerName,
            _loadFiles().Select(file => file.OutputPath));
        RefreshFiles();
        if (result.Succeeded)
            return;
        var message = result.Status switch
        {
            GeneratedFileOpenStatus.MissingPath or
            GeneratedFileOpenStatus.NonAbsolutePath or
            GeneratedFileOpenStatus.FileNotFound =>
                "Este archivo no está disponible en este equipo. La asociación se conservará hasta que decida eliminarla de la lista.",
            GeneratedFileOpenStatus.UnsupportedExtension => "Solo se pueden visualizar archivos .xlsx asociados.",
            GeneratedFileOpenStatus.NotAssociated => "El archivo ya no pertenece al batch actual.",
            GeneratedFileOpenStatus.TemporaryCopyFailed =>
                "No se pudo preparar una copia temporal. El archivo original no fue modificado.",
            _ => "No fue posible abrir la copia temporal. Verifique que exista una aplicación para archivos de Excel."
        };
        AppDialog.Show(message, "Archivo de Vencimientos", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void EditFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ExpirationsGeneratedFileDisplay item)
            return;
        if (!IsStillAssociated(item.File))
        {
            AppDialog.Show("El archivo ya no pertenece al batch actual.", "Archivo no asociado",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var start = _editService.BeginEdit(item.Path);
        if (!start.Succeeded)
        {
            ShowEditStartError(start);
            return;
        }

        var session = start.Session!;
        while (true)
        {
            var checkChoice = AppDialog.Show(
                "El archivo se abrió para edición.\n\nRealice los cambios en Excel, guarde el archivo y ciérrelo. Luego presione “Comprobar cambios”.",
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
                    "El archivo continúa abierto en Excel. Guárdelo, ciérrelo e intente nuevamente.",
                    "Archivo todavía abierto", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }
            if (check.Status == WorkbookChangeCheckStatus.NoChanges)
            {
                _editService.Cancel(session);
                AppDialog.Show(
                    "No se detectaron cambios. El archivo asociado no fue reemplazado.",
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
                "Se detectaron cambios en la copia editada.\n\n¿Desea reemplazar de forma segura el archivo asociado? La ruta, el nombre y el slot BrokerId/Variant se conservarán.",
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
                sha256 => _persistReplacement(item.File, sha256));
            if (replacement.Status == WorkbookReplaceStatus.FileLocked)
            {
                AppDialog.Show(
                    "La copia editada o el original continúa abierto. Cierre el archivo e intente nuevamente.",
                    "Archivo todavía abierto", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }
            if (replacement.Succeeded)
            {
                await _afterMutation();
                RefreshFiles();
                AppDialog.Show(
                    "El archivo fue reemplazado correctamente. El SHA del batch actual se actualizó y el correo requerirá revisión.",
                    "Archivo reemplazado", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            AppDialog.Show(
                "No se pudo reemplazar el archivo.\n\n" +
                (replacement.Status == WorkbookReplaceStatus.RecoveryFailed
                    ? "No fue posible confirmar la recuperación automática. Revise el registro técnico."
                    : "El archivo anterior se conservó sin cambios.") +
                SafeDetail(replacement.ErrorMessage),
                "No se pudo reemplazar", MessageBoxButton.OK, MessageBoxImage.Error);
            _editService.Cancel(session);
            return;
        }
    }

    private async void UnlinkFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ExpirationsGeneratedFileDisplay item)
            return;
        var choice = AppDialog.Show(
            "El archivo dejará de estar asociado y no se adjuntará al correo.\n\nEl archivo físico permanecerá en su ubicación actual.",
            "Eliminar de la lista",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            "Eliminar de la lista",
            "Cancelar");
        if (choice != MessageBoxResult.Yes)
            return;
        _unlinkFile(item.File);
        await _afterMutation();
        RefreshFiles();
    }

    private async void AddManualFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Agregar archivo manual al batch de Vencimientos",
            Filter = "Archivos de Excel (*.xlsx)|*.xlsx",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;
        var result = _addManualFileWithTarget is null
            ? _addManualFile(dialog.FileName)
            : _addManualFileWithTarget(dialog.FileName, SelectedManualTarget?.SlotKey);
        if (!result.Succeeded)
        {
            AppDialog.Show(result.ErrorMessage, "No se pudo agregar el archivo",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await _afterMutation();
        RefreshFiles();
    }

    private bool IsStillAssociated(ExpirationsGeneratedFile file) => _loadFiles().Any(candidate =>
        candidate.BrokerId == file.BrokerId &&
        candidate.Variant == file.Variant &&
        PathsEqual(candidate.OutputPath, file.OutputPath));

    private void RefreshFiles()
    {
        Files.Clear();
        foreach (var file in _loadFiles()
                     .OrderBy(file => file.Variant == ExpirationsGeneratedFileVariant.Manual ? 1 : 0)
                     .ThenBy(file => file.Variant)
                     .ThenBy(file => file.OutputPath, StringComparer.OrdinalIgnoreCase))
        {
            Files.Add(new ExpirationsGeneratedFileDisplay(file));
        }
        GeneratedFilesGrid?.Items.Refresh();
        RefreshManualTargets();
        OnPropertyChanged(nameof(FileCountText));
        OnPropertyChanged(nameof(EmptyFilesVisibility));
    }

    private void RefreshManualTargets()
    {
        ManualTargetOptions.Clear();
        var additional = new ExpirationsManualTargetOption(null, "Archivo adicional");
        ManualTargetOptions.Add(additional);
        foreach (var slot in _requiredSlots.DistinctBy(slot => slot.Key))
        {
            ManualTargetOptions.Add(new ExpirationsManualTargetOption(
                slot.Key,
                $"Reemplaza: {slot.DisplayName}" +
                (slot.ExpectedVariant == ExpirationsGeneratedFileVariant.Standard
                    ? string.Empty
                    : $" ({slot.ExpectedVariant})")));
        }
        var files = _loadFiles();
        var missing = _requiredSlots.Where(slot =>
                !files.Any(file =>
                    (file.Variant == slot.ExpectedVariant &&
                     file.DestinationGroup == slot.DestinationGroup &&
                     !file.ReplacesSlot.HasValue) ||
                    file.ReplacesSlot == slot.Key))
            .DistinctBy(slot => slot.Key)
            .ToList();
        SelectedManualTarget = missing.Count == 1
            ? ManualTargetOptions.First(option => option.SlotKey == missing[0].Key)
            : additional;
        OnPropertyChanged(nameof(SelectedManualTarget));
    }

    private static void ShowEditStartError(WorkbookEditStartResult result)
    {
        var message = result.Status switch
        {
            WorkbookEditStartStatus.OriginalNotFound => "No se encontró el archivo original. La asociación se conservará.",
            WorkbookEditStartStatus.UnsupportedExtension => "Solo se pueden editar archivos .xlsx asociados.",
            WorkbookEditStartStatus.FileLocked => "El archivo original está abierto o bloqueado. Ciérrelo e intente nuevamente.",
            WorkbookEditStartStatus.InvalidWorkbook => "El archivo no es un libro .xlsx válido o está dañado.",
            _ => "No fue posible crear y abrir la copia temporal." + SafeDetail(result.ErrorMessage)
        };
        AppDialog.Show(message, "No se pudo editar", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void ShowChangeCheckError(WorkbookChangeCheckResult result)
    {
        var message = result.Status switch
        {
            WorkbookChangeCheckStatus.TemporaryFileNotFound => "No se encontró la copia temporal editada.",
            WorkbookChangeCheckStatus.OriginalNotFound => "No se encontró el archivo original.",
            WorkbookChangeCheckStatus.UnsupportedExtension => "La copia temporal o el original ya no tiene extensión .xlsx.",
            WorkbookChangeCheckStatus.InvalidWorkbook => "La copia editada no es un libro .xlsx válido o está dañada.",
            WorkbookChangeCheckStatus.OriginalChanged =>
                "El archivo original cambió mientras se editaba la copia. No se reemplazará para evitar perder cambios.",
            _ => "No fue posible comprobar la copia editada." + SafeDetail(result.ErrorMessage)
        };
        AppDialog.Show(message, "No se pudieron comprobar los cambios",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return left.Equals(right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string SafeDetail(string detail) =>
        string.IsNullOrWhiteSpace(detail) ? string.Empty : $"\n\nDetalle: {detail}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class ExpirationsGeneratedFileDisplay
{
    public ExpirationsGeneratedFileDisplay(ExpirationsGeneratedFile file)
    {
        File = file;
        Path = file.OutputPath;
        FileName = string.IsNullOrWhiteSpace(Path) ? "(sin nombre)" : System.IO.Path.GetFileName(Path);
        OriginText = file.Variant == ExpirationsGeneratedFileVariant.Manual ? "Manual" : "Generado";
        DestinationText = file.ReplacesSlot is { } replacement
            ? $"Reemplaza {ExpirationsDestinationGroups.DisplayName(replacement.DestinationGroup)}"
            : ExpirationsDestinationGroups.DisplayName(file.DestinationGroup);
        StatusText = GeneratedFileViewerService.GetAvailability(Path) == GeneratedFileAvailability.Available
            ? "Disponible"
            : "No encontrado";
        try
        {
            ModifiedText = System.IO.File.Exists(Path)
                ? System.IO.File.GetLastWriteTime(Path).ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture)
                : "—";
        }
        catch
        {
            ModifiedText = "—";
        }
        ReviewText = file.IsManuallyEdited
            ? "Editado manualmente · Requiere revisión"
            : file.RequiresReview
                ? "Requiere revisión"
                : string.Empty;
    }

    public ExpirationsGeneratedFile File { get; }
    public string Path { get; }
    public string FileName { get; }
    public string OriginText { get; }
    public string DestinationText { get; }
    public string ModifiedText { get; }
    public string StatusText { get; }
    public string ReviewText { get; }
}

public sealed record ExpirationsManualTargetOption(
    ExpirationsAttachmentSlotKey? SlotKey,
    string DisplayName);
