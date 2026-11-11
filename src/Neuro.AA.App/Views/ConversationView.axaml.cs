using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Neuro.AA.App.ViewModels;

namespace Neuro.AA.App.Views;

public partial class ConversationView : UserControl
{
	private ChatViewModel? vm;

	public ConversationView()
	{
		this.InitializeComponent();

		// Enter sends; Shift+Enter keeps the new line. Handled before the text box adds the line.
		this.Composer.AddHandler(KeyDownEvent, this.Composer_KeyDown, RoutingStrategies.Tunnel);

		this.AttachButton.Click += this.AttachButton_Click;
		this.MessageScroller.AddHandler(DragDrop.DropEvent, this.Messages_Drop);
		this.MessageScroller.AddHandler(DragDrop.DragOverEvent, this.Messages_DragOver);
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
			this.vm.Messages.CollectionChanged -= this.Messages_CollectionChanged;

		this.vm = this.DataContext as ChatViewModel;

		if (this.vm is not null)
			this.vm.Messages.CollectionChanged += this.Messages_CollectionChanged;
	}

	private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		// Follow the conversation to its newest message.
		Dispatcher.UIThread.Post(() => this.MessageScroller.ScrollToEnd(), DispatcherPriority.Background);
	}

	private void Composer_KeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && this.vm is not null)
		{
			e.Handled = true;
			if (this.vm.SendCommand.CanExecute(null))
				this.vm.SendCommand.Execute(null);
		}
	}
}
