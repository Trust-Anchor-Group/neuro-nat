using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Waher.Networking.XMPP.DataForms;
using Waher.Networking.XMPP.DataForms.FieldTypes;
using Waher.Networking.XMPP.DataForms.Layout;
using Waher.Networking.XMPP.DataForms.ValidationMethods;
using Page = Waher.Networking.XMPP.DataForms.Layout.Page;

namespace Neuro.Nat.App.Controls;

/// <summary>
/// Shows an XEP-0004 data form, the forms a Neuron uses to edit nodes, create them and take
/// command parameters. Values go into the form as they are typed, so it can be submitted at any
/// time. Layout pages (XEP-0141) become tabs, validation (XEP-0122) is checked as fields are
/// left, and fields that ask for it are posted back (XEP-0336) so the Neuron can update the form.
/// </summary>
public sealed class DataFormView : Decorator
{
	public static readonly StyledProperty<DataForm?> FormProperty =
		AvaloniaProperty.Register<DataFormView, DataForm?>(nameof(Form));

	private DataForm? attached;

	public DataForm? Form
	{
		get => this.GetValue(FormProperty);
		set => this.SetValue(FormProperty, value);
	}

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);

		if (change.Property == FormProperty)
		{
			if (this.attached is not null)
				this.attached.OnRemoteUpdate -= this.Form_OnRemoteUpdate;

			this.attached = this.Form;

			if (this.attached is not null)
				this.attached.OnRemoteUpdate += this.Form_OnRemoteUpdate;

			this.Build();
		}
	}

	private Task Form_OnRemoteUpdate(object? sender, DataForm e)
	{
		Dispatcher.UIThread.Post(this.Build);
		return Task.CompletedTask;
	}

	private void Build()
	{
		DataForm? form = this.Form;
		if (form is null)
		{
			this.Child = null;
			return;
		}

		StackPanel root = new() { Spacing = 14 };

		foreach (string line in form.Instructions ?? [])
		{
			if (!string.IsNullOrWhiteSpace(line))
				root.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Classes = { "meta" } });
		}

		Page[] pages = form.Pages ?? [];
		if (pages.Length > 1)
		{
			TabControl tabs = new() { Padding = new Thickness(0, 10, 0, 0) };
			foreach (Page p in pages)
				tabs.Items.Add(new TabItem { Header = string.IsNullOrWhiteSpace(p.Label) ? "Page" : p.Label, Content = this.Elements(form, p.Elements) });
			root.Children.Add(tabs);
		}
		else if (pages.Length == 1)
			root.Children.Add(this.Elements(form, pages[0].Elements));
		else
			root.Children.Add(this.Elements(form, [.. form.Fields.Select(f => (LayoutElement)new FieldReference(form, f.Var))]));

		// Fields fill the width up to a readable measure, aligned left; the rest stays empty.
		Grid frame = new();
		frame.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1000, GridUnitType.Star)) { MaxWidth = 620 });
		frame.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
		frame.Children.Add(root);
		this.Child = frame;
	}

	private Control Elements(DataForm form, IEnumerable<LayoutElement>? elements)
	{
		StackPanel panel = new() { Spacing = 14 };

		foreach (LayoutElement e in elements ?? [])
		{
			switch (e)
			{
				case FieldReference r when form[r.Var] is Field f:
					if (this.FieldControl(f) is Control c)
						panel.Children.Add(c);
					break;

				case TextElement t when !string.IsNullOrWhiteSpace(t.Text):
					panel.Children.Add(new TextBlock { Text = t.Text, TextWrapping = TextWrapping.Wrap, Classes = { "meta" } });
					break;

				case Section s:
					StackPanel section = new() { Spacing = 10 };
					if (!string.IsNullOrWhiteSpace(s.Label))
						section.Children.Add(new TextBlock { Text = s.Label, Classes = { "section" } });
					section.Children.Add(this.Elements(form, s.Elements));
					panel.Children.Add(new Border
					{
						BorderBrush = this.Brush("Line"),
						BorderThickness = new Thickness(0, 1, 0, 0),
						Padding = new Thickness(0, 12, 0, 0),
						Child = section,
					});
					break;
			}
		}

		return panel;
	}

	private Control? FieldControl(Field f)
	{
		if (f is HiddenField)
			return null;

		string label = string.IsNullOrWhiteSpace(f.Label) ? f.Var : f.Label;

		if (f is FixedField)
		{
			return new SelectableTextBlock
			{
				Text = string.Join(Environment.NewLine, f.ValueStrings ?? []),
				TextWrapping = TextWrapping.Wrap,
			};
		}

		StackPanel box = new() { Spacing = 5 };
		TextBlock error = new()
		{
			Classes = { "error" },
			TextWrapping = TextWrapping.Wrap,
			Text = f.Error,
			IsVisible = f.HasError,
		};

		void ShowError()
		{
			error.Text = f.Error;
			error.IsVisible = f.HasError;
		}

		async Task SetAsync(params string[] values)
		{
			try
			{
				await f.SetValue(values);
			}
			catch (Exception)
			{
				// Validation shows the problem on the field.
			}
			ShowError();
		}

		Control editor;

		switch (f)
		{
			case BooleanField:
				CheckBox check = new()
				{
					Content = label,
					IsChecked = IsTrue(f.ValueString),
					IsEnabled = !f.ReadOnly,
				};
				check.IsCheckedChanged += async (_, _) => await SetAsync(check.IsChecked == true ? "true" : "false");
				box.Children.Add(check);
				if (!string.IsNullOrWhiteSpace(f.Description))
					box.Children.Add(Description(f.Description));
				box.Children.Add(error);
				return box;

			case ListSingleField:
				ComboBox combo = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !f.ReadOnly };
				List<KeyValuePair<string, string>> options = [.. f.Options ?? []];
				if (!string.IsNullOrEmpty(f.ValueString) && !options.Any(o => o.Value == f.ValueString))
					options.Insert(0, new KeyValuePair<string, string>(f.ValueString, f.ValueString));
				foreach (KeyValuePair<string, string> o in options)
					combo.Items.Add(new ComboBoxItem { Content = string.IsNullOrEmpty(o.Key) ? o.Value : o.Key, Tag = o.Value });
				combo.SelectedIndex = options.FindIndex(o => o.Value == f.ValueString);
				combo.SelectionChanged += async (_, _) =>
				{
					if (combo.SelectedItem is ComboBoxItem item && item.Tag is string v && v != f.ValueString)
						await SetAsync(v);
				};
				editor = combo;

				if (f.ValidationMethod is OpenValidation && !f.ReadOnly)
				{
					// Open lists also take a value of one's own.
					TextBox own = new() { Text = f.ValueString, PlaceholderText = "Or type a value", MinWidth = 260 };
					own.LostFocus += async (_, _) =>
					{
						if (own.Text is string t && t != f.ValueString)
							await SetAsync(t);
					};
					editor = new StackPanel { Spacing = 6, Children = { combo, own } };
				}
				break;

			case ListMultiField:
				StackPanel checks = new() { Spacing = 4 };
				HashSet<string> selected = [.. f.ValueStrings ?? []];
				foreach (KeyValuePair<string, string> o in f.Options ?? [])
				{
					CheckBox cb = new()
					{
						Content = string.IsNullOrEmpty(o.Key) ? o.Value : o.Key,
						IsChecked = selected.Contains(o.Value),
						IsEnabled = !f.ReadOnly,
					};
					cb.IsCheckedChanged += async (_, _) =>
					{
						if (cb.IsChecked == true)
							selected.Add(o.Value);
						else
							selected.Remove(o.Value);
						await SetAsync([.. (f.Options ?? []).Select(x => x.Value).Where(selected.Contains)]);
					};
					checks.Children.Add(cb);
				}
				editor = checks;
				break;

			case TextMultiField or JidMultiField:
				TextBox multi = new()
				{
					Text = string.Join(Environment.NewLine, f.ValueStrings ?? []),
					AcceptsReturn = true,
					TextWrapping = TextWrapping.Wrap,
					MinHeight = 90,
					MaxHeight = 260,
					IsReadOnly = f.ReadOnly,
					FontFamily = this.Font("Font.Mono"),
				};
				multi.TextChanged += async (_, _) =>
				{
					if (!f.PostBack)
						await SetAsync(Lines(multi.Text));
				};
				multi.LostFocus += async (_, _) =>
				{
					if (f.PostBack)
						await SetAsync(Lines(multi.Text));
					ShowError();
				};
				editor = multi;
				break;

			default:
				TextBox text = new()
				{
					Text = f.ValueString,
					IsReadOnly = f.ReadOnly,
					MinWidth = 260,
					HorizontalAlignment = HorizontalAlignment.Stretch,
				};
				if (f is TextPrivateField)
					text.PasswordChar = '•';
				if (f.NotSame)
					text.PlaceholderText = "Several values";

				text.TextChanged += async (_, _) =>
				{
					if (!f.PostBack)
					{
						try
						{
							await f.SetValue(text.Text ?? string.Empty);
						}
						catch (Exception)
						{
						}

						// Clear a remark as soon as the value is fixed; new remarks wait for the field to be left.
						if (!f.HasError)
							ShowError();
					}
				};
				text.LostFocus += async (_, _) =>
				{
					if (f.PostBack)
						await SetAsync(text.Text ?? string.Empty);
					ShowError();
				};
				editor = text;
				break;
		}

		TextBlock head = new() { Text = label + (f.Required ? " *" : string.Empty), FontWeight = FontWeight.SemiBold };
		if (f.Required)
			ToolTip.SetTip(head, "Required");

		box.Children.Add(head);
		if (!string.IsNullOrWhiteSpace(f.Description))
			box.Children.Add(Description(f.Description));
		box.Children.Add(editor);
		box.Children.Add(error);
		return box;
	}

	private static TextBlock Description(string text) => new()
	{
		Text = text,
		TextWrapping = TextWrapping.Wrap,
		Classes = { "meta" },
	};

	private static bool IsTrue(string? s) => s is not null && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1");

	private static string[] Lines(string? s)
		=> string.IsNullOrEmpty(s) ? [] : s.Replace("\r\n", "\n").Split('\n');

	private IBrush? Brush(string key)
		=> this.TryFindResource(key, this.ActualThemeVariant, out object? v) ? v as IBrush : null;

	private FontFamily Font(string key)
		=> this.TryFindResource(key, out object? v) && v is FontFamily f ? f : FontFamily.Default;
}
