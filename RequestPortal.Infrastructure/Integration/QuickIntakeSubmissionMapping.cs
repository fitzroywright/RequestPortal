using RequestPortal.Application.Integration;
using RequestPortal.Domain.Requests;

namespace RequestPortal.Infrastructure.Integration;

public static class QuickIntakeSubmissionMapping
{
    public static ExternalApplicationSubmission Apply(ExternalApplicationSubmission submission, QuickIntakeRecord? intake)
    {
        if (intake is null) return submission;
        // This paired sentinel identifies migration defaults, not actual answers.
        bool captured = !(intake.DateOfBirth == new DateOnly(1900, 1, 1) && intake.StreetAddress == "Not captured");
        return submission with
        {
            // The owner confirms historical amounts were weekly but mislabeled MonthlyIncome.
            // Preserve storage and amounts; never convert or rewrite historical records.
            WeeklyIncome = captured && intake.MonthlyIncome >= 0 ? intake.MonthlyIncome : null,
            ApplicantMaritalStatus = captured ? intake.MaritalStatus : null,
            HasChildrenUnder18LivingFullTime = captured ? intake.HasChildrenUnder18LivingFullTime : null,
            HasDisabilityOrSeriousMedicalCondition = captured ? intake.HasDisabilityOrSeriousMedicalCondition : null,
            CurrentAssistancePrograms = captured ? intake.AssistancePrograms.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null,
            VictimCircumstances = intake.VictimCircumstances?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        };
    }
}
