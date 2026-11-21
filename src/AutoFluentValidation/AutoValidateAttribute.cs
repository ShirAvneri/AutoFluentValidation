using System;

namespace AutoFluentValidation;

/// <summary>
/// Marks a class, record or struct for compile-time validator generation.
/// The source generator emits a <c>public partial class {TypeName}Validator : AbstractValidator&lt;TypeName&gt;</c>
/// in the same namespace, with rules derived from validation attributes, DataAnnotations and conventions.
/// </summary>
/// <remarks>
/// The generated validator is <c>partial</c> and calls <c>partial void ConfigureRules()</c> at the end of its
/// constructor, so you can add handwritten rules in your own half of the class.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class AutoValidateAttribute : Attribute {
	/// <summary>Name of the generated validator class. Defaults to <c>{TypeName}Validator</c>.</summary>
	public string? ValidatorName { get; set; }

	/// <summary>
	/// Translate <c>System.ComponentModel.DataAnnotations</c> attributes
	/// (Required, StringLength, MinLength, MaxLength, Length, Range, EmailAddress, RegularExpression, CreditCard, Compare)
	/// into FluentValidation rules. Default: <c>true</c>.
	/// </summary>
	public bool UseDataAnnotations { get; set; } = true;

	/// <summary>
	/// Add <c>NotNull()</c> for reference-type properties declared as non-nullable in a
	/// nullable-enabled context (e.g. <c>string Name</c> but not <c>string? Name</c>). Default: <c>true</c>.
	/// </summary>
	public bool NullabilityConvention { get; set; } = true;

	/// <summary>Add <c>IsInEnum()</c> for enum-typed properties. Default: <c>true</c>.</summary>
	public bool EnumConvention { get; set; } = true;

	/// <summary>
	/// Validate properties whose type (or collection element type) is itself marked with
	/// <see cref="AutoValidateAttribute"/> using that type's generated validator. Default: <c>true</c>.
	/// </summary>
	public bool NestedValidation { get; set; } = true;

	/// <summary>
	/// When <c>true</c>, each property stops at its first failing rule
	/// (<c>RuleLevelCascadeMode = CascadeMode.Stop</c>). Default: <c>false</c>.
	/// </summary>
	public bool StopOnFirstFailure { get; set; }
}

/// <summary>Excludes a property from all generated rules, including conventions.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class SkipValidationAttribute : Attribute { }
