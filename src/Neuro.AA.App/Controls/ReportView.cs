using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Neuro.AA.Engine.Concentrator;
using Waher.Networking.XMPP.Concentrator.Queries;
using Waher.Things.Queries;

namespace Neuro.AA.App.Controls;

/// <summary>
/// Shows the results of a query (a report) as they arrive: sections, tables and objects, with
/// the Neuron's messages on top. Large tables are virtualized.
/// </summary>
public sealed class ReportView : Decorator
{
	public static readonly StyledProperty<QueryRun?> RunProperty =
		AvaloniaProperty.Register<ReportView, QueryRun?>(nameof(Run));

	private const int VirtualizeAbove = 60;
	private readonly DispatcherTimer timer;
	private QueryRun? attached;
	private int dirty;

	public ReportView()
	{
		this.timer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
		{
			if (Interlocked.Exchange(ref this.dirty, 0) == 1)
				this.Build();
		});
	}

	public QueryRun? Run
	{
		get => this.GetValue(RunProperty);
		set => this.SetValue(RunProperty, value);
	}

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);

		if (change.Property == RunProperty)
		{
			if (this.attached is not null)
				this.attached.Changed -= this.Run_Changed;

			this.attached = this.Run;

			if (this.attached is not null)
			{
				this.attached.Changed += this.Run_Changed;
				this.timer.Start();
			}
			else
				this.timer.Stop();

			this.Build();
		}
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnDetachedFromVisualTree(e);
		this.timer.Stop();
	}

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		if (this.attached is not null)
			this.timer.Start();
	}

	private void Run_Changed(object? sender, EventArgs e) => Interlocked.Exchange(ref this.dirty, 1);

	private void Build()
	{
		QueryRun? run = this.Run;
		if (run is null)
		{
			this.Child = null;
			return;
		}

		StackPanel root = new() { Spacing = 16 };
		IReadOnlyList<QueryMessage> messages = run.Messages;

		// Messages about the report as a whole go on top; the rest go in the section they came from.
		foreach (QueryMessage m in messages.Where(m => m.Section is null))
			root.Children.Add(this.MessageControl(m));

		foreach (QueryItem item in run.Items)
		{
			if (this.ItemControl(item, 0, messages) is Control c)
				root.Children.Add(c);
		}

		if (run.Items.Count == 0 && run.State != QueryRunState.Running && run.Messages.Count == 0)
			root.Children.Add(new TextBlock { Text = "The report is empty.", Classes = { "meta" } });

		this.Child = root;
	}

	private Control? ItemControl(QueryItem item, int depth, IReadOnlyList<QueryMessage> messages)
	{
		switch (item)
		{
			case QuerySection s:
				StackPanel section = new() { Spacing = 10 };
				section.Children.Add(new TextBlock
				{
					Text = s.Header,
					FontWeight = FontWeight.SemiBold,
					FontSize = depth == 0 ? 15 : 13.5,
					TextWrapping = TextWrapping.Wrap,
				});
				foreach (QueryItem child in s.Content)
				{
					if (this.ItemControl(child, depth + 1, messages) is Control c)
						section.Children.Add(c);
				}
				foreach (QueryMessage m in messages.Where(m => ReferenceEquals(m.Section, s)))
					section.Children.Add(this.MessageControl(m));
				return section;

			case QueryTable t:
				return this.TableControl(t);

			case QueryObject o:
				return this.ObjectControl(o);

			default:
				return null;
		}
	}

	/// <summary>
	/// A message from the Neuron. Errors are what the Neuron's own report code ran into (a script
	/// failing on empty data, for example), so they are marked as coming from the Neuron.
	/// </summary>
	private Control MessageControl(QueryMessage m)
	{
		string[] lines = m.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		bool error = m.Type is QueryEventType.Error or QueryEventType.Exception;
		bool warning = m.Type == QueryEventType.Warning;

		if (!error && !warning)
			return new SelectableTextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap, Classes = { "meta" } };

		StackPanel content = new() { Spacing = 4 };
		content.Children.Add(new TextBlock
		{
			Text = error ? "The Neuron reported an error here" : "The Neuron reported a warning here",
			FontSize = 11.5,
			Foreground = this.Brush("Ink.Secondary"),
		});
		content.Children.Add(new SelectableTextBlock
		{
			Text = lines.Length > 0 ? lines[0] : m.Text,
			TextWrapping = TextWrapping.Wrap,
			Foreground = this.Brush(error ? "Severity.Error" : "Severity.Warning"),
		});
		if (lines.Length > 1)
		{
			SelectableTextBlock detail = new()
			{
				Text = string.Join('\n', lines.Skip(1)),
				TextWrapping = TextWrapping.Wrap,
				Classes = { "mono" },
				FontSize = 11.5,
				Foreground = this.Brush("Ink.Secondary"),
			};
			content.Children.Add(detail);
		}

		return new Border
		{
			Background = this.Brush(error ? "State.Bad.Subtle" : "Surface.Hover"),
			CornerRadius = new CornerRadius(6),
			Padding = new Thickness(12, 8),
			Child = content,
		};
	}

	/// <summary>
	/// An object in a report, as the Neuron sent it: charts and other images are shown as images,
	/// Markdown as formatted text, plain text as text. Anything else says what it is.
	/// </summary>
	private Control ObjectControl(QueryObject o)
	{
		string type = o.ContentType ?? string.Empty;
		byte[]? bin = o.Binary;

		if (o.Object is Waher.Content.Markdown.MarkdownDocument md)
			return new MarkdownView { Markdown = md.MarkdownText, HorizontalAlignment = HorizontalAlignment.Left };

		if (bin is not null && type.StartsWith("text/markdown", StringComparison.OrdinalIgnoreCase))
			return new MarkdownView { Markdown = System.Text.Encoding.UTF8.GetString(bin), HorizontalAlignment = HorizontalAlignment.Left };

		if (bin is not null && type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !type.Contains("svg", StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				using MemoryStream ms = new(bin);
				Avalonia.Media.Imaging.Bitmap bitmap = new(ms);
				return new Image
				{
					Source = bitmap,
					Stretch = Stretch.Uniform,
					StretchDirection = StretchDirection.DownOnly,
					MaxWidth = bitmap.Size.Width,
					HorizontalAlignment = HorizontalAlignment.Left,
				};
			}
			catch (Exception)
			{
				// Shown as a description below.
			}
		}

		if (o.Object is string text)
			return new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap };

		if (bin is not null && type.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
			return new SelectableTextBlock { Text = System.Text.Encoding.UTF8.GetString(bin), TextWrapping = TextWrapping.Wrap };

		string what = string.IsNullOrEmpty(type) ? "Content" : type;
		return new TextBlock
		{
			Text = bin is null ? what : $"{what}, {bin.Length:N0} bytes (cannot be shown here)",
			Classes = { "meta" },
		};
	}

	private Control TableControl(QueryTable t)
	{
		Column[] columns = t.TableDefinition.Columns ?? [];
		Record[] records = t.Records;
		double[] widths = new double[columns.Length];

		for (int i = 0; i < columns.Length; i++)
		{
			int chars = (columns[i].Header ?? columns[i].ColumnId).Length;
			foreach (Record r in records.Take(300))
			{
				if (i < r.Elements.Length)
					chars = Math.Max(chars, Format(r.Elements[i], columns[i]).Length);
			}
			widths[i] = Math.Clamp(chars * 7.4 + 22, 56, 440);
		}

		Control Row(object?[] cells, bool header)
		{
			Grid g = new();
			for (int i = 0; i < columns.Length; i++)
			{
				g.ColumnDefinitions.Add(new ColumnDefinition(widths[i], GridUnitType.Pixel));

				string text = header ? (columns[i].Header ?? columns[i].ColumnId) : (i < cells.Length ? Format(cells[i], columns[i]) : string.Empty);
				TextBlock tb = new()
				{
					Text = text,
					TextTrimming = TextTrimming.CharacterEllipsis,
					Margin = new Thickness(i == 0 ? 0 : 6, 0, 6, 0),
					VerticalAlignment = VerticalAlignment.Center,
					TextAlignment = columns[i].Alignment switch
					{
						ColumnAlignment.Right => TextAlignment.Right,
						ColumnAlignment.Center => TextAlignment.Center,
						_ => TextAlignment.Left,
					},
				};

				if (header)
				{
					tb.FontWeight = FontWeight.SemiBold;
					tb.Foreground = this.Brush("Ink.Secondary");
				}
				else
					ToolTip.SetTip(tb, text.Length > 40 ? text : null);

				Grid.SetColumn(tb, i);
				g.Children.Add(tb);
			}

			return new Border
			{
				Height = header ? 30 : 26,
				BorderBrush = this.Brush("Line"),
				BorderThickness = new Thickness(0, 0, 0, 1),
				Child = g,
			};
		}

		StackPanel table = new() { Margin = new Thickness(0, 0, 0, 12) };
		// The table's name, unless it only repeats the heading of its section.
		if (!string.IsNullOrWhiteSpace(t.TableDefinition.Name) && !(t.Parent is QuerySection ps && ps.Header == t.TableDefinition.Name))
			table.Children.Add(new TextBlock { Text = t.TableDefinition.Name, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) });

		table.Children.Add(Row([], true));

		if (records.Length <= VirtualizeAbove)
		{
			foreach (Record r in records)
				table.Children.Add(Row(r.Elements, false));
		}
		else
		{
			ListBox list = new()
			{
				ItemsSource = records,
				Height = 440,
				Classes = { "stream" },
				ItemTemplate = new FuncDataTemplate<Record>((r, _) => r is null ? new Panel() : Row(r.Elements, false)),
				ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
			};
			table.Children.Add(list);
		}

		table.Children.Add(new TextBlock
		{
			Text = records.Length == 1 ? "1 row" : $"{records.Length:N0} rows" + (t.IsDone ? string.Empty : ", more coming…"),
			Classes = { "meta" },
			Margin = new Thickness(0, 6, 0, 0),
		});

		// Wide tables scroll sideways on their own, so the page does not.
		return new ScrollViewer
		{
			HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
			VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
			Content = table,
		};
	}

	/// <summary>Every table of a report as CSV, one after the other, each under its section's name.</summary>
	public static string ToCsv(QueryRun run)
	{
		System.Text.StringBuilder sb = new();

		void Walk(QueryItem item, string? heading)
		{
			switch (item)
			{
				case QuerySection s:
					foreach (QueryItem child in s.Content)
						Walk(child, s.Header);
					break;

				case QueryTable t:
					Column[] columns = t.TableDefinition.Columns ?? [];
					if (sb.Length > 0)
						sb.AppendLine();
					string? name = string.IsNullOrWhiteSpace(t.TableDefinition.Name) ? heading : t.TableDefinition.Name;
					if (!string.IsNullOrWhiteSpace(name))
						sb.AppendLine(name);
					sb.Append(ViewModels.Export.Csv([.. columns.Select(c => c.Header ?? c.ColumnId)],
						t.Records.Select(r => (IReadOnlyList<string?>)[.. columns.Select((c, i) => i < r.Elements.Length ? Format(r.Elements[i], c) : string.Empty)])));
					break;
			}
		}

		foreach (QueryItem item in run.Items)
			Walk(item, null);

		return sb.ToString();
	}

	private static string Format(object? v, Column c) => v switch
	{
		null => string.Empty,
		DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture) : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
		double x when c.NrDecimals is byte n => x.ToString("N" + n, CultureInfo.CurrentCulture),
		decimal x when c.NrDecimals is byte n => x.ToString("N" + n, CultureInfo.CurrentCulture),
		float x when c.NrDecimals is byte n => x.ToString("N" + n, CultureInfo.CurrentCulture),
		bool b => b ? "Yes" : "No",
		IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
		_ => v.ToString() ?? string.Empty,
	};

	private IBrush? Brush(string key)
		=> this.TryFindResource(key, this.ActualThemeVariant, out object? v) ? v as IBrush : null;
}
