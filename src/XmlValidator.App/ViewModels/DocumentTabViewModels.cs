using CommunityToolkit.Mvvm.ComponentModel;
using XmlValidator.App.Services;

namespace XmlValidator.App.ViewModels;

/// <summary>Zajednički dio tabova koji rade nad otvorenim fajlom.</summary>
public abstract partial class DocumentTabViewModel : ViewModelBase
{
    /// <summary>Otvoren fajl; <c>null</c> dok fajl nije otvoren (prikazuje se prazno stanje).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    public partial OpenedDocument? Document { get; private set; }

    public bool HasDocument => Document is not null;

    /// <summary>Naslov taba.</summary>
    public abstract string Header { get; }

    /// <summary>Naslov praznog stanja.</summary>
    public string EmptyStateTitle => "Nijedan fajl nije otvoren";

    /// <summary>Sljedeći korak u praznom stanju.</summary>
    public abstract string EmptyStateHint { get; }

    /// <summary>Postavlja otvoren fajl (poziva glavni prozor).</summary>
    public virtual void SetDocument(OpenedDocument? document) => Document = document;

    /// <summary>Sadržaj za „Sačuvaj kao” (originalni bajtovi, osim ako tab ima izmijenjen sadržaj).</summary>
    public virtual byte[]? GetContentToSave() => Document?.Content;

    /// <summary>Razlog zbog kojeg sadržaj ne može da se sačuva, ili <c>null</c>.</summary>
    public virtual string? GetSaveProblem() => null;

    /// <summary>Da li tab ima izmjene koje nisu sačuvane (pitanje prije otvaranja drugog fajla ili zatvaranja).</summary>
    public virtual bool HasUnsavedChanges => false;

    /// <summary>Sadržaj vraćen iz <see cref="GetContentToSave"/> je sačuvan.</summary>
    public virtual void MarkSaved()
    {
    }
}
