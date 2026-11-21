using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace AutoFluentValidation.SourceGenerator;

/// <summary>
/// Everything needed to emit one validator. Contains only strings and value types so the
/// incremental pipeline can cache it by value and skip re-emission when nothing changed.
/// </summary>
internal sealed record ValidatorSpec(
	string? Namespace,
	string ValidatorName,
	string ModelFullName,
	/// <summary>Emit the <c>public</c> modifier; otherwise none, so the user's declaration (or internal) applies.</summary>
	bool IsPublic,
	bool StopOnFirstFailure,
	EquatableArray<string> RuleStatements) {
	public string ValidatorFullName => Namespace is null ? "global::" + ValidatorName : "global::" + Namespace + "." + ValidatorName;
	public string HintName => (Namespace is null ? "" : Namespace + ".") + ValidatorName + ".g.cs";
}

internal sealed record RegistrationSpec(
	string Namespace,
	EquatableArray<RegistrationEntry> Entries);

internal sealed record RegistrationEntry(string ModelFullName, string ValidatorFullName);

/// <summary>An immutable array with value equality, for use in incremental generator models.</summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T> where T : IEquatable<T> {
	private readonly ImmutableArray<T> _items;

	public EquatableArray(ImmutableArray<T> items) => _items = items;

	public ImmutableArray<T> Items => _items.IsDefault ? ImmutableArray<T>.Empty : _items;
	public int Length => Items.Length;

	public bool Equals(EquatableArray<T> other) => Items.SequenceEqual(other.Items);
	public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

	public override int GetHashCode() {
		unchecked {
			int hash = 17;
			foreach (var item in Items) hash = hash * 31 + (item?.GetHashCode() ?? 0);
			return hash;
		}
	}

	public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
