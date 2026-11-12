using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.Nat.Engine.Chat;

namespace Neuro.Nat.App.ViewModels;

/// <summary>What the box at the bottom of a conversation is doing.</summary>
public enum ComposerMode
{
	/// <summary>Writing a new message.</summary>
	Write,

	/// <summary>Writing an answer to one message, which is quoted above it.</summary>
	Reply,

	/// <summary>Changing a message that was already sent.</summary>
	Edit,
}

/// <summary>An emoji offered while ":name" is being typed.</summary>
public sealed partial class EmojiSuggestion(EmojiChoice choice) : ObservableObject
{
	public EmojiChoice Choice { get; } = choice;

	/// <summary>The one Tab or Enter picks.</summary>
	[ObservableProperty] private bool isSelected;
}

/// <summary>
/// A question the conversation asks before it does something that needs a second thought or
/// one more piece of text: a reason, a new subject, or the room's name typed in before the room
/// is deleted. It sits under the conversation's header until answered.
/// </summary>
public sealed partial class AskViewModel(string title, Func<string, Task> run) : ObservableObject
{
	private readonly Func<string, Task> run = run;

	public string Title { get; } = title;
	public string? Text { get; init; }

	/// <summary>Set when the question takes text: what the empty box says.</summary>
	public string? Placeholder { get; init; }

	public bool HasInput => this.Placeholder is not null;
	public string ConfirmText { get; init; } = "OK";
	public bool IsDanger { get; init; }

	/// <summary>Set when the answer has to be exactly this before the button works.</summary>
	public string? MustMatch { get; init; }

	/// <summary>Set when the box may not be left empty.</summary>
	public bool NeedsText { get; init; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanConfirm))]
	private string input = string.Empty;

	[ObservableProperty] private string? error;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanConfirm))]
	private bool isBusy;

	public bool CanConfirm => !this.IsBusy
		&& (this.MustMatch is null || string.Equals(this.Input.Trim(), this.MustMatch, StringComparison.OrdinalIgnoreCase))
		&& (!this.NeedsText || this.Input.Trim().Length > 0);

	/// <summary>Does what was asked. Returns true when it worked; otherwise the error stays on the question.</summary>
	public async Task<bool> ConfirmAsync()
	{
		if (!this.CanConfirm)
			return false;

		this.IsBusy = true;
		this.Error = null;
		try
		{
			await this.run(this.Input.Trim());
			return true;
		}
		catch (Exception ex)
		{
			this.Error = ex is TimeoutException ? "No answer came back in time." : ex.Message;
			return false;
		}
		finally
		{
			this.IsBusy = false;
		}
	}
}

// Messages: reply, change, delete, emoji, and older messages a page at a time.
public sealed partial class ChatViewModel
{
	/// <summary>How many messages a conversation shows at first, and how many more each step back adds.</summary>
	public const int PageSize = 50;

	private readonly Dictionary<string, string> drafts = [];
	private string? draftBeforeEdit;
	private string? oldestShownId;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsBannerOpen), nameof(IsEditing), nameof(SendText))]
	private ComposerMode mode;

	/// <summary>The message being answered or changed.</summary>
	[ObservableProperty] private MessageViewModel? target;

	[ObservableProperty] private string bannerTitle = string.Empty;
	[ObservableProperty] private string bannerText = string.Empty;

	public bool IsBannerOpen => this.Mode != ComposerMode.Write;
	public bool IsEditing => this.Mode == ComposerMode.Edit;
	public string SendText => this.Mode == ComposerMode.Edit ? "Save" : "Send";

	/// <summary>Where the caret is in the box, so an emoji goes in where you are typing.</summary>
	[ObservableProperty] private int caret;

	/// <summary>Raised when the box should get the keyboard: after Reply, Edit or an emoji.</summary>
	public event Action? ComposerFocusWanted;

	/// <summary>There are messages older than the ones shown.</summary>
	[ObservableProperty] private bool hasOlder;

	/// <summary>True while older messages are being put in above the ones shown. The view keeps its place.</summary>
	public bool IsAddingOlder { get; private set; }

	// ----- The question under the header -----

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsAsking))]
	private AskViewModel? ask;

	public bool IsAsking => this.Ask is not null;

	[RelayCommand]
	private async Task ConfirmAsk()
	{
		if (this.Ask is AskViewModel a && await a.ConfirmAsync() && ReferenceEquals(this.Ask, a))
			this.Ask = null;
	}

	[RelayCommand]
	private void CancelAsk() => this.Ask = null;

	// ----- Drafts -----

	private static string DraftKey(PersonViewModel p) => p.AccountId.ToString("N") + "|" + p.BareJid.ToLowerInvariant();

	/// <summary>What you were writing stays with the conversation you were writing it in.</summary>
	partial void OnSelectedPersonChanging(PersonViewModel? value)
	{
		if (this.reordering)
			return;

		if (this.SelectedPerson is PersonViewModel old)
		{
			string keep = this.Mode == ComposerMode.Edit ? this.draftBeforeEdit ?? string.Empty : this.Draft;
			if (string.IsNullOrWhiteSpace(keep))
				this.drafts.Remove(DraftKey(old));
			else
				this.drafts[DraftKey(old)] = keep;
		}

		this.SetMode(ComposerMode.Write, null);
		this.Ask = null;
	}

	/// <summary>Puts back what was being written to this person or room.</summary>
	private void RestoreDraft(PersonViewModel? p)
	{
		this.Draft = p is not null && this.drafts.TryGetValue(DraftKey(p), out string? saved) ? saved : string.Empty;
		this.Caret = this.Draft.Length;
	}

	partial void OnDraftChanged(string value) => this.UpdateSuggestions();

	private void SetMode(ComposerMode mode, MessageViewModel? target)
	{
		if (this.Target is MessageViewModel old)
			old.IsTarget = false;

		this.Target = target;
		this.Mode = target is null ? ComposerMode.Write : mode;
		if (target is not null)
			target.IsTarget = true;

		if (this.Mode != ComposerMode.Edit)
			this.draftBeforeEdit = null;
	}

	private string WhoWrote(MessageViewModel m)
		=> m.IsMine ? "yourself" : m.Sender ?? this.SelectedPerson?.Label ?? "them";

	// ----- Reply, change, delete -----

	/// <summary>Answers one message: it is quoted above what you write.</summary>
	[RelayCommand]
	private void Reply(MessageViewModel? m)
	{
		if (m is null || m.IsDeleted)
			return;

		if (this.Mode == ComposerMode.Edit)
			this.Draft = this.draftBeforeEdit ?? string.Empty;

		this.SetMode(ComposerMode.Reply, m);
		this.BannerTitle = "Replying to " + this.WhoWrote(m);
		this.BannerText = OneLine(m.Markdown);
		this.ComposerFocusWanted?.Invoke();
	}

	/// <summary>Puts a message you sent back in the box to be changed.</summary>
	[RelayCommand]
	private void Edit(MessageViewModel? m)
	{
		if (m is null || !m.CanChange)
			return;

		if (this.Mode != ComposerMode.Edit)
			this.draftBeforeEdit = this.Draft;

		string before = this.draftBeforeEdit ?? string.Empty;
		this.SetMode(ComposerMode.Edit, m);
		this.draftBeforeEdit = before;
		this.BannerTitle = "Changing your message";
		this.BannerText = "Enter saves, Esc cancels";
		this.Draft = m.Markdown;
		this.Caret = this.Draft.Length;
		this.ComposerFocusWanted?.Invoke();
	}

	/// <summary>Up in an empty box: change the last message you sent. Returns false when there is none.</summary>
	public bool EditLast()
	{
		if (this.Mode != ComposerMode.Write || this.Draft.Length > 0)
			return false;

		MessageViewModel? last = this.Messages.LastOrDefault(m => m.CanChange);
		if (last is null)
			return false;

		this.Edit(last);
		return true;
	}

	/// <summary>
	/// Esc: closes the emoji suggestions, or leaves Reply or Edit. Returns false when there was
	/// nothing to leave.
	/// </summary>
	public bool CancelComposer()
	{
		if (this.HasSuggestions)
		{
			this.dismissedWord = Emoji.WordAt(this.Draft, this.Caret)?.Typed;
			this.UpdateSuggestions();
			return true;
		}

		if (this.Mode == ComposerMode.Write)
			return false;

		this.LeaveBanner();
		this.SendError = null;
		return true;
	}

	/// <summary>Back to writing a new message, with what was in the box before a change was started.</summary>
	private void LeaveBanner()
	{
		if (this.Mode == ComposerMode.Edit)
		{
			this.Draft = this.draftBeforeEdit ?? string.Empty;
			this.Caret = this.Draft.Length;
		}

		this.SetMode(ComposerMode.Write, null);
	}

	[RelayCommand]
	private void CancelBanner() => this.CancelComposer();

	/// <summary>The quote a reply starts with: who wrote it in a room, and no more than a few lines of it.</summary>
	private string ReplyQuote(MessageViewModel m)
	{
		string[] lines = m.Markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Trim().Split('\n');
		string text = string.Join('\n', lines.Take(4));
		bool cut = lines.Length > 4;
		if (text.Length > 400)
		{
			text = text[..400];
			cut = true;
		}

		if (cut)
			text += " …";

		if (this.SelectedPerson?.IsRoom == true && !m.IsMine && !string.IsNullOrEmpty(m.Sender))
			text = "**" + m.Sender + ":** " + text;

		return Export.Quote(text);
	}

	/// <summary>Deletes a message, after asking. Yours go for everyone; somebody else's only from your history.</summary>
	[RelayCommand]
	private void Delete(MessageViewModel? m)
	{
		if (m is null || this.SelectedConversation is not Conversation c)
			return;

		// One that never left has nothing to take back.
		if (m.IsMine && !m.CanChange)
		{
			_ = this.DeleteNow(c, m);
			return;
		}

		string who = this.WhoWrote(m);
		this.Ask = m.IsMine
			? new AskViewModel("Delete this message for everyone?", _ => this.DeleteNow(c, m))
			{
				Text = "This cannot be undone.",
				ConfirmText = "Delete for everyone",
				IsDanger = true,
			}
			: new AskViewModel("Remove this message from your history?", _ => this.DeleteNow(c, m))
			{
				Text = who + " keeps their copy.",
				ConfirmText = "Remove",
				IsDanger = true,
			};
	}

	private async Task DeleteNow(Conversation c, MessageViewModel m)
	{
		if (ReferenceEquals(this.Target, m))
			this.LeaveBanner();

		await this.chat.DeleteAsync(c, m.Id);
		if (this.SelectedPerson is PersonViewModel p)
			this.LoadConversation(p, false);
		this.Refresh();
	}

	/// <summary>Sends a message again that did not go the first time.</summary>
	[RelayCommand]
	private async Task SendAgain(MessageViewModel? m)
	{
		if (m is null || !m.IsMine || m.Error is null || this.SelectedPerson is not PersonViewModel p || this.SelectedConversation is not Conversation c)
			return;

		string text = m.Markdown;
		await this.chat.DeleteAsync(c, m.Id);
		await this.chat.SendAsync(p.AccountId, p.BareJid, text);
		this.LoadConversation(p, false);
	}

	// ----- Older messages -----

	/// <summary>Shows one more page of older messages above the ones shown.</summary>
	[RelayCommand]
	private void ShowOlder()
	{
		if (!this.HasOlder || this.SelectedPerson is not PersonViewModel p)
			return;

		Conversation? conv = p.IsRoom ? this.chat.GetRoom(p.AccountId, p.BareJid) : this.chat.Find(p.AccountId, p.BareJid);
		if (conv is null)
			return;

		IReadOnlyList<ChatMessage> all = conv.Messages;
		int from = this.FirstShown(all, conv);
		this.oldestShownId = all[Math.Max(0, from - PageSize)].Id;

		this.IsAddingOlder = true;
		try
		{
			this.LoadConversation(p, false);
		}
		finally
		{
			this.IsAddingOlder = false;
		}
	}

	/// <summary>The index of the oldest message to show: a page from the end, further back when older ones were asked for or are unread.</summary>
	private int FirstShown(IReadOnlyList<ChatMessage> all, Conversation? conv)
	{
		int from = Math.Max(0, all.Count - PageSize);
		if (this.oldestShownId is string oldest)
		{
			int at = IndexOf(all, oldest);
			if (at >= 0)
				from = Math.Min(from, at);
		}

		// Never start below the "New" line.
		if ((this.newLineId ?? (conv is { Unread: > 0 } ? conv.FirstUnreadId : null)) is string firstNew)
		{
			int at = IndexOf(all, firstNew);
			if (at >= 0)
				from = Math.Min(from, at);
		}

		return from;
	}

	private static int IndexOf(IReadOnlyList<ChatMessage> all, string id)
	{
		for (int i = 0; i < all.Count; i++)
		{
			if (all[i].Id == id)
				return i;
		}

		return -1;
	}

	// ----- Emoji -----

	public IReadOnlyList<EmojiGroup> EmojiGroups => Emoji.Groups;
	public ObservableCollection<EmojiSuggestion> Suggestions { get; } = [];

	[ObservableProperty] private bool hasSuggestions;

	private string? dismissedWord;

	partial void OnCaretChanged(int value) => this.UpdateSuggestions();

	private void UpdateSuggestions()
	{
		(int Start, string Typed)? word = Emoji.WordAt(this.Draft, this.Caret);
		if (word is null)
			this.dismissedWord = null;

		IReadOnlyList<EmojiChoice> found = word is null || word.Value.Typed == this.dismissedWord ? [] : Emoji.Suggest(word.Value.Typed);
		if (!found.SequenceEqual(this.Suggestions.Select(s => s.Choice)))
		{
			this.Suggestions.Clear();
			foreach (EmojiChoice e in found)
				this.Suggestions.Add(new EmojiSuggestion(e));

			if (this.Suggestions.Count > 0)
				this.Suggestions[0].IsSelected = true;
		}

		this.HasSuggestions = this.Suggestions.Count > 0;
	}

	/// <summary>Left and right among the suggestions.</summary>
	public bool MoveSuggestion(int by)
	{
		if (!this.HasSuggestions)
			return false;

		int at = Math.Max(0, this.Suggestions.ToList().FindIndex(s => s.IsSelected));
		int to = (at + by + this.Suggestions.Count) % this.Suggestions.Count;
		this.Suggestions[at].IsSelected = false;
		this.Suggestions[to].IsSelected = true;
		return true;
	}

	/// <summary>Tab or Enter while suggestions show: puts the selected emoji in place of the ":name".</summary>
	public bool AcceptSuggestion()
	{
		if (this.Suggestions.FirstOrDefault(s => s.IsSelected) is not EmojiSuggestion s)
			return false;

		this.PickSuggestion(s);
		return true;
	}

	[RelayCommand]
	private void PickSuggestion(EmojiSuggestion? s)
	{
		if (s is null || Emoji.WordAt(this.Draft, this.Caret) is not { } word)
			return;

		this.InsertAt(word.Start, word.Start + 1 + word.Typed.Length, s.Choice.Symbol + " ");
	}

	/// <summary>Puts an emoji from the picker in where the caret is.</summary>
	[RelayCommand]
	private void InsertEmoji(EmojiChoice? e)
	{
		if (e is null)
			return;

		int at = Math.Clamp(this.Caret, 0, this.Draft.Length);
		this.InsertAt(at, at, e.Symbol);
	}

	private void InsertAt(int start, int end, string text)
	{
		string draft = this.Draft;
		start = Math.Clamp(start, 0, draft.Length);
		end = Math.Clamp(end, start, draft.Length);
		this.Draft = draft[..start] + text + draft[end..];
		this.Caret = start + text.Length;
		this.UpdateSuggestions();
		this.ComposerFocusWanted?.Invoke();
	}
}
