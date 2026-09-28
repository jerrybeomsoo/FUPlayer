using FUPlayer.Core.Localization;

namespace FUPlayer.App.ViewModels;

/// <summary>An option shown in combo boxes and segmented selectors, its label and description in the interface's language.</summary>
public sealed record Choice(string Label, object Value, string? Description = null)
{
    public string Label { get; init; } = Loc.T(Label);

    public string? Description { get; init; } = Description is null ? null : Loc.T(Description);

    public static Choice? Find(IEnumerable<Choice> choices, object? value) =>
        choices.FirstOrDefault(c => Equals(c.Value, value));

    public override string ToString() => Label;
}
