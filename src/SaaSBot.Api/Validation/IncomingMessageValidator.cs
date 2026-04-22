using FluentValidation;
using SaaSBot.Domain.Models;

namespace SaaSBot.Api.Validation;

/// <summary>
/// Validator for IncomingMessage to ensure data quality and security.
/// </summary>
public sealed class IncomingMessageValidator : AbstractValidator<IncomingMessage>
{
    public IncomingMessageValidator()
    {
        RuleFor(m => m.Channel)
            .NotEmpty().WithMessage("Channel is required")
            .MaximumLength(50).WithMessage("Channel must be max 50 characters");

        RuleFor(m => m.ChannelUserId)
            .NotEmpty().WithMessage("ChannelUserId is required")
            .MaximumLength(100).WithMessage("ChannelUserId must be max 100 characters");

        RuleFor(m => m.PhoneNumber)
            .MaximumLength(20).WithMessage("PhoneNumber must be max 20 characters")
            .When(m => !string.IsNullOrWhiteSpace(m.PhoneNumber));

        RuleFor(m => m.Text)
            .NotEmpty().WithMessage("Message text is required")
            .MaximumLength(4096).WithMessage("Message text must be max 4096 characters");

        RuleFor(m => m.ExternalMessageId)
            .MaximumLength(100).WithMessage("ExternalMessageId must be max 100 characters")
            .When(m => !string.IsNullOrWhiteSpace(m.ExternalMessageId));

        RuleFor(m => m.InteractiveType)
            .MaximumLength(50).WithMessage("InteractiveType must be max 50 characters")
            .When(m => !string.IsNullOrWhiteSpace(m.InteractiveType));

        RuleFor(m => m.ButtonId)
            .MaximumLength(100).WithMessage("ButtonId must be max 100 characters")
            .When(m => !string.IsNullOrWhiteSpace(m.ButtonId));

        RuleFor(m => m.ButtonTitle)
            .MaximumLength(100).WithMessage("ButtonTitle must be max 100 characters")
            .When(m => !string.IsNullOrWhiteSpace(m.ButtonTitle));

        RuleFor(m => m.ListReplyId)
            .MaximumLength(100).WithMessage("ListReplyId must be max 100 characters")
            .When(m => !string.IsNullOrWhiteSpace(m.ListReplyId));

        RuleFor(m => m.ListReplyTitle)
            .MaximumLength(200).WithMessage("ListReplyTitle must be max 200 characters")
            .When(m => !string.IsNullOrWhiteSpace(m.ListReplyTitle));
    }
}
