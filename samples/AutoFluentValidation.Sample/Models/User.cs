using System.ComponentModel.DataAnnotations;

namespace AutoFluentValidation.Sample.Models;

[AutoValidate]
public class User {
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

    public Plan Plan { get; set; } // convention: IsInEnum()
    public Address Address { get; set; } = new(); // convention: NotNull() + nested AddressValidator
    public List<Interest> Interests { get; set; } = new(); // convention: RuleForEach + InterestValidator

    [MaximumLength(140, Severity = Severity.Warning)]
    public string? Bio { get; set; }
}

// Your half of the generated partial validator: add anything attributes can't express.
public partial class UserValidator {
    partial void ConfigureRules() {
        RuleFor(x => x.UserName).NotEqual("admin").WithMessage("That user name is reserved");
    }
}