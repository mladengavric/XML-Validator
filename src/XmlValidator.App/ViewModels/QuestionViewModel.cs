using CommunityToolkit.Mvvm.Input;

namespace XmlValidator.App.ViewModels;

/// <summary>
/// Kratko pitanje „Da/Ne” preko glavnog prozora, bez naslovne trake i bez dugmeta „Otkaži”: klik van pitanja
/// ili Esc ga zatvara bez odgovora.
/// </summary>
public sealed partial class QuestionViewModel(string title, string message) : ViewModelBase
{
    private readonly TaskCompletionSource<bool?> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Title { get; } = title;

    public string Message { get; } = message;

    public string YesText => "Da";

    public string NoText => "Ne";

    /// <summary>Odgovor: <c>true</c> = Da, <c>false</c> = Ne, <c>null</c> = zatvoreno bez odgovora.</summary>
    public Task<bool?> Answer => _answer.Task;

    [RelayCommand]
    private void Yes() => _answer.TrySetResult(true);

    [RelayCommand]
    private void No() => _answer.TrySetResult(false);

    /// <summary>Klik van pitanja ili Esc.</summary>
    [RelayCommand]
    public void Dismiss() => _answer.TrySetResult(null);
}
