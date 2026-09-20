using FluentValidation;
using Vouch.Application.Features.Auth;

namespace Vouch.Application.Features.Auth;

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("A valid email address is required.")
            .MaximumLength(120).WithMessage("Email must not exceed 120 characters.");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.")
            .MinimumLength(8).WithMessage("Password must be at least 8 characters.")
            .MaximumLength(128).WithMessage("Password must not exceed 128 characters.")
            .Matches(@"[A-Z]").WithMessage("Password must contain at least one uppercase letter.")
            .Matches(@"[a-z]").WithMessage("Password must contain at least one lowercase letter.")
            .Matches(@"\d").WithMessage("Password must contain at least one number.");

        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage("Full name is required.")
            .MaximumLength(120).WithMessage("Full name must not exceed 120 characters.");

        RuleFor(x => x.Faculty)
            .NotEmpty().WithMessage("Faculty is required.")
            .MaximumLength(100).WithMessage("Faculty must not exceed 100 characters.");

        RuleFor(x => x.Department)
            .NotEmpty().WithMessage("Department is required.")
            .MaximumLength(100).WithMessage("Department must not exceed 100 characters.");

        RuleFor(x => x.AcademicYear)
            .InclusiveBetween(1, 7).WithMessage("Academic year must be between 1 and 7.");

        RuleFor(x => x.CampusCode)
            .NotEmpty().WithMessage("Campus code is required.")
            .MaximumLength(20).WithMessage("Campus code must not exceed 20 characters.");
    }
}

public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("A valid email address is required.");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.");
    }
}

public class CompleteOnboardingRequestValidator : AbstractValidator<CompleteOnboardingRequest>
{
    public CompleteOnboardingRequestValidator()
    {
        RuleFor(x => x.Bio)
            .MaximumLength(280).WithMessage("Bio must not exceed 280 characters.");

        RuleFor(x => x.DeepValues)
            .NotNull().WithMessage("Deep values are required.")
            .Must(v => v.Count >= 1 && v.Count <= 5)
                .WithMessage("Select between 1 and 5 deep values.");

        RuleForEach(x => x.DeepValues)
            .NotEmpty().WithMessage("Each deep value must not be empty.")
            .MaximumLength(60).WithMessage("Each deep value must not exceed 60 characters.");

        RuleFor(x => x.IntellectualInterests)
            .NotNull().WithMessage("Intellectual interests are required.")
            .Must(i => i.Count >= 1 && i.Count <= 5)
                .WithMessage("Select between 1 and 5 intellectual interests.");
    }
}
