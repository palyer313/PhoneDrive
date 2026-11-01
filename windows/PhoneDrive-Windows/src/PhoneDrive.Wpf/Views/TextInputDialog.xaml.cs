using System.Windows;
using System.Windows.Input;

namespace PhoneDrive.Wpf.Views;

public partial class TextInputDialog : Window
{
    private TextInputDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initial;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    public string Value => InputBox.Text;

    public static string? Show(string title, string prompt, string initial = "", Window? owner = null)
    {
        var dialog = new TextInputDialog(title, prompt, initial)
        {
            Owner = owner ?? Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DialogResult = true;
        if (e.Key == Key.Escape) DialogResult = false;
    }
}
