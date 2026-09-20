using FluentValidation;
using Vouch.Application.Features.Messaging;

namespace Vouch.Application.Features.Messaging;

public class SendMessageRequestValidator : AbstractValidator<SendMessageRequest>
{
    public SendMessageRequestValidator()
    {
        RuleFor(x => x.Body)
            .NotEmpty().WithMessage("Message body is required.")
            .MinimumLength(1).WithMessage("Message must not be empty.")
            .MaximumLength(2000).WithMessage("Message must not exceed 2000 characters.")
            .Must(b => !string.IsNullOrWhiteSpace(b)).WithMessage("Message must not be blank or whitespace only.");
    }
}
