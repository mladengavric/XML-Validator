using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XmlValidator.App.Services;

namespace XmlValidator.App.ViewModels;

/// <summary>
/// Dijalog za poruku, potvrdu, izbor jedne opcije ili unos teksta. Prikaz se zatvara na
/// <see cref="CloseRequested"/> (<c>true</c> = potvrđeno).
/// </summary>
public sealed partial class DialogViewModel : ViewModelBase
{
    public DialogViewModel(string title, string message, MessageSeverity severity, string confirmText, string? cancelText)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(confirmText);
        Title = title;
        Message = message;
        Severity = severity;
        ConfirmText = confirmText;
        CancelText = cancelText;
    }

    public string Title { get; }

    public string Message { get; }

    public MessageSeverity Severity { get; }

    /// <summary>Detalji (npr. greške), jedna stavka po liniji.</summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    public bool HasDetails => Details.Count > 0;

    public string ConfirmText { get; }

    /// <summary>Tekst dugmeta za odustajanje; <c>null</c> za poruku sa jednim dugmetom.</summary>
    public string? CancelText { get; }

    public bool HasCancel => CancelText is not null;

    /// <summary>Potvrda pokreće opasnu akciju (brisanje, prepisivanje): dugme se ističe kao opasno.</summary>
    public bool IsDangerous { get; init; }

    /// <summary>Ponuđene opcije (dijalog izbora); prazno za ostale dijaloge.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    public bool HasOptions => Options.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string? SelectedOption { get; set; }

    /// <summary>Dijalog traži unos teksta.</summary>
    public bool IsPrompt { get; init; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string InputText { get; set; } = string.Empty;

    /// <summary>Ishod: <c>true</c> potvrđeno, <c>false</c> odustao (ili zatvoren prozor).</summary>
    public bool? Result { get; private set; }

    public event EventHandler<bool>? CloseRequested;

    private bool CanConfirm() =>
        (!HasOptions || SelectedOption is not null) && (!IsPrompt || !string.IsNullOrWhiteSpace(InputText));

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => Close(true);

    [RelayCommand]
    private void Cancel() => Close(false);

    private void Close(bool result)
    {
        Result = result;
        CloseRequested?.Invoke(this, result);
    }
}
