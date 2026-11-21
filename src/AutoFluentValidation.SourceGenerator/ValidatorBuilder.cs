using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static AutoFluentValidation.SourceGenerator.SymbolHelpers;

namespace AutoFluentValidation.SourceGenerator;

/// <summary>An <c>[AutoValidate]</c> model that passed the model-level checks and will get a validator.</summary>
internal sealed class ModelCandidate {
	public ModelCandidate(INamedTypeSymbol model, AttributeData attribute) {
		Model = model;
		Attribute = attribute;
		ValidatorFullName = SymbolHelpers.ValidatorFullName(model, attribute);
	}

	public INamedTypeSymbol Model { get; }
	public AttributeData Attribute { get; }
	public string ValidatorFullName { get; }
	public Location? Location => AttributeLocation(Attribute, Model);
}

/// <summary>
/// Turns one <c>[AutoValidate]</c> model symbol into a <see cref="ValidatorSpec"/>: one
/// <c>RuleFor</c>/<c>RuleForEach</c> statement per property, plus diagnostics for anything it could not translate.
/// </summary>
/// <remarks>
/// Rules that are reported as diagnostics are skipped, so the generated code always compiles and never throws
/// from its constructor: arguments that FluentValidation would reject at runtime (e.g. <c>min &gt; max</c>,
/// an invalid regex) are caught here instead.
/// </remarks>
internal sealed class ValidatorBuilder {
	private const string AfvPrefix = "AutoFluentValidation.";
	private const string DataAnnotationsPrefix = "System.ComponentModel.DataAnnotations.";
	private const string ApplyToCurrent = "global::FluentValidation.ApplyConditionTo.CurrentValidator";

	private readonly Compilation _compilation;
	private readonly INamedTypeSymbol _model;
	private readonly INamedTypeSymbol? _validator; // the user's half of the partial validator, if any
	private readonly string _validatorFullName;
	private readonly IReadOnlyDictionary<INamedTypeSymbol, string> _generatedValidators;
	private readonly List<Diagnostic> _diagnostics;

	private readonly bool _useDataAnnotations;
	private readonly bool _nullabilityConvention;
	private readonly bool _enumConvention;
	private readonly bool _nestedValidation;

	private ValidatorBuilder(ModelCandidate candidate, Compilation compilation,
		IReadOnlyDictionary<INamedTypeSymbol, string> generatedValidators, List<Diagnostic> diagnostics) {
		_compilation = compilation;
		_model = candidate.Model;
		_validatorFullName = candidate.ValidatorFullName;
		_validator = compilation.Assembly.GetTypeByMetadataName(ValidatorMetadataName(_model, candidate.Attribute));
		_generatedValidators = generatedValidators;
		_diagnostics = diagnostics;
		var attr = candidate.Attribute;
		_useDataAnnotations = BoolArg(attr, "UseDataAnnotations", true);
		_nullabilityConvention = BoolArg(attr, "NullabilityConvention", true);
		_enumConvention = BoolArg(attr, "EnumConvention", true);
		_nestedValidation = BoolArg(attr, "NestedValidation", true);
	}

	// ---------------------------------------------------------------- model-level checks

	/// <summary>
	/// Checks that a validator can be generated for <paramref name="model"/> at all. Reports a diagnostic and
	/// returns <c>false</c> otherwise. Name collisions between models are checked separately by the caller.
	/// </summary>
	public static bool CheckModel(INamedTypeSymbol model, AttributeData attr, Compilation compilation, ICollection<Diagnostic> diagnostics) {
		var location = AttributeLocation(attr, model);
		var display = model.ToDisplayString();

		for (INamedTypeSymbol? cur = model; cur is not null; cur = cur.ContainingType) {
			if (cur.IsGenericType) {
				diagnostics.Add(Diagnostic.Create(DiagnosticDescriptors.GenericModelNotSupported, location, display));
				return false;
			}
			if (cur.IsFileLocal || cur.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal)) {
				diagnostics.Add(Diagnostic.Create(DiagnosticDescriptors.InaccessibleModel, location, display));
				return false;
			}
		}

		string? reason = null;
		var name = ValidatorName(model, attr);
		if (model.IsStatic)
			reason = "static classes can't be validated";
		else if (model.IsRefLikeType)
			reason = "a ref struct can't be the type argument of AbstractValidator<T>";
		else if (!IsValidIdentifier(name))
			reason = "ValidatorName '" + name + "' is not a valid C# identifier";
		else if (compilation.Assembly.GetTypeByMetadataName(ValidatorMetadataName(model, attr)) is { } existing) {
			if (SameType(existing, model))
				reason = "the validator name '" + name + "' is the model's own name; set ValidatorName to a different name";
			else if (existing.TypeKind != TypeKind.Class || existing.IsStatic)
				reason = "'" + existing.ToDisplayString() + "' already exists and is not a non-static class";
			else if (!IsPartial(existing))
				reason = "'" + existing.ToDisplayString() + "' already exists and is not declared partial; add the 'partial' modifier, or set ValidatorName";
		}

		if (reason is null) return true;
		diagnostics.Add(Diagnostic.Create(DiagnosticDescriptors.ValidatorNotGenerated, location, display, reason));
		return false;
	}

	private static bool DeclaresAccessibility(INamedTypeSymbol type) =>
		type.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax t && t.Modifiers.Any(m =>
			m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.InternalKeyword) ||
			m.IsKind(SyntaxKind.PrivateKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword)));

	private static bool IsPartial(INamedTypeSymbol type) =>
		type.DeclaringSyntaxReferences.All(r => r.GetSyntax() is TypeDeclarationSyntax t && t.Modifiers.Any(SyntaxKind.PartialKeyword));

	// ---------------------------------------------------------------- build

	/// <param name="generatedValidators">
	/// Validator full names for every model of this compilation that gets a validator; nested validation only
	/// references validators that will actually exist.
	/// </param>
	public static ValidatorSpec Build(ModelCandidate candidate, Compilation compilation,
		IReadOnlyDictionary<INamedTypeSymbol, string> generatedValidators, List<Diagnostic> diagnostics) {
		var builder = new ValidatorBuilder(candidate, compilation, generatedValidators, diagnostics);
		var statements = new List<string>();
		foreach (var property in GetValidatableProperties(candidate.Model))
			builder.BuildProperty(property, statements);

		// If the user's half of the validator states its accessibility, that applies to the whole class and the
		// generated part must not contradict it (CS0262). Otherwise follow the model: public if it is visible from
		// outside the assembly, else internal (the default when no part declares any).
		bool isPublic;
		if (builder._validator is { } userHalf && DeclaresAccessibility(userHalf))
			isPublic = false;
		else {
			isPublic = true;
			for (INamedTypeSymbol? cur = candidate.Model; cur is not null; cur = cur.ContainingType)
				isPublic &= cur.DeclaredAccessibility == Accessibility.Public;
		}

		return new ValidatorSpec(
			Namespace(candidate.Model),
			ValidatorName(candidate.Model, candidate.Attribute),
			candidate.Model.FullName(),
			isPublic,
			BoolArg(candidate.Attribute, "StopOnFirstFailure", false),
			new EquatableArray<string>(statements.ToImmutableArray()));
	}

	// ---------------------------------------------------------------- per property

	private void BuildProperty(IPropertySymbol property, List<string> statements) {
		// Attributes whose arguments don't bind (e.g. while the user is still typing) are left to the compiler.
		var attributes = GetEffectiveAttributes(property)
			.Where(IsUsable)
			.Select(a => (Attr: a, Name: a.AttributeClass!.ToDisplayString()))
			.ToList();
		if (attributes.Any(a => a.Name == AfvPrefix + "SkipValidationAttribute")) return;

		var ctx = new PropertyContext(property);
		var components = new List<string>();

		// An unconditional null/empty check makes the nullability convention redundant. A conditional one
		// ([NotEmpty(When = ...)]) doesn't: the property must still not be null when the condition is false.
		bool explicitNullCheck = attributes.Any(a => IsUnconditional(a.Attr) && (
			a.Name is AfvPrefix + "NotNullAttribute" or AfvPrefix + "NotEmptyAttribute" ||
			_useDataAnnotations && a.Name == DataAnnotationsPrefix + "RequiredAttribute"));
		bool explicitEnumCheck = attributes.Any(a => a.Name == AfvPrefix + "IsInEnumAttribute");

		// Convention: non-nullable reference type in a nullable-enabled context => NotNull.
		if (_nullabilityConvention && !explicitNullCheck && property.Type.IsReferenceType &&
		    property.NullableAnnotation == NullableAnnotation.NotAnnotated)
			components.Add(".NotNull()");

		foreach (var (attr, name) in attributes) {
			List<string>? cores = null;
			string options = "";
			if (name.StartsWith(AfvPrefix, StringComparison.Ordinal)) {
				cores = TranslateRuleAttribute(attr, name.Substring(AfvPrefix.Length), ctx);
				if (cores is { Count: > 0 }) options = RuleOptions(attr, ctx);
			}
			else if (_useDataAnnotations && name.StartsWith(DataAnnotationsPrefix, StringComparison.Ordinal)) {
				var shortName = name.Substring(DataAnnotationsPrefix.Length);
				cores = TranslateDataAnnotation(attr, shortName, ctx);
				if (cores is { Count: > 0 }) options = DataAnnotationOptions(attr, shortName, ctx);
			}
			if (cores is null) continue;
			foreach (var core in cores) components.Add(core + options);
		}

		// Convention: enums must hold a defined value.
		if (_enumConvention && !explicitEnumCheck && ctx.Underlying.TypeKind == TypeKind.Enum)
			components.Add(".IsInEnum()");

		// Convention: nested [AutoValidate] types use their generated validator. The validator is fetched
		// lazily (Func overload) so self-referencing models don't recurse during construction.
		string? eachStatement = null;
		if (_nestedValidation) {
			if (!IsNullableValueType(property.Type) && property.Type is INamedTypeSymbol nested &&
			    NestedValidator(nested) is { } nestedValidator) {
				components.Add(".SetValidator(_ => " + nestedValidator + ".Shared)");
			}
			else if (GetElementType(property.Type) is INamedTypeSymbol element && !IsNullableValueType(element) &&
			         NestedValidator(element) is { } elementValidator) {
				eachStatement = "RuleForEach(x => x." + ctx.Name + ")\n.SetValidator(_ => " + elementValidator + ".Shared);";
			}
		}

		if (components.Count > 0)
			statements.Add("RuleFor(x => x." + ctx.Name + ")\n" + string.Join("\n", components) + ";");
		if (eachStatement is not null)
			statements.Add(eachStatement);
	}

	/// <summary>
	/// The generated validator for an <c>[AutoValidate]</c> type, if one exists and can be used from here: for a model
	/// in this compilation, only if a validator is generated for it; for a model in a referenced assembly, only if
	/// its validator (with a <c>Shared</c> instance) is present there and accessible.
	/// </summary>
	private string? NestedValidator(INamedTypeSymbol type) {
		if (GetAutoValidateAttribute(type) is not { } attr) return null;
		if (SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, _compilation.Assembly))
			return _generatedValidators.TryGetValue(type, out var name) ? name : null;

		var validator = type.ContainingAssembly?.GetTypeByMetadataName(ValidatorMetadataName(type, attr));
		if (validator is null || !_compilation.IsSymbolAccessibleWithin(validator, _compilation.Assembly)) return null;
		var shared = validator.GetMembers("Shared").OfType<IPropertySymbol>().FirstOrDefault(p => p.IsStatic);
		if (shared is null || !_compilation.IsSymbolAccessibleWithin(shared, _compilation.Assembly)) return null;
		return validator.FullName();
	}

	private sealed class PropertyContext {
		public PropertyContext(IPropertySymbol property) {
			Property = property;
			Type = property.Type;
			Underlying = UnwrapNullable(property.Type);
			Name = Identifier(property.Name);
		}
		public IPropertySymbol Property { get; }
		public ITypeSymbol Type { get; }
		/// <summary>The property type with <c>Nullable&lt;T&gt;</c> removed.</summary>
		public ITypeSymbol Underlying { get; }
		public string Name { get; }
	}

	// ---------------------------------------------------------------- AutoFluentValidation attributes

	private List<string>? TranslateRuleAttribute(AttributeData attr, string shortName, PropertyContext ctx) {
		var args = attr.ConstructorArguments;
		string display = StripAttributeSuffix(shortName);

		switch (shortName) {
			case "NotNullAttribute": return One(".NotNull()");
			case "NotEmptyAttribute": return One(".NotEmpty()");

			case "MinimumLengthAttribute":
				return RequireString(attr, display, ctx) && CheckLengths(attr, display, ctx, Int(args[0]), null)
					? One($".MinimumLength({Int(args[0])})") : null;
			case "MaximumLengthAttribute":
				return RequireString(attr, display, ctx) && CheckLengths(attr, display, ctx, null, Int(args[0]))
					? One($".MaximumLength({Int(args[0])})") : null;
			case "LengthBetweenAttribute":
				return RequireString(attr, display, ctx) && CheckLengths(attr, display, ctx, Int(args[0]), Int(args[1]))
					? One($".Length({Int(args[0])}, {Int(args[1])})") : null;
			case "ExactLengthAttribute":
				return RequireString(attr, display, ctx) && CheckLengths(attr, display, ctx, Int(args[0]), null)
					? One($".Length({Int(args[0])})") : null;
			case "EmailAttribute":
				return RequireString(attr, display, ctx) ? One(".EmailAddress()") : null;
			case "CreditCardNumberAttribute":
				return RequireString(attr, display, ctx) ? One(".CreditCard()") : null;
			case "MatchesAttribute": {
				if (!RequireString(attr, display, ctx)) return null;
				var pattern = args[0].Value as string;
				return CheckRegex(attr, display, ctx, pattern) ? One($".Matches({StringLiteral(pattern!)})") : null;
			}

			case "GreaterThanAttribute":
			case "GreaterThanOrEqualToAttribute":
			case "LessThanAttribute":
			case "LessThanOrEqualToAttribute": {
				if (!RequireNumeric(attr, display, ctx)) return null;
				var value = Numeric(attr, display, ctx, args[0].Value);
				return value is null ? null : One($".{display}({value})");
			}

			case "InclusiveBetweenAttribute":
			case "ExclusiveBetweenAttribute": {
				if (!RequireNumeric(attr, display, ctx)) return null;
				var bounds = Bounds(attr, display, ctx, args[0].Value, args[1].Value);
				return bounds is null ? null : One($".{display}({bounds.Value.From}, {bounds.Value.To})");
			}

			case "PrecisionScaleAttribute": {
				if (ctx.Underlying.SpecialType != SpecialType.System_Decimal) { NotApplicable(attr, display, ctx); return null; }
				int precision = Int(args[0]), scale = Int(args[1]);
				// FluentValidation throws from the validator's constructor for these.
				string? problem = precision <= 0 ? "precision must be greater than 0"
					: scale < 0 ? "scale must not be negative"
					: scale > precision ? $"scale ({scale}) must not be greater than precision ({precision})"
					: null;
				if (problem is not null) { Invalid(attr, display, ctx, problem); return null; }
				return One($".PrecisionScale({precision}, {scale}, {BoolLiteral(args[2].Value is true)})");
			}

			case "IsInEnumAttribute":
				if (ctx.Underlying.TypeKind != TypeKind.Enum) { NotApplicable(attr, display, ctx); return null; }
				return One(".IsInEnum()");

			case "IsEnumNameAttribute":
				if (!RequireString(attr, display, ctx)) return null;
				if (args[0].Value is not ITypeSymbol { TypeKind: TypeKind.Enum } enumType) {
					Invalid(attr, display, ctx, "the type must be an enum");
					return null;
				}
				return One($".IsEnumName(typeof({enumType.FullName()}), {BoolLiteral(args[1].Value is true)})");

			case "EqualToAttribute":
			case "NotEqualToAttribute": {
				var other = ResolveSiblingProperty(attr, display, ctx, args[0].Value as string);
				if (other is null) return null;
				return One($".{(shortName == "EqualToAttribute" ? "Equal" : "NotEqual")}(x => x.{Identifier(other.Name)})");
			}

			case "MustAttribute": {
				var must = ResolveMust(attr, ctx, args[0].Value as string ?? "");
				return must is null ? null : One(must);
			}

			case "ValidateWithAttribute":
				return ResolveValidateWith(attr, ctx, args[0].Value as INamedTypeSymbol);

			default:
				return null; // AutoValidate / SkipValidation / unknown — not a rule
		}
	}

	private string RuleOptions(AttributeData attr, PropertyContext ctx) {
		var sb = new StringBuilder();
		if (StringArg(attr, "Message") is { } message) sb.Append(".WithMessage(").Append(StringLiteral(message)).Append(')');
		if (StringArg(attr, "ErrorCode") is { } code) sb.Append(".WithErrorCode(").Append(StringLiteral(code)).Append(')');
		if (NamedArg(attr, "Severity") is { Value: int severity } && severity != 0) {
			// FluentValidation.Severity: Error = 0, Warning = 1, Info = 2.
			// An undefined value (e.g. (Severity)7) keeps FluentValidation's default, Error.
			var severityName = severity switch { 1 => "Warning", 2 => "Info", _ => null };
			if (severityName is not null)
				sb.Append(".WithSeverity(global::FluentValidation.Severity.").Append(severityName).Append(')');
		}
		if (StringArg(attr, "When") is { } when && ResolveCondition(attr, ctx, when, "When") is { } whenExpr)
			sb.Append(".When(x => ").Append(whenExpr).Append(", ").Append(ApplyToCurrent).Append(')');
		if (StringArg(attr, "Unless") is { } unless && ResolveCondition(attr, ctx, unless, "Unless") is { } unlessExpr)
			sb.Append(".Unless(x => ").Append(unlessExpr).Append(", ").Append(ApplyToCurrent).Append(')');
		return sb.ToString();
	}

	private string? ResolveCondition(AttributeData attr, PropertyContext ctx, string memberName, string optionName) {
		foreach (var member in GetMembersIncludingBase(_model, memberName)) {
			if (!IsAccessibleFromValidator(member)) continue;
			if (member is IPropertySymbol { IsStatic: false, IsIndexer: false, Type.SpecialType: SpecialType.System_Boolean, GetMethod: { } getter } p &&
			    IsAccessibleFromValidator(getter))
				return "x." + Identifier(p.Name);
			if (member is IMethodSymbol { IsStatic: false, Parameters.Length: 0, ReturnType.SpecialType: SpecialType.System_Boolean, IsGenericMethod: false, MethodKind: MethodKind.Ordinary } m)
				return "x." + Identifier(m.Name) + "()";
		}
		Report(DiagnosticDescriptors.MemberNotFound, attr, StripAttributeSuffix(attr.AttributeClass!.Name) + "(" + optionName + ")", ctx.Property.Name, memberName,
			"a bool property or parameterless bool method", _model.Name);
		return null;
	}

	private IPropertySymbol? ResolveSiblingProperty(AttributeData attr, string display, PropertyContext ctx, string? otherName) {
		var other = otherName is null ? null : GetValidatableProperties(_model).FirstOrDefault(p => p.Name == otherName);
		if (other is null || !CanCompareWith(other.Type, ctx.Type)) {
			Report(DiagnosticDescriptors.MemberNotFound, attr, display, ctx.Property.Name, otherName ?? "(null)",
				"a public property of type '" + ctx.Type.ToDisplayString() + "'", _model.Name);
			return null;
		}
		return other;
	}

	/// <summary>
	/// <c>Equal(x =&gt; x.Other)</c> compiles when Other converts implicitly to the property type (e.g. <c>int</c> to
	/// <c>int?</c>). For <c>object</c> and delegate-typed properties the lambda itself would also convert to the
	/// property type and the call would bind to the wrong overload, so those require the exact same type.
	/// </summary>
	private bool CanCompareWith(ITypeSymbol other, ITypeSymbol property) {
		if (SameType(other, property)) return true;
		if (property.SpecialType == SpecialType.System_Object || property.TypeKind is TypeKind.Delegate or TypeKind.Dynamic) return false;
		return _compilation.HasImplicitConversion(other, property);
	}

	private string? ResolveMust(AttributeData attr, PropertyContext ctx, string methodName) {
		static bool IsBoolMethod(ISymbol s, out IMethodSymbol method) {
			method = (s as IMethodSymbol)!;
			return s is IMethodSymbol { ReturnType.SpecialType: SpecialType.System_Boolean, IsGenericMethod: false, MethodKind: MethodKind.Ordinary };
		}

		var call = Identifier(methodName);

		// 1) Methods on the user's partial half of the validator (any accessibility: it's the same class).
		if (_validator is not null) {
			foreach (var member in _validator.GetMembers(methodName)) {
				if (!IsBoolMethod(member, out var m)) continue;
				var target = m.IsStatic ? _validatorFullName + "." + call : call;
				if (m.Parameters.Length == 1 && Accepts(m.Parameters[0], ctx.Type))
					return $".Must(v => {target}(v))";
				if (m.Parameters.Length == 2 && Accepts(m.Parameters[0], _model) && Accepts(m.Parameters[1], ctx.Type))
					return $".Must((m, v) => {target}(m, v))";
			}
		}

		// 2) Methods on the model.
		foreach (var member in GetMembersIncludingBase(_model, methodName)) {
			if (!IsBoolMethod(member, out var m) || !IsAccessibleFromValidator(m)) continue;
			if (m.IsStatic) {
				var target = m.ContainingType.FullName() + "." + call;
				if (m.Parameters.Length == 1 && Accepts(m.Parameters[0], ctx.Type))
					return $".Must(v => {target}(v))";
				if (m.Parameters.Length == 2 && Accepts(m.Parameters[0], _model) && Accepts(m.Parameters[1], ctx.Type))
					return $".Must((m, v) => {target}(m, v))";
			}
			else {
				if (m.Parameters.Length == 0) return $".Must((m, v) => m.{call}())";
				if (m.Parameters.Length == 1 && Accepts(m.Parameters[0], ctx.Type))
					return $".Must((m, v) => m.{call}(v))";
			}
		}

		Report(DiagnosticDescriptors.PredicateNotFound, attr, methodName, ctx.Property.Name, ctx.Type.ToDisplayString(), _model.Name);
		return null;
	}

	/// <summary>Whether an argument of <paramref name="argumentType"/> can be passed to <paramref name="parameter"/>.</summary>
	private bool Accepts(IParameterSymbol parameter, ITypeSymbol argumentType) =>
		parameter.RefKind is RefKind.None or RefKind.In &&
		(SameType(parameter.Type, argumentType) || _compilation.HasImplicitConversion(argumentType, parameter.Type));

	private List<string>? ResolveValidateWith(AttributeData attr, PropertyContext ctx, INamedTypeSymbol? validatorType) {
		// IValidator<in T> is contravariant, so a validator for a base type or interface also works.
		var expected = _compilation.GetTypeByMetadataName("FluentValidation.IValidator`1")?.Construct(ctx.Type);
		bool usable = validatorType is { TypeKind: TypeKind.Class, IsAbstract: false, IsStatic: false, IsUnboundGenericType: false } &&
		              expected is not null && _compilation.HasImplicitConversion(validatorType, expected) &&
		              _compilation.IsSymbolAccessibleWithin(validatorType, _compilation.Assembly) &&
		              validatorType.InstanceConstructors.Any(c => c.Parameters.Length == 0 && _compilation.IsSymbolAccessibleWithin(c, _compilation.Assembly));
		if (!usable) {
			Report(DiagnosticDescriptors.InvalidValidatorType, attr, validatorType?.ToDisplayString() ?? "(null)", ctx.Type.ToDisplayString());
			return null;
		}
		return One($".SetValidator(new {validatorType!.FullName()}())");
	}

	// ---------------------------------------------------------------- DataAnnotations

	private List<string>? TranslateDataAnnotation(AttributeData attr, string shortName, PropertyContext ctx) {
		var args = attr.ConstructorArguments;
		string display = StripAttributeSuffix(shortName);

		switch (shortName) {
			case "RequiredAttribute":
				if (IsString(ctx.Type))
					return One(BoolArg(attr, "AllowEmptyStrings", false) ? ".NotNull()" : ".NotEmpty()");
				if (ctx.Type.IsReferenceType || IsNullableValueType(ctx.Type)) return One(".NotNull()");
				return null; // non-nullable value types always have a value

			case "StringLengthAttribute": {
				if (!RequireString(attr, display, ctx)) return null;
				int max = Int(args[0]);
				int min = NamedArg(attr, "MinimumLength") is { Value: int m } ? m : 0;
				if (!CheckLengths(attr, display, ctx, min, max)) return null;
				return One(min > 0 ? $".Length({min}, {max})" : $".MaximumLength({max})");
			}

			case "MinLengthAttribute":
				return args.Length == 0 ? null : LengthOrCount(attr, display, ctx, Int(args[0]), null);
			case "MaxLengthAttribute":
				// [MaxLength] without a value, or -1, means "the maximum the database allows": no rule.
				if (args.Length == 0 || Int(args[0]) == -1) return null;
				if (Int(args[0]) <= 0) { Invalid(attr, display, ctx, "the length must be greater than 0, or -1 for no limit"); return null; }
				return LengthOrCount(attr, display, ctx, null, Int(args[0]));
			case "LengthAttribute": // .NET 8
				return args.Length < 2 ? null : LengthOrCount(attr, display, ctx, Int(args[0]), Int(args[1]));

			case "RangeAttribute": {
				if (args.Length == 3 || args[0].Kind == TypedConstantKind.Type) {
					Report(DiagnosticDescriptors.UnsupportedDataAnnotation, attr, display, ctx.Property.Name,
						"the Range(Type, string, string) form is not supported; use [InclusiveBetween] or a hand-written rule");
					return null;
				}
				if (!RequireNumeric(attr, display, ctx)) return null;
				var bounds = Bounds(attr, display, ctx, args[0].Value, args[1].Value);
				if (bounds is null) return null;
				var (from, to) = bounds.Value;
				bool minExclusive = BoolArg(attr, "MinimumIsExclusive", false);
				bool maxExclusive = BoolArg(attr, "MaximumIsExclusive", false);
				if (minExclusive == maxExclusive)
					return One($".{(minExclusive ? "ExclusiveBetween" : "InclusiveBetween")}({from}, {to})");
				return new List<string> {
					minExclusive ? $".GreaterThan({from})" : $".GreaterThanOrEqualTo({from})",
					maxExclusive ? $".LessThan({to})" : $".LessThanOrEqualTo({to})"
				};
			}

			case "EmailAddressAttribute":
				return RequireString(attr, display, ctx) ? One(".EmailAddress()") : null;
			case "CreditCardAttribute":
				return RequireString(attr, display, ctx) ? One(".CreditCard()") : null;
			case "RegularExpressionAttribute": {
				if (!RequireString(attr, display, ctx)) return null;
				var pattern = args[0].Value as string;
				if (!CheckRegex(attr, display, ctx, pattern)) return null;
				// DataAnnotations semantics: the whole value must match (FluentValidation's Matches is a search, and
				// '$' would also accept a trailing newline), and null or empty values are valid.
				return One($".Matches({StringLiteral(@"\A(?:" + pattern + @")\z")})" +
				           $".When(x => !string.IsNullOrEmpty(x.{ctx.Name}), {ApplyToCurrent})");
			}

			case "CompareAttribute": {
				var other = ResolveSiblingProperty(attr, display, ctx, args[0].Value as string);
				return other is null ? null : One($".Equal(x => x.{Identifier(other.Name)})");
			}

			default:
				return null; // Display, DataType, Key, etc. are not validation rules
		}
	}

	/// <summary><c>ErrorMessage</c> becomes <c>WithMessage</c>, with its <c>{0}</c>, <c>{1}</c>... placeholders mapped.</summary>
	private string DataAnnotationOptions(AttributeData attr, string shortName, PropertyContext ctx) {
		if (NamedArg(attr, "ErrorMessageResourceType") is { Value: not null } || StringArg(attr, "ErrorMessageResourceName") is not null) {
			Report(DiagnosticDescriptors.UnsupportedDataAnnotation, attr, StripAttributeSuffix(shortName), ctx.Property.Name,
				"ErrorMessageResourceType/ErrorMessageResourceName are not supported, so FluentValidation's default message is used; " +
				"set a localized message in ConfigureRules() instead");
			return "";
		}
		if (StringArg(attr, "ErrorMessage") is not { } message) return "";
		var translated = TranslateDataAnnotationsMessage(message, DataAnnotationMessageArgs(attr, shortName));
		return ".WithMessage(" + StringLiteral(translated) + ")";
	}

	/// <summary>The values DataAnnotations passes as <c>{1}</c>, <c>{2}</c>... when formatting each attribute's message.</summary>
	private static string[] DataAnnotationMessageArgs(AttributeData attr, string shortName) {
		var args = attr.ConstructorArguments;
		string Arg(int i) => i < args.Length ? FormatConstant(args[i].Value) : "";
		return shortName switch {
			"StringLengthAttribute" => new[] { Arg(0), NamedArg(attr, "MinimumLength") is { Value: int m } ? m.ToString(CultureInfo.InvariantCulture) : "0" },
			"MinLengthAttribute" or "MaxLengthAttribute" or "RegularExpressionAttribute" or "CompareAttribute" => new[] { Arg(0) },
			"LengthAttribute" or "RangeAttribute" => new[] { Arg(0), Arg(1) },
			_ => Array.Empty<string>()
		};
	}

	private static string FormatConstant(object? value) => value switch {
		double d => d.ToString(CultureInfo.InvariantCulture),
		float f => f.ToString(CultureInfo.InvariantCulture),
		_ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
	};

	private List<string>? LengthOrCount(AttributeData attr, string display, PropertyContext ctx, int? min, int? max) {
		if (!IsString(ctx.Type) && !IsCollection(ctx.Type)) {
			NotApplicable(attr, display, ctx);
			return null;
		}
		if (!CheckLengths(attr, display, ctx, min, max)) return null;
		if (IsString(ctx.Type)) {
			if (min is not null && max is not null) return One($".Length({min}, {max})");
			return One(min is not null ? $".MinimumLength({min})" : $".MaximumLength({max})");
		}
		var count = CountExpression(ctx.Type, "v");
		if (min is not null && max is not null)
			return One($".Must(v => v == null || ({count} >= {min} && {count} <= {max})).WithMessage(\"'{{PropertyName}}' must contain between {min} and {max} items.\")");
		if (min is not null)
			return One($".Must(v => v == null || {count} >= {min}).WithMessage(\"'{{PropertyName}}' must contain at least {min} items.\")");
		return One($".Must(v => v == null || {count} <= {max}).WithMessage(\"'{{PropertyName}}' must contain at most {max} items.\")");
	}

	// ---------------------------------------------------------------- argument checks

	private bool CheckLengths(AttributeData attr, string display, PropertyContext ctx, int? min, int? max) {
		string? problem = min < 0 ? $"the minimum length ({min}) must not be negative"
			: max < 0 ? $"the maximum length ({max}) must not be negative"
			: min > max ? $"the minimum length ({min}) is greater than the maximum length ({max})"
			: null;
		if (problem is null) return true;
		Invalid(attr, display, ctx, problem);
		return false;
	}

	private bool CheckRegex(AttributeData attr, string display, PropertyContext ctx, string? pattern) {
		if (pattern is null) { Invalid(attr, display, ctx, "the pattern is null"); return false; }
		try {
			// The static API caches parsed patterns, so re-running the generator on every edit stays cheap.
			Regex.IsMatch(string.Empty, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
			return true;
		}
		catch (RegexMatchTimeoutException) {
			return true; // the pattern parsed; only matching was slow
		}
		catch (ArgumentException ex) {
			Invalid(attr, display, ctx, "'" + pattern + "' is not a valid regular expression (" + ex.Message + ")");
			return false;
		}
	}

	private string? Numeric(AttributeData attr, string display, PropertyContext ctx, object? value) {
		var literal = NumericLiteral(value, ctx.Underlying, out var error);
		if (literal is null) Invalid(attr, display, ctx, error!);
		return literal;
	}

	private (string From, string To)? Bounds(AttributeData attr, string display, PropertyContext ctx, object? from, object? to) {
		var fromLiteral = Numeric(attr, display, ctx, from);
		if (fromLiteral is null) return null;
		var toLiteral = Numeric(attr, display, ctx, to);
		if (toLiteral is null) return null;
		if (ToDouble(from) > ToDouble(to)) {
			// FluentValidation's InclusiveBetween/ExclusiveBetween throw from the validator's constructor.
			Invalid(attr, display, ctx, $"the lower bound ({FormatConstant(from)}) is greater than the upper bound ({FormatConstant(to)})");
			return null;
		}
		return (fromLiteral, toLiteral);
	}

	// ---------------------------------------------------------------- helpers

	private static List<string> One(string core) => new() { core };

	private static int Int(TypedConstant constant) => constant.Value is int i ? i : 0;

	private static string StripAttributeSuffix(string name) =>
		name.EndsWith("Attribute", StringComparison.Ordinal) ? name.Substring(0, name.Length - "Attribute".Length) : name;

	/// <summary>Accessible from the generated validator, which lives in the model's assembly but not inside the model.</summary>
	private static bool IsAccessibleFromValidator(ISymbol symbol) =>
		symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal;

	private static bool IsUnconditional(AttributeData attr) =>
		StringArg(attr, "When") is null && StringArg(attr, "Unless") is null;

	/// <summary>The attribute bound without errors, so its arguments can be read safely.</summary>
	private static bool IsUsable(AttributeData attr) =>
		attr.AttributeClass is { TypeKind: not TypeKind.Error } &&
		attr.AttributeConstructor is not null &&
		!attr.ConstructorArguments.Any(HasError) &&
		!attr.NamedArguments.Any(kv => HasError(kv.Value));

	private static bool HasError(TypedConstant constant) =>
		constant.Kind == TypedConstantKind.Error ||
		constant.Kind == TypedConstantKind.Type && constant.Value is IErrorTypeSymbol ||
		constant.Kind == TypedConstantKind.Array && !constant.Values.IsDefault && constant.Values.Any(HasError);

	/// <summary>
	/// The property's attributes plus those on the properties it overrides (all rule attributes are
	/// <c>Inherited = true</c>); an attribute type declared on the override wins over the base declaration.
	/// </summary>
	private static List<AttributeData> GetEffectiveAttributes(IPropertySymbol property) {
		var result = new List<AttributeData>(property.GetAttributes());
		for (var overridden = property.OverriddenProperty; overridden is not null; overridden = overridden.OverriddenProperty) {
			foreach (var attr in overridden.GetAttributes()) {
				if (attr.AttributeClass is null) continue;
				if (!result.Any(r => SymbolEqualityComparer.Default.Equals(r.AttributeClass, attr.AttributeClass)))
					result.Add(attr);
			}
		}
		return result;
	}

	private bool RequireString(AttributeData attr, string display, PropertyContext ctx) {
		if (IsString(ctx.Type)) return true;
		NotApplicable(attr, display, ctx);
		return false;
	}

	private bool RequireNumeric(AttributeData attr, string display, PropertyContext ctx) {
		if (IsNumeric(ctx.Underlying)) return true;
		NotApplicable(attr, display, ctx);
		return false;
	}

	private void NotApplicable(AttributeData attr, string display, PropertyContext ctx) =>
		Report(DiagnosticDescriptors.RuleNotApplicable, attr, display, ctx.Property.Name, ctx.Type.ToDisplayString());

	private void Invalid(AttributeData attr, string display, PropertyContext ctx, string problem) =>
		Report(DiagnosticDescriptors.InvalidRuleArgument, attr, display, ctx.Property.Name, problem);

	private void Report(DiagnosticDescriptor descriptor, AttributeData attr, params object[] args) =>
		_diagnostics.Add(Diagnostic.Create(descriptor, AttributeLocation(attr, _model), args));
}
