using FluentValidation;
using Vouch.Application.Features.Vouching;

namespace Vouch.Application.Features.Vouching;

public class SubmitVouchRequestValidator : AbstractValidator<SubmitVouchRequest>
{
    public SubmitVouchRequestValidator()
    {
        RuleFor(x => x.TargetUserId)
            .NotEmpty().WithMessage("Target user ID is required.");

        RuleFor(x => x.Traits)
            .IsInEnum().WithMessage("Invalid character trait value.");

        RuleFor(x => x.Note)
            .MaximumLength(500).WithMessage("Vouch note must not exceed 500 characters.")
            .When(x => x.Note is not null);
    }
}
