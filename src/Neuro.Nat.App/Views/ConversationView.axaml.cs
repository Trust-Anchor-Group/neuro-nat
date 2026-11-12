using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Neuro.Nat.App.ViewModels;

namespace Neuro.Nat.App.Views;

public partial class ConversationView : UserControl
{
	private ChatViewModel? vm;

	public ConversationView()
	{
		this.InitializeComponent();

		// Enter sends; Shift+Enter keeps the new line. Handled before the text box adds the line.
		this.Composer.AddHandler(KeyDownEvent, this.Composer_KeyDown, RoutingStrategies.Tunnel);
		this.AskInput.AddHandler(KeyDownEvent, this.AskInput_KeyDown, RoutingStrategies.Tunnel);
		this.GroupBox.AddHandler(KeyDownEvent, this.GroupBox_KeyDown, RoutingStrategies.Tunnel);
		this.NameBox.AddHandler(KeyDownEvent, this.NameBox_KeyDown, RoutingStrategies.Tunnel);

		this.AttachButton.Click += this.AttachButton_Click;
		this.OlderButton.Click += (_, _) => this.ShowOlder();
		this.MessageScroller.ScrollChanged += this.MessageScroller_ScrollChanged;
		this.MessageScroller.AddHandler(DragDrop.DropEvent, this.Messages_Drop);
		this.MessageScroller.AddHandler(DragDrop.DragOverEvent, this.Messages_DragOver);

		// A click on an emoji in the picker puts it in the box and closes the picker.
		this.EmojiButton.AddHandler(Button.ClickEvent, this.Emoji_Click);
		if (this.EmojiButton.Flyout is Flyout picker)
		{
			picker.Opened += (_, _) =>
			{
				if (picker.Content is Control content)
				{
					content.RemoveHandler(Button.ClickEvent, this.Emoji_Click);
					content.AddHandler(Button.ClickEvent, this.Emoji_Click);
				}
			};
		}


		// A conversation too narrow to share its width closes the panel beside it. Chat and the
		// Inbox both show the same conversation, so the one on screen is the one that counts.
		this.LayoutUpdated += (_, _) => this.ReportWidth();
	}

	private void ReportWidth()
	{
		if (this.IsEffectivelyVisible && this.Bounds.Width > 0)
		{
			this.vm?.SetNarrow(this.Bounds.Width < NarrowBelow);
			this.vm?.SetWide(this.Bounds.Width >= WideFrom);
		}
	}

	/// <summary>From this width there is room for a third column: a person's details open beside the conversation by themselves.</summary>
	public const double WideFrom = 1300;

	/// <summary>Below this width the conversation keeps all of it: the panel beside it only opens when asked for.</summary>
	public const double NarrowBelow = 720;

	private void Emoji_Click(object? sender, RoutedEventArgs e)
	{
		if (e.Source is Button { DataContext: EmojiChoice choice } && this.vm is not null)
		{
			e.Handled = true;
			this.EmojiButton.Flyout?.Hide();
			this.vm.InsertEmojiCommand.Execute(choice);
		}
	}

	private void FocusComposer()
	{
		Dispatcher.UIThread.Post(() =>
		{
			this.Composer.Focus();
			if (this.vm is not null)
				this.Composer.CaretIndex = Math.Clamp(this.vm.Caret, 0, this.Composer.Text?.Length ?? 0);
		}, DispatcherPriority.Background);
	}

	private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		// A question that takes text gets the keyboard, so the answer can be typed right away.
		if (e.PropertyName == nameof(ChatViewModel.Ask) && this.vm?.Ask is { HasInput: true })
		{
			Dispatcher.UIThread.Post(() =>
			{
				this.AskInput.Focus();
				this.AskInput.SelectAll();
			}, DispatcherPriority.Background);
		}
	}

	private void AskInput_KeyDown(object? sender, KeyEventArgs e)
	{
		if (this.vm is null)
			return;

		if (e.Key == Key.Enter)
		{
			e.Handled = true;
			this.vm.ConfirmAskCommand.Execute(null);
		}
		else if (e.Key == Key.Escape)
		{
			e.Handled = true;
			this.vm.CancelAskCommand.Execute(null);
		}
	}

	private void GroupBox_KeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && this.vm is not null)
		{
			e.Handled = true;
			this.vm.AddGroupCommand.Execute(null);
		}
	}

	private void NameBox_KeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && this.vm is { CanSaveName: true })
		{
			e.Handled = true;
			this.vm.SaveNameCommand.Execute(null);
		}
	}

	/// <summary>Older messages go in above the ones shown, and what you were reading stays where it was.</summary>
	private void ShowOlder()
	{
		if (this.vm is not { HasOlder: true })
			return;

		double before = this.MessageScroller.Extent.Height;
		Avalonia.Vector at = this.MessageScroller.Offset;
		this.vm.ShowOlderCommand.Execute(null);
		Dispatcher.UIThread.Post(() =>
		{
			double grown = this.MessageScroller.Extent.Height - before;
			if (grown > 0)
				this.MessageScroller.Offset = new Avalonia.Vector(at.X, at.Y + grown);
		}, DispatcherPriority.Loaded);
	}

	private void MessageScroller_ScrollChanged(object? sender, ScrollChangedEventArgs e)
	{
		// Scrolling up to the top asks for the page before.
		if (e.OffsetDelta.Y < 0 && this.MessageScroller.Offset.Y < 4 && this.vm is { HasOlder: true })
			this.ShowOlder();
	}

	private async void AttachButton_Click(object? sender, RoutedEventArgs e)
	{
		if (this.vm is null || TopLevel.GetTopLevel(this) is not TopLevel top)
			return;

		IReadOnlyList<Avalonia.Platform.Storage.IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(
			new Avalonia.Platform.Storage.FilePickerOpenOptions { AllowMultiple = true, Title = "Send files" });

		List<string> paths = [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
		if (paths.Count > 0)
			await this.vm.SendFilesAsync(paths);
	}

	private void Messages_DragOver(object? sender, DragEventArgs e)
	{
		e.DragEffects = this.vm?.CanSendFiles == true && e.DataTransfer.Contains(Avalonia.Input.DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
	}

	private async void Messages_Drop(object? sender, DragEventArgs e)
	{
		if (this.vm is null || !this.vm.CanSendFiles)
			return;

		List<string> paths = [.. (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>()];
		if (paths.Count > 0)
			await this.vm.SendFilesAsync(paths);
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);

		if (this.vm is not null)
		{
			this.vm.Messages.CollectionChanged -= this.Messages_CollectionChanged;
			this.vm.ComposerFocusWanted -= this.FocusComposer;
			this.vm.PropertyChanged -= this.Vm_PropertyChanged;
		}

		this.vm = this.DataContext as ChatViewModel;

		if (this.vm is not null)
		{
			this.vm.Messages.CollectionChanged += this.Messages_CollectionChanged;
			this.vm.ComposerFocusWanted += this.FocusComposer;
			this.vm.PropertyChanged += this.Vm_PropertyChanged;
			this.ReportWidth();
		}
	}

	private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		// Follow the conversation to its newest message. A message that changed, went, or came
		// in above the ones shown leaves you where you are.
		bool atEnd = e.Action == NotifyCollectionChangedAction.Reset
			|| (e.Action == NotifyCollectionChangedAction.Add && this.vm is { IsAddingOlder: false } && e.NewStartingIndex >= this.vm.Messages.Count - 1);
		if (atEnd)
			Dispatcher.UIThread.Post(() => this.MessageScroller.ScrollToEnd(), DispatcherPriority.Background);
	}

	private void Composer_KeyDown(object? sender, KeyEventArgs e)
	{
		if (this.vm is null)
			return;

		switch (e.Key)
		{
			case Key.Enter when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
				e.Handled = true;
				if (!this.vm.AcceptSuggestion() && this.vm.SendCommand.CanExecute(null))
					this.vm.SendCommand.Execute(null);
				break;

			case Key.Tab when this.vm.HasSuggestions:
				e.Handled = this.vm.AcceptSuggestion();
				break;

			case Key.Down when this.vm.HasSuggestions:
				e.Handled = this.vm.MoveSuggestion(1);
				break;

			case Key.Up when this.vm.HasSuggestions:
				e.Handled = this.vm.MoveSuggestion(-1);
				break;

			// Up in an empty box: change the last message you sent.
			case Key.Up when string.IsNullOrEmpty(this.Composer.Text):
				e.Handled = this.vm.EditLast();
				break;

			case Key.Escape:
				e.Handled = this.vm.CancelComposer();
				break;
		}
	}
}
