using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using FUPlayer.App.Services;

namespace FUPlayer.App.Controls;

/// <summary>A native child container for the separately sandboxed Firefox window.</summary>
public sealed class FirefoxHost : NativeControlHost
{
    public static readonly StyledProperty<FirefoxSession?> SessionProperty =
        AvaloniaProperty.Register<FirefoxHost, FirefoxSession?>(nameof(Session));
    private nint _handle;

    public FirefoxSession? Session { get => GetValue(SessionProperty); set => SetValue(SessionProperty, value); }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        IPlatformHandle control = base.CreateNativeControlCore(parent);
        _handle = control.Handle;
        Session?.SetHost(_handle);
        return control;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Session?.SetHost(0);
        _handle = 0;
        base.DestroyNativeControlCore(control);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SessionProperty)
        {
            change.GetOldValue<FirefoxSession?>()?.SetHost(0);
            Session?.SetHost(_handle);
        }
    }
}
