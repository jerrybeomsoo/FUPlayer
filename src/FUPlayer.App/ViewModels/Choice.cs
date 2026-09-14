namespace FUPlayer.App.ViewModels;

/// <summary>An option shown in combo boxes and segmented selectors.</summary>
public sealed record Choice(string Label, object Value, string? Description = null)
{
    public static Choice? Find(IEnumerable<Choice> choices, object? value) =>
        choices.FirstOrDefault(c => Equals(c.Value, value));

    public override string ToString() => Label;
}
