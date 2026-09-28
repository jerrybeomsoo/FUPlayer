using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using FUPlayer.App.Controls;
using FUPlayer.Core.Localization;

namespace FUPlayer.App.Services;

/// <summary>
/// Translates the text written into the views, as each control is loaded: a TextBlock's text and its runs, a
/// setting row's title and description, a button's caption, a switch's on and off captions, a tooltip, a text box's
/// placeholder, a menu's header.
/// Only text written as it is gets translated; text bound to a view model is the view model's to translate, which
/// is also what keeps a song called "Library" from being shown as the page of that name.
/// </summary>
internal static class LiteralTranslator
{
    /// <summary>Starts translating every control as it loads. Call once, before the first window is built.</summary>
    public static void Install()
    {
        if (Loc.Language == "en")
        {
            return;
        }

        Control.LoadedEvent.AddClassHandler<Control>((control, _) => Translate(control), RoutingStrategies.Direct, handledEventsToo: true);
    }

    private static void Translate(Control control)
    {
        switch (control)
        {
            case TextBlock text:
                Literal(text, TextBlock.TextProperty);
                if (text.Inlines is { } inlines)
                {
                    foreach (Inline inline in inlines)
                    {
                        if (inline is Run run)
                        {
                            Literal(run, Run.TextProperty);
                        }
                    }
                }

                break;

            case SettingRow row:
                Literal(row, SettingRow.TitleProperty);
                Literal(row, SettingRow.DescriptionProperty);
                break;

            case TextBox box:
                Literal(box, TextBox.PlaceholderTextProperty);
                break;

            case MenuItem item:
                Literal(item, HeaderedSelectingItemsControl.HeaderProperty);
                break;

            case ToggleSwitch toggle:
                Literal(toggle, ToggleSwitch.OnContentProperty);
                Literal(toggle, ToggleSwitch.OffContentProperty);
                break;
        }

        if (control is ContentControl { Content: string })
        {
            Literal(control, ContentControl.ContentProperty);
        }

        if (control is HeaderedContentControl { Header: string })
        {
            Literal(control, HeaderedContentControl.HeaderProperty);
        }

        if (ToolTip.GetTip(control) is string)
        {
            Literal(control, ToolTip.TipProperty);
        }
    }

    private static void Literal(AvaloniaObject target, AvaloniaProperty property)
    {
        if (target.GetValue(property) is not string { Length: > 0 } text
            || BindingOperations.GetBindingExpressionBase(target, property) is not null)
        {
            return;
        }

        string translated = Loc.T(text);
        if (!ReferenceEquals(translated, text) && translated != text)
        {
            target.SetValue(property, translated);
        }
    }
}
