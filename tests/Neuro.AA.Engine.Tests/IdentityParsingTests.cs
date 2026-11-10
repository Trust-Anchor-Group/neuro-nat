using Neuro.AA.Engine.Identity;

namespace Neuro.AA.Engine.Tests;

/// <summary>Reading the chat admin's answer to <c>LegalId</c>.</summary>
public class IdentityParsingTests
{
	[Fact]
	public void ReadsMarkdownTableWithEscapes()
	{
		string answer = "| ID | Expires |\n|:---|:-------:|\n| 2f1c\\-0b7e@legal\\.example\\.com | 2027\\-05\\-01 |\n";
		(string? id, DateTime? expires) = IdentityService.ParseLegalIdAnswer(answer);
		Assert.Equal("2f1c-0b7e@legal.example.com", id);
		Assert.Equal(new DateTime(2027, 5, 1), expires);
	}

	[Fact]
	public void ReadsNoIdentity()
	{
		(string? id, DateTime? expires) = IdentityService.ParseLegalIdAnswer("| ID | Expires |\n|:---|:-------:|\n| N/A | N/A |\n");
		Assert.Null(id);
		Assert.Null(expires);
	}

	[Fact]
	public void ReadsTabSeparatedText()
	{
		(string? id, DateTime? expires) = IdentityService.ParseLegalIdAnswer("ID\tExpires\r\nabc@legal.\t2027-05-01");
		Assert.Equal("abc@legal.", id);
		Assert.Equal(new DateTime(2027, 5, 1), expires);
	}
}
