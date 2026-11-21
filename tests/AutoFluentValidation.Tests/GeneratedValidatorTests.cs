using AutoFluentValidation.Tests.Models;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.DependencyInjection;

namespace AutoFluentValidation.Tests;

/// <summary>End-to-end: validators generated at build time for the models in Models/TestModels.cs.</summary>
public class GeneratedValidatorTests {
	private static Customer ValidCustomer() => new() {
		Name = "Ada", Email = "ada@example.com", Age = 30, Discount = 0.1m,
		Address = new Address { Street = "1 Main St", Zip = "12345", City = "Springfield" },
		Orders = { new Order(2, new[] { "gift" }) },
	};

	private static IEnumerable<string> FailedProperties(ValidationResult result) => result.Errors.Select(e => e.PropertyName);

	[Fact]
	public void Valid_customer_passes() {
		var result = new CustomerValidator().Validate(ValidCustomer());
		Assert.True(result.IsValid, string.Join("; ", result.Errors));
	}

	[Fact]
	public void Attribute_rules_are_applied() {
		var c = ValidCustomer();
		c.Name = new string('x', 51);
		c.Age = 17;
		c.Discount = 0.9m;
		c.Code = "X-1";
		var result = new CustomerValidator().Validate(c);
		Assert.Equal(new[] { "Name", "Age", "Discount", "Code" }, FailedProperties(result));
		Assert.Equal("TOO_YOUNG", result.Errors.Single(e => e.PropertyName == "Age").ErrorCode);
	}

	[Fact]
	public void Custom_message_is_used() {
		var c = ValidCustomer();
		c.Email = "not-an-email";
		var error = Assert.Single(new CustomerValidator().Validate(c).Errors);
		Assert.Equal("Please supply a valid email", error.ErrorMessage);
	}

	[Fact]
	public void Severity_is_applied() {
		var c = ValidCustomer();
		c.Nickname = "far-too-long-nickname";
		var error = Assert.Single(new CustomerValidator().Validate(c).Errors);
		Assert.Equal(Severity.Warning, error.Severity);
	}

	[Fact]
	public void When_condition_only_applies_to_its_rule() {
		var c = ValidCustomer();
		Assert.True(new CustomerValidator().Validate(c).IsValid);
		c.IsBusiness = true;
		Assert.Equal(new[] { "CompanyName" }, FailedProperties(new CustomerValidator().Validate(c)));
	}

	[Fact]
	public void Nullability_convention_adds_not_null_for_non_nullable_references() {
		var c = ValidCustomer();
		c.Orders = null!;
		c.Ignored = null!; // [SkipValidation]
		Assert.Equal(new[] { "Orders" }, FailedProperties(new CustomerValidator().Validate(c)));
	}

	[Fact]
	public void Nullability_convention_can_be_disabled() {
		var p = new Product { Sku = "SKU-1", Price = 1.5m, NotCheckedForNull = null! };
		Assert.True(new CatalogProductValidator().Validate(p).IsValid);
	}

	[Fact]
	public void Enum_convention_adds_is_in_enum() {
		var c = ValidCustomer();
		c.Type = (CustomerType)42;
		Assert.Equal(new[] { "Type" }, FailedProperties(new CustomerValidator().Validate(c)));
	}

	[Fact]
	public void Nested_and_collection_models_are_validated_with_their_generated_validators() {
		var c = ValidCustomer();
		c.Address!.Street = null;
		c.Orders.Add(new Order(0, Array.Empty<string>()));
		var result = new CustomerValidator().Validate(c);
		Assert.Equal(new[] { "Address.Street", "Orders[1].Quantity", "Orders[1].Tags" }, FailedProperties(result));
	}

	[Fact]
	public void Self_referencing_models_do_not_recurse_forever() {
		var c = ValidCustomer();
		c.Manager = new Customer { Name = "", Age = 40, Address = null };
		Assert.Equal(new[] { "Manager.Name" }, FailedProperties(new CustomerValidator().Validate(c)));
	}

	[Fact]
	public void Hand_written_rules_from_partial_class_are_included() {
		var a = new Address { Street = "x", Zip = "12345", City = "" };
		Assert.Equal(new[] { "City" }, FailedProperties(new AddressValidator().Validate(a)));
	}

	[Fact]
	public void DataAnnotations_are_translated() {
		var r = new Registration {
			Email = "", Password = "short", ConfirmPassword = "different",
			Rating = 11, Tags = new() { "a", "b", "c", "d" },
		};
		var result = new RegistrationValidator().Validate(r);
		Assert.Equal(new[] { "Email", "Password", "ConfirmPassword", "Rating", "Tags" }, FailedProperties(result));
		Assert.Equal("'Tags' must contain at most 3 items.", result.Errors.Last().ErrorMessage);
	}

	[Fact]
	public void Stop_on_first_failure_sets_rule_level_cascade_mode() {
		var r = new Registration { Email = "" }; // fails NotEmpty, would also fail EmailAddress
		Assert.Single(new RegistrationValidator().Validate(r).Errors);
	}

	[Fact]
	public void RegularExpression_requires_a_full_match_and_maps_placeholder() {
		var a = new Address { Street = "x", City = "y", Zip = "123456" };
		var error = Assert.Single(new AddressValidator().Validate(a).Errors);
		Assert.Equal("Zip must be five digits", error.ErrorMessage);
	}

	[Theory]
	[InlineData(null, true)]
	[InlineData("", true)]          // DataAnnotations treats empty as valid; [Required] is what rejects it
	[InlineData("12345", true)]
	[InlineData("12345\n", false)]  // '$' would have accepted a trailing newline
	[InlineData("x12345", false)]
	[InlineData("123456", false)]
	public void RegularExpression_matches_DataAnnotations_semantics(string? zip, bool valid) {
		var a = new Address { Street = "x", City = "y", Zip = zip };
		Assert.Equal(valid, new AddressValidator().Validate(a).IsValid);
		Assert.Equal(valid, System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
			a, new System.ComponentModel.DataAnnotations.ValidationContext(a), null, validateAllProperties: true));
	}

	[Fact]
	public void Must_resolves_validator_methods_and_other_rules_apply() {
		var p = new Product { Sku = "nope", Price = 1234.567m, Category = "Robot", Dimensions = new Dimensions { Width = 0 } };
		Assert.Equal(new[] { "Sku", "Price", "Category", "Dimensions.Width" }, FailedProperties(new CatalogProductValidator().Validate(p)));
	}

	[Fact]
	public void Internal_models_get_internal_validators() {
		Assert.False(typeof(InternalThingValidator).IsPublic);
		Assert.False(new InternalThingValidator().Validate(new InternalThing()).IsValid);
	}

	[Fact]
	public void AddAutoValidators_registers_every_generated_validator() {
		using var provider = new ServiceCollection().AddAutoValidators().BuildServiceProvider();
		using var scope = provider.CreateScope();
		Assert.IsType<CustomerValidator>(scope.ServiceProvider.GetRequiredService<IValidator<Customer>>());
		Assert.IsType<CatalogProductValidator>(scope.ServiceProvider.GetRequiredService<IValidator<Product>>());
		Assert.NotNull(scope.ServiceProvider.GetRequiredService<AddressValidator>());
	}

	[Fact]
	public void AddAutoValidators_can_be_called_more_than_once() {
		var services = new ServiceCollection().AddAutoValidators().AddAutoValidators(ServiceLifetime.Singleton);
		using var provider = services.BuildServiceProvider();
		Assert.Single(provider.GetServices<IValidator<Customer>>());
		Assert.Single(services, d => d.ServiceType == typeof(CustomerValidator));
	}
}
