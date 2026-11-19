using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Neuro.Nat.App.ViewModels;

namespace Neuro.Nat.App.Controls;

public partial class QueryTableView : UserControl
{
	private const int PageSize = 12;
	private QueryResult? Result => (this.DataContext as ScriptReply)?.Table;
	private ScriptValue? ListValue => (this.DataContext as ScriptReply)?.List;
	private Control? selectedDetails;
	private int page;
	private int selected = -1;
	private int previewCount = 3;

	public QueryTableView()
	{
		this.InitializeComponent();
		this.SizeChanged += (_, e) =>
		{
			int count = e.NewSize.Width switch { < 340 => 1, < 560 => 2, < 840 => 3, _ => 4 };
			if (this.ListValue is null && this.previewCount != count)
			{
				this.previewCount = count;
				this.ShowPage();
			}
		};
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		this.page = 0;
		this.selected = -1;
		this.selectedDetails = null;
		this.Original.Text = null;
		this.Original.IsVisible = false;
		this.Records.IsVisible = true;
		this.ToggleOriginal.Content = "Show original";
		this.Summary.Text = this.Result is { } result ? Ui.Count(result.Rows.Count, "row") + "  /  " + Ui.Count(result.Columns.Count, "field") : this.ListValue?.Summary;
		this.CopyResult.Content = this.ListValue is null ? "Copy as CSV" : "Copy list";
		this.CopyResult.IsEnabled = this.Result?.Columns.Count > 0 || this.ListValue is not null;
		this.ShowPage();
	}

	private void ShowPage()
	{
		this.Records.Children.Clear();
		int count = this.ListValue?.Items?.Count ?? this.Result?.Rows.Count ?? 0;
		int start = this.page * PageSize;
		int end = Math.Min(start + PageSize, count);
		this.Pager.IsVisible = !this.Original.IsVisible && count > PageSize;
		this.PageLabel.Text = $"{start + 1}–{end} of {count}";
		this.Previous.IsEnabled = this.page > 0;
		this.Next.IsEnabled = end < count;

		if (this.ListValue?.Items is { } items)
		{
			for (int i = start; i < end; i++)
				this.Records.Children.Add(ScriptValueTree.Field($"[{i}]", items[i]));
			return;
		}
		if (this.Result is not { } result || result.Rows.Count == 0 && result.Columns.Count == 0)
			return;

		string[] columns = result.PreviewColumns(this.previewCount);
		Grid header = PreviewGrid(columns.Length);
		header.Margin = new Thickness(8, 3);
		Put(header, new TextBlock { Text = "#", Classes = { "meta" } }, 0);
		for (int i = 0; i < columns.Length; i++)
			Put(header, new TextBlock { Text = columns[i], Classes = { "meta" }, TextTrimming = TextTrimming.CharacterEllipsis }, i + 1);
		this.Records.Children.Add(header);

		for (int i = start; i < end; i++)
		{
			int index = i;
			var record = result.Rows[index];
			bool open = this.selected == index;
			Grid preview = PreviewGrid(columns.Length);
			Put(preview, new TextBlock { Text = (index + 1).ToString(), Classes = { "meta" } }, 0);
			for (int j = 0; j < columns.Length; j++)
			{
				record.TryGetValue(columns[j], out ScriptValue? value);
				TextBlock cell = new() { Text = value is null ? "—" : value.Summary ?? Export.OneLine(value.Text), TextTrimming = TextTrimming.CharacterEllipsis };
				cell.ContextMenu = CopyMenu(result, index, value);
				Put(preview, cell, j + 1);
			}
			Put(preview, new TextBlock { Text = open ? "▾" : "›", Classes = { "meta" } }, columns.Length + 1);
			Button row = new() { Content = preview, Classes = { "ghost", "record" } };
			row.Classes.Set("open", open);
			AutomationProperties.SetName(row, (open ? "Close" : "Open") + $" row {index + 1}");
			row.Click += (_, _) =>
			{
				this.selected = open ? -1 : index;
				this.selectedDetails = null;
				this.ShowPage();
				if (this.Records.Children.OfType<Button>().ElementAtOrDefault(index - start) is { } replacement)
					replacement.Focus();
			};
			this.Records.Children.Add(row);
			if (open)
				this.Records.Children.Add(this.selectedDetails ??= Details(result, index));
		}
	}

	private static Grid PreviewGrid(int fields)
	{
		Grid grid = new() { ColumnSpacing = 8 };
		grid.ColumnDefinitions.Add(new ColumnDefinition(40, GridUnitType.Pixel));
		for (int j = 0; j < fields; j++)
			grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
		grid.ColumnDefinitions.Add(new ColumnDefinition(14, GridUnitType.Pixel));
		return grid;
	}

	private static void Put(Grid grid, Control control, int column)
	{
		Grid.SetColumn(control, column);
		grid.Children.Add(control);
	}

	private static Control Details(QueryResult result, int index)
	{
		StackPanel fields = new() { Spacing = 5 };
		DockPanel toolbar = new();
		Button copyRow = new() { Content = "Copy row", Classes = { "ghost", "small" } };
		copyRow.Click += async (_, _) => await Ui.Copy(result.CopyRow(index), "the row", allowEmpty: true);
		DockPanel.SetDock(copyRow, Dock.Right);
		toolbar.Children.Add(copyRow);
		toolbar.Children.Add(new TextBlock { Text = $"Row {index + 1}", Classes = { "section" }, VerticalAlignment = VerticalAlignment.Center });
		fields.Children.Add(toolbar);
		foreach (var (name, value) in result.Rows[index])
			fields.Children.Add(ScriptValueTree.Field(name, value));
		return new Border { Classes = { "fields" }, Child = fields };
	}

	private static ContextMenu CopyMenu(QueryResult result, int index, ScriptValue? value)
	{
		MenuItem cell = new() { Header = "Copy cell", IsEnabled = value is not null };
		cell.Click += async (_, _) => await Ui.Copy(value?.Text, "the cell", allowEmpty: true);
		MenuItem row = new() { Header = "Copy row" };
		row.Click += async (_, _) => await Ui.Copy(result.CopyRow(index), "the row", allowEmpty: true);
		return new ContextMenu { Items = { cell, row } };
	}

	private void ToggleOriginalClick(object? sender, RoutedEventArgs e)
	{
		bool original = !this.Original.IsVisible;
		this.Original.IsVisible = original;
		this.Original.Text = original ? this.Result?.Original ?? this.ListValue?.Text : null;
		this.Records.IsVisible = !original;
		this.ToggleOriginal.Content = original ? (this.ListValue is null ? "Show table" : "Show list") : "Show original";
		this.Pager.IsVisible = !original && (this.ListValue?.Items?.Count ?? this.Result?.Rows.Count) > PageSize;
	}

	private async void CopyResultClick(object? sender, RoutedEventArgs e)
	{
		if (this.ListValue is { } list)
			await Ui.Copy(list.Text, "the list");
		else
			await Ui.Copy(this.Result?.Csv(), "the table as CSV");
	}
	private void PreviousClick(object? sender, RoutedEventArgs e) => this.ChangePage(-1);
	private void NextClick(object? sender, RoutedEventArgs e) => this.ChangePage(1);

	private void ChangePage(int step)
	{
		this.page += step;
		this.selected = -1;
		this.selectedDetails = null;
		this.ShowPage();
	}
}
