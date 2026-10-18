using FluentValidation;

namespace PouryaKit.Reporting.Module;

internal sealed class RequestReportCommandValidator : AbstractValidator<RequestReportCommand>
{
    public const int KindLength = 100;

    public RequestReportCommandValidator()
    {
        RuleFor(command => command.Kind).NotEmpty().MaximumLength(KindLength);
        RuleFor(command => command.Format).IsInEnum();
        RuleFor(command => command.Parameters).Must(parameters => parameters.Count <= 20).WithMessage("A report takes at most 20 parameters.");
        RuleForEach(command => command.Parameters).ChildRules(parameter =>
        {
            parameter.RuleFor(entry => entry.Key).NotEmpty().MaximumLength(100);
            parameter.RuleFor(entry => entry.Value).NotNull().MaximumLength(1000);
        });
    }
}
