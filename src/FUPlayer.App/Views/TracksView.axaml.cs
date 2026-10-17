using Avalonia.Controls;
using Avalonia.Input;
using FUPlayer.App.ViewModels;

namespace FUPlayer.App.Views;

/// <summary>Double-click or Enter plays the selected track by itself.</summary>
public partial class TracksView : UserControl
{
    public TracksView()
    {
        InitializeComponent();
        List.DoubleTapped += (_, _) => PlaySelected();
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                PlaySelected();
                e.Handled = true;
            }
        };
    }

    private void PlaySelected()
    {
        if (DataContext is TracksViewModel { SelectedRow: { } row } tracks)
        {
            tracks.PlayOnly(row);
        }
    }
}
