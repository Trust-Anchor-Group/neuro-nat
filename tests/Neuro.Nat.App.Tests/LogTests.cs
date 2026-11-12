namespace Neuro.Nat.App.Tests;

/// <summary>Nothing private lands in the log, not even inside the text of an exception from a library.</summary>
public class LogTests
{
	[Fact]
	public void TheWorstCasesAreScrubbed()
	{
		const string messageText = "Zebra-quartz 7731 vault code";
		const string roomPassword = "Room-pw-mauve-4471";
		const string token = "ghp_ABCdef1234567890tokenXYZ";
		const string hash = "c2NyYW0tc2hhLTI1Ni1hZ3JlZWQ=";

		string line = string.Join(Environment.NewLine,
			$"<message from='kim@lab.local/Phone-7f3a' to='ops@lab.local/NAT-1'><body>{messageText}</body><content xmlns='urn:xmpp:content' type='text/markdown'>**{messageText}**</content></message>",
			$"<presence to='ops@muc.lab.local/Mille'><x xmlns='http://jabber.org/protocol/muc'><password>{roomPassword}</password></x></presence> password={roomPassword}",
			$"https://user:{token}@github.example/repo?token={token}&x=1",
			$"Authorization: Bearer {token}",
			$"{{\"Value\":\"{hash}\",\"HashMethod\":\"SCRAM-SHA-256\"}}",
			"Reply from kim@lab.local/Phone-7f3a in the conversation");

		string scrubbed = AppLog.Scrub(line);

		Assert.DoesNotContain(messageText, scrubbed);
		Assert.DoesNotContain(roomPassword, scrubbed);
		Assert.DoesNotContain(token, scrubbed);
		Assert.DoesNotContain(hash, scrubbed);
		Assert.DoesNotContain("Phone-7f3a", scrubbed);
		Assert.DoesNotContain("NAT-1", scrubbed);

		// What helps to find a fault stays.
		Assert.Contains("kim@lab.local", scrubbed);
		Assert.Contains("github.example/repo", scrubbed);
	}

	[Fact]
	public void AnExceptionWithAStanzaInItIsWrittenScrubbed()
	{
		string file = Path.Combine(Path.GetTempPath(), "Nat.Log." + Guid.NewGuid().ToString("N"), "nat.log");
		string? was = AppLog.FilePath;
		try
		{
			AppLog.FilePath = file;
			AppLog.Error("Could not send.", new InvalidOperationException("Stanza: <message to='kim@lab.local/Laptop'><body>Meet at the vault, code 4471</body></message>"));
		}
		finally
		{
			AppLog.FilePath = was;
		}

		string written = File.ReadAllText(file);
		Assert.Contains("Could not send.", written);
		Assert.DoesNotContain("code 4471", written);
		Assert.DoesNotContain("Laptop", written);
		Directory.Delete(Path.GetDirectoryName(file)!, true);
	}
}
