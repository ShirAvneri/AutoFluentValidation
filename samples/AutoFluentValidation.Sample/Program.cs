using AutoFluentValidation.Sample;
using AutoFluentValidation.Sample.Models; // generated AddAutoValidators() lives in the assembly's root namespace
using Microsoft.Extensions.DependencyInjection;

// Every validator below is generated at compile time — there is no hand-written RuleFor for these models
// except the one rule added in SignUpRequestValidator.ConfigureRules().
using var provider = new ServiceCollection()
    .AddAutoValidators() // generated: registers IValidator<T> for every [AutoValidate] model, no reflection
    .BuildServiceProvider();

var validator = provider.GetRequiredService<IValidator<User>>();

var request = new User
{
    UserName = "x",
    Email = "not-an-email",
    Password = "secret123",
    ConfirmPassword = "secret321",
    Age = 12,
    Plan = (Plan)99,
    Address = new Address { Line1 = "", PostCode = "ABC" },
    Interests = { new Interest() }
};

var result = validator.Validate(request);

Console.WriteLine(result.IsValid ? "Valid!" : "Validation failed:");

foreach (var error in result.Errors)
    Console.WriteLine($"  [{error.Severity}] {error.PropertyName}: {error.ErrorMessage}");