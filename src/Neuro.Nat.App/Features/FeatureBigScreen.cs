using Avalonia.Threading;
using Neuro.Nat.App.ViewModels;

namespace Neuro.Nat.App.Features;

/// <summary>A page a feature adds to the big screen, as the big screen knows pages.</summary>
internal sealed class FeatureBigScreenPage(FeatureScreenPage page) : IBigScreenPage
{
	private object? content;

	public string Key => page.Key;

	public string Title => page.Title;

	/// <summary>The feature lists the page only while it has something to show.</summary>
	public bool HasContent => true;

	public object? Content => this.content ??= page.Content();
}

public sealed partial class FeatureSet
{
	/// <summary>
	/// Keeps the big screen's added pages (<see cref="BigScreenPages.Added"/>) in step with the
	/// pages the features list now: a page comes when a feature lists it and goes when it stops.
	/// Without features nothing is added. Changes are made on the UI thread.
	/// </summary>
	public void FeedBigScreen(IList<IBigScreenPage> pages)
	{
		void Sync()
		{
			List<FeatureScreenPage> now = [.. this.BigScreenPages()];
			for (int i = pages.Count - 1; i >= 0; i--)
			{
				if (pages[i] is FeatureBigScreenPage p && !now.Any(n => n.Key == p.Key))
					pages.RemoveAt(i);
			}

			foreach (FeatureScreenPage n in now)
			{
				if (!pages.Any(p => p is FeatureBigScreenPage f && f.Key == n.Key))
					pages.Add(new FeatureBigScreenPage(n));
			}
		}

		Sync();
		this.Changed += (_, _) => Dispatcher.UIThread.Post(Sync);
	}
}
