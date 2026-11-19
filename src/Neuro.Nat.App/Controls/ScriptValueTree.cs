using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Neuro.Nat.App.ViewModels;

namespace Neuro.Nat.App.Controls;

/// <summary>A field or list item. Child controls are created when the operator opens the branch.</summary>
internal static class ScriptValueTree
{
	private const int BatchSize = 24;

	public static Grid Field(string name, ScriptValue value, int depth = 0)
	{
		Grid field = new() { ColumnDefinitions = new("2*,4*,Auto"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 8 };
		field.Children.Add(new SelectableTextBlock { Text = name, Classes = { "meta" }, TextWrapping = TextWrapping.Wrap });

		Control display;
		if (value.Summary is string summary)
		{
			if (value.Fields?.Count > 0 || value.Items?.Count > 0)
			{
				ToggleButton toggle = new() { Content = "▸  " + summary, Classes = { "ghost", "small" }, HorizontalAlignment = HorizontalAlignment.Left };
				AutomationProperties.SetName(toggle, name + ", " + summary);
				Border? children = null;
				toggle.IsCheckedChanged += (_, _) =>
				{
					bool open = toggle.IsChecked == true;
					if (open && children is null)
					{
						children = Children(value, depth);
						Grid.SetRow(children, 1);
						Grid.SetColumnSpan(children, 3);
						field.Children.Add(children);
					}
					if (children is not null)
						children.IsVisible = open;
					toggle.Content = (open ? "▾  " : "▸  ") + summary;
				};
				display = toggle;
			}
			else
				display = new TextBlock { Text = summary, Classes = { "meta" } };
		}
		else
			display = new SelectableTextBlock { Text = value.Text, TextWrapping = TextWrapping.Wrap };
		Grid.SetColumn(display, 1);
		field.Children.Add(display);

		Button copy = new() { Content = "Copy", Classes = { "ghost", "small" }, VerticalAlignment = VerticalAlignment.Top };
		AutomationProperties.SetName(copy, "Copy cell " + name);
		copy.Click += async (_, _) => await Ui.Copy(value.Text, "the value", allowEmpty: true);
		Grid.SetColumn(copy, 2);
		field.Children.Add(copy);
		return field;
	}

	private static Border Children(ScriptValue value, int depth)
	{
		StackPanel panel = new() { Spacing = 4 };
		var members = value.Fields?.AsEnumerable() ?? value.Items!.Select((v, i) => KeyValuePair.Create($"[{i}]", v));
		var iterator = members.GetEnumerator();
		int remaining = value.Fields?.Count ?? value.Items!.Count;
		Button more = new() { Classes = { "link" } };
		void Append()
		{
			panel.Children.Remove(more);
			int count = Math.Min(BatchSize, remaining);
			for (int i = 0; i < count && iterator.MoveNext(); i++)
				panel.Children.Add(Field(iterator.Current.Key, iterator.Current.Value, depth + 1));
			remaining -= count;
			if (remaining == 0)
				iterator.Dispose();
			else
			{
				more.Content = $"Show next {Math.Min(BatchSize, remaining)} ({remaining} left)";
				panel.Children.Add(more);
			}
		}
		more.Click += (_, _) => Append();
		Append();
		// Keep room for values on a narrow screen, even several branches down.
		return new Border { Classes = { "branch" }, Margin = new Thickness(depth < 4 ? 8 : 0, 3, 0, 4),
			Padding = new Thickness(depth < 4 ? 8 : 0, 1, 0, 1), Child = panel };
	}
}
