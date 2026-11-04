using Avalonia.Controls;
using XmlValidator.App.ViewModels;

namespace XmlValidator.App.Views;

public partial class DialogWindow : Window
{
    public DialogWindow()
    {
        InitializeComponent();
        Opened += (_, _) => FocusInitialControl();
    }

    /// <summary>
    /// Početni fokus: polje za unos (sa izabranim tekstom), lista opcija, a kod opasne akcije
    /// dugme „Otkaži”, da Enter ili razmak slučajno ne potvrde brisanje.
    /// </summary>
    private void FocusInitialControl()
    {
        if (DataContext is not DialogViewModel viewModel)
        {
            return;
        }

        if (viewModel.IsPrompt)
        {
            InputBox.Focus();
            InputBox.SelectAll();
        }
        else if (viewModel.HasOptions)
        {
            OptionsList.Focus();
        }
        else if (viewModel.IsDangerous && viewModel.HasCancel)
        {
            CancelButton.Focus();
        }
        else
        {
            ConfirmButton.Focus();
        }
    }
}
