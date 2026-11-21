using System.ComponentModel.DataAnnotations;
using FluentValidation;

namespace AutoFluentValidation.Tests.Models;

public enum CustomerType { Individual = 1, Business = 2 }

[AutoValidate]
public class Customer {
	[NotEmpty, MaximumLength(50)]
	public string Name { get; set; } = "";

	[Email(Message = "Please supply a valid email")]
	public string? Email { get; set; }

	[GreaterThanOrEqualTo(18, ErrorCode = "TOO_YOUNG")]
	public int Age { get; set; }

	[InclusiveBetween(0.0, 0.5)]
	public decimal Discount { get; set; }

	public CustomerType Type { get; set; } = CustomerType.Individual;

	public Address? Address { get; set; }

	public List<Order> Orders { get; set; } = new();

	[Must(nameof(BeValidCode))]
	public string? Code { get; set; }
	public static bool BeValidCode(string? code) => code is null || code.StartsWith("C-");

	public bool IsBusiness { get; set; }

	[NotEmpty(When = nameof(IsBusiness))]
	public string? CompanyName { get; set; }

	[MaximumLength(10, Severity = Severity.Warning)]
	public string? Nickname { get; set; }

	[SkipValidation]
	public string Ignored { get; set; } = null!;

	public Customer? Manager { get; set; }
}

[AutoValidate]
public class Address {
	[Required]
	public string? Street { get; set; }

	[RegularExpression(@"\d{5}", ErrorMessage = "{0} must be five digits")]
	public string? Zip { get; set; }

	public string? City { get; set; }
}

/// <summary>Hand-written rules added to the generated validator.</summary>
public partial class AddressValidator {
	partial void ConfigureRules() {
		RuleFor(x => x.City).NotEmpty();
	}
}

[AutoValidate]
public record Order(
	[property: GreaterThan(0)] int Quantity,
	[property: MinLength(1)] string[] Tags);

[AutoValidate(StopOnFirstFailure = true)]
public class Registration {
	[Required, EmailAddress]
	public string? Email { get; set; }

	[StringLength(20, MinimumLength = 8)]
	public string? Password { get; set; }

	[Compare(nameof(Password))]
	public string? ConfirmPassword { get; set; }

	[Range(1, 10)]
	public int? Rating { get; set; }

	[MaxLength(3)]
	public List<string>? Tags { get; set; }
}

public class Dimensions {
	public double Width { get; set; }
}

public class DimensionsValidator : AbstractValidator<Dimensions> {
	public DimensionsValidator() => RuleFor(x => x.Width).GreaterThan(0);
}

[AutoValidate(ValidatorName = "CatalogProductValidator", NullabilityConvention = false)]
public class Product {
	[Must(nameof(CatalogProductValidator.BeKnownSku))]
	public string Sku { get; set; } = "";

	[PrecisionScale(5, 2)]
	public decimal Price { get; set; }

	[IsEnumName(typeof(CustomerType))]
	public string? Category { get; set; }

	[ValidateWith(typeof(DimensionsValidator))]
	public Dimensions? Dimensions { get; set; }

	public string NotCheckedForNull { get; set; } = null!;
}

public partial class CatalogProductValidator {
	private static readonly HashSet<string> KnownSkus = new() { "SKU-1", "SKU-2" };
	internal bool BeKnownSku(string sku) => KnownSkus.Contains(sku);
}

[AutoValidate]
internal class InternalThing {
	[NotEmpty]
	public string? Value { get; set; }
}
