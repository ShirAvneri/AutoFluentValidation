using System;
using FluentValidation;

namespace AutoFluentValidation;

/// <summary>
/// Base class for all AutoFluentValidation rule attributes. Each attribute becomes one validator in
/// the property's <c>RuleFor(...)</c> chain, in declaration order.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public abstract class ValidationRuleAttribute : Attribute {
	/// <summary>Custom error message (<c>WithMessage</c>). FluentValidation placeholders such as <c>{PropertyName}</c> are supported.</summary>
	public string? Message { get; set; }

	/// <summary>Custom error code (<c>WithErrorCode</c>).</summary>
	public string? ErrorCode { get; set; }

	/// <summary>Severity of the failure (<c>WithSeverity</c>). Default: <see cref="FluentValidation.Severity.Error"/>.</summary>
	public Severity Severity { get; set; } = Severity.Error;

	/// <summary>
	/// Name of a <c>bool</c> property, or parameterless <c>bool</c> method, on the model.
	/// The rule only runs when it returns <c>true</c>. Use <c>nameof(...)</c>.
	/// </summary>
	public string? When { get; set; }

	/// <summary>
	/// Name of a <c>bool</c> property, or parameterless <c>bool</c> method, on the model.
	/// The rule only runs when it returns <c>false</c>. Use <c>nameof(...)</c>.
	/// </summary>
	public string? Unless { get; set; }
}

/// <summary><c>NotNull()</c></summary>
public sealed class NotNullAttribute : ValidationRuleAttribute { }

/// <summary><c>NotEmpty()</c> — not null, not empty/whitespace string, not empty collection, not default value.</summary>
public sealed class NotEmptyAttribute : ValidationRuleAttribute { }

/// <summary><c>MinimumLength(n)</c> (strings).</summary>
/// <param name="length">The minimum number of characters. Must not be negative.</param>
public sealed class MinimumLengthAttribute(int length) : ValidationRuleAttribute {
	/// <summary>The minimum number of characters.</summary>
	public int Length { get; } = length;
}

/// <summary><c>MaximumLength(n)</c> (strings).</summary>
/// <param name="length">The maximum number of characters. Must not be negative.</param>
public sealed class MaximumLengthAttribute(int length) : ValidationRuleAttribute {
	/// <summary>The maximum number of characters.</summary>
	public int Length { get; } = length;
}

/// <summary><c>Length(min, max)</c> (strings).</summary>
/// <param name="min">The minimum number of characters. Must not be negative.</param>
/// <param name="max">The maximum number of characters. Must not be less than <paramref name="min"/>.</param>
public sealed class LengthBetweenAttribute(int min, int max) : ValidationRuleAttribute {
	/// <summary>The minimum number of characters.</summary>
	public int Min { get; } = min;
	/// <summary>The maximum number of characters.</summary>
	public int Max { get; } = max;
}

/// <summary><c>Length(n)</c> — exact length (strings).</summary>
/// <param name="length">The required number of characters. Must not be negative.</param>
public sealed class ExactLengthAttribute(int length) : ValidationRuleAttribute {
	/// <summary>The required number of characters.</summary>
	public int Length { get; } = length;
}

/// <summary><c>EmailAddress()</c> (strings).</summary>
public sealed class EmailAttribute : ValidationRuleAttribute { }

/// <summary><c>CreditCard()</c> (strings).</summary>
public sealed class CreditCardNumberAttribute : ValidationRuleAttribute { }

/// <summary>
/// <c>Matches(pattern)</c> (strings). Like FluentValidation, this is a search, not a full match; anchor with <c>^</c>
/// and <c>$</c> if needed. Invalid patterns are reported at compile time.
/// </summary>
/// <param name="pattern">A .NET regular expression.</param>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = true)]
public sealed class MatchesAttribute(string pattern) : ValidationRuleAttribute {
	/// <summary>The regular expression.</summary>
	public string Pattern { get; } = pattern;
}

/// <summary>Base for numeric comparison attributes. The constant is converted to the property's type.</summary>
public abstract class ComparisonRuleAttribute : ValidationRuleAttribute {
	private protected ComparisonRuleAttribute(object value) => Value = value;
	/// <summary>The value to compare against.</summary>
	public object Value { get; }
}

/// <summary><c>GreaterThan(value)</c></summary>
public sealed class GreaterThanAttribute : ComparisonRuleAttribute {
	/// <summary>Creates the rule with a <c>int</c> value, converted to the property type at compile time.</summary>
	/// <param name="value">The value to compare against. It must be representable in the property type.</param>
	public GreaterThanAttribute(int value) : base(value) { }
	/// <summary>Creates the rule with a <c>long</c> value, converted to the property type at compile time.</summary>
	/// <param name="value">The value to compare against. It must be representable in the property type.</param>
	public GreaterThanAttribute(long value) : base(value) { }
	/// <summary>Creates the rule with a <c>double</c> value, converted to the property type at compile time.</summary>
	/// <param name="value">The value to compare against. It must be representable in the property type.</param>
	public GreaterThanAttribute(double value) : base(value) { }
}

/// <summary><c>GreaterThanOrEqualTo(value)</c></summary>
public sealed class GreaterThanOrEqualToAttribute : ComparisonRuleAttribute {
	/// <summary>Creates the rule with a <c>int</c> value, converted to the property type at compile time.</summary>
	/// <param name="value">The value to compare against. It must be representable in the property type.</param>
	public GreaterThanOrEqualToAttribute(int value) : base(value) { }
	/// <summary>Creates the rule with a <c>long</c> value, converted to the property type at compile time.</summary>
	/// <param name="value">The value to compare against. It must be representable in the property type.</param>
	public GreaterThanOrEqualToAttribute(long value) : base(value) { }
	/// <summary>Creates the rule with a <c>double</c> value, converted to the property type at compile time.</summary>
	/// <param name="value">The value to compare against. It must be representable in the property type.</param>
	public GreaterThanOrEqualToAttribute(double value) : base(value) { }
}

/// <summary><c>LessThan(value)</c></summary>
public sealed class LessThanAttribute : ComparisonRuleAttribute {
	/// <summary>Creates the rule with a <c>int</c> value, converted to the property type at compile time.</summary>
	/// <param name="value">The value to compare against. It must be representable in the property type.</param>
	public LessThanAttribute(int value) : base(value) { }
	/// <summary>Creates the rule with a <c>long</c> value, converted to the property type at compile time.</summary>
	/// <param name="value">The value to compare against. It must be representable in the property type.</param>
	public LessThanAttribute(long value) : base(value) { }
	/// <summary>Creates the rule with a <c>double</c> value, converted to the property type at compile time.</summary>
	/// <param name="value">The value to compare against. It must be representable in the property type.</param>
	public LessThanAttribute(double value) : base(value) { }
}

/// <summary><c>LessThanOrEqualTo(value)</c></summary>
public sealed class LessThanOrEqualToAttribute : ComparisonRuleAttribute {
	/// <summary>Creates the rule with a <c>int</c> value, converted to the property type at compile time.</summary>
	/// <param name="value">The value to compare against. It must be representable in the property type.</param>
	public LessThanOrEqualToAttribute(int value) : base(value) { }
	/// <summary>Creates the rule with a <c>long</c> value, converted to the property type at compile time.</summary>
	/// <param name="value">The value to compare against. It must be representable in the property type.</param>
	public LessThanOrEqualToAttribute(long value) : base(value) { }
	/// <summary>Creates the rule with a <c>double</c> value, converted to the property type at compile time.</summary>
	/// <param name="value">The value to compare against. It must be representable in the property type.</param>
	public LessThanOrEqualToAttribute(double value) : base(value) { }
}

/// <summary>Base for range attributes. Bounds are converted to the property's type.</summary>
public abstract class RangeRuleAttribute : ValidationRuleAttribute {
	private protected RangeRuleAttribute(object from, object to) { From = from; To = to; }
	/// <summary>Lower bound.</summary>
	public object From { get; }
	/// <summary>Upper bound.</summary>
	public object To { get; }
}

/// <summary><c>InclusiveBetween(from, to)</c></summary>
public sealed class InclusiveBetweenAttribute : RangeRuleAttribute {
	/// <summary>Creates the rule with <c>int</c> bounds, converted to the property type at compile time.</summary>
	/// <param name="from">The lower bound.</param>
	/// <param name="to">The upper bound. Must not be less than <paramref name="from"/>.</param>
	public InclusiveBetweenAttribute(int from, int to) : base(from, to) { }
	/// <summary>Creates the rule with <c>long</c> bounds, converted to the property type at compile time.</summary>
	/// <param name="from">The lower bound.</param>
	/// <param name="to">The upper bound. Must not be less than <paramref name="from"/>.</param>
	public InclusiveBetweenAttribute(long from, long to) : base(from, to) { }
	/// <summary>Creates the rule with <c>double</c> bounds, converted to the property type at compile time.</summary>
	/// <param name="from">The lower bound.</param>
	/// <param name="to">The upper bound. Must not be less than <paramref name="from"/>.</param>
	public InclusiveBetweenAttribute(double from, double to) : base(from, to) { }
}

/// <summary><c>ExclusiveBetween(from, to)</c></summary>
public sealed class ExclusiveBetweenAttribute : RangeRuleAttribute {
	/// <summary>Creates the rule with <c>int</c> bounds, converted to the property type at compile time.</summary>
	/// <param name="from">The lower bound.</param>
	/// <param name="to">The upper bound. Must not be less than <paramref name="from"/>.</param>
	public ExclusiveBetweenAttribute(int from, int to) : base(from, to) { }
	/// <summary>Creates the rule with <c>long</c> bounds, converted to the property type at compile time.</summary>
	/// <param name="from">The lower bound.</param>
	/// <param name="to">The upper bound. Must not be less than <paramref name="from"/>.</param>
	public ExclusiveBetweenAttribute(long from, long to) : base(from, to) { }
	/// <summary>Creates the rule with <c>double</c> bounds, converted to the property type at compile time.</summary>
	/// <param name="from">The lower bound.</param>
	/// <param name="to">The upper bound. Must not be less than <paramref name="from"/>.</param>
	public ExclusiveBetweenAttribute(double from, double to) : base(from, to) { }
}

/// <summary><c>PrecisionScale(precision, scale, ignoreTrailingZeros)</c> (decimals).</summary>
/// <param name="precision">The maximum total number of digits. Must be greater than 0.</param>
/// <param name="scale">The maximum number of digits after the decimal point. Must be between 0 and <paramref name="precision"/>.</param>
/// <param name="ignoreTrailingZeros">Whether trailing zeros after the decimal point are ignored.</param>
public sealed class PrecisionScaleAttribute(int precision, int scale, bool ignoreTrailingZeros = false) : ValidationRuleAttribute {
	/// <summary>The maximum total number of digits.</summary>
	public int Precision { get; } = precision;
	/// <summary>The maximum number of digits after the decimal point.</summary>
	public int Scale { get; } = scale;
	/// <summary>Whether trailing zeros after the decimal point are ignored.</summary>
	public bool IgnoreTrailingZeros { get; } = ignoreTrailingZeros;
}

/// <summary><c>IsInEnum()</c>. Added automatically for enum properties unless the convention is disabled.</summary>
public sealed class IsInEnumAttribute : ValidationRuleAttribute { }

/// <summary><c>IsEnumName(typeof(TEnum), caseSensitive)</c> (strings).</summary>
/// <param name="enumType">The enum whose member names are allowed.</param>
/// <param name="caseSensitive">Whether names are compared case-sensitively.</param>
public sealed class IsEnumNameAttribute(Type enumType, bool caseSensitive = true) : ValidationRuleAttribute {
	/// <summary>The enum whose member names are allowed.</summary>
	public Type EnumType { get; } = enumType;
	/// <summary>Whether names are compared case-sensitively.</summary>
	public bool CaseSensitive { get; } = caseSensitive;
}

/// <summary><c>Equal(x =&gt; x.Other)</c> — must equal another property of the same model. Use <c>nameof(...)</c>.</summary>
/// <param name="otherProperty">The name of the other property. Use <c>nameof(...)</c>.</param>
public sealed class EqualToAttribute(string otherProperty) : ValidationRuleAttribute {
	/// <summary>The name of the other property.</summary>
	public string OtherProperty { get; } = otherProperty;
}

/// <summary><c>NotEqual(x =&gt; x.Other)</c> — must differ from another property of the same model. Use <c>nameof(...)</c>.</summary>
/// <param name="otherProperty">The name of the other property. Use <c>nameof(...)</c>.</param>
public sealed class NotEqualToAttribute(string otherProperty) : ValidationRuleAttribute {
	/// <summary>The name of the other property.</summary>
	public string OtherProperty { get; } = otherProperty;
}

/// <summary>
/// <c>Must(predicate)</c>. Names a method resolved at compile time, looked up first on the validator
/// (your partial half) and then on the model. Supported shapes:
/// <list type="bullet">
/// <item><c>bool M(TProperty value)</c> — validator instance/static, or model static</item>
/// <item><c>bool M(TModel model, TProperty value)</c> — validator instance/static, or model static</item>
/// <item><c>bool M()</c> or <c>bool M(TProperty value)</c> — model instance method</item>
/// </list>
/// Parameters may be of any type the argument converts to implicitly (e.g. <c>object</c> or a base type).
/// </summary>
/// <param name="predicateMethod">The name of the predicate method. Use <c>nameof(...)</c>.</param>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = true)]
public sealed class MustAttribute(string predicateMethod) : ValidationRuleAttribute {
	/// <summary>The name of the predicate method.</summary>
	public string PredicateMethod { get; } = predicateMethod;
}

/// <summary>
/// <c>SetValidator(new TValidator())</c> — validates the property with an existing (hand-written or generated)
/// validator that has a parameterless constructor.
/// </summary>
/// <param name="validatorType">A non-abstract class implementing <c>IValidator&lt;T&gt;</c> for the property type (or a base type of it), with an accessible parameterless constructor.</param>
public sealed class ValidateWithAttribute(Type validatorType) : ValidationRuleAttribute {
	/// <summary>The validator type.</summary>
	public Type ValidatorType { get; } = validatorType;
}
