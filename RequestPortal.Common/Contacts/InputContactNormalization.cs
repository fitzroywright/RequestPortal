using System.Reflection;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace RequestPortal.Common.Contacts;

/// <summary>One normalization path for Razor form models; ModelState keeps the user's attempted input on failure.</summary>
public static class InputContactNormalization
{
    public static void Apply(object input,ModelStateDictionary state,string prefix="")
    {
        foreach(var property in input.GetType().GetProperties(BindingFlags.Public|BindingFlags.Instance))
        {
            if(property.PropertyType!=typeof(string) || !property.CanWrite)continue;
            bool email=property.IsDefined(typeof(ContactEmailAttribute));bool phone=property.IsDefined(typeof(ContactTelephoneAttribute));
            if(!email && !phone)continue;
            string key=prefix.Length==0?property.Name:prefix+"."+property.Name;
            try
            {
                string? value=property.GetValue(input) as string;
                property.SetValue(input,email?ContactEmail.Normalize(value):ApplicantTelephone.NormalizeNew(value));
            }
            catch(ArgumentException error){state.AddModelError(key,error.Message);}
        }
    }
}
