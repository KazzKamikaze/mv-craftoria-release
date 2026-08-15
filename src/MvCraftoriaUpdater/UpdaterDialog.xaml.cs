using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MvCraftoriaUpdater;

public partial class UpdaterDialog : Window
{
    private readonly bool confirmation;

    private UpdaterDialog(
        Window owner,
        string heading,
        string message,
        string primaryText,
        bool confirmation,
        DialogTone tone)
    {
        InitializeComponent();
        Owner = owner;
        this.confirmation = confirmation;
        DataContext = CreateContent(heading, message, tone);
        PrimaryButton.Content = primaryText;
        SecondaryButton.Visibility = confirmation ? Visibility.Visible : Visibility.Collapsed;
    }

    internal static bool Confirm(Window owner, string heading, string message, string primaryText)
    {
        var dialog = new UpdaterDialog(owner, heading, message, primaryText, true, DialogTone.Confirmation);
        return dialog.ShowDialog() == true;
    }

    internal static void ShowInformation(Window owner, string heading, string message)
    {
        var tone = heading.Contains("complete", StringComparison.OrdinalIgnoreCase)
            || heading.Contains("ready", StringComparison.OrdinalIgnoreCase)
                ? DialogTone.Success
                : heading.Contains("fail", StringComparison.OrdinalIgnoreCase)
                    || heading.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    || heading.Contains("not recognized", StringComparison.OrdinalIgnoreCase)
                    ? DialogTone.Error
                    : DialogTone.Information;
        var dialog = new UpdaterDialog(owner, heading, message, "Done", false, tone);
        dialog.ShowDialog();
    }

    private static DialogContent CreateContent(string heading, string message, DialogTone tone)
    {
        var (symbol, accent, background) = tone switch
        {
            DialogTone.Success => ("✓", Color.FromRgb(87, 168, 154), Color.FromRgb(23, 40, 37)),
            DialogTone.Error => ("!", Color.FromRgb(224, 108, 117), Color.FromRgb(48, 26, 30)),
            DialogTone.Information => ("i", Color.FromRgb(109, 188, 175), Color.FromRgb(23, 40, 37)),
            _ => ("?", Color.FromRgb(242, 163, 58), Color.FromRgb(47, 37, 24))
        };
        return new DialogContent(
            heading,
            message,
            symbol,
            new SolidColorBrush(accent),
            new SolidColorBrush(background));
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = confirmation ? false : true;
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private sealed record DialogContent(
        string Heading,
        string Message,
        string Symbol,
        Brush AccentBrush,
        Brush AccentBackground);

    private enum DialogTone
    {
        Confirmation,
        Information,
        Error,
        Success
    }
}
