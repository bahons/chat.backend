using System.Text.Json.Nodes;
using chat.Api.Businesses;
using chat.Contracts;
using chat.Domain.Businesses;
using chat.Domain.Identity;
using chat.Domain.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi;

namespace chat.Api.Endpoints;

internal static class ApiDocumentation
{
    public const string Version = "0.5.0";
    private const string Bearer = "Bearer";
    private static readonly string[] ContentTypes = ["application/pdf", "image/jpeg", "image/png"];

    public static RouteHandlerBuilder WithApiErrors(this RouteHandlerBuilder route, params int[] statuses)
    {
        foreach (var status in statuses)
        {
            route.Produces<ErrorEnvelope>(status);
        }

        return route;
    }

    public static void Configure(OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "chat API";
            document.Info.Version = Version;
            document.Servers?.Clear();
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
            {
                [Bearer] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" }
            };
            AddEnums(document);
            return Task.CompletedTask;
        });
        options.AddOperationTransformer(DescribeOperationAsync);
    }

    private static async Task DescribeOperationAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        var requiresBearer = metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any();
        if (requiresBearer)
        {
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(Bearer, context.Document)] = []
            });
        }

        if (context.Document is null)
        {
            return;
        }

        var schema = await context.GetOrCreateSchemaAsync(typeof(ErrorEnvelope), cancellationToken: cancellationToken);
        operation.Responses ??= new OpenApiResponses();
        if (context.Description.ParameterDescriptions.Count > 0 || operation.RequestBody is not null)
        {
            AddError("400", "Invalid request");
        }

        if (operation.RequestBody is not null)
        {
            AddError("413", "Request body too large");
            AddError("415", "Unsupported media type");
        }

        if (!metadata.OfType<DisableRateLimitingAttribute>().Any())
        {
            AddError("429", "Rate limit exceeded (RATE_LIMITED)");
            if (operation.Responses["429"] is OpenApiResponse throttled)
            {
                throttled.Headers ??= new Dictionary<string, IOpenApiHeader>();
                throttled.Headers["Retry-After"] = new OpenApiHeader
                {
                    Description = "Seconds until the applicable rate limit can admit another request.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1" }
                };
            }
        }

        AddError("500", "Internal error");
        AddError("403", "Access denied by transport policy or resource authorization");
        if (requiresBearer)
        {
            AddError("401", "Authentication required");
        }

        DescribeSpecialCases(operation);

        void AddError(string status, string description)
        {
            operation.Responses.TryAdd(status, new OpenApiResponse
            {
                Description = description,
                Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = schema } }
            });
        }
    }

    private static void DescribeSpecialCases(OpenApiOperation operation)
    {
        switch (operation.OperationId)
        {
            case "SubmitBusinessApplication":
                operation.Description = "Returns an opaque acknowledgement. An email already in use returns the same successful response shape.";
                break;
            case "ReapplyBusinessApplication":
                operation.Description = "An active owner submits a new draft for a rejected business while retaining prior decisions.";
                break;
            case "ListConversations":
                operation.Description = "New traversals use immutable creation time and ID with a versioned opaque cursor. Legacy cursors remain supported.";
                break;
            case "RegisterPushDevice":
                operation.Description = "A token can be transferred to another account only after the prior owner explicitly releases it.";
                break;
            case "UploadVerificationDocument":
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = BusinessDrafts.TokenHeader,
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "Opaque token for the application draft. Missing or invalid tokens return 404.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 43, MaxLength = 43 }
                });
                if (operation.RequestBody?.Content is { } uploads)
                {
                    foreach (var media in uploads.Values)
                    {
                        media.Schema = BinarySchema();
                    }
                }

                break;
            case "DownloadVerificationDocument":
                operation.Responses!["200"] = new OpenApiResponse
                {
                    Description = "Scanned document returned as an attachment.",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/octet-stream"] = new() { Schema = BinarySchema() }
                    },
                    Headers = new Dictionary<string, IOpenApiHeader>
                    {
                        ["Content-Disposition"] = new OpenApiHeader { Schema = new OpenApiSchema { Type = JsonSchemaType.String } }
                    }
                };
                break;
            case "ListBusinessApplications":
                var status = operation.Parameters?.OfType<OpenApiParameter>().FirstOrDefault(parameter => parameter.Name == "status");
                if (status?.Schema is OpenApiSchema statusSchema)
                {
                    statusSchema.Enum = Values(Enum.GetNames<BusinessStatus>());
                }

                break;
        }

        if (operation.OperationId is "SendMessage" or "CompleteUpload" or "DecideBusinessApplication"
            && operation.Responses!.TryGetValue("409", out var response) && response is OpenApiResponse conflict)
        {
            conflict.Description = operation.OperationId == "DecideBusinessApplication"
                ? "State conflict (CONFLICT) or a document is unavailable (MEDIA_NOT_READY)."
                : "An attachment is missing, unavailable or incompatible (MEDIA_NOT_READY).";
        }

        if (operation.Responses!.TryGetValue("503", out var unavailable) && unavailable is OpenApiResponse serviceUnavailable)
        {
            serviceUnavailable.Description = operation.OperationId switch
            {
                "RegisterClient" or "ForgotPassword" or "SubmitBusinessApplication" => "Account request could not be queued. Retry later.",
                "CompleteUpload" => "Scanner or storage is unavailable. Retry before the completion deadline.",
                "SendMessage" or "MarkConversationRead" => "Delivery queue is overloaded. Retry later with the same request.",
                _ => serviceUnavailable.Description
            };
        }
    }

    private static OpenApiSchema BinarySchema() => new() { Type = JsonSchemaType.String, Format = "binary" };

    private static void AddEnums(OpenApiDocument document)
    {
        foreach (var name in new[] { "AuthTokens", "MeResponse" })
        {
            Add(name, "userKind", Enum.GetNames<UserKind>());
        }

        Add("MembershipSummary", "role", Enum.GetNames<BusinessRole>());
        foreach (var name in new[] { "BusinessApplicationDetails", "BusinessApplicationReceipt", "BusinessApplicationSummary", "BusinessProfile" })
        {
            Add(name, "status", Enum.GetNames<BusinessStatus>());
        }

        Add("VerificationDocumentInfo", "scanStatus", Enum.GetNames<VerificationScanStatus>());
        Add("CompletedUpload", "status", [nameof(AttachmentStatus.READY), nameof(AttachmentStatus.QUARANTINED)]);
        Add("ConversationView", "side", Enum.GetNames<ParticipantSide>());
        Add("MessageView", "authorSide", Enum.GetNames<ParticipantSide>());
        Add("ReadReceiptView", "readerSide", Enum.GetNames<ParticipantSide>());
        Add("SendMessageRequest", "type", Enum.GetNames<MessageType>());
        Add("MessageView", "type", Enum.GetNames<MessageType>());
        foreach (var name in new[] { "MessageAttachmentView", "PresignUploadRequest", "VerificationDocumentInfo" })
        {
            Add(name, "contentType", ContentTypes);
        }

        Add("DecisionRequest", "decision", ["APPROVE", "REJECT"]);
        Add("HealthResponse", "status", ["healthy"]);
        Add("ApiError", "code", ["VALIDATION_ERROR", "UNAUTHENTICATED", "FORBIDDEN", "NOT_FOUND", "CONFLICT",
            "RATE_LIMITED", "VERIFICATION_REQUIRED", "SUSPENDED", "MEDIA_NOT_READY", "INTERNAL_ERROR"]);

        void Add(string type, string property, string[] values)
        {
            if (document.Components?.Schemas?.TryGetValue(type, out var contract) == true
                && contract.Properties?.TryGetValue(property, out var field) == true && field is OpenApiSchema schema)
            {
                schema.Enum = Values(values);
            }
        }
    }

    private static IList<JsonNode> Values(string[] values) => values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList();
}
