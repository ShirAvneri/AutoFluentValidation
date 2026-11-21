using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AutoFluentValidation.SourceGenerator;

/// <summary>
/// Generates a partial <c>AbstractValidator&lt;T&gt;</c> for every type marked with <c>[AutoValidate]</c>,
/// plus an <c>AddAutoValidators()</c> DI extension when Microsoft.Extensions.DependencyInjection is referenced.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class AutoValidatorGenerator : IIncrementalGenerator {
	/// <summary>Step names, for incremental-caching tests.</summary>
	internal static class TrackingNames {
		public const string ModelNames = nameof(ModelNames);
		public const string Analysis = nameof(Analysis);
		public const string Specs = nameof(Specs);
		public const string Diagnostics = nameof(Diagnostics);
		public const string Registration = nameof(Registration);
	}

	internal const string RegistrationHintName = "AutoValidatorServiceCollectionExtensions.g.cs";

	public void Initialize(IncrementalGeneratorInitializationContext context) {
		// Cheap, syntax-driven step: which types carry [AutoValidate]? Yields metadata names (strings, cache-friendly).
		var modelNames = context.SyntaxProvider.ForAttributeWithMetadataName(
				SymbolHelpers.AutoValidateAttributeName,
				static (node, _) => node is TypeDeclarationSyntax,
				static (ctx, _) => SymbolHelpers.MetadataName((INamedTypeSymbol)ctx.TargetSymbol))
			.Collect()
			.WithTrackingName(TrackingNames.ModelNames);

		// Semantic step. Rules can reference other files (nested models, the user's half of the validator,
		// Must predicates), so analysis runs against the whole compilation rather than per syntax node. Its outputs
		// are value-equatable, so validators whose rules didn't change are not re-emitted.
		var analysis = modelNames.Combine(context.CompilationProvider)
			.Select(static (pair, ct) => Analyze(pair.Left, pair.Right, ct))
			.WithTrackingName(TrackingNames.Analysis);

		context.RegisterSourceOutput(analysis.Select(static (a, _) => a.Diagnostics).WithTrackingName(TrackingNames.Diagnostics),
			static (spc, diagnostics) => {
				foreach (var d in diagnostics) spc.ReportDiagnostic(d);
			});

		context.RegisterSourceOutput(analysis.SelectMany(static (a, _) => a.Specs.Items).WithTrackingName(TrackingNames.Specs),
			static (spc, spec) => spc.AddSource(spec.HintName, SourceEmitter.EmitValidator(spec)));

		context.RegisterSourceOutput(analysis.Select(static (a, _) => a.Registration).WithTrackingName(TrackingNames.Registration),
			static (spc, registration) => {
				if (registration is not null)
					spc.AddSource(RegistrationHintName, SourceEmitter.EmitRegistration(registration));
			});
	}

	/// <remarks>
	/// <see cref="Diagnostic"/> has value equality (descriptor, location, arguments), so unchanged diagnostics compare
	/// equal across runs. They keep real source locations so <c>#pragma warning disable</c> works on them.
	/// </remarks>
	private sealed record AnalysisResult(
		EquatableArray<ValidatorSpec> Specs,
		EquatableArray<Diagnostic> Diagnostics,
		RegistrationSpec? Registration);

	private static AnalysisResult Analyze(ImmutableArray<string> metadataNames, Compilation compilation, CancellationToken ct) {
		var diagnostics = new List<Diagnostic>();

		// Pass 1: model-level checks. Only the compilation's own assembly is searched: GetTypeByMetadataName on the
		// compilation returns null when a referenced assembly happens to define a type with the same name.
		var candidates = new List<ModelCandidate>();
		foreach (var metadataName in metadataNames.Distinct().OrderBy(n => n, StringComparer.Ordinal)) {
			ct.ThrowIfCancellationRequested();
			if (compilation.Assembly.GetTypeByMetadataName(metadataName) is not { } model) continue;
			if (SymbolHelpers.GetAutoValidateAttribute(model) is not { } attr) continue;
			if (ValidatorBuilder.CheckModel(model, attr, compilation, diagnostics))
				candidates.Add(new ModelCandidate(model, attr));
		}

		// Two models must not produce the same validator. Hint names are compared case-insensitively by Roslyn
		// (and are mirrored to a possibly case-insensitive file system), so names are too.
		var unique = new List<ModelCandidate>();
		foreach (var group in candidates.GroupBy(c => c.ValidatorFullName, StringComparer.OrdinalIgnoreCase)) {
			if (group.Count() == 1) {
				unique.Add(group.First());
				continue;
			}
			foreach (var c in group) {
				var others = string.Join(", ", group.Where(o => o != c).Select(o => "'" + o.Model.ToDisplayString() + "'"));
				diagnostics.Add(Diagnostic.Create(DiagnosticDescriptors.ValidatorNotGenerated, c.Location, c.Model.ToDisplayString(),
					"its validator name '" + c.ValidatorFullName.Substring("global::".Length) + "' is also used by " + others + "; set ValidatorName to make it unique"));
			}
		}

		// Pass 2: build. A failure in one model must not take down every other validator in the project.
		var generated = unique.ToDictionary<ModelCandidate, INamedTypeSymbol, string>(c => c.Model, c => c.ValidatorFullName, SymbolEqualityComparer.Default);
		var specs = ImmutableArray.CreateBuilder<ValidatorSpec>();
		foreach (var candidate in unique.OrderBy(c => c.ValidatorFullName, StringComparer.Ordinal)) {
			ct.ThrowIfCancellationRequested();
			var modelDiagnostics = new List<Diagnostic>();
			try {
				specs.Add(ValidatorBuilder.Build(candidate, compilation, generated, modelDiagnostics));
				diagnostics.AddRange(modelDiagnostics);
			}
			catch (Exception ex) when (ex is not OperationCanceledException) {
				diagnostics.Add(Diagnostic.Create(DiagnosticDescriptors.InternalError, candidate.Location,
					candidate.Model.ToDisplayString(), ex.GetType().Name + ": " + ex.Message));
			}
		}

		RegistrationSpec? registration = null;
		bool hasDependencyInjection =
			compilation.GetTypeByMetadataName("Microsoft.Extensions.DependencyInjection.IServiceCollection") is not null &&
			compilation.GetTypeByMetadataName("Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions") is not null;
		if (hasDependencyInjection && specs.Count > 0) {
			registration = new RegistrationSpec(
				SymbolHelpers.SanitizeNamespace(compilation.AssemblyName),
				new EquatableArray<RegistrationEntry>(specs.Select(s => new RegistrationEntry(s.ModelFullName, s.ValidatorFullName)).ToImmutableArray()));
		}

		return new AnalysisResult(
			new EquatableArray<ValidatorSpec>(specs.ToImmutable()),
			new EquatableArray<Diagnostic>(diagnostics.ToImmutableArray()),
			registration);
	}
}
