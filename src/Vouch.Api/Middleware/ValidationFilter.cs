using FluentValidation;

namespace Vouch.Api.Middleware;

/// <summary>
/// Step 11: Generic endpoint filter that runs FluentValidation on any
/// request body of type T before the handler executes.
/// 
/// Usage: .AddEndpointFilter&lt;ValidationFilter&lt;MyRequest&gt;&gt;()
/// Returns 400 with all validation errors as { errors: { field: [messages] } }
/// if validation fails; otherwise passes through to the handler.
/// </summary>
public class ValidationFilter<T> : IEndpointFilter where T : class
{
    private readonly IValidator<T> _validator;

    public ValidationFilter(IValidator<T> validator)
    {
        _validator = validator;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var argument = context.Arguments.OfType<T>().FirstOrDefault();

        if (argument is null)
            return await next(context);

        var result = await _validator.ValidateAsync(argument, context.HttpContext.RequestAborted);

        if (!result.IsValid)
        {
            var errors = result.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(e => e.ErrorMessage).ToArray()
                );

            return Results.ValidationProblem(errors);
        }

        return await next(context);
    }
}
