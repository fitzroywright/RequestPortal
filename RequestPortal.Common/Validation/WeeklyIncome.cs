using System.Globalization;
using System.Text.RegularExpressions;

namespace RequestPortal.Common.Validation;

public static partial class WeeklyIncome
{
    public const decimal MaximumMonthly=999999999999.99m;
    public static decimal MaximumWeekly => decimal.Floor(MaximumMonthly*12m/52m*100m)/100m;
    [GeneratedRegex(@"\A(?:0|[1-9][0-9]*)(?:\.[0-9]{1,2})?\z",RegexOptions.CultureInvariant)]
    private static partial Regex Format();
    public static bool TryParse(string? raw,out decimal value)
    {
        value=0;
        return raw is not null && raw.Length<=15 && Format().IsMatch(raw) &&
            decimal.TryParse(raw,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out value) && value<=MaximumWeekly;
    }
    public static decimal ToMonthly(decimal weekly)
    {
        if(weekly<0 || weekly>MaximumWeekly || decimal.Round(weekly,2)!=weekly)
            throw new ArgumentException("Enter weekly income in JMD with at most two decimal places, within the supported range.");
        return decimal.Round(weekly*52m/12m,2,MidpointRounding.ToEven);
    }
    public static string Display(decimal amount) => "JMD "+amount.ToString("N2",CultureInfo.GetCultureInfo("en-JM"));
}
