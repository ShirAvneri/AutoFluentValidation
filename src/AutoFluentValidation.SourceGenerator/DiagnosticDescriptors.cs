using Microsoft.CodeAnalysis;

namespace AutoFluentValidation.SourceGenerator;

internal static class DiagnosticDescriptors {
	private const string Category = "AutoFluentValidation";
	private const string HelpLink = "https://github.com/ShirAvneri/AutoFluentValidation#diagnostics";

	private static DiagnosticDescriptor Create(string id, string title, string message, DiagnosticSeverity severity) =>
		new(id, title, message, Category, severity, isEnabledByDefault: true, helpLinkUri: HelpLink);

	public static readonly DiagnosticDescriptor GenericModelNotSupported = Create(
		"AFV001", "Generic models are not supported",
		"[AutoValidate] cannot be applied to generic type '{0}'; write the validator by hand",
		DiagnosticSeverity.Error);

	public static readonly DiagnosticDescriptor RuleNotApplicable = Create(
		"AFV002", "Validation attribute does not apply to the property type",
		"[{0}] cannot be applied to property '{1}' of type '{2}'; the rule was skipped",
		DiagnosticSeverity.Warning);

	public static readonly DiagnosticDescriptor MemberNotFound = Create(
		"AFV003", "Referenced member not found",
		"[{0}] on property '{1}' refers to '{2}', which is not {3} on '{4}'",
		DiagnosticSeverity.Error);

	public static readonly DiagnosticDescriptor PredicateNotFound = Create(
		"AFV004", "Must predicate not found",
		"No method '{0}' with a supported signature was found for property '{1}'. Expected on the validator: bool {0}({2}) or bool {0}({3}, {2}); on the model: static versions of those, or instance bool {0}() / bool {0}({2}).",
		DiagnosticSeverity.Error);

	public static readonly DiagnosticDescriptor InvalidValidatorType = Create(
		"AFV005", "Invalid validator type",
		"[ValidateWith] type '{0}' must implement IValidator<{1}> and have a public parameterless constructor",
		DiagnosticSeverity.Error);

	public static readonly DiagnosticDescriptor UnsupportedDataAnnotation = Create(
		"AFV006", "DataAnnotations usage not supported",
		"[{0}] on property '{1}' is not translated: {2}",
		DiagnosticSeverity.Warning);

	public static readonly DiagnosticDescriptor InaccessibleModel = Create(
		"AFV007", "Model is not accessible",
		"[AutoValidate] type '{0}' must be public or internal (including all containing types), and not file-local, so a validator can be generated for it",
		DiagnosticSeverity.Error);

	public static readonly DiagnosticDescriptor InvalidRuleArgument = Create(
		"AFV008", "Invalid rule argument",
		"[{0}] on property '{1}' has an invalid argument: {2}; the rule was skipped",
		DiagnosticSeverity.Error);

	public static readonly DiagnosticDescriptor ValidatorNotGenerated = Create(
		"AFV009", "Validator cannot be generated",
		"No validator was generated for '{0}': {1}",
		DiagnosticSeverity.Error);

	public static readonly DiagnosticDescriptor InternalError = Create(
		"AFV010", "Internal generator error",
		"AutoFluentValidation failed to generate the validator for '{0}': {1}. Please report this issue.",
		DiagnosticSeverity.Error);
}
