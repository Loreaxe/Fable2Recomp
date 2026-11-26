using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// A keyed cache whose entries stay valid only while the objects they were built FROM are the
/// same instances: a pattern-book catalogue is good while its template tree and font library
/// are the ones it walked, an item label while the GDB and the text banks it resolved through
/// are the ones loaded. An entry built from anything else is a miss and is replaced on the next
/// put, so a remount, a reloaded string book or a retired tree never serves a stale value.
///
/// Identity, not equality: the inputs are large live objects, and "the same instance" is the
/// contract every one of them already keeps (a rebuilt tree is a new node, a reloaded bank a new
/// object).
/// </summary>
public sealed class Engine_UiIdentityCache<TKey, TValue>
	where TKey : notnull
	where TValue : class
{
	private sealed class Entry
	{
		public required TValue Value { get; init; }
		public required object?[] BuiltFrom { get; init; }
	}

	private readonly Dictionary<TKey, Entry> _entries;

	public Engine_UiIdentityCache(IEqualityComparer<TKey>? comparer = null)
	{
		_entries = new Dictionary<TKey, Entry>(comparer);
	}

	/// <summary>Entries held, valid or not — a stale entry is dropped only when it is asked for.</summary>
	public int Count => _entries.Count;

	/// <summary>
	/// The cached value for <paramref name="key"/> when it was built from exactly these
	/// instances; otherwise false, and a stale entry is forgotten.
	/// </summary>
	public bool TryGet(TKey key, out TValue value, params object?[] builtFrom)
	{
		if (_entries.TryGetValue(key, out Entry? entry))
		{
			if (SameInstances(entry.BuiltFrom, builtFrom))
			{
				value = entry.Value;
				return true;
			}

			_entries.Remove(key);
		}

		value = null!;
		return false;
	}

	/// <summary>Records <paramref name="value"/> as built from these instances, replacing any entry.</summary>
	public void Put(TKey key, TValue value, params object?[] builtFrom)
	{
		_entries[key] = new Entry { Value = value, BuiltFrom = (object?[])builtFrom.Clone() };
	}

	public bool Remove(TKey key) => _entries.Remove(key);

	/// <summary>Every value held, valid or not.</summary>
	public IEnumerable<TValue> Values
	{
		get
		{
			foreach (Entry entry in _entries.Values)
				yield return entry.Value;
		}
	}

	public void Clear() => _entries.Clear();

	private static bool SameInstances(object?[] was, object?[] now)
	{
		if (was.Length != now.Length)
		{
			return false;
		}

		for (int i = 0; i < was.Length; ++i)
		{
			if (!ReferenceEquals(was[i], now[i]))
			{
				return false;
			}
		}

		return true;
	}
}
