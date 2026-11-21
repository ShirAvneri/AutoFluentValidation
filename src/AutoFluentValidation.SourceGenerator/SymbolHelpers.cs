using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AutoFluentValidation.SourceGenerator;

internal static class SymbolHelpers {
	public const string AutoValidateAttributeName = "AutoFluentValidation.AutoValidateAttribute";

	public static string FullName(this ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

	/// <summary>Name usable with <see cref="Compilation.GetTypeByMetadataName"/> (nested types joined with '+').</summary>
	public static string MetadataName(INamedTypeSymbol type) {
		var parts = new Stack<string>();
		for (INamedTypeSymbol? cur = type; cur is not null; cur = cur.ContainingType) parts.Push(cur.MetadataName);
		var name = string.Join("+", parts);
		var ns = Namespace(type);
		return ns is null ? name : ns + "." + name;
	}

	public static string? Namespace(INamedTypeSymbol type) =>
		type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null;

	public static AttributeData? GetAutoValidateAttribute(ITypeSymbol type) =>
		type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == AutoValidateAttributeName);

	/// <summary>Simple name of the validator generated for <paramref name="model"/>.</summary>
	public static string ValidatorName(INamedTypeSymbol model, AttributeData autoValidate) {
		if (NamedArg(autoValidate, "ValidatorName") is { Value: string custom } && !string.IsNullOrWhiteSpace(custom)) return custom;
		var parts = new Stack<string>();
		for (INamedTypeSymbol? cur = model; cur is not null; cur = cur.ContainingType) parts.Push(cur.Name);
		return string.Concat(parts) + "Validator";
	}

	public static string ValidatorFullName(INamedTypeSymbol model, AttributeData autoValidate) {
		var ns = Namespace(model);
		var name = ValidatorName(model, autoValidate);
		return ns is null ? "global::" + name : "global::" + ns + "." + name;
	}

	/// <summary>Name usable with <see cref="IAssemblySymbol.GetTypeByMetadataName"/> for the model's validator.</summary>
	public static string ValidatorMetadataName(INamedTypeSymbol model, AttributeData autoValidate) {
		var ns = Namespace(model);
		var name = ValidatorName(model, autoValidate);
		return ns is null ? name : ns + "." + name;
	}

	/// <summary>Where to report a diagnostic about an attribute: the attribute itself, else the type it annotates.</summary>
	public static Location? AttributeLocation(AttributeData attr, ISymbol fallback) =>
		attr.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? fallback.Locations.FirstOrDefault();

	public static TypedConstant? NamedArg(AttributeData attr, string name) {
		foreach (var kv in attr.NamedArguments)
			if (kv.Key == name) return kv.Value;
		return null;
	}

	public static bool BoolArg(AttributeData attr, string name, bool defaultValue) =>
		NamedArg(attr, name) is { Value: bool b } ? b : defaultValue;

	public static string? StringArg(AttributeData attr, string name) =>
		NamedArg(attr, name) is { Value: string s } ? s : null;

	/// <summary>Public, readable, non-static, non-indexer properties, most-derived first; hidden members are skipped.</summary>
	public static List<IPropertySymbol> GetValidatableProperties(INamedTypeSymbol type) {
		var seen = new HashSet<string>();
		var result = new List<IPropertySymbol>();
		var chain = new List<INamedTypeSymbol>();
		for (var cur = type; cur is not null && cur.SpecialType != SpecialType.System_Object && cur.SpecialType != SpecialType.System_ValueType; cur = cur.BaseType)
			chain.Add(cur);

		// Collect derived-first so "new" members hide base ones, then emit base-first for a natural rule order.
		var perLevel = new List<List<IPropertySymbol>>();
		foreach (var level in chain) {
			var props = new List<IPropertySymbol>();
			foreach (var p in level.GetMembers().OfType<IPropertySymbol>()) {
				if (p.IsStatic || p.IsIndexer || p.DeclaredAccessibility != Accessibility.Public) continue;
				if (p.GetMethod is null || p.GetMethod.DeclaredAccessibility != Accessibility.Public) continue;
				// RuleFor(x => x.P) is an expression tree with P as a generic argument: by-ref returns,
				// ref structs (Span<T>) and pointers can't be used there.
				if (p.ReturnsByRef || p.ReturnsByRefReadonly || p.Type.IsRefLikeType ||
				    p.Type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer) continue;
				if (seen.Add(p.Name)) props.Add(p);
			}
			perLevel.Add(props);
		}
		for (int i = perLevel.Count - 1; i >= 0; i--) result.AddRange(perLevel[i]);
		return result;
	}

	public static IEnumerable<ISymbol> GetMembersIncludingBase(INamedTypeSymbol type, string name) {
		for (var cur = type; cur is not null; cur = cur.BaseType)
			foreach (var m in cur.GetMembers(name))
				yield return m;
	}

	public static ITypeSymbol UnwrapNullable(ITypeSymbol type) =>
		type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n ? n.TypeArguments[0] : type;

	public static bool IsNullableValueType(ITypeSymbol type) =>
		type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

	public static bool IsNumeric(ITypeSymbol type) => type.SpecialType switch {
		SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16 or
		SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 or
		SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal => true,
		_ => false
	};

	public static bool IsString(ITypeSymbol type) => type.SpecialType == SpecialType.System_String;

	/// <summary>Element type of an array or <c>IEnumerable&lt;T&gt;</c> (strings excluded).</summary>
	public static ITypeSymbol? GetElementType(ITypeSymbol type) {
		if (IsString(type)) return null;
		if (type is IArrayTypeSymbol array) return array.ElementType;
		if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T } self)
			return self.TypeArguments[0];
		return type.AllInterfaces
			.FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
			?.TypeArguments[0];
	}

	public static bool IsCollection(ITypeSymbol type) =>
		!IsString(type) && (type is IArrayTypeSymbol || GetElementType(type) is not null ||
		                    type.SpecialType == SpecialType.System_Collections_IEnumerable ||
		                    type.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_Collections_IEnumerable));

	/// <summary>C# expression that counts the items of a collection-typed variable <paramref name="variable"/>.</summary>
	public static string CountExpression(ITypeSymbol type, string variable) {
		if (type is IArrayTypeSymbol) return variable + ".Length";
		bool hasCount = type is INamedTypeSymbol named &&
			(named.TypeKind == TypeKind.Interface
				? new[] { named }.Concat(named.AllInterfaces).SelectMany(i => i.GetMembers("Count"))
				: GetMembersIncludingBase(named, "Count"))
			.OfType<IPropertySymbol>().Any(p => !p.IsStatic && p.Type.SpecialType == SpecialType.System_Int32 && p.DeclaredAccessibility == Accessibility.Public);
		if (hasCount) return variable + ".Count";
		if (GetElementType(type) is not null) return "global::System.Linq.Enumerable.Count(" + variable + ")";
		// Non-generic IEnumerable: Enumerable.Count needs an IEnumerable<T>.
		return "global::System.Linq.Enumerable.Count(global::System.Linq.Enumerable.Cast<object>(" + variable + "))";
	}

	public static bool SameType(ITypeSymbol a, ITypeSymbol b) => SymbolEqualityComparer.Default.Equals(a, b);

	public static string Identifier(string name) =>
		SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

	public static string StringLiteral(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

	public static string BoolLiteral(bool value) => value ? "true" : "false";

	/// <summary>
	/// Formats an int/long/double attribute constant as a literal cast to <paramref name="target"/>
	/// (e.g. <c>(decimal)0.5D</c>). Returns <c>null</c> and sets <paramref name="error"/> when the constant can't be
	/// represented in the target type, which would otherwise be a compile error (overflow) or silently change the
	/// value (truncating <c>0.5</c> to <c>0</c> for an <c>int</c>).
	/// </summary>
	public static string? NumericLiteral(object? value, ITypeSymbol target, out string? error) {
		error = null;
		double asDouble;
		string literal;
		bool integral;
		switch (value) {
			case int i: asDouble = i; literal = i.ToString(CultureInfo.InvariantCulture); integral = true; break;
			case long l: asDouble = l; literal = l.ToString(CultureInfo.InvariantCulture) + "L"; integral = true; break;
			case double d: asDouble = d; literal = d.ToString("R", CultureInfo.InvariantCulture) + "D"; integral = false; break;
			case float f: asDouble = f; literal = f.ToString("R", CultureInfo.InvariantCulture) + "F"; integral = false; break;
			default:
				error = "'" + (value ?? "null") + "' is not a numeric constant";
				return null;
		}
		if (double.IsNaN(asDouble) || double.IsInfinity(asDouble)) {
			error = "'" + FormatValue(value) + "' is not a finite number";
			return null;
		}

		bool isIntegralTarget = IsIntegral(target.SpecialType);
		if (isIntegralTarget && !integral && Math.Floor(asDouble) != asDouble) {
			error = FormatValue(value) + " is not a whole number, so it can't be compared with '" + target.ToDisplayString() + "' without truncation";
			return null;
		}
		if (!FitsIn(value!, target.SpecialType)) {
			error = FormatValue(value) + " is outside the range of '" + target.ToDisplayString() + "'";
			return null;
		}

		if (literal.StartsWith("-", StringComparison.Ordinal)) literal = "(" + literal + ")";
		return "(" + target.FullName() + ")" + literal;
	}

	/// <summary>Numeric value of an int/long/double constant (for ordering checks such as <c>from &lt;= to</c>).</summary>
	public static double ToDouble(object? value) => value switch {
		int i => i, long l => l, double d => d, float f => f, _ => double.NaN
	};

	private static bool IsIntegral(SpecialType type) => type is
		SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16 or
		SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64;

	/// <summary>Whether a finite int/long/double constant converts to <paramref name="target"/> without overflow.</summary>
	private static bool FitsIn(object value, SpecialType target) {
		switch (target) {
			case SpecialType.System_Double: return true;
			case SpecialType.System_Single: return Math.Abs(ToDouble(value)) <= float.MaxValue;
		}
		decimal exact;
		switch (value) {
			case int i: exact = i; break;
			case long l: exact = l; break;
			default:
				var d = ToDouble(value);
				if (Math.Abs(d) >= 7.9e28) return false; // beyond decimal, and so beyond every integral type
				exact = (decimal)d;
				break;
		}
		return target switch {
			SpecialType.System_SByte => exact >= sbyte.MinValue && exact <= sbyte.MaxValue,
			SpecialType.System_Byte => exact >= byte.MinValue && exact <= byte.MaxValue,
			SpecialType.System_Int16 => exact >= short.MinValue && exact <= short.MaxValue,
			SpecialType.System_UInt16 => exact >= ushort.MinValue && exact <= ushort.MaxValue,
			SpecialType.System_Int32 => exact >= int.MinValue && exact <= int.MaxValue,
			SpecialType.System_UInt32 => exact >= uint.MinValue && exact <= uint.MaxValue,
			SpecialType.System_Int64 => exact >= long.MinValue && exact <= long.MaxValue,
			SpecialType.System_UInt64 => exact >= ulong.MinValue && exact <= ulong.MaxValue,
			_ => true, // decimal: already bounded above
		};
	}

	private static string FormatValue(object? value) => value switch {
		double d => d.ToString("R", CultureInfo.InvariantCulture),
		float f => f.ToString("R", CultureInfo.InvariantCulture),
		_ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null"
	};

	/// <summary>
	/// Converts a DataAnnotations <c>ErrorMessage</c> (a composite format string: <c>{0}</c> is the display name,
	/// <c>{1}</c>, <c>{2}</c>... are attribute arguments) into a FluentValidation message. <c>{0}</c> becomes
	/// <c>{PropertyName}</c>, the others are replaced by <paramref name="args"/>, and <c>{{</c>/<c>}}</c> are unescaped.
	/// </summary>
	public static string TranslateDataAnnotationsMessage(string message, IReadOnlyList<string> args) {
		var sb = new StringBuilder(message.Length + 16);
		for (int i = 0; i < message.Length; i++) {
			char c = message[i];
			bool doubled = i + 1 < message.Length && message[i + 1] == c;
			if ((c == '{' || c == '}') && doubled) { sb.Append(c); i++; continue; }
			if (c == '{') {
				int end = message.IndexOf('}', i + 1);
				if (end > i + 1 && int.TryParse(message.Substring(i + 1, end - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) {
					if (n == 0) { sb.Append("{PropertyName}"); i = end; continue; }
					if (n - 1 < args.Count) { sb.Append(args[n - 1]); i = end; continue; }
				}
			}
			sb.Append(c);
		}
		return sb.ToString();
	}

	public static bool IsValidIdentifier(string name) =>
		SyntaxFacts.IsValidIdentifier(name) && SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None;

	public static string SanitizeNamespace(string? assemblyName) {
		if (string.IsNullOrWhiteSpace(assemblyName)) return "AutoFluentValidation.Generated";
		var parts = assemblyName!.Split('.').Where(p => p.Length > 0).Select(p => {
			var sb = new StringBuilder();
			foreach (var c in p) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
			if (char.IsDigit(sb[0])) sb.Insert(0, '_');
			var s = sb.ToString();
			return SyntaxFacts.GetKeywordKind(s) != SyntaxKind.None ? "@" + s : s;
		});
		return string.Join(".", parts);
	}
}
