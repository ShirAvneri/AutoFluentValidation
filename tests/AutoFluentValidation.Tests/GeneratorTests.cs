using System.Collections.Immutable;
using AutoFluentValidation.SourceGenerator;
using FluentValidation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;

namespace AutoFluentValidation.Tests;

/// <summary>Runs the generator in memory against small snippets to check diagnostics and emitted code.</summary>
public class GeneratorTests {
	private static readonly ImmutableArray<MetadataReference> BaseReferences =
		((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
		.Split(Path.PathSeparator)
		// The test host's TPA list includes app-local dependencies; keep DI out so its presence can be controlled per test.
		.Where(p => !Path.GetFileName(p).StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.OrdinalIgnoreCase))
		.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
		.Concat(new[] {
			MetadataReference.CreateFromFile(typeof(IValidator).Assembly.Location),
			MetadataReference.CreateFromFile(typeof(AutoValidateAttribute).Assembly.Location),
		})
		.ToImmutableArray();

	private sealed record RunResult(
		ImmutableArray<Diagnostic> GeneratorDiagnostics,
		ImmutableArray<Diagnostic> CompilationDiagnostics,
		ImmutableDictionary<string, string> Sources,
		Compilation Output);

	private static CSharpCompilation CreateCompilation(string source, bool withDependencyInjection = false,
		IEnumerable<MetadataReference>? extraReferences = null, string assemblyName = "TestAssembly") {
		var references = withDependencyInjection
			? BaseReferences.Add(MetadataReference.CreateFromFile(typeof(IServiceCollection).Assembly.Location))
			: BaseReferences;
		return CSharpCompilation.Create(assemblyName,
			new[] { CSharpSyntaxTree.ParseText(source, ParseOptions) },
			references.AddRange(extraReferences ?? Enumerable.Empty<MetadataReference>()),
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
	}

	private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

	private static GeneratorDriver CreateDriver() =>
		CSharpGeneratorDriver.Create(
			new[] { new AutoValidatorGenerator().AsSourceGenerator() },
			parseOptions: ParseOptions,
			driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

	private static RunResult Run(string source, bool withDependencyInjection = false, IEnumerable<MetadataReference>? extraReferences = null) =>
		Run(CreateCompilation(source, withDependencyInjection, extraReferences));

	private static RunResult Run(Compilation compilation) {
		var driver = CreateDriver().RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
		var runResult = driver.GetRunResult();
		// A generator exception only surfaces as a CS8785 warning in real builds, and drops every generated file.
		Assert.All(runResult.Results, r => Assert.Null(r.Exception));
		var sources = runResult.Results.SelectMany(r => r.GeneratedSources)
			.ToImmutableDictionary(s => s.HintName, s => s.SourceText.ToString());
		return new RunResult(generatorDiagnostics, output.GetDiagnostics(), sources, output);
	}

	private static void AssertCompilesCleanly(RunResult result) {
		var problems = result.CompilationDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).ToList();
		Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
	}

	private const string Usings = "using AutoFluentValidation; using System.Collections.Generic; using System.ComponentModel.DataAnnotations;\n";

	[Fact]
	public void Generates_expected_rules_and_compiles_without_warnings() {
		var result = Run(Usings + """
			namespace Shop;
			[AutoValidate]
			public class Item {
			    [NotEmpty, MaximumLength(20)] public string Name { get; set; } = "";
			    [GreaterThan(0)] public decimal? Price { get; set; }
			    public Item? Parent { get; set; }
			    public List<Item> Children { get; set; } = new();
			}
			""");

		Assert.Empty(result.GeneratorDiagnostics);
		AssertCompilesCleanly(result);
		var code = result.Sources["Shop.ItemValidator.g.cs"];
		Assert.Contains("public partial class ItemValidator : global::FluentValidation.AbstractValidator<global::Shop.Item>", code);
		Assert.Contains(".NotEmpty()\n                .MaximumLength(20);", code);
		Assert.Contains(".GreaterThan((decimal)0);", code);
		Assert.Contains("RuleFor(x => x.Parent)\n                .SetValidator(_ => global::Shop.ItemValidator.Shared);", code);
		Assert.Contains("RuleForEach(x => x.Children)", code);
		Assert.Contains("partial void ConfigureRules();", code);
	}

	[Fact]
	public void Registration_is_only_generated_when_dependency_injection_is_referenced() {
		const string source = "using AutoFluentValidation; namespace N; [AutoValidate] public class M { [NotEmpty] public string? A { get; set; } }";
		Assert.DoesNotContain("AutoValidatorServiceCollectionExtensions.g.cs", Run(source).Sources.Keys);

		var withDi = Run(source, withDependencyInjection: true);
		AssertCompilesCleanly(withDi);
		var registration = withDi.Sources["AutoValidatorServiceCollectionExtensions.g.cs"];
		Assert.Contains("namespace TestAssembly", registration);
		Assert.Contains("typeof(global::FluentValidation.IValidator<global::N.M>), typeof(global::N.MValidator)", registration);
	}

	[Fact]
	public void Model_in_global_namespace_and_nested_model_are_supported() {
		var result = Run("""
			using AutoFluentValidation;
			[AutoValidate] public class Root { [NotEmpty] public string? A { get; set; } }
			namespace N { public class Outer { [AutoValidate] public class Inner { [NotEmpty] public string? B { get; set; } } } }
			""");
		Assert.Empty(result.GeneratorDiagnostics);
		AssertCompilesCleanly(result);
		Assert.Contains("RootValidator.g.cs", result.Sources.Keys);
		Assert.Contains("N.OuterInnerValidator.g.cs", result.Sources.Keys);
	}

	[Theory]
	[InlineData("[AutoValidate] public class G<T> { }", "AFV001")]
	[InlineData("[AutoValidate] public class M { [Email] public int A { get; set; } }", "AFV002")]
	[InlineData("[AutoValidate] public class M { [GreaterThan(1)] public string? A { get; set; } }", "AFV002")]
	[InlineData("[AutoValidate] public class M { [EqualTo(\"Nope\")] public string? A { get; set; } }", "AFV003")]
	[InlineData("[AutoValidate] public class M { [EqualTo(nameof(B))] public string? A { get; set; } public int B { get; set; } }", "AFV003")]
	[InlineData("[AutoValidate] public class M { [NotEmpty(When = \"Nope\")] public string? A { get; set; } }", "AFV003")]
	[InlineData("[AutoValidate] public class M { [Must(\"Nope\")] public string? A { get; set; } }", "AFV004")]
	[InlineData("[AutoValidate] public class M { [ValidateWith(typeof(string))] public string? A { get; set; } }", "AFV005")]
	[InlineData("[AutoValidate] public class M { [Range(typeof(System.DateTime), \"2000-01-01\", \"2100-01-01\")] public System.DateTime A { get; set; } }", "AFV006")]
	[InlineData("public class O { [AutoValidate] private class P { } }", "AFV007")]
	[InlineData("[AutoValidate] file class F { }", "AFV007")]
	// AFV008: arguments that wouldn't compile, would silently change, or would make FluentValidation throw.
	[InlineData("[AutoValidate] public class M { [LessThan(300)] public byte A { get; set; } }", "AFV008")]
	[InlineData("[AutoValidate] public class M { [GreaterThan(0.5)] public int A { get; set; } }", "AFV008")]
	[InlineData("[AutoValidate] public class M { [GreaterThan(double.NaN)] public double A { get; set; } }", "AFV008")]
	[InlineData("[AutoValidate] public class M { [LessThan(1e30)] public decimal A { get; set; } }", "AFV008")]
	[InlineData("[AutoValidate] public class M { [GreaterThan(-1)] public uint A { get; set; } }", "AFV008")]
	[InlineData("[AutoValidate] public class M { [InclusiveBetween(10, 1)] public int A { get; set; } }", "AFV008")]
	[InlineData("[AutoValidate] public class M { [Range(10, 1)] public int A { get; set; } }", "AFV008")]
	[InlineData("[AutoValidate] public class M { [LengthBetween(5, 2)] public string? A { get; set; } }", "AFV008")]
	[InlineData("[AutoValidate] public class M { [MinimumLength(-1)] public string? A { get; set; } }", "AFV008")]
	[InlineData("[AutoValidate] public class M { [StringLength(5, MinimumLength = 10)] public string? A { get; set; } }", "AFV008")]
	[InlineData("[AutoValidate] public class M { [MaxLength(0)] public string? A { get; set; } }", "AFV008")]
	[InlineData("[AutoValidate] public class M { [PrecisionScale(2, 5)] public decimal A { get; set; } }", "AFV008")]
	[InlineData("[AutoValidate] public class M { [Matches(\"(\")] public string? A { get; set; } }", "AFV008")]
	[InlineData("[AutoValidate] public class M { [Matches(null!)] public string? A { get; set; } }", "AFV008")]
	[InlineData("[AutoValidate] public class M { [RegularExpression(\"[\")] public string? A { get; set; } }", "AFV008")]
	[InlineData("[AutoValidate] public class M { [IsEnumName(typeof(string))] public string? A { get; set; } }", "AFV008")]
	// AFV009: a validator that can't be generated.
	[InlineData("[AutoValidate] public static class S { }", "AFV009")]
	[InlineData("[AutoValidate] public ref struct R { }", "AFV009")]
	[InlineData("[AutoValidate(ValidatorName = \"Not Valid\")] public class M { }", "AFV009")]
	[InlineData("[AutoValidate(ValidatorName = \"class\")] public class M { }", "AFV009")]
	[InlineData("[AutoValidate(ValidatorName = nameof(M))] public class M { }", "AFV009")]
	[InlineData("[AutoValidate] public class M { } public class MValidator { }", "AFV009")]
	[InlineData("[AutoValidate] public class M { } public partial struct MValidator { }", "AFV009")]
	// AFV006: DataAnnotations resource messages.
	[InlineData("[AutoValidate] public class M { [Required(ErrorMessageResourceName = \"X\", ErrorMessageResourceType = typeof(M))] public string? A { get; set; } }", "AFV006")]
	public void Reports_diagnostics(string declaration, string expectedId) {
		var result = Run(Usings + "namespace N;\n" + declaration);
		var diagnostic = Assert.Single(result.GeneratorDiagnostics);
		Assert.Equal(expectedId, diagnostic.Id);
		// Bad rules are skipped, so whatever was generated must still compile.
		Assert.DoesNotContain(result.CompilationDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
	}

	[Fact]
	public void Conventions_can_be_switched_off() {
		var result = Run(Usings + """
			namespace N;
			public enum E { A }
			[AutoValidate] public class Child { [NotEmpty] public string? X { get; set; } }
			[AutoValidate(NullabilityConvention = false, EnumConvention = false, NestedValidation = false, UseDataAnnotations = false)]
			public class M {
			    public string S { get; set; } = "";
			    public E Kind { get; set; }
			    public Child C { get; set; } = new();
			    [Required] public string? R { get; set; }
			}
			""");
		AssertCompilesCleanly(result);
		var code = result.Sources["N.MValidator.g.cs"];
		Assert.DoesNotContain("RuleFor(", code);
	}

	[Fact]
	public void Must_supports_all_documented_shapes() {
		var result = Run(Usings + """
			namespace N;
			[AutoValidate]
			public class M {
			    [Must(nameof(ModelStatic))] public string? A { get; set; }
			    [Must(nameof(ModelStatic2))] public string? B { get; set; }
			    [Must(nameof(ModelInstance))] public string? C { get; set; }
			    [Must(nameof(ModelInstance1))] public string? D { get; set; }
			    [Must("ValidatorInstance")] public string? E { get; set; }
			    [Must("ValidatorStatic2")] public string? F { get; set; }
			    public static bool ModelStatic(string? v) => true;
			    public static bool ModelStatic2(M m, string? v) => true;
			    public bool ModelInstance() => true;
			    public bool ModelInstance1(string? v) => true;
			}
			public partial class MValidator {
			    private bool ValidatorInstance(string? v) => true;
			    private static bool ValidatorStatic2(M m, string? v) => true;
			}
			""");
		Assert.Empty(result.GeneratorDiagnostics);
		AssertCompilesCleanly(result);
		var code = result.Sources["N.MValidator.g.cs"];
		Assert.Contains(".Must(v => global::N.M.ModelStatic(v))", code);
		Assert.Contains(".Must((m, v) => global::N.M.ModelStatic2(m, v))", code);
		Assert.Contains(".Must((m, v) => m.ModelInstance())", code);
		Assert.Contains(".Must((m, v) => m.ModelInstance1(v))", code);
		Assert.Contains(".Must(v => ValidatorInstance(v))", code);
		Assert.Contains(".Must((m, v) => global::N.MValidator.ValidatorStatic2(m, v))", code);
	}

	[Fact]
	public void Collection_length_annotations_use_count() {
		var result = Run(Usings + """
			namespace N;
			[AutoValidate]
			public class M {
			    [MinLength(1)] public string[]? Arr { get; set; }
			    [MaxLength(5)] public IReadOnlyList<int>? List { get; set; }
			    [Length(1, 3)] public IEnumerable<int>? Seq { get; set; }
			}
			""");
		Assert.Empty(result.GeneratorDiagnostics);
		AssertCompilesCleanly(result);
		var code = result.Sources["N.MValidator.g.cs"];
		Assert.Contains("v.Length >= 1", code);
		Assert.Contains("v.Count <= 5", code);
		Assert.Contains("global::System.Linq.Enumerable.Count(v) >= 1", code);
	}

	[Theory]
	[InlineData("[AutoValidate] public class M { [LessThan(255)] public byte A { get; set; } }")]
	[InlineData("[AutoValidate] public class M { [GreaterThan(1.0)] public int A { get; set; } }")]
	[InlineData("[AutoValidate] public class M { [InclusiveBetween(-5, 5)] public sbyte? A { get; set; } }")]
	[InlineData("[AutoValidate] public class M { [LessThan(long.MaxValue)] public ulong A { get; set; } }")]
	[InlineData("[AutoValidate] public class M { [LessThan(1e300)] public double A { get; set; } }")]
	[InlineData("[AutoValidate] public class M { [ExclusiveBetween(0.5, 0.5)] public float A { get; set; } }")]
	[InlineData("[AutoValidate] public class M { [MaxLength(-1)] public string? A { get; set; } }")]
	[InlineData("[AutoValidate] public class M { [PrecisionScale(4, 4)] public decimal? A { get; set; } }")]
	public void Accepts_valid_boundary_arguments(string declaration) {
		var result = Run(Usings + "namespace N;\n" + declaration);
		Assert.Empty(result.GeneratorDiagnostics);
		AssertCompilesCleanly(result);
	}

	[Theory]
	// Attributes whose arguments don't bind yet, as while typing in the IDE. The compiler reports these; the
	// generator must neither crash nor add noise.
	[InlineData("[MinimumLength] public string? A { get; set; }")]
	[InlineData("[LengthBetween(1)] public string? A { get; set; }")]
	[InlineData("[GreaterThan(\"x\")] public int A { get; set; }")]
	[InlineData("[Range] public int A { get; set; }")]
	[InlineData("[ValidateWith(typeof(Missing))] public string? A { get; set; }")]
	public void Incomplete_attributes_do_not_crash_the_generator(string member) {
		var result = Run(Usings + "namespace N;\n[AutoValidate] public class M { " + member + " }");
		Assert.Empty(result.GeneratorDiagnostics);
		Assert.Contains("N.MValidator.g.cs", result.Sources.Keys);
	}

	[Fact]
	public void Validator_name_collisions_are_reported_for_every_model_involved() {
		var result = Run(Usings + """
			namespace N;
			[AutoValidate(ValidatorName = "SharedValidator")] public class A { }
			[AutoValidate(ValidatorName = "SharedValidator")] public class B { }
			[AutoValidate(ValidatorName = "sharedvalidator")] public class C { }
			""");
		Assert.Equal(3, result.GeneratorDiagnostics.Count(d => d.Id == "AFV009"));
		Assert.Empty(result.Sources);
		AssertCompilesCleanly(result);
	}

	[Fact]
	public void Nested_models_without_a_generated_validator_are_not_referenced() {
		// G<T> is reported (AFV001) and gets no validator, so M must not reference one for it.
		var result = Run(Usings + """
			namespace N;
			[AutoValidate] public class G<T> { }
			[AutoValidate] public class M {
			    public G<int>? Single { get; set; }
			    public List<G<int>> Many { get; set; } = new();
			}
			""");
		Assert.Equal("AFV001", Assert.Single(result.GeneratorDiagnostics).Id);
		AssertCompilesCleanly(result);
		Assert.DoesNotContain("SetValidator", result.Sources["N.MValidator.g.cs"]);
	}

	[Fact]
	public void Nested_models_from_a_referenced_assembly_use_its_public_validators_only() {
		var library = Run(CreateCompilation(Usings + """
			namespace Lib;
			[AutoValidate] public class PublicModel { [NotEmpty] public string? A { get; set; } }
			[AutoValidate] public class ModelWithInternalValidator { }
			internal partial class ModelWithInternalValidatorValidator { }
			""", assemblyName: "Lib"));
		AssertCompilesCleanly(library);
		using var image = new MemoryStream();
		Assert.True(library.Output.Emit(image).Success);
		var libraryReference = MetadataReference.CreateFromImage(image.ToArray());

		var result = Run(Usings + """
			namespace App;
			[AutoValidate] public class M {
			    public Lib.PublicModel? A { get; set; }
			    public Lib.ModelWithInternalValidator? B { get; set; }
			}
			""", extraReferences: new[] { libraryReference });
		Assert.Empty(result.GeneratorDiagnostics);
		AssertCompilesCleanly(result);
		var code = result.Sources["App.MValidator.g.cs"];
		Assert.Contains(".SetValidator(_ => global::Lib.PublicModelValidator.Shared)", code);
		Assert.DoesNotContain("ModelWithInternalValidatorValidator", code);
	}

	[Fact]
	public void Compatible_types_are_accepted_for_Must_EqualTo_and_ValidateWith() {
		var result = Run(Usings + """
			using FluentValidation;
			namespace N;
			public class Animal { public string? Name { get; set; } }
			public class Dog : Animal { }
			public class AnimalValidator : AbstractValidator<Animal> { }
			[AutoValidate]
			public class M {
			    [Must(nameof(IsSet))] public int A { get; set; }
			    [EqualTo(nameof(A))] public int? B { get; set; }
			    [ValidateWith(typeof(AnimalValidator))] public Dog? Pet { get; set; }
			    [Must(nameof(HasName))] public Dog? Other { get; set; }
			    public static bool IsSet(object value) => value is not null;
			    public static bool HasName(M model, Animal? animal) => animal?.Name is not null;
			}
			""");
		Assert.Empty(result.GeneratorDiagnostics);
		AssertCompilesCleanly(result);
		var code = result.Sources["N.MValidator.g.cs"];
		Assert.Contains(".Must(v => global::N.M.IsSet(v))", code);
		Assert.Contains(".Equal(x => x.A)", code);
		Assert.Contains(".SetValidator(new global::N.AnimalValidator())", code);
		Assert.Contains(".Must((m, v) => global::N.M.HasName(m, v))", code);
	}

	[Fact]
	public void Object_typed_properties_require_the_same_type_for_EqualTo() {
		var result = Run(Usings + "namespace N; [AutoValidate] public class M { [EqualTo(nameof(B))] public object? A { get; set; } public string? B { get; set; } }");
		Assert.Equal("AFV003", Assert.Single(result.GeneratorDiagnostics).Id);
	}

	[Fact]
	public void DataAnnotations_message_placeholders_are_mapped() {
		var result = Run(Usings + """
			namespace N;
			[AutoValidate]
			public class M {
			    [StringLength(10, MinimumLength = 2, ErrorMessage = "{0} must have {2} to {1} characters, not {{3}}")]
			    public string? A { get; set; }
			    [Range(1, 5, ErrorMessage = "{0}: {1}..{2}")]
			    public int B { get; set; }
			}
			""");
		Assert.Empty(result.GeneratorDiagnostics);
		var code = result.Sources["N.MValidator.g.cs"];
		Assert.Contains(".WithMessage(\"{PropertyName} must have 2 to 10 characters, not {3}\")", code);
		Assert.Contains(".WithMessage(\"{PropertyName}: 1..5\")", code);
	}

	[Fact]
	public void Conditional_null_checks_keep_the_nullability_convention() {
		var result = Run(Usings + """
			namespace N;
			[AutoValidate]
			public class M {
			    public bool Flag { get; set; }
			    [NotEmpty(When = nameof(Flag))] public string Conditional { get; set; } = "";
			    [NotEmpty] public string Unconditional { get; set; } = "";
			}
			""");
		var code = result.Sources["N.MValidator.g.cs"];
		Assert.Contains("RuleFor(x => x.Conditional)\n                .NotNull()\n                .NotEmpty().When(", code);
		Assert.Contains("RuleFor(x => x.Unconditional)\n                .NotEmpty();", code);
	}

	[Fact]
	public void Attributes_on_overridden_properties_are_inherited() {
		var result = Run(Usings + """
			namespace N;
			public abstract class Base { [MaximumLength(5)] public virtual string? Name { get; set; } }
			[AutoValidate] public class M : Base { [NotEmpty] public override string? Name { get; set; } }
			""");
		Assert.Empty(result.GeneratorDiagnostics);
		AssertCompilesCleanly(result);
		Assert.Contains("RuleFor(x => x.Name)\n                .NotEmpty()\n                .MaximumLength(5);", result.Sources["N.MValidator.g.cs"]);
	}

	[Fact]
	public void Properties_that_cannot_be_used_in_an_expression_are_skipped() {
		var result = Run(Usings + """
			namespace N;
			[AutoValidate]
			public class M {
			    private int _value;
			    public ref int ByRef => ref _value;
			    public System.Span<int> Span => default;
			    [NotEmpty] public string? Ok { get; set; }
			}
			""");
		Assert.Empty(result.GeneratorDiagnostics);
		AssertCompilesCleanly(result);
		Assert.DoesNotContain("ByRef", result.Sources["N.MValidator.g.cs"]);
	}

	[Fact]
	public void Collection_length_works_for_non_generic_collections() {
		var result = Run(Usings + """
			namespace N;
			public class Bag : System.Collections.IEnumerable { public System.Collections.IEnumerator GetEnumerator() { yield break; } }
			[AutoValidate] public class M { [MaxLength(2)] public Bag? Items { get; set; } }
			""");
		Assert.Empty(result.GeneratorDiagnostics);
		AssertCompilesCleanly(result);
		Assert.Contains("Enumerable.Cast<object>(v)", result.Sources["N.MValidator.g.cs"]);
	}

	[Fact]
	public void Registration_is_idempotent() {
		var result = Run("using AutoFluentValidation; namespace N; [AutoValidate] public class M { }", withDependencyInjection: true);
		AssertCompilesCleanly(result);
		var registration = result.Sources["AutoValidatorServiceCollectionExtensions.g.cs"];
		Assert.Contains("TryAddEnumerable(services,", registration);
		Assert.Contains("TryAdd(services,", registration);
	}

	[Fact]
	public void Unrelated_edits_do_not_re_emit_validators() {
		var compilation = CreateCompilation(Usings + """
			namespace N;
			[AutoValidate] public class A { [NotEmpty] public string? X { get; set; } }
			[AutoValidate] public class B { [MaxLength(3)] public string? Y { get; set; } }
			""");
		var driver = CreateDriver().RunGenerators(compilation);
		var edited = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace N; public class Unrelated { }", ParseOptions));
		var second = driver.RunGenerators(edited).GetRunResult().Results.Single();

		static void AssertNotRerun(IEnumerable<IncrementalGeneratorRunStep> steps) =>
			Assert.All(steps.SelectMany(step => step.Outputs),
				o => Assert.True(o.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged, o.Reason.ToString()));

		var specSteps = second.TrackedSteps[AutoValidatorGenerator.TrackingNames.Specs];
		Assert.Equal(2, specSteps.SelectMany(step => step.Outputs).Count());
		AssertNotRerun(specSteps);
		AssertNotRerun(second.TrackedSteps[AutoValidatorGenerator.TrackingNames.Diagnostics]);
	}

	[Fact]
	public void Generated_code_carries_the_generator_version() {
		var version = typeof(AutoValidatorGenerator).Assembly.GetName().Version!.ToString(3);
		var code = Run("using AutoFluentValidation; namespace N; [AutoValidate] public class M { }").Sources["N.MValidator.g.cs"];
		Assert.Contains($"GeneratedCode(\"AutoFluentValidation\", \"{version}\")", code);
		Assert.Contains("ExcludeFromCodeCoverage", code);
	}
}
