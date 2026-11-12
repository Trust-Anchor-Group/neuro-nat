using Avalonia.Data.Converters;

namespace Neuro.Nat.App.Converters;

/// <summary>Small boolean-to-text converters used by the shell.</summary>
public static class BoolToTextConverter
{
	/// <summary>"Close" when true, "Add account" when false.</summary>
	public static readonly IValueConverter CloseOrAdd =
		new FuncValueConverter<bool, string>(open => open ? "Close" : "Add account");

	/// <summary>Right alignment for true (my messages), left for false.</summary>
	public static readonly IValueConverter RightIfTrue =
		new FuncValueConverter<bool, Avalonia.Layout.HorizontalAlignment>(right => right ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left);

	/// <summary>" · edited" for corrected messages.</summary>
	public static readonly IValueConverter Edited =
		new FuncValueConverter<bool, string>(edited => edited ? " · edited" : string.Empty);
}

/// <summary>Boolean-to-spacing converters.</summary>
public static class BoolToThicknessConverter
{
	/// <summary>More space above a message that starts a new group (new sender, or a pause).</summary>
	public static readonly IValueConverter GroupGap =
		new FuncValueConverter<bool, Avalonia.Thickness>(starts => starts ? new Avalonia.Thickness(0, 10, 0, 0) : new Avalonia.Thickness(0, 2, 0, 0));
}
