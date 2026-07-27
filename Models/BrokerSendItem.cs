using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text.Json.Serialization;
using ECS.CommissionsMailer.Helpers;

namespace ECS.CommissionsMailer.Models;

public sealed class BrokerSendItem : ObservableObject
{
    private Guid _brokerId;
    private string _brokerName = string.Empty;
    private List<string> _primaryRecipients = [];
    private List<BrokerAssistant> _assistants = [];
    private bool _isSelected = true;
    private ObservableCollection<string> _attachmentPaths = [];
    private ObservableCollection<string> _generatedAttachmentPaths = [];
    private SendStatus _status;
    private string _lastError = string.Empty;
    private bool _isSending;
    private bool _requiresReview;
    private string _reviewNote = string.Empty;
    private bool _requiresBatchReview;
    private string _batchReviewNote = string.Empty;
    private string? _seedKey;

    public BrokerSendItem()
    {
        AttachCollection(_attachmentPaths);
        AttachGeneratedCollection(_generatedAttachmentPaths);
    }

    public Guid BrokerId
    {
        get => _brokerId;
        set => SetProperty(ref _brokerId, value);
    }

    public string BrokerName
    {
        get => _brokerName;
        set
        {
            if (SetProperty(ref _brokerName, value))
            {
                OnPropertyChanged(nameof(BrokerIdentityText));
            }
        }
    }

    public string? SeedKey
    {
        get => _seedKey;
        set => SetProperty(ref _seedKey, value);
    }

    public List<string> PrimaryRecipients
    {
        get => _primaryRecipients;
        set
        {
            if (SetProperty(ref _primaryRecipients, value ?? []))
            {
                OnPropertyChanged(nameof(PrimaryRecipientsText));
                OnPropertyChanged(nameof(BrokerIdentityText));
            }
        }
    }

    public List<BrokerAssistant> Assistants
    {
        get => _assistants;
        set
        {
            if (SetProperty(ref _assistants, value ?? []))
            {
                NotifyAssistantProperties();
            }
        }
    }

    public bool RequiresReview
    {
        get => _requiresReview;
        set => SetProperty(ref _requiresReview, value);
    }

    public string ReviewNote
    {
        get => _reviewNote;
        set => SetProperty(ref _reviewNote, value ?? string.Empty);
    }

    public bool RequiresBatchReview
    {
        get => _requiresBatchReview;
        set => SetProperty(ref _requiresBatchReview, value);
    }

    public string BatchReviewNote
    {
        get => _batchReviewNote;
        set => SetProperty(ref _batchReviewNote, value ?? string.Empty);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ObservableCollection<string> AttachmentPaths
    {
        get => _attachmentPaths;
        set
        {
            if (ReferenceEquals(_attachmentPaths, value))
            {
                return;
            }

            DetachCollection(_attachmentPaths);
            _attachmentPaths = value ?? [];
            AttachCollection(_attachmentPaths);
            OnPropertyChanged();
            NotifyAttachmentProperties();
        }
    }

    public ObservableCollection<string> GeneratedAttachmentPaths
    {
        get => _generatedAttachmentPaths;
        set
        {
            if (ReferenceEquals(_generatedAttachmentPaths, value))
            {
                return;
            }

            DetachGeneratedCollection(_generatedAttachmentPaths);
            _generatedAttachmentPaths = value ?? [];
            AttachGeneratedCollection(_generatedAttachmentPaths);
            OnPropertyChanged();
            NotifyAttachmentProperties();
        }
    }

    public SendStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string LastError
    {
        get => _lastError;
        set => SetProperty(ref _lastError, value ?? string.Empty);
    }

    [JsonIgnore]
    public bool IsSending
    {
        get => _isSending;
        set => SetProperty(ref _isSending, value);
    }

    [JsonIgnore]
    public string PrimaryRecipientsText => string.Join("; ", PrimaryRecipients);

    [JsonIgnore]
    public string BrokerIdentityText => string.IsNullOrWhiteSpace(PrimaryRecipientsText)
        ? BrokerName
        : $"{BrokerName} — {PrimaryRecipientsText}";

    [JsonIgnore]
    public IReadOnlyList<BrokerAssistant> ActiveAssistants => Assistants.Where(value => value.IsActive).ToList();

    [JsonIgnore]
    public int AssistantCount => Assistants.Count;

    [JsonIgnore]
    public string AssistantSummary => Assistants.Count == 0
        ? "Sin asistentes"
        : $"{Assistants.Count} · {string.Join(", ", Assistants.Select(value => value.Name))}";

    [JsonIgnore]
    public string AssistantTooltip => Assistants.Count == 0
        ? "Este corredor no tiene asistentes asociados."
        : string.Join(Environment.NewLine, Assistants.Select(value =>
            $"{value.Name} — {value.Email}{(value.IsActive ? string.Empty : " (inactivo)")}"));

    [JsonIgnore]
    public int AttachmentCount => AttachmentPaths.Count;

    [JsonIgnore]
    public string AttachmentCountText => AttachmentCount == 1 ? "1 archivo" : $"{AttachmentCount} archivos";

    [JsonIgnore]
    public string AttachmentSummary => AttachmentPaths.Count == 0
        ? "Sin archivos"
        : string.Join(Environment.NewLine, AttachmentPaths.Select(Path.GetFileName));

    [JsonIgnore]
    public IReadOnlyList<string> ManualAttachmentPaths
    {
        get
        {
            var generated = GeneratedAttachmentPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return AttachmentPaths.Where(path => !generated.Contains(path)).ToList();
        }
    }

    [JsonIgnore]
    public bool HasGeneratedAttachments => GeneratedAttachmentPaths.Count > 0;

    [JsonIgnore]
    public bool HasManualAttachments => ManualAttachmentPaths.Count > 0;

    [JsonIgnore]
    public string StatusText => Status switch
    {
        SendStatus.Pending => "Pendiente",
        SendStatus.Ready => "Listo",
        SendStatus.Sending => "Enviando",
        SendStatus.Sent => "Enviado",
        SendStatus.Error => "Error",
        SendStatus.ReviewRequired => "Revisión requerida",
        _ => "Pendiente"
    };

    public void RefreshComputedProperties()
    {
        OnPropertyChanged(nameof(PrimaryRecipientsText));
        OnPropertyChanged(nameof(BrokerIdentityText));
        NotifyAssistantProperties();
        NotifyAttachmentProperties();
        OnPropertyChanged(nameof(StatusText));
    }

    private void AttachCollection(ObservableCollection<string> collection) =>
        collection.CollectionChanged += AttachmentPaths_CollectionChanged;

    private void DetachCollection(ObservableCollection<string> collection) =>
        collection.CollectionChanged -= AttachmentPaths_CollectionChanged;

    private void AttachmentPaths_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        NotifyAttachmentProperties();

    private void AttachGeneratedCollection(ObservableCollection<string> collection) =>
        collection.CollectionChanged += GeneratedAttachmentPaths_CollectionChanged;

    private void DetachGeneratedCollection(ObservableCollection<string> collection) =>
        collection.CollectionChanged -= GeneratedAttachmentPaths_CollectionChanged;

    private void GeneratedAttachmentPaths_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        NotifyAttachmentProperties();

    private void NotifyAttachmentProperties()
    {
        OnPropertyChanged(nameof(AttachmentCount));
        OnPropertyChanged(nameof(AttachmentCountText));
        OnPropertyChanged(nameof(AttachmentSummary));
        OnPropertyChanged(nameof(ManualAttachmentPaths));
        OnPropertyChanged(nameof(HasGeneratedAttachments));
        OnPropertyChanged(nameof(HasManualAttachments));
    }

    private void NotifyAssistantProperties()
    {
        OnPropertyChanged(nameof(ActiveAssistants));
        OnPropertyChanged(nameof(AssistantCount));
        OnPropertyChanged(nameof(AssistantSummary));
        OnPropertyChanged(nameof(AssistantTooltip));
    }
}
