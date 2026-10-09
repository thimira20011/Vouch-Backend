using FluentValidation;
using Vouch.Application.Features.Vouching;
using Vouch.Domain.Enums;

namespace Vouch.Application.Features.Vouching;

public class SubmitVouchRequestValidator : AbstractValidator<SubmitVouchRequest>
{
    public SubmitVouchRequestValidator()
    {
        RuleFor(x => x.TargetUserId)
            .NotEmpty().WithMessage("Target user ID is required.");

        RuleFor(x => x.Traits)
            .Must(t => t != CharacterTrait.None && (t & ~(CharacterTrait.Sincere | CharacterTrait.Respectful |
                CharacterTrait.AcademicallyMotivated | CharacterTrait.Empathetic | CharacterTrait.Reliable | CharacterTrait.Creative)) == 0)
            .WithMessage("Choose at least one valid character trait.");

        RuleFor(x => x.Note)
            .MaximumLength(500).WithMessage("Vouch note must not exceed 500 characters.")
            .When(x => x.Note is not null);
    }
}

public class RequestPeerVouchRequestValidator : AbstractValidator<RequestPeerVouchRequest>
{
    public RequestPeerVouchRequestValidator() => RuleFor(x => x.RequestedVoucherId).NotEmpty();
}
