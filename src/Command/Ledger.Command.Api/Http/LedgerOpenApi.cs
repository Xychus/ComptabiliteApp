using Ledger.Command.Api.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;

namespace Ledger.Command.Api.Http;

/// <summary>
/// Contrat OpenAPI de l'API de commandes (ADR 0014), généré au build dans <c>docs/api/openapi.json</c>
/// et servi en développement. Le titre et la version d'API (<c>v1</c>) sont fixes : la version
/// SemVer du composant n'y figure pas, pour que le contrat ne change qu'avec l'API.
/// </summary>
public static class LedgerOpenApi
{
    public const string DocumentName = "v1";
    public const string DocumentRoute = "/openapi/{documentName}.json";
    public const string BearerScheme = "Bearer";

    public static IServiceCollection AddLedgerOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(DocumentName, options =>
        {
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Grand livre — API de commandes",
                    Version = "v1",
                    Description = "Écritures en partie double, contre-passations, périodes et plan comptable. "
                        + "Les erreurs sont des Problem Details (RFC 9457) avec les membres code et details. "
                        + "Les champs sequenceNumber, recordedAt, period et hash sont attribués par le serveur.",
                };
                document.Servers = [];
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
                document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = $"Jeton Keycloak, audience {LedgerAuthentication.DefaultAudience}, scope {LedgerAuthentication.WriteScope}.",
                };
                return Task.CompletedTask;
            });
            options.AddSchemaTransformer((schema, context, _) =>
            {
                if (context.JsonTypeInfo.Type == typeof(ProblemDetails) || context.JsonTypeInfo.Type == typeof(HttpValidationProblemDetails))
                {
                    schema.Properties ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
                    schema.Properties["code"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Code de l'erreur de domaine (ex. EntryNotBalanced) ; type = urn:ledger:problem:{code}." };
                    schema.Properties["details"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        AdditionalProperties = new OpenApiSchema { Type = JsonSchemaType.String },
                        Description = "Cause précise du rejet (ex. lineIndex, account, expectedVersion).",
                    };
                    schema.Properties["traceId"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Identifiant de trace W3C de la requête." };
                }

                return Task.CompletedTask;
            });
            options.AddOperationTransformer((operation, context, _) =>
            {
                if (context.Description.RelativePath?.StartsWith("v1/", StringComparison.Ordinal) == true)
                {
                    operation.Security =
                    [
                        new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(BearerScheme, context.Document)] = [LedgerAuthentication.WriteScope] },
                    ];
                }

                return Task.CompletedTask;
            });
        });
}
