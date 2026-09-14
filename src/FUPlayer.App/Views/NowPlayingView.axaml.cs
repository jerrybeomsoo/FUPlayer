using Avalonia.Controls;
using FUPlayer.App.Controls;
using FUPlayer.App.ViewModels;

namespace FUPlayer.App.Views;

public partial class NowPlayingView : UserControl
{
    private AnalyzerViewModel? _analyzer;

    public NowPlayingView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_analyzer is not null)
        {
            _analyzer.FrameReady -= OnFrameReady;
            _analyzer.Cleared -= OnCleared;
        }

        _analyzer = (DataContext as NowPlayingViewModel)?.Analyzer;
        if (_analyzer is not null)
        {
            _analyzer.FrameReady += OnFrameReady;
            _analyzer.Cleared += OnCleared;
        }
    }

    private void OnFrameReady(AnalyzerFrame frame)
    {
        if (_analyzer is null)
        {
            return;
        }

        if (_analyzer.IsSpectrogram)
        {
            Spectrogram.Push(frame);
        }
        else if (_analyzer.IsWaterfall)
        {
            Waterfall.Push(frame);
        }
        else
        {
            Spectrum.Update(frame);
        }
    }

    private void OnCleared()
    {
        Spectrum.Clear();
        Spectrogram.Clear();
        Waterfall.Clear();
    }
}
