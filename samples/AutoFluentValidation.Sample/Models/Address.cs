using System.ComponentModel.DataAnnotations;

namespace AutoFluentValidation.Sample.Models;

[AutoValidate]
public class Address {
    [NotEmpty] public string Line1 { get; set; } = "";
    [RegularExpression(@"\d{5}")] public string? PostCode { get; set; }
}