using FluentValidation;
using Vouch.Application.Features.Moderation;

namespace Vouch.Application.Features.Moderation;

public class CreateReportRequestValidator : AbstractValidator<CreateReportRequest>
{
    public CreateReportRequestValidator()
    {
        RuleFor(x => x.ReportedUserId)
            .NotEmpty().WithMessage("Reported user ID is required.");

        RuleFor(x => x.Category)
            .IsInEnum().WithMessage("Invalid report category.");

        RuleFor(x => x.Details)
            .NotEmpty().WithMessage("Report details are required.")
            .MinimumLength(10).WithMessage("Please provide at least 10 characters of detail.")
            .MaximumLength(1000).WithMessage("Report details must not exceed 1000 characters.");
    }
}

public class CreateAmbassadorInviteRequestValidator : AbstractValidator<CreateAmbassadorInviteRequest>
{
    public CreateAmbassadorInviteRequestValidator()
    {
        RuleFor(x => x.CampusId)
            .NotEmpty().WithMessage("Campus ID is required.");

        RuleFor(x => x.IntendedEmail)
            .EmailAddress().WithMessage("Intended email must be a valid email address.")
            .MaximumLength(120).WithMessage("Intended email must not exceed 120 characters.")
            .When(x => x.IntendedEmail is not null);
    }
}
