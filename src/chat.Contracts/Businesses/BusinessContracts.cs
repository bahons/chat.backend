using System.ComponentModel.DataAnnotations;
using chat.Contracts.Identity;

namespace chat.Contracts.Businesses;

public sealed record BusinessDetailsRequest(
    [property: Required, StringLength(200)] string LegalName,
    [property: Required, StringLength(100)] string DisplayName,
    [property: Required, StringLength(64)] string RegistrationNumber,
    [property: Required, RegularExpression("^[A-Za-z]{2}$")] string Country,
    [property: Required, StringLength(100)] string Category,
    [property: StringLength(2000)] string? Description);

public sealed record SubmitBusinessRequest(
    [property: Required] RegisterClientRequest Owner,
    [property: Required] BusinessDetailsRequest Business,
    [property: Required, MinLength(1), MaxLength(5)] Guid[] VerificationDocumentIds,
    [property: Required, StringLength(43, MinimumLength = 43)] string ApplicationDraftToken);

public sealed record ReapplyBusinessRequest(
    [property: Required, MinLength(1), MaxLength(5)] Guid[] VerificationDocumentIds,
    [property: Required, StringLength(43, MinimumLength = 43)] string ApplicationDraftToken);

public sealed record UpdateBusinessRequest(
    [property: StringLength(100)] string? DisplayName,
    [property: StringLength(100)] string? Category,
    [property: StringLength(2000)] string? Description);

public sealed record DecisionRequest(
    [property: Required, RegularExpression("^(APPROVE|REJECT)$")] string Decision,
    [property: StringLength(1000)] string? Reason);

public sealed record SuspendBusinessRequest([property: Required, StringLength(1000)] string Reason);

public sealed record BusinessDraftResponse(Guid DraftId, string ApplicationDraftToken, DateTimeOffset ExpiresAt);
public sealed record VerificationDocumentInfo(Guid Id, string ContentType, int Size, string ScanStatus = "PENDING");
public sealed record BusinessProfile(Guid Id, string DisplayName, string Category, string? Description, string Status, bool IsActive);
public sealed record BusinessCard(Guid Id, string DisplayName, string Category, string? Description);
public sealed record BusinessApplicationReceipt(Guid Id, Guid BusinessId, string Status);
public sealed record BusinessApplicationSummary(Guid Id, Guid BusinessId, string DisplayName, string Status, DateTimeOffset SubmittedAt);
public sealed record BusinessApplicationDetails(Guid Id, Guid BusinessId, string LegalName, string DisplayName,
    string RegistrationNumber, string Country, string Category, string Status, DateTimeOffset SubmittedAt,
    DateTimeOffset? ReviewedAt, string? Reason, IReadOnlyList<VerificationDocumentInfo> Documents);
