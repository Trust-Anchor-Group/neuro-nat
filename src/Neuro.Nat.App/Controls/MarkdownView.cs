using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Waher.Content.Markdown;
using Waher.Content.Markdown.Model;
using Waher.Content.Markdown.Model.BlockElements;
using Waher.Content.Markdown.Model.SpanElements;
using Waher.Content.Markdown.Rendering;
using MdAlignment = Waher.Content.Markdown.Model.TextAlignment;
using MdLineBreak = Waher.Content.Markdown.Model.SpanElements.LineBreak;
using MdUnderline = Waher.Content.Markdown.Model.SpanElements.Underline;

namespace Neuro.Nat.App.Controls;

/// <summary>
/// Shows Markdown as native controls. Parsing uses the Waher Markdown engine, the same one the
/// Neuron writes its messages with, so tables, code and links come out as the Neuron meant them.
/// Covers what Neurons and chat use: headings, paragraphs, emphasis, code, links, lists, quotes
/// and tables. Anything else is shown as its plain text.
/// </summary>
public sealed class MarkdownView : Decorator
{
	public static readonly StyledProperty<string?> MarkdownProperty =
		AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

	private readonly StackPanel root = new() { Spacing = 10 };
	private int version;

	public MarkdownView()
	{
		this.Child = this.root;

		// Colours come from the theme at render time, so render again when the theme changes.
		this.ActualThemeVariantChanged += (_, _) =>
		{
			if (TopLevel.GetTopLevel(this) is not null)
				_ = this.RenderAsync(this.Markdown);
		};
	}

	public string? Markdown
	{
		get => this.GetValue(MarkdownProperty);
		set => this.SetValue(MarkdownProperty, value);
	}

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);

		if (change.Property == MarkdownProperty)
			_ = this.RenderAsync(this.Markdown);
	}

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		_ = this.RenderAsync(this.Markdown);
	}

	private async Task RenderAsync(string? markdown)
	{
		int v = ++this.version;
		List<Control> blocks = [];

		if (!string.IsNullOrWhiteSpace(markdown))
		{
			try
			{
				MarkdownDocument doc = await MarkdownDocument.CreateAsync(markdown, new MarkdownSettings(null, false));
				foreach (MarkdownElement e in doc.Elements)
				{
					if (await this.BlockAsync(e) is Control c)
						blocks.Add(c);
				}
			}
			catch (Exception)
			{
				blocks.Clear();
				blocks.Add(Text(markdown));
			}
		}

		void Apply()
		{
			if (v != this.version)
				return;

			this.root.Children.Clear();
			this.root.Children.AddRange(blocks);
		}

		if (Dispatcher.UIThread.CheckAccess())
			Apply();
		else
			await Dispatcher.UIThread.InvokeAsync(Apply);
	}

	private async Task<Control?> BlockAsync(MarkdownElement e)
	{
		switch (e)
		{
			case Header h:
				SelectableTextBlock head = Text(null, FontWeight.SemiBold);
				head.FontSize = h.Level switch { 1 => 16, 2 => 14.5, _ => 13.5 };
				await this.InlinesAsync(h.Children, head.Inlines!);
				return head;

			case Paragraph p:
				SelectableTextBlock para = Text(null);
				await this.InlinesAsync(p.Children, para.Inlines!);
				return para.Inlines!.Count == 0 ? null : para;

			case Table t:
				return await this.TableAsync(t);

			case CodeBlock cb:
				return new Border
				{
					Classes = { "code" },
					Background = this.Brush("Surface.Code"),
					CornerRadius = new CornerRadius(6),
					Padding = new Thickness(10, 8),
					Child = new SelectableTextBlock
					{
						Text = string.Join('\n', cb.Rows ?? []),
						FontFamily = this.Font("Font.Mono"),
						FontSize = 12,
						TextWrapping = TextWrapping.Wrap,
					},
				};

			case BulletList or NumberedList:
				return await this.ListAsync(e);

			case BlockQuote q:
				StackPanel quote = new() { Spacing = 6 };
				foreach (MarkdownElement c in q.Children)
				{
					if (await this.BlockAsync(c) is Control b)
						quote.Children.Add(b);
				}
				return new Border
				{
					BorderBrush = this.Brush("Line.Strong"),
					BorderThickness = new Thickness(3, 0, 0, 0),
					Padding = new Thickness(10, 0, 0, 0),
					Child = quote,
				};

			case HorizontalRule:
				return new Border { Height = 1, Background = this.Brush("Line"), Margin = new Thickness(0, 4) };

			case InvisibleBreak or SectionSeparator or CommentBlock:
				return null;

			case HtmlBlock html:
				// HTML around text, such as the <font class="result"> chat admin wraps results in:
				// the tags are dropped and the text inside is shown.
				SelectableTextBlock inner = Text(null);
				await this.InlinesAsync(html.Children, inner.Inlines!);
				return inner.Inlines!.Count == 0 ? null : inner;

			case BlockElementSingleChild single:
				return single.Child is null ? null : await this.BlockAsync(single.Child);

			default:
				if (e.IsBlockElement && e.Children is not null)
				{
					StackPanel nested = new() { Spacing = 8 };
					foreach (MarkdownElement c in e.Children)
					{
						if (await this.BlockAsync(c) is Control b)
							nested.Children.Add(b);
					}
					return nested;
				}

				if (!e.IsBlockElement)
				{
					// Inline content at block level, for example a list item without a paragraph.
					SelectableTextBlock inline = Text(null);
					await this.InlinesAsync([e], inline.Inlines!);
					return inline;
				}

				string plain = await PlainAsync(e);
				return string.IsNullOrWhiteSpace(plain) ? null : Text(plain);
		}
	}

	private async Task<Control> TableAsync(Table t)
	{
		int columns = Math.Max(1, t.Columns);
		Grid grid = new();
		for (int i = 0; i < columns; i++)
			grid.ColumnDefinitions.Add(new ColumnDefinition(i == columns - 1 ? GridLength.Star : GridLength.Auto));

		int row = 0;

		async Task AddRowAsync(MarkdownElement[] cells, bool header)
		{
			if (header && cells.All(c => c is null || string.IsNullOrWhiteSpace(PlainSync(c))))
				return;

			grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

			if (row > 0)
			{
				Border line = new() { Height = 1, Background = this.Brush("Line"), VerticalAlignment = VerticalAlignment.Top };
				Grid.SetRow(line, row);
				Grid.SetColumnSpan(line, columns);
				grid.Children.Add(line);
			}

			for (int i = 0; i < cells.Length && i < columns; i++)
			{
				if (cells[i] is not MarkdownElement cell)
					continue;

				int span = 1;
				while (i + span < cells.Length && i + span < columns && cells[i + span] is null)
					span++;

				SelectableTextBlock tb = Text(null, header ? FontWeight.SemiBold : FontWeight.Normal);
				tb.Margin = new Thickness(i == 0 ? 0 : 16, 6, 0, 6);
				if (header)
					tb.Foreground = this.Brush("Ink.Secondary");
				else if (i == 0 && columns == 2)
					tb.Foreground = this.Brush("Ink.Secondary");

				MdAlignment a = i < (t.ColumnAlignments?.Length ?? 0) ? t.ColumnAlignments![i] : MdAlignment.Left;
				tb.TextAlignment = a switch
				{
					MdAlignment.Right => Avalonia.Media.TextAlignment.Right,
					MdAlignment.Center => Avalonia.Media.TextAlignment.Center,
					_ => Avalonia.Media.TextAlignment.Left,
				};

				await this.InlinesAsync([cell], tb.Inlines!);
				Grid.SetRow(tb, row);
				Grid.SetColumn(tb, i);
				Grid.SetColumnSpan(tb, span);
				grid.Children.Add(tb);
			}

			row++;
		}

		foreach (MarkdownElement[] h in t.Headers ?? [])
			await AddRowAsync(h, true);

		foreach (MarkdownElement[] r in t.Rows ?? [])
			await AddRowAsync(r, false);

		return grid;
	}

	private async Task<Control> ListAsync(MarkdownElement list)
	{
		StackPanel panel = new() { Spacing = 4 };
		int n = 0;

		foreach (MarkdownElement item in list.Children ?? [])
		{
			n++;
			string marker = item is NumberedItem ni ? ni.Number.ToString(System.Globalization.CultureInfo.CurrentCulture) + "." : "•";
			MarkdownElement? body = item is BlockElementSingleChild s ? s.Child : item;

			Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
			TextBlock bullet = new() { Text = marker, Margin = new Thickness(2, 0, 8, 0), Foreground = this.Brush("Ink.Secondary") };
			row.Children.Add(bullet);

			if (body is not null && await this.BlockAsync(body) is Control c)
			{
				Grid.SetColumn(c, 1);
				row.Children.Add(c);
			}

			panel.Children.Add(row);
		}

		return panel;
	}

	private async Task InlinesAsync(IEnumerable<MarkdownElement> elements, InlineCollection target)
	{
		foreach (MarkdownElement e in elements)
		{
			switch (e)
			{
				case InlineText t:
					target.Add(new Run(t.Value));
					break;

				case Strong s:
					Span bold = new() { FontWeight = FontWeight.SemiBold };
					await this.InlinesAsync(s.Children, bold.Inlines);
					target.Add(bold);
					break;

				case Emphasize em:
					Span italic = new() { FontStyle = FontStyle.Italic };
					await this.InlinesAsync(em.Children, italic.Inlines);
					target.Add(italic);
					break;

				case MdUnderline or Insert:
					Span under = new() { TextDecorations = TextDecorations.Underline };
					await this.InlinesAsync(e.Children ?? [], under.Inlines);
					target.Add(under);
					break;

				case StrikeThrough or Delete:
					Span strike = new() { TextDecorations = TextDecorations.Strikethrough };
					await this.InlinesAsync(e.Children ?? [], strike.Inlines);
					target.Add(strike);
					break;

				case InlineCode code:
					target.Add(new Run(code.Code)
					{
						FontFamily = this.Font("Font.Mono"),
						FontSize = 12,
						Background = this.Brush("Surface.Code"),
					});
					break;

				case Link link:
					target.Add(this.LinkInline(await PlainAsync(link), link.Url));
					break;

				case Multimedia media when media.Items is { Length: > 0 } items:
					MultimediaItem item = items[0];
					string alt = await PlainAsync(media);
					if ((item.ContentType ?? string.Empty).StartsWith("image/", StringComparison.Ordinal) &&
						!item.ContentType!.Contains("svg", StringComparison.Ordinal))
					{
						target.Add(new InlineUIContainer(this.ImageControl(item.Url, alt)));
					}
					else
						target.Add(this.LinkInline(string.IsNullOrWhiteSpace(alt) ? item.Url : alt, item.Url));
					break;

				case AutomaticLinkUrl url:
					target.Add(this.LinkInline(url.URL, url.URL));
					break;

				case AutomaticLinkMail mail:
					target.Add(this.LinkInline(mail.EMail, "mailto:" + mail.EMail));
					break;

				case MdLineBreak:
					target.Add(new Avalonia.Controls.Documents.LineBreak());
					break;

				case HtmlEntity entity:
					target.Add(new Run(WebUtility.HtmlDecode("&" + entity.Entity + ";")));
					break;

				case HtmlEntityUnicode unicode:
					target.Add(new Run(char.ConvertFromUtf32(unicode.Code)));
					break;

				case InlineHTML:
					// Tags such as <a href> or <br>: the text between them is rendered on its own.
					break;

				case EmojiReference emoji:
					target.Add(new Run(emoji.Emoji?.Unicode ?? await PlainAsync(e)));
					break;

				default:
					if (e.Children is not null)
					{
						Span span = new();
						await this.InlinesAsync(e.Children, span.Inlines);
						target.Add(span);
					}
					else
						target.Add(new Run(await PlainAsync(e)));
					break;
			}
		}
	}

	private Inline LinkInline(string text, string? url)
	{
		if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
			uri.Scheme is not ("http" or "https" or "mailto"))
		{
			return new Run(text);
		}

		TextBlock tb = new()
		{
			Text = string.IsNullOrWhiteSpace(text) ? url : text,
			Foreground = this.Brush("Accent.Ink"),
			TextDecorations = TextDecorations.Underline,
			Cursor = new Cursor(StandardCursorType.Hand),
		};
		ToolTip.SetTip(tb, url);
		tb.Tapped += async (_, _) =>
		{
			if (TopLevel.GetTopLevel(this) is TopLevel top)
				await top.Launcher.LaunchUriAsync(uri);
		};

		return new InlineUIContainer(tb) { BaselineAlignment = Avalonia.Media.BaselineAlignment.TextBottom };
	}

	private static readonly HttpClient images = new() { Timeout = TimeSpan.FromSeconds(20) };

	/// <summary>An image from a link, loaded in the background; a click opens it full size.</summary>
	private Control ImageControl(string url, string alt)
	{
		TextBlock placeholder = new() { Text = "Loading " + (string.IsNullOrWhiteSpace(alt) ? "image" : alt) + "…", Classes = { "meta" } };
		Border frame = new()
		{
			CornerRadius = new CornerRadius(8),
			ClipToBounds = true,
			Child = placeholder,
			Cursor = new Cursor(StandardCursorType.Hand),
			Margin = new Thickness(0, 2),
		};
		ToolTip.SetTip(frame, string.IsNullOrWhiteSpace(alt) ? url : alt);

		frame.Tapped += async (_, _) =>
		{
			if (TopLevel.GetTopLevel(this) is TopLevel top && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
				await top.Launcher.LaunchUriAsync(uri);
		};

		_ = Task.Run(async () =>
		{
			try
			{
				byte[] data = await images.GetByteArrayAsync(url);
				await Dispatcher.UIThread.InvokeAsync(() =>
				{
					Avalonia.Media.Imaging.Bitmap bmp = new(new MemoryStream(data));
					frame.Child = new Image { Source = bmp, MaxWidth = 360, MaxHeight = 280, Stretch = Stretch.Uniform };
				});
			}
			catch (Exception)
			{
				await Dispatcher.UIThread.InvokeAsync(() =>
					frame.Child = new TextBlock
					{
						Text = string.IsNullOrWhiteSpace(alt) ? url : alt,
						Foreground = this.Brush("Accent.Ink"),
						TextDecorations = TextDecorations.Underline,
					});
			}
		});

		return frame;
	}

	private static SelectableTextBlock Text(string? text, FontWeight? weight = null) => new()
	{
		Text = text,
		TextWrapping = TextWrapping.Wrap,
		FontWeight = weight ?? FontWeight.Normal,
	};

	private static async Task<string> PlainAsync(MarkdownElement e)
	{
		TextRenderer r = new();
		await e.Render(r);
		return r.ToString().Trim();
	}

	private static string PlainSync(MarkdownElement e) => PlainAsync(e).GetAwaiter().GetResult();

	private IBrush? Brush(string key)
		=> this.TryFindResource(key, this.ActualThemeVariant, out object? v) ? v as IBrush : null;

	private FontFamily Font(string key)
		=> this.TryFindResource(key, out object? v) && v is FontFamily f ? f : FontFamily.Default;
}
