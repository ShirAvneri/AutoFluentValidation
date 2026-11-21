# AutoFluentValidation

Compile-time generation of [FluentValidation](https://github.com/FluentValidation/FluentValidation) validators.
Put attributes on your models, or reuse the DataAnnotations they already have, and a C# source generator writes
the `AbstractValidator<T>` classes for you. The generated validators are ordinary FluentValidation validators
with no runtime reflection, and you can still add hand-written rules on top.

The models below come from the [sample project](samples/AutoFluentValidation.Sample/Models):

```csharp
using System.ComponentModel.DataAnnotations;

namespace AutoFluentValidation.Sample.Models;

public enum Plan { Free, Pro }

[AutoValidate]
public class User
{
    [LengthBetween(3, 20), Matches("^[a-z0-9_]+$", Message = "{PropertyName} may only contain lowercase letters, digits and _")]
    public string UserName { get; set; } = "";

    [Required, EmailAddress] // plain DataAnnotations work too
    public string? Email { get; set; }

    [MinimumLength(8)]
    public string Password { get; set; } = "";

    [EqualTo(nameof(Password), Message = "Passwords must match")]
    public string ConfirmPassword { get; set; } = "";

    [InclusiveBetween(13, 120)]
    public int Age { get; set; }

    public Plan Plan { get; set; }                          // convention: IsInEnum()
    public Address Address { get; set; } = new();           // convention: NotNull() + AddressValidator
    public List<Interest> Interests { get; set; } = new();  // convention: RuleForEach + InterestValidator

    [MaximumLength(140, Severity = Severity.Warning)]
    public string? Bio { get; set; }
}

[AutoValidate]
public class Address
{
    [NotEmpty] public string Line1 { get; set; } = "";
    [RegularExpression(@"\d{5}")] public string? PostCode { get; set; }
}

[AutoValidate]
public class Interest
{
    [NotEmpty, MaximumLength(25)] public string Title { get; set; } = "";
    [MaximumLength(250)] public string? Description { get; set; }
}
```

For `User`, the generator produces this (`global::` prefixes trimmed):

```csharp
public partial class UserValidator : AbstractValidator<User>
{
    public static UserValidator Shared => ...;   // lazily created, used for nested validation

    public UserValidator()
    {
        RuleFor(x => x.UserName)
            .NotNull()
            .Length(3, 20)
            .Matches("^[a-z0-9_]+$").WithMessage("{PropertyName} may only contain lowercase letters, digits and _");
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress();
        RuleFor(x => x.Password)
            .NotNull()
            .MinimumLength(8);
        RuleFor(x => x.ConfirmPassword)
            .NotNull()
            .Equal(x => x.Password).WithMessage("Passwords must match");
        RuleFor(x => x.Age)
            .InclusiveBetween((int)13, (int)120);
        RuleFor(x => x.Plan)
            .IsInEnum();
        RuleFor(x => x.Address)
            .NotNull()
            .SetValidator(_ => AddressValidator.Shared);
        RuleFor(x => x.Interests)
            .NotNull();
        RuleForEach(x => x.Interests)
            .SetValidator(_ => InterestValidator.Shared);
        RuleFor(x => x.Bio)
            .MaximumLength(140).WithSeverity(Severity.Warning);
        ConfigureRules();
    }

    partial void ConfigureRules();
}
```

`AddressValidator` and `InterestValidator` are generated the same way.

## Getting started

```xml
<PackageReference Include="AutoFluentValidation" Version="0.1.0" />
```

The package contains the attributes and the source generator. It also brings in FluentValidation.

### One package, one namespace

* **Don't reference FluentValidation yourself.** AutoFluentValidation depends on FluentValidation
  `[12.2.0, 13.0.0)` and sets the version. Patch and minor updates within 12.x are allowed, and the next (breaking)
  major version is blocked until AutoFluentValidation supports it.
* **Write `using AutoFluentValidation;`, not `using FluentValidation;`.** The package adds `global using FluentValidation;`
  to your project. FluentValidation types such as `IValidator<T>` and `Severity`, and the rule methods such as `NotEmpty()`
  that you use in `ConfigureRules()`, are available without a `using`. This works whether or not `<ImplicitUsings>` is on.
* To opt out, for example if a name like `ValidationException` clashes with `System.ComponentModel.DataAnnotations`:

  ```xml
  <AutoFluentValidationImportFluentValidation>false</AutoFluentValidationImportFluentValidation>
  ```

1. Mark a class, record or struct with `[AutoValidate]`.
2. Annotate its properties (see the tables below).
3. Use the generated `{TypeName}Validator` like any other FluentValidation validator, or register all of them at once:

```csharp
using AutoFluentValidation.Sample;   // the generated extension lives in your assembly's root namespace

using var provider = new ServiceCollection()
    .AddAutoValidators()             // IValidator<T> + concrete type for every generated validator
    .BuildServiceProvider();         // or AddAutoValidators(ServiceLifetime.Singleton)

var validator = provider.GetRequiredService<IValidator<User>>();
var result = validator.Validate(user);
```

`AddAutoValidators` is generated only when `Microsoft.Extensions.DependencyInjection.Abstractions` is referenced.
It lists the validators explicitly instead of scanning assemblies, so it works with trimming and Native AOT.
Registrations that already exist are kept (`TryAdd`), so calling it more than once, or registering one of the
validators yourself first, is safe.

## Generated files in your project (`AutoValidators/`)

Every build writes the generated validators, and the `AddAutoValidators` registration, into an `AutoValidators/`
folder in the project that references the package:

```
AutoFluentValidation.Sample/
  AutoValidators/
    AutoFluentValidation.Sample.Models.AddressValidator.g.cs
    AutoFluentValidation.Sample.Models.InterestValidator.g.cs
    AutoFluentValidation.Sample.Models.UserValidator.g.cs
    AutoValidatorServiceCollectionExtensions.g.cs
  Models/
    Address.cs
    Interest.cs
    Plan.cs
    User.cs
  Program.cs
```

* **The folder is a mirror and isn't compiled.** The compiler uses the generator's in-memory output, and the package
  excludes everything under the folder from compilation. Don't edit these files, because the next build overwrites
  them. Put your changes in `ConfigureRules()` instead.
* **It stays in sync.** When you rename or remove a model, its old `*.g.cs` file is deleted. Other files in the folder
  are left alone. If you delete the folder, the next build recreates it.
* **It's yours to commit or ignore.** Committing the folder lets reviewers see generated rule changes in diffs.
  To ignore it instead, add `AutoValidators/` to `.gitignore`.
* **Don't put hand-written `.cs` files in it.** Nothing in the folder is compiled.

You can configure it in your `.csproj`:

```xml
<PropertyGroup>
  <AutoValidatorsDirectory>Validation/Generated</AutoValidatorsDirectory>  <!-- default: AutoValidators -->
  <AutoValidatorsEmitToProject>false</AutoValidatorsEmitToProject>         <!-- turn the mirror off -->
</PropertyGroup>
```

The package's `build/AutoFluentValidation.props` and `.targets` set this up, and NuGet imports them automatically.
If you reference the projects directly instead of the package, import those two files yourself (see `samples/`).

## Adding hand-written rules

The generated validator is `partial`. Implement `ConfigureRules` in your own half for anything that attributes can't
express. Methods you declare there can also be used as `[Must]` predicates:

This is the sample's `UserValidator` half, from `Models/User.cs`:

```csharp
public partial class UserValidator
{
    partial void ConfigureRules()
    {
        RuleFor(x => x.UserName).NotEqual("admin").WithMessage("That user name is reserved");
    }
}
```

A `[Must]` predicate can live in the same place. For example, `[Must(nameof(UserValidator.BeAvailable))]` on
`User.UserName` would use:

```csharp
public partial class UserValidator
{
    private static readonly HashSet<string> Taken = new() { "root", "support" };
    internal bool BeAvailable(string userName) => !Taken.Contains(userName); // internal, so nameof() can see it from User
}
```

The generated part declares the parameterless constructor, so don't declare another one. Your half may state the
class's accessibility (for example `internal partial class UserValidator` for a public model); the generated part then
leaves it to you. Without one, the validator is public when the model is public, and internal otherwise.

## Rule attributes (`AutoFluentValidation` namespace)

| Attribute | Generates | Applies to |
|---|---|---|
| `[NotNull]` | `NotNull()` | any |
| `[NotEmpty]` | `NotEmpty()` | any |
| `[MinimumLength(n)]` / `[MaximumLength(n)]` | `MinimumLength(n)` / `MaximumLength(n)` | string |
| `[LengthBetween(min, max)]` / `[ExactLength(n)]` | `Length(min, max)` / `Length(n)` | string |
| `[Email]` | `EmailAddress()` | string |
| `[CreditCardNumber]` | `CreditCard()` | string |
| `[Matches(pattern)]` | `Matches(pattern)` (search, not full match). Can be repeated. | string |
| `[IsEnumName(typeof(E), caseSensitive)]` | `IsEnumName(...)` | string |
| `[GreaterThan(v)]`, `[GreaterThanOrEqualTo(v)]`, `[LessThan(v)]`, `[LessThanOrEqualTo(v)]` | same | numeric and nullable numeric |
| `[InclusiveBetween(a, b)]` / `[ExclusiveBetween(a, b)]` | same | numeric and nullable numeric |
| `[PrecisionScale(p, s, ignoreTrailingZeros)]` | `PrecisionScale(...)` | decimal / decimal? |
| `[IsInEnum]` | `IsInEnum()` | enum / enum? |
| `[EqualTo(nameof(Other))]` / `[NotEqualTo(nameof(Other))]` | `Equal(x => x.Other)` / `NotEqual(...)` | property of the same type |
| `[Must(nameof(Method))]` | `Must(...)` | see below |
| `[ValidateWith(typeof(MyValidator))]` | `SetValidator(new MyValidator())`. A validator for a base type or interface of the property works too. | any |
| `[SkipValidation]` | excludes the property, conventions included | any |

Each rule attribute accepts these options:

* `Message`: `WithMessage(...)`. FluentValidation placeholders such as `{PropertyName}` work.
* `ErrorCode`: `WithErrorCode(...)`.
* `Severity`: `WithSeverity(...)`.
* `When` / `Unless`: the name of a `bool` property, or a parameterless `bool` method, on the model. It applies to that rule only.

Numeric constants are converted to the property's type. For example, `[GreaterThan(0)]` on a `decimal?` becomes `GreaterThan((decimal)0)`.
A constant that doesn't fit the type (`[LessThan(300)]` on a `byte`), would be truncated (`[GreaterThan(0.5)]` on an `int`)
or isn't finite is reported as AFV008 instead.

Arguments are checked at compile time, so a generated validator never fails to compile or throws from its constructor:
negative lengths, `min > max`, `scale > precision` and invalid regular expressions are reported as AFV008 and the rule is skipped.

### `[Must]` predicate shapes

The method is resolved at compile time. The generator looks on the validator (your partial half) first, then on the model.
Parameters can be of any type the argument converts to implicitly, such as `object` or a base type:

| Where | Signature |
|---|---|
| validator (instance or static) | `bool M(TProperty value)` or `bool M(TModel model, TProperty value)` |
| model, static | `bool M(TProperty value)` or `bool M(TModel model, TProperty value)` |
| model, instance | `bool M()` or `bool M(TProperty value)` |

## DataAnnotations (on by default)

| DataAnnotation | Generates |
|---|---|
| `[Required]` | `NotEmpty()` for strings (`NotNull()` if `AllowEmptyStrings = true`), `NotNull()` for other references and `Nullable<T>`. No rule for non-nullable value types. |
| `[StringLength(max, MinimumLength = min)]` | `Length(min, max)` or `MaximumLength(max)` |
| `[MinLength]`, `[MaxLength]`, `[Length]` | string length rules. On arrays and collections, an item-count check. |
| `[Range(min, max)]` | `InclusiveBetween`, `ExclusiveBetween`, or a mix, following `MinimumIsExclusive` / `MaximumIsExclusive` |
| `[EmailAddress]`, `[CreditCard]` | `EmailAddress()`, `CreditCard()` |
| `[RegularExpression(p)]` | `Matches(@"\A(?:p)\z")`, skipped for null or empty values. This keeps DataAnnotations' behaviour: the whole value must match, and an empty value is valid (use `[Required]` to reject it). |
| `[Compare(nameof(Other))]` | `Equal(x => x.Other)` |

`ErrorMessage` becomes `WithMessage`. `{0}` is mapped to `{PropertyName}`, and the attribute's own placeholders
(`{1}`, `{2}`, for example the minimum and maximum of `[Range]` and `[StringLength]`) are replaced by their values.
`ErrorMessageResourceType` isn't supported (AFV006): the rule is kept with FluentValidation's default message.
`[MaxLength]` and `[MaxLength(-1)]` mean "no limit" and produce no rule. Annotations that aren't validation rules,
such as `[Display]` and `[Key]`, are ignored. Attributes on a property that is overridden are inherited by the override.

## Conventions

You can turn each convention off on `[AutoValidate]`:

| Option (default) | Behaviour |
|---|---|
| `NullabilityConvention` (true) | `NotNull()` for reference-type properties declared non-nullable in a `#nullable enable` context, unless the property already has an unconditional null or empty check |
| `EnumConvention` (true) | `IsInEnum()` for enum properties |
| `NestedValidation` (true) | Properties whose type is `[AutoValidate]` use `SetValidator(...)`. Collections of such types use `RuleForEach(...)`. The validator is resolved lazily, so self-referencing models work. Models from referenced assemblies use that assembly's generated validator when it is public. |
| `UseDataAnnotations` (true) | translate DataAnnotations |
| `StopOnFirstFailure` (false) | `RuleLevelCascadeMode = CascadeMode.Stop` |
| `ValidatorName` | overrides the default `{TypeName}Validator`. Nested types default to `{Outer}{Inner}Validator`. |

## Diagnostics

Mistakes are reported at compile time, in the IDE and the build, instead of at runtime:

| Id | Severity | Meaning |
|---|---|---|
| AFV001 | Error | `[AutoValidate]` on a generic type |
| AFV002 | Warning | Attribute doesn't fit the property type (e.g. `[Email]` on `int`). The rule is skipped. |
| AFV003 | Error | `EqualTo`, `Compare`, `When` or `Unless` names a member that doesn't exist or has the wrong type |
| AFV004 | Error | No `[Must]` method with a supported signature |
| AFV005 | Error | `[ValidateWith]` type isn't an `IValidator<TProperty>` with a public parameterless constructor |
| AFV006 | Warning | Unsupported DataAnnotations form, e.g. `Range(typeof(DateTime), ...)` |
| AFV007 | Error | Model, or a type that contains it, is private, protected or file-local |
| AFV008 | Error | Invalid rule argument: a constant that doesn't fit the property type, `min > max`, a negative length, an invalid regex. The rule is skipped. |
| AFV009 | Error | No validator can be generated: static class or ref struct, invalid `ValidatorName`, two models with the same validator name, or an existing type with that name that isn't a `partial class` |
| AFV010 | Error | Internal generator error for one model (the others are still generated). Please report it. |
| AFV100 | Error | (MSBuild) `AutoValidatorsDirectory` is the project directory or one of its parents |

Diagnostics can be suppressed like any compiler warning: `#pragma warning disable AFV002`, `<NoWarn>`, or `.editorconfig`.

## How it works

* Roslyn `IIncrementalGenerator` using `ForAttributeWithMetadataName`. Analysis produces value-equatable models,
  so validators that haven't changed aren't emitted again.
* For each model it looks at public readable properties, including inherited ones, and emits one `RuleFor` chain per
  property. Conventions and attributes are applied in declaration order.
* Rules that can't be translated are reported and skipped, and a failure in one model doesn't affect the others.
* An MSBuild target that runs after `CoreCompile` copies the generator's output from `obj/.../generated` into
  `AutoValidators/` (see above). Your IDE also shows the in-memory sources under *Dependencies → Analyzers*.
* The MSBuild integration also ships in `buildTransitive/`, so projects that get the package through another project
  behave the same as direct references.

## Limitations

* Generic models aren't supported (AFV001). Write those validators by hand.
* The generator needs a Roslyn 4.8+ compiler (.NET 8 SDK, or Visual Studio 17.8 or later).
* The generated validator has a parameterless constructor, so it can't take constructor-injected dependencies.
  For rules that need services, write a separate validator and include it with `Include(...)` in `ConfigureRules`.
* Comparison attributes take compile-time numeric constants. Compare dates in `ConfigureRules`.
* Async rules (`MustAsync`) go in `ConfigureRules`.

## Repository layout

```
src/AutoFluentValidation                  attributes (net8.0); its NuGet package also ships the generator
  build/, buildTransitive/                MSBuild props/targets that mirror generated files into AutoValidators/
src/AutoFluentValidation.SourceGenerator  the incremental source generator (netstandard2.0)
tests/AutoFluentValidation.Tests          end-to-end tests of generated validators + in-memory generator tests
samples/AutoFluentValidation.Sample       console demo
```

```
dotnet test
dotnet run --project samples/AutoFluentValidation.Sample
dotnet pack src/AutoFluentValidation -c Release
```
