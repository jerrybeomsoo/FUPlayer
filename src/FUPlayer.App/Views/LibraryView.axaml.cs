using Avalonia.Controls;
using FUPlayer.App.ViewModels;

namespace FUPlayer.App.Views;

public partial class LibraryView : UserControl
{
    private const double TileWidth = 180;
    private const double TileSpacing = 20;

    public LibraryView()
    {
        InitializeComponent();
        AlbumScroller.SizeChanged += (_, e) => UpdateColumns(e.NewSize.Width);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        UpdateColumns(AlbumScroller.Bounds.Width);
    }

    private void UpdateColumns(double width)
    {
        if (DataContext is LibraryViewModel library && width > 0)
        {
            library.Columns = Math.Max(1, (int)((width - 12 + TileSpacing) / (TileWidth + TileSpacing)));
        }
    }
}
