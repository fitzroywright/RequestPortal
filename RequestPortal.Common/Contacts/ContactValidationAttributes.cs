using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using System.ComponentModel.DataAnnotations;

namespace RequestPortal.Common.Contacts;

[AttributeUsage(AttributeTargets.Property)]
public sealed class ContactEmailAttribute : ValidationAttribute
{
    public ContactEmailAttribute() : base("Enter a valid email address, for example name@example.com.") {}
    public override bool IsValid(object? value)
    {try {ContactEmail.Normalize(value as string);return true;}catch(ArgumentException){return false;}}
}
[AttributeUsage(AttributeTargets.Property)]
public sealed class ContactTelephoneAttribute : ValidationAttribute, IClientModelValidator
{
    public void AddValidation(ClientModelValidationContext context)
    {
        context.Attributes.TryAdd("data-val","true");
        context.Attributes.TryAdd("data-val-contacttelephone",ErrorMessage!);
    }
    public ContactTelephoneAttribute() : base("Enter a valid telephone number; include a country code for international numbers.") {}
    public override bool IsValid(object? value)
    {try {ApplicantTelephone.NormalizeNew(value as string);return true;}catch(ArgumentException){return false;}}
}
