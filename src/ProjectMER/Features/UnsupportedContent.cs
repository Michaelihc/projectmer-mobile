using System.Text;

namespace ProjectMER.Features;

/// <summary>
/// Aggregates "skipped" and "adapted" notices so a load logs one warning per kind with a count, for example
/// <c>Schematic "Bunker": skipped 12 Text blocks (TextToy is not available in the Carl Mod client)</c>.
/// </summary>
/// <remarks>
/// Unsupported data is never removed from the loaded <c>MapSchematic</c> or schematic data, so saving a map keeps it.
/// Scopes nest: a schematic loaded by a map reports under its own name. Without an open scope, a notice is logged
/// once per process for each distinct message.
/// </remarks>
public static class UnsupportedContent
{
	private static readonly Stack<Scope> Scopes = new();

	private static readonly HashSet<string> LoggedOutsideScope = [];

	/// <summary>
	/// Opens a reporting scope. Dispose it to log the collected notices.
	/// </summary>
	/// <param name="context">The context named in each warning, such as <c>Map "Facility"</c>.</param>
	/// <returns>The scope to dispose.</returns>
	public static IDisposable Begin(string context)
	{
		Scope scope = new(context);
		Scopes.Push(scope);
		return scope;
	}

	/// <summary>
	/// Takes an open scope off the reporting stack without logging it, so work that continues in later frames (schematics
	/// built across frames) keeps collecting into one scope. <see cref="Resume"/> puts it back; dispose it as usual to log.
	/// </summary>
	/// <param name="scope">A scope from <see cref="Begin"/>.</param>
	internal static void Suspend(IDisposable scope)
	{
		if (scope is not Scope target || !Scopes.Contains(target))
			return;

		while (Scopes.Count > 0 && !ReferenceEquals(Scopes.Pop(), target))
		{
		}
	}

	/// <summary>
	/// Puts a suspended scope back on top of the reporting stack.
	/// </summary>
	/// <param name="scope">A scope from <see cref="Begin"/>.</param>
	internal static void Resume(IDisposable scope)
	{
		if (scope is Scope target && !Scopes.Contains(target))
			Scopes.Push(target);
	}

	/// <summary>
	/// Counts content that is not spawned.
	/// </summary>
	/// <param name="what">What was skipped, plural, such as <c>Text blocks</c>.</param>
	/// <param name="reason">Why.</param>
	public static void Skip(string what, string reason) => Report("skipped", what, reason);

	/// <summary>
	/// Counts content that spawns with a different behaviour than in ProjectMER.
	/// </summary>
	/// <param name="what">What was adapted, plural, such as <c>spot lights</c>.</param>
	/// <param name="reason">How.</param>
	public static void Adapt(string what, string reason) => Report("adapted", what, reason);

	private static void Report(string verb, string what, string reason)
	{
		if (Scopes.Count == 0)
		{
			string message = $"{verb} {what} ({reason})";
			if (LoggedOutsideScope.Add(message))
				Logger.Warn(message);

			return;
		}

		Scope scope = Scopes.Peek();
		string key = verb + "\n" + what + "\n" + reason;
		scope.Counts.TryGetValue(key, out int count);
		scope.Counts[key] = count + 1;
	}

	private sealed class Scope(string context) : IDisposable
	{
		public readonly Dictionary<string, int> Counts = [];

		private bool _disposed;

		public void Dispose()
		{
			if (_disposed)
				return;

			_disposed = true;
			while (Scopes.Contains(this))
			{
				// Close inner scopes that were not disposed (exceptions) together with this one.
				if (ReferenceEquals(Scopes.Pop(), this))
					break;
			}

			foreach (KeyValuePair<string, int> pair in Counts)
			{
				string[] parts = pair.Key.Split('\n');
				StringBuilder sb = new();
				sb.Append(context).Append(": ").Append(parts[0]).Append(' ').Append(pair.Value).Append(' ').Append(parts[1]);
				sb.Append(" (").Append(parts[2]).Append(')');
				Logger.Warn(sb.ToString());
			}
		}
	}
}
