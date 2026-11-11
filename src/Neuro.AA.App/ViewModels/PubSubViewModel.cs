using System.Collections.ObjectModel;
using System.Xml;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuro.AA.Engine.Accounts;
using Neuro.AA.Engine.PubSub;

namespace Neuro.AA.App.ViewModels;

/// <summary>An item on the selected node, with its payload laid out for reading.</summary>
public sealed record PubSubItemRow(PubSubItemInfo Item, string PrettyXml)
{
	public string Title => this.Item.ItemId;
	public string Meta => this.Item.Publisher is null ? string.Empty : "by " + this.Item.Publisher;
}

/// <summary>
/// The Publish/subscribe workspace: the server's pubsub nodes and the account's personal (PEP)
/// nodes; read, publish and retract items; create and delete nodes.
/// </summary>
public sealed partial class PubSubViewModel : ObservableObject
{
	private readonly PubSubService pubsub;
	private readonly AccountManager accounts;

	public PubSubViewModel(PubSubService pubsub, AccountManager accounts)
	{
		this.pubsub = pubsub;
		this.accounts = accounts;
	}

	public ObservableCollection<DeviceAccountChoice> AccountChoices { get; } = [];
	public ObservableCollection<PubSubNodeInfo> Nodes { get; } = [];
	public ObservableCollection<PubSubItemRow> Items { get; } = [];

	[ObservableProperty] private DeviceAccountChoice? account;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsService), nameof(Title))]
	private bool isPersonal;

	public bool IsService => !this.IsPersonal;

	/// <summary>What the list of nodes is called where it is shown.</summary>
	public string Title => !this.IsPinned ? "Publish/subscribe" : this.IsPersonal ? "Storage" : "Nodes";

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasNode))]
	private PubSubNodeInfo? selectedNode;

	public bool HasNode => this.SelectedNode is not null;

	[ObservableProperty] private bool hasNodes;
	[ObservableProperty] private bool hasItems;
	[ObservableProperty] private bool isBusy;
	[ObservableProperty] private string? error;
	[ObservableProperty] private string serviceText = string.Empty;
	[ObservableProperty] private string newNodeName = string.Empty;
	[ObservableProperty] private string publishItemId = string.Empty;
	[ObservableProperty] private string publishXml = "<value xmlns='urn:example'>42</value>";
	[ObservableProperty] private bool isConfirmingDelete;

	partial void OnAccountChanged(DeviceAccountChoice? value)
	{
		if (!this.loading)
			_ = this.LoadNodesAsync();
	}

	partial void OnIsPersonalChanged(bool value)
	{
		if (!this.loading)
			_ = this.LoadNodesAsync();
	}

	partial void OnSelectedNodeChanged(PubSubNodeInfo? value)
	{
		this.IsConfirmingDelete = false;
		_ = this.LoadItemsAsync();
	}

	/// <summary>True on an account's or a Neuron's page, where the account and the kind of nodes are already chosen.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Title))]
	private bool isPinned;

	/// <summary>
	/// Shows one account's nodes: its personal storage (on the account's page) or the nodes of the
	/// publish/subscribe service on its Neuron (on the Neuron's page).
	/// </summary>
	public void Show(Guid accountId, bool personal)
	{
		this.IsPinned = true;
		this.Activate();
		DeviceAccountChoice? choice = this.AccountChoices.FirstOrDefault(c => c.Id == accountId);
		if (!ReferenceEquals(choice, this.Account) || this.IsPersonal != personal)
		{
			this.loading = true;
			try
			{
				this.Account = choice;
				this.IsPersonal = personal;
			}
			finally
			{
				this.loading = false;
			}

			_ = this.LoadNodesAsync();
		}
	}

	private bool loading;

	/// <summary>Called when the workspace opens.</summary>
	public void Activate()
	{
		foreach (AccountSession s in this.accounts.Sessions)
		{
			if (!this.AccountChoices.Any(c => c.Id == s.Profile.Id))
				this.AccountChoices.Add(new DeviceAccountChoice(s.Profile.Id, s.Profile.Label));
		}

		if (this.Account is null)
			this.Account = this.AccountChoices.FirstOrDefault(c => this.pubsub.IsAvailable(c.Id)) ?? this.AccountChoices.FirstOrDefault();
		else
			_ = this.LoadNodesAsync();
	}

	private async Task RunAsync(Func<Task> action)
	{
		this.IsBusy = true;
		this.Error = null;
		try
		{
			await action();
		}
		catch (Exception ex)
		{
			this.Error = ex.Message;
		}
		finally
		{
			this.IsBusy = false;
		}
	}

	private Task LoadNodesAsync()
	{
		this.Nodes.Clear();
		this.HasNodes = false;
		this.SelectedNode = null;
		if (this.Account is not { } a)
			return Task.CompletedTask;

		this.ServiceText = this.IsPersonal ? "Personal storage of " + a.Label : this.pubsub.Service(a.Id) is string svc ? "Service " + svc : this.pubsub.Problem(a.Id) ?? "Looking for the service…";

		return this.RunAsync(async () =>
		{
			IReadOnlyList<PubSubNodeInfo> nodes = this.IsPersonal ? await this.pubsub.PersonalNodesAsync(a.Id) : await this.pubsub.NodesAsync(a.Id);
			foreach (PubSubNodeInfo n in nodes)
				this.Nodes.Add(n);
			this.HasNodes = this.Nodes.Count > 0;
		});
	}

	private Task LoadItemsAsync()
	{
		this.Items.Clear();
		this.HasItems = false;
		if (this.Account is not { } a || this.SelectedNode is not { } n)
			return Task.CompletedTask;

		return this.RunAsync(async () =>
		{
			foreach (PubSubItemInfo i in await this.pubsub.ItemsAsync(a.Id, n))
				this.Items.Add(new PubSubItemRow(i, Pretty(i.PayloadXml)));
			this.HasItems = this.Items.Count > 0;
		});
	}

	private static string Pretty(string xml)
	{
		try
		{
			return XElement.Parse(xml).ToString(SaveOptions.None);
		}
		catch (XmlException)
		{
			return xml;
		}
	}

	[RelayCommand]
	private void SetPersonal(string? which) => this.IsPersonal = which == "personal";

	[RelayCommand]
	private Task Reload() => this.SelectedNode is null ? this.LoadNodesAsync() : this.LoadItemsAsync();

	[RelayCommand]
	private Task CreateNode()
	{
		if (this.Account is not { } a || string.IsNullOrWhiteSpace(this.NewNodeName))
			return Task.CompletedTask;

		return this.RunAsync(async () =>
		{
			PubSubNodeInfo n = await this.pubsub.CreateNodeAsync(a.Id, this.NewNodeName.Trim());
			this.NewNodeName = string.Empty;
			this.Nodes.Add(n);
			this.HasNodes = true;
			this.SelectedNode = n;
		});
	}

	[RelayCommand]
	private Task Publish()
	{
		if (this.Account is not { } a || this.SelectedNode is not { } n)
			return Task.CompletedTask;

		try
		{
			XElement.Parse(this.PublishXml);
		}
		catch (XmlException ex)
		{
			this.Error = "The payload is not well-formed XML: " + ex.Message;
			return Task.CompletedTask;
		}

		return this.RunAsync(async () =>
		{
			await this.pubsub.PublishAsync(a.Id, n, string.IsNullOrWhiteSpace(this.PublishItemId) ? null : this.PublishItemId.Trim(), this.PublishXml);
			this.PublishItemId = string.Empty;
			await this.LoadItemsAsync();
		});
	}

	[RelayCommand]
	private Task Retract(PubSubItemRow? row)
	{
		if (this.Account is not { } a || this.SelectedNode is not { } n || row is null)
			return Task.CompletedTask;

		return this.RunAsync(async () =>
		{
			await this.pubsub.RetractAsync(a.Id, n, row.Item.ItemId);
			this.Items.Remove(row);
			this.HasItems = this.Items.Count > 0;
		});
	}

	[RelayCommand]
	private void AskDelete() => this.IsConfirmingDelete = true;

	[RelayCommand]
	private void CancelDelete() => this.IsConfirmingDelete = false;

	[RelayCommand]
	private Task DeleteNode()
	{
		if (this.Account is not { } a || this.SelectedNode is not { } n)
			return Task.CompletedTask;

		this.IsConfirmingDelete = false;
		return this.RunAsync(async () =>
		{
			await this.pubsub.DeleteNodeAsync(a.Id, n);
			this.Nodes.Remove(n);
			this.HasNodes = this.Nodes.Count > 0;
			this.SelectedNode = null;
		});
	}
}
