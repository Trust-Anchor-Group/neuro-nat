using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Neuro.Nat.App.ViewModels;

namespace Neuro.Nat.App.Views;

/// <summary>
/// Plugs the window into <see cref="Ui"/>: the clipboard, the file window for Save, and
/// windows of their own for the things that can have one.
/// </summary>
public static class UiHost
{
	private static readonly Dictionary<object, Window> open = new(ReferenceEqualityComparer.Instance);

	public static void Attach(Window main)
	{
		Ui.SetClipboard = async text =>
		{
			IClipboard? clipboard = (Active() ?? main).Clipboard;
			if (clipboard is not null)
				await clipboard.SetTextAsync(text);
		};

		Ui.SaveFile = async (name, formats) =>
		{
			IStorageProvider storage = (Active() ?? main).StorageProvider;
			IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
			{
				Title = "Save",
				SuggestedFileName = name,
				DefaultExtension = formats[0].Extension,
				ShowOverwritePrompt = true,
				FileTypeChoices = [.. formats.Select(f => new FilePickerFileType(f.Name) { Patterns = ["*." + f.Extension] })],
			});

			if (file is null)
				return null;

			// The file type picked decides the format; an unknown extension gets the first one.
			string extension = Path.GetExtension(file.Name).TrimStart('.');
			SaveFormat format = formats.FirstOrDefault(f => string.Equals(f.Extension, extension, StringComparison.OrdinalIgnoreCase)) ?? formats[0];

			await using Stream stream = await file.OpenWriteAsync();
			await using StreamWriter writer = new(stream, new System.Text.UTF8Encoding(false));
			await writer.WriteAsync(format.Content());
			return file.Name;
		};

		Ui.OpenTextFile = async kind =>
		{
			IStorageProvider storage = (Active() ?? main).StorageProvider;
			IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
			{
				Title = "Open",
				AllowMultiple = false,
				FileTypeFilter = [new FilePickerFileType(kind) { Patterns = ["*.txt"] }, FilePickerFileTypes.All],
			});

			if (files.Count == 0)
				return null;

			await using Stream stream = await files[0].OpenReadAsync();
			using StreamReader reader = new(stream);
			return (files[0].Name, await reader.ReadToEndAsync());
		};

		Ui.OpenWindow = vm => Open(main, vm);
		main.Closed += (_, _) =>
		{
			foreach (Window w in open.Values.ToList())
				w.Close();
		};
	}

	private static Window? Active()
		=> Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d ? d.Windows.FirstOrDefault(w => w.IsActive) : null;

	/// <summary>Opens a window for a view model, or brings its window forward when it has one already.</summary>
	private static void Open(Window main, object vm)
	{
		if (open.TryGetValue(vm, out Window? existing))
		{
			existing.Activate();
			return;
		}

		Window? window = vm switch
		{
			SnifferViewModel traffic => new Window
			{
				Title = "Traffic: " + traffic.Title,
				Width = 980,
				Height = 620,
				MinWidth = 520,
				MinHeight = 260,
				Icon = main.Icon,
				Content = new TrafficView { DataContext = traffic, Margin = new Thickness(14), IsInOwnWindow = true },
			},
			_ => null,
		};

		if (window is null)
			return;

		window.KeyDown += (_, e) =>
		{
			if (e.Key == Avalonia.Input.Key.F && e.KeyModifiers == Avalonia.Input.KeyModifiers.Control)
				e.Handled = FocusFind(window);
		};

		open[vm] = window;
		window.Closed += (_, _) => open.Remove(vm);
		window.Show();
	}

	/// <summary>
	/// Puts the keyboard in the find or filter box of whatever is in front: the first visible
	/// text box marked "find". Returns false when the screen has none.
	/// </summary>
	public static bool FocusFind(Visual root)
	{
		foreach (TextBox box in root.GetVisualDescendants().OfType<TextBox>())
		{
			if (box.Classes.Contains("find") && box.IsEffectivelyVisible && box.IsEffectivelyEnabled)
			{
				box.Focus();
				box.SelectAll();
				return true;
			}
		}

		return false;
	}
}
