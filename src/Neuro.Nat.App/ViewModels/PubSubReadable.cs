using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Neuro.Nat.App.ViewModels;

/// <summary>One line of a readable item: "Temperature", "23.1 °C".</summary>
public sealed record ReadableField(string Label, string Value);

/// <summary>
/// What a published item says, for the kinds the app knows: a picture, a nickname, a mood, a
/// tune, a place, readings from a sensor. Everything else stays XML.
/// </summary>
public sealed record Readable(string Kind, string? Summary, IReadOnlyList<ReadableField> Fields, byte[]? Picture = null)
{
	private static readonly IReadOnlyList<ReadableField> none = [];

	/// <summary>Reads a payload. Null when it is not one of the kinds the app shows in words.</summary>
	public static Readable? From(string xml)
	{
		XElement e;
		try
		{
			e = XElement.Parse(xml);
		}
		catch (XmlException)
		{
			return null;
		}

		try
		{
			return (e.Name.NamespaceName, e.Name.LocalName) switch
			{
				("urn:xmpp:avatar:data", "data") => Avatar(e),
				("urn:xmpp:avatar:metadata", "metadata") => AvatarInfo(e),
				("http://jabber.org/protocol/nick", "nick") => new Readable("Nickname", e.Value.Trim(), none),
				("http://jabber.org/protocol/mood", "mood") => Mood(e),
				("http://jabber.org/protocol/tune", "tune") => Tune(e),
				("http://jabber.org/protocol/geoloc", "geoloc") => Place(e),
				("urn:xmpp:bookmarks:1", "conference") => Bookmark(e),
				(_, "resp" or "fields" or "sensorData") when IsSensorData(e.Name.NamespaceName) => Readings(e),
				_ => null,
			};
		}
		catch (FormatException)
		{
			return null;
		}
	}

	private static bool IsSensorData(string ns)
		=> ns is "urn:ieee:iot:sd:1.0" or "urn:nfi:iot:sd:1.0" or "urn:xmpp:iot:sensordata" or "urn:ietf:params:xml:ns:iotsensordata";

	private static string Size(long bytes)
		=> bytes < 1024 ? bytes.ToString(CultureInfo.CurrentCulture) + " bytes" : (bytes / 1024.0).ToString(bytes < 10 * 1024 ? "0.#" : "0", CultureInfo.CurrentCulture) + " kB";

	private static Readable Avatar(XElement e)
	{
		byte[] picture = Convert.FromBase64String(e.Value.Trim());
		return new Readable("Avatar picture", "A picture, " + Size(picture.Length), none, picture);
	}

	private static Readable AvatarInfo(XElement e)
	{
		List<ReadableField> fields = [];
		foreach (XElement info in e.Elements().Where(x => x.Name.LocalName == "info"))
		{
			string type = (string?)info.Attribute("type") ?? "picture";
			string size = long.TryParse((string?)info.Attribute("bytes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long b) ? Size(b) : string.Empty;
			string w = (string?)info.Attribute("width") ?? string.Empty;
			string h = (string?)info.Attribute("height") ?? string.Empty;
			string pixels = w.Length > 0 && h.Length > 0 ? w + " × " + h : string.Empty;
			fields.Add(new ReadableField(type, string.Join(", ", new[] { pixels, size }.Where(s => s.Length > 0))));
		}

		return new Readable("Avatar details", fields.Count == 0 ? "No picture is set" : null, fields);
	}

	private static Readable Mood(XElement e)
	{
		string? mood = e.Elements().FirstOrDefault(x => x.Name.LocalName != "text")?.Name.LocalName;
		string? text = e.Elements().FirstOrDefault(x => x.Name.LocalName == "text")?.Value.Trim();
		string said = mood is null ? "No mood" : char.ToUpper(mood[0], CultureInfo.CurrentCulture) + mood[1..].Replace('_', ' ');
		return new Readable("Mood", string.IsNullOrEmpty(text) ? said : said + ": " + text, none);
	}

	private static string? Child(XElement e, string name)
	{
		string? v = e.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim();
		return string.IsNullOrEmpty(v) ? null : v;
	}

	private static Readable Tune(XElement e)
	{
		string? title = Child(e, "title");
		string? artist = Child(e, "artist");
		if (title is null && artist is null)
			return new Readable("Tune", "Nothing is playing", none);

		List<ReadableField> fields = [];
		if (Child(e, "source") is string source)
			fields.Add(new ReadableField("From", source));
		if (int.TryParse(Child(e, "length"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
			fields.Add(new ReadableField("Length", TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture)));

		return new Readable("Tune", artist is null ? title : title is null ? artist : title + " by " + artist, fields);
	}

	private static Readable Place(XElement e)
	{
		List<ReadableField> fields = [];
		void Add(string label, string name, string? unit = null)
		{
			if (Child(e, name) is string v)
				fields.Add(new ReadableField(label, unit is null ? v : v + " " + unit));
		}

		Add("Description", "description");
		Add("Street", "street");
		Add("Locality", "locality");
		Add("Region", "region");
		Add("Country", "country");
		Add("Latitude", "lat");
		Add("Longitude", "lon");
		Add("Altitude", "alt", "m");
		Add("Accuracy", "accuracy", "m");
		Add("Speed", "speed", "m/s");
		Add("Time", "timestamp");

		string? where = Child(e, "locality") ?? Child(e, "description");
		string? at = Child(e, "lat") is string lat && Child(e, "lon") is string lon ? lat + ", " + lon : null;
		return new Readable("Location", where is null ? at ?? "No place is given" : at is null ? where : where + " (" + at + ")", fields);
	}

	private static Readable Bookmark(XElement e)
	{
		string name = (string?)e.Attribute("name") ?? "A room";
		bool joins = (string?)e.Attribute("autojoin") is "true" or "1";
		List<ReadableField> fields = [];
		if (Child(e, "nick") is string nick)
			fields.Add(new ReadableField("Your name there", nick));
		return new Readable("Room bookmark", name + (joins ? ", joined when you connect" : ", not joined on its own"), fields);
	}

	/// <summary>
	/// Sensor data: each reading has a name, a value and often a unit, under the moment it was
	/// taken and the thing it was taken from.
	/// </summary>
	private static Readable Readings(XElement e)
	{
		List<ReadableField> fields = [];
		string? when = null;
		int things = 0;
		foreach (XElement x in e.Descendants())
		{
			switch (x.Name.LocalName)
			{
				case "nd" or "node":
					things++;
					break;

				case "ts" or "timestamp":
					when = (string?)x.Attribute("v") ?? (string?)x.Attribute("value") ?? when;
					break;

				case "q" or "numeric" or "s" or "string" or "b" or "boolean" or "i" or "int" or "l" or "long" or "d" or "date" or "dt" or "dateTime" or "t" or "time" or "e" or "enum" or "dr" or "duration":
					string? name = (string?)x.Attribute("n") ?? (string?)x.Attribute("name");
					string? value = (string?)x.Attribute("v") ?? (string?)x.Attribute("value");
					if (name is null || value is null)
						break;

					string? unit = (string?)x.Attribute("u") ?? (string?)x.Attribute("unit");
					if (x.Name.LocalName is "b" or "boolean")
						value = value is "true" or "1" ? "Yes" : "No";
					fields.Add(new ReadableField(name, string.IsNullOrEmpty(unit) ? value : value + " " + unit));
					break;
			}
		}

		if (fields.Count == 0)
			return new Readable("Sensor data", "No readings", none);

		string summary = fields.Count == 1 ? "1 reading" : fields.Count.ToString(CultureInfo.CurrentCulture) + " readings";
		if (things > 1)
			summary += " from " + things.ToString(CultureInfo.CurrentCulture) + " things";
		if (when is not null)
			summary += DateTime.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTime t)
				? ", taken " + t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
				: ", taken " + when;

		return new Readable("Sensor data", summary, fields);
	}

	/// <summary>The payload laid out for reading, one element to a line.</summary>
	public static string Pretty(string xml)
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

	/// <summary>
	/// What is wrong with XML somebody is typing, with the line and place it goes wrong at.
	/// Null when it can be published as it is.
	/// </summary>
	public static (string Text, int Line, int Position)? Problem(string xml)
	{
		if (string.IsNullOrWhiteSpace(xml))
			return ("Nothing to publish yet.", 0, 0);

		try
		{
			XElement.Parse(xml);
			return null;
		}
		catch (XmlException ex)
		{
			// The parser ends its message with "Line 3, position 14."; that is said once, up front.
			string why = ex.Message;
			int at = why.LastIndexOf(" Line ", StringComparison.Ordinal);
			if (at > 0)
				why = why[..at];

			return ex.LineNumber > 0
				? ($"Line {ex.LineNumber}, place {ex.LinePosition}: {why}", ex.LineNumber, ex.LinePosition)
				: (why, 0, 0);
		}
	}
}
