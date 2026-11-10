using Neuro.AA.Engine.Events;
using System.Security;
using System.Security.Cryptography;
using System.Xml;
using Neuro.AA.Engine.Attention;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.Events;
using Waher.Networking.XMPP.HttpFileUpload;
using Waher.Networking.XMPP.MUC;
using Waher.Networking.XMPP.P2P;
using Waher.Networking.XMPP.ServiceDiscovery;
using Waher.Security.EllipticCurves.E2EE;
using Waher.Security.E2EE;
using Waher.Security;

namespace Neuro.AA.Engine.Chat;

/// <summary>What one account's connection carries for chat: encryption, file upload and rooms.</summary>
internal sealed class AccountChat : IDisposable
{
	public AccountChat(Guid accountId, XmppClient client, bool trustServer)
	{
		this.AccountId = accountId;
		this.Client = client;
		this.TrustServer = trustServer;

		// The same key types and strength as IoTClient, Lil'Sis and Neuro-Access. Keys are
		// published in presence; contacts' keys arrive the same way.
		this.E2e = new EndpointSecurity(client, 128, new Edwards25519Endpoint(), new Edwards448Endpoint());
		this.Upload = new HttpFileUploadClient(client);
		this.Muc = new MultiUserChatClient(client, string.Empty);
	}

	public Guid AccountId { get; }
	public XmppClient Client { get; }
	public bool TrustServer { get; }
	public EndpointSecurity E2e { get; }
	public HttpFileUploadClient Upload { get; private set; }
	public MultiUserChatClient Muc { get; }

	/// <summary>The group chat service of the account's server, when it has one.</summary>
	public string? MucService { get; private set; }

	public bool CanUpload => this.Upload.HasSupport;

	public async Task DiscoverAsync()
	{
		try
		{
			await this.Upload.DiscoverAsync().WaitAsync(TimeSpan.FromSeconds(20));
		}
		catch (Exception)
		{
			// No upload service.
		}

		try
		{
			ServiceItemsDiscoveryEventArgs items = await this.Client.ServiceItemsDiscoveryAsync(this.Client.Domain).WaitAsync(TimeSpan.FromSeconds(20));
			foreach (Item item in items.Items ?? [])
			{
				ServiceDiscoveryEventArgs info = await this.Client.ServiceDiscoveryAsync(item.JID).WaitAsync(TimeSpan.FromSeconds(10));
				if (info.Ok && info.HasFeature("http://jabber.org/protocol/muc"))
				{
					this.MucService = item.JID;
					break;
				}
			}
		}
		catch (Exception)
		{
			// No group chat service found.
		}
	}

	public void Dispose()
	{
		this.E2e.Dispose();
		this.Upload.Dispose();
		this.Muc.Dispose();
	}
}

public sealed partial class ChatService
{
	/// <summary>Full JIDs of a contact's online resources that published encryption keys.</summary>
	private List<string> SecureResources(AccountChat a, string bareJid)
	{
		RosterItem? item = a.Client.GetRosterItem(bareJid);
		List<string> result = [];
		foreach (PresenceEventArgs p in item?.Resources ?? [])
		{
			if (p.IsOnline && !string.IsNullOrEmpty(p.From) && a.E2e.ContainsKey(p.From))
				result.Add(p.From);
		}

		return result;
	}

	/// <summary>Whether messages to a contact go end-to-end encrypted, and the key's fingerprint.</summary>
	public ContactSecurity Security(Guid accountId, string bareJid)
	{
		if (this.ChatOf(accountId) is not AccountChat a)
			return new ContactSecurity(false, null, null, 0);

		// Chat with a Neuron goes in the clear over TLS (see DeliverAsync).
		if (this.monitors.For(accountId)?.Neurons.Any(n => n.State is WatchState.Live or WatchState.Subscribing or WatchState.Forbidden && string.Equals(n.BareJid, bareJid, StringComparison.OrdinalIgnoreCase)) == true)
			return new ContactSecurity(false, null, null, 0);

		List<string> secure = this.SecureResources(a, bareJid);
		if (secure.Count == 0)
			return new ContactSecurity(false, null, null, 0);

		IE2eEndpoint[] keys = a.E2e.GetE2eEndpoints(secure[0]) ?? [];
		// The Ed25519 key, when the contact has one, so both sides show the same fingerprint.
		IE2eEndpoint? key = keys.FirstOrDefault(k => k.LocalName == "ed25519") ?? keys.FirstOrDefault();
		return new ContactSecurity(true, key is null ? null : Fingerprint(key.PublicKey), key?.LocalName, secure.Count);
	}

	/// <summary>The fingerprint of this account's own key, for contacts to compare.</summary>
	public string? OwnFingerprint(Guid accountId)
	{
		if (this.ChatOf(accountId) is not AccountChat a)
			return null;

		IE2eEndpoint? key = a.E2e.FindLocalEndpoint("ed25519");
		return key is null ? null : Fingerprint(key.PublicKey);
	}

	/// <summary>SHA-256 of a public key, first 10 bytes, in groups of four hex digits.</summary>
	public static string Fingerprint(byte[] publicKey)
	{
		byte[] h = SHA256.HashData(publicKey);
		string hex = Convert.ToHexString(h, 0, 10);
		return string.Join(' ', Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4)));
	}

	// ---- Files -----------------------------------------------------------------------------

	/// <summary>True when the account's server takes file uploads.</summary>
	public bool CanSendFiles(Guid accountId) => this.ChatOf(accountId)?.CanUpload == true;

	/// <summary>Largest file the server takes, if it says.</summary>
	public long? MaxFileSize(Guid accountId) => this.ChatOf(accountId)?.Upload.MaxFileSize;

	/// <summary>
	/// Uploads a file to the account's server and sends its link: as an image when it is one,
	/// otherwise as a download link, with the out-of-band element other clients look for.
	/// </summary>
	public async Task<ChatMessage> SendFileAsync(Guid accountId, string jid, string path, IProgress<string>? progress = null)
	{
		AccountChat a = this.ChatOf(accountId) ?? throw new InvalidOperationException("The account is not connected.");
		if (!a.CanUpload)
			throw new InvalidOperationException("The account's server does not take file uploads.");

		FileInfo file = new(path);
		if (a.Upload.MaxFileSize is long max && file.Length > max)
			throw new InvalidOperationException($"The file is {file.Length / 1048576.0:0.0} MB; the server takes at most {max / 1048576.0:0.0} MB.");

		string contentType = ContentTypeOf(file.Extension);
		progress?.Report("Uploading " + file.Name + "…");

		HttpFileUploadEventArgs slot = await a.Upload.RequestUploadSlotAsync(file.Name, contentType, file.Length).WaitAsync(TimeSpan.FromSeconds(30));
		if (!slot.Ok)
			throw new InvalidOperationException(string.IsNullOrEmpty(slot.ErrorText) ? "The server did not give an upload slot." : slot.ErrorText);

		try
		{
			await using FileStream f = File.OpenRead(path);
			await slot.PUT(f, contentType, 600000, a.TrustServer);
		}
		catch (Exception ex)
		{
			throw new InvalidOperationException($"The upload to {new Uri(slot.PutUrl).GetLeftPart(UriPartial.Authority)} failed: {ex.Message}", ex);
		}

		string url = slot.GetUrl;
		bool image = contentType.StartsWith("image/", StringComparison.Ordinal);
		string name = MarkdownEscape(file.Name);
		string markdown = image
			? $"![{name}]({url})"
			: $"[{name}]({url}) ({SizeText(file.Length)})";

		Conversation c = this.Find(accountId, jid) ?? this.Get(accountId, jid);
		string id = Guid.NewGuid().ToString("N");
		ChatMessage m = new()
		{
			Id = id,
			MessageId = id,
			Direction = ChatDirection.Out,
			Timestamp = DateTime.Now,
			Markdown = markdown,
			Text = file.Name + " " + url,
			Sender = c.IsRoom ? this.Room(accountId, jid)?.NickName : null,
		};
		c.Add(m);

		string xml = MarkdownXml(markdown) +
			"<x xmlns='jabber:x:oob'><url>" + SecurityElement.Escape(url) + "</url><desc>" + SecurityElement.Escape(file.Name) + "</desc></x>";

		try
		{
			if (await this.DeliverAsync(accountId, jid, c.IsRoom, id, xml, url))
			{
				m = m with { Encrypted = true };
				c.Replace(m);
			}
		}
		catch (Exception ex)
		{
			m = m with { Error = ex.Message };
			c.Replace(m);
		}

		this.Touch();
		this.ConversationChanged?.Invoke(this, c);
		return m;
	}

	/// <summary>Adds a file link from an out-of-band element (XEP-0066) when the text does not show it.</summary>
	private static IncomingNotification WithOutOfBand(IncomingNotification n, MessageEventArgs e)
	{
		if (e.Message is not XmlElement message)
			return n;

		foreach (XmlNode node in message.ChildNodes)
		{
			if (node is XmlElement x && x.LocalName == "x" && x.NamespaceURI == "jabber:x:oob")
			{
				string url = x["url"]?.InnerText?.Trim() ?? string.Empty;
				string desc = x["desc"]?.InnerText?.Trim() ?? string.Empty;
				if (url.Length == 0 || n.Markdown.Contains(url, StringComparison.Ordinal))
					continue;

				string name = desc.Length > 0 ? desc : Path.GetFileName(new Uri(url, UriKind.RelativeOrAbsolute).IsAbsoluteUri ? new Uri(url).LocalPath : url);
				bool image = ContentTypeOf(Path.GetExtension(name.Length > 0 ? name : url)).StartsWith("image/", StringComparison.Ordinal);
				string link = image ? $"![{MarkdownEscape(name)}]({url})" : $"[{MarkdownEscape(name.Length > 0 ? name : url)}]({url})";
				string body = n.Markdown.Trim() == url ? string.Empty : n.Markdown;
				n = n with { Markdown = body.Length == 0 ? link : body + "\n\n" + link };
			}
		}

		return n;
	}

	private static string MarkdownEscape(string s)
		=> s.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]").Replace("*", "\\*").Replace("_", "\\_");

	private static string SizeText(long bytes) => bytes switch
	{
		< 1024 => bytes + " bytes",
		< 1048576 => $"{bytes / 1024.0:0.#} KB",
		_ => $"{bytes / 1048576.0:0.#} MB",
	};

	internal static string ContentTypeOf(string extension) => extension.ToLowerInvariant().TrimStart('.') switch
	{
		"png" => "image/png",
		"jpg" or "jpeg" => "image/jpeg",
		"gif" => "image/gif",
		"webp" => "image/webp",
		"bmp" => "image/bmp",
		"svg" => "image/svg+xml",
		"pdf" => "application/pdf",
		"txt" or "log" => "text/plain",
		"md" => "text/markdown",
		"xml" => "text/xml",
		"json" => "application/json",
		"csv" => "text/csv",
		"zip" => "application/zip",
		"html" or "htm" => "text/html",
		_ => "application/octet-stream",
	};
}
