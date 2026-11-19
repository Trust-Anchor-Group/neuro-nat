using Avalonia;
using Avalonia.Controls;
using Neuro.Nat.App.ViewModels;

namespace Neuro.Nat.App.Controls;

/// <summary>Displays a broker reply in the console or a runbook.</summary>
public sealed class ScriptReplyView : Decorator
{
	public static readonly StyledProperty<ScriptReply?> ReplyProperty =
		AvaloniaProperty.Register<ScriptReplyView, ScriptReply?>(nameof(Reply));

	public ScriptReply? Reply
	{
		get => this.GetValue(ReplyProperty);
		set => this.SetValue(ReplyProperty, value);
	}

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == ReplyProperty)
			this.Child = this.Reply?.Table is not null || this.Reply?.List is not null
				? new QueryTableView { DataContext = this.Reply } : new MarkdownView { Markdown = this.Reply?.Markdown };
	}
}
