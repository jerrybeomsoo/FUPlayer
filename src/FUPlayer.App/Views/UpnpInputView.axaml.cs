using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;

namespace FUPlayer.App.Views;

public partial class UpnpInputView : UserControl
{
    public UpnpInputView()
    {
        InitializeComponent();

        // The name takes effect when the box is left, since each change restarts the renderer; Enter leaves it too.
        NameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                BindingOperations.GetBindingExpressionBase(NameBox, TextBox.TextProperty)?.UpdateSource();
                e.Handled = true;
            }
        };
    }
}
