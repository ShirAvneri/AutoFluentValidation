namespace AutoFluentValidation.Sample.Models;

[AutoValidate]
public class Interest {
    [NotEmpty, MaximumLength(25)] public string Title { get; set; } = "";
    [MaximumLength(250)] public string? Description { get; set; }
}