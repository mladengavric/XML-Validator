using Avalonia.Controls;

namespace XmlValidator.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        // Početni fokus na „Zatvori” (Enter i Esc zatvaraju prozor).
        Opened += (_, _) => CloseButton.Focus();
    }
}
