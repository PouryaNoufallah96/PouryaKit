using FluentValidation;
using FluentValidation.Results;
using PouryaKit.Cqrs;

namespace PouryaKit.Validation;

/// <summary>The rule that checks a paged query's <see cref="PageRequest"/> against its <see cref="PageDeclaration"/>.</summary>
public static class PageRuleExtensions
{
    extension<T>(IRuleBuilder<T, PageRequest> rule)
    {
        /// <summary>
        /// Fails with each validation Error <see cref="PageDeclaration.Validate(PageRequest)"/> returns for the request,
        /// with its message, its target as the property name and its code as the error code, so the Errors come back from
        /// the validation step as the declaration states them. A null request adds nothing; the validator's own
        /// <c>NotNull</c> refuses it.
        /// </summary>
        public IRuleBuilderOptionsConditions<T, PageRequest> AllowedBy(PageDeclaration declaration)
        {
            ArgumentNullException.ThrowIfNull(rule);
            ArgumentNullException.ThrowIfNull(declaration);

            return rule.Custom(
                (page, context) =>
                {
                    if (page is null)
                    {
                        return;
                    }

                    foreach (var error in declaration.Validate(page).Errors)
                    {
                        context.AddFailure(
                            new ValidationFailure(error.Target, error.Message)
                            {
                                ErrorCode = error.Code,
                            }
                        );
                    }
                }
            );
        }
    }
}
