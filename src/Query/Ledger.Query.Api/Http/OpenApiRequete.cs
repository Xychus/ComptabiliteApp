using Ledger.Query.Api.Securite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;

namespace Ledger.Query.Api.Http;

/// <summary>
/// Contrat OpenAPI de l'API de requête (ADR 0019), généré au build dans
/// <c>docs/api/query-openapi.json</c> et servi en développement. Le titre et la version d'API
/// (<c>v1</c>) sont fixes : la version SemVer du composant n'y figure pas.
/// </summary>
public static class OpenApiRequete
{
    public const string NomDocument = "v1";
    public const string RouteDocument = "/openapi/{documentName}.json";
    public const string SchemaBearer = "Bearer";

    /// <summary>
    /// Paramètres de requête obligatoires : liés en <c>string?</c> pour que leur absence donne un
    /// <c>400</c> par champ (Problem Details), ils sont déclarés obligatoires dans le contrat.
    /// </summary>
    public static readonly IReadOnlySet<string> ParametresObligatoires = new HashSet<string>(["entityId", "period"], StringComparer.Ordinal);

    public static IServiceCollection AjouterOpenApiRequete(this IServiceCollection services) =>
        services.AddOpenApi(NomDocument, options =>
        {
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Grand livre — API de requête",
                    Version = "v1",
                    Description = "Soldes, balance de vérification, sous-livres, écritures, périodes et TPS/TVQ, lus dans les projections. "
                        + "Chaque réponse porte freshness (throughSequence, journalSequence, stale). "
                        + "Les montants sont des chaînes décimales accompagnées de leur devise. "
                        + "Les erreurs sont des Problem Details (RFC 9457) avec les membres code et details.",
                };
                document.Servers = [];
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
                document.Components.SecuritySchemes[SchemaBearer] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = $"Jeton Keycloak, audience {AuthentificationRequete.AudienceParDefaut}, scope {AuthentificationRequete.PorteeLecture}.",
                };
                return Task.CompletedTask;
            });
            options.AddSchemaTransformer((schema, context, _) =>
            {
                if (context.JsonTypeInfo.Type == typeof(ProblemDetails) || context.JsonTypeInfo.Type == typeof(HttpValidationProblemDetails))
                {
                    schema.Properties ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
                    schema.Properties["code"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Code de l'erreur (ex. AccountNotFound) ; type = urn:ledger:problem:{code}." };
                    schema.Properties["details"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        AdditionalProperties = new OpenApiSchema { Type = JsonSchemaType.String },
                        Description = "Cause précise du rejet (ex. entityId, accountId, period).",
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
                        new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(SchemaBearer, context.Document)] = [AuthentificationRequete.PorteeLecture] },
                    ];
                }

                var obligatoires = (operation.Parameters ?? []).OfType<OpenApiParameter>()
                    .Where(p => p.In == ParameterLocation.Query && p.Name is { } nom && ParametresObligatoires.Contains(nom));
                foreach (var parametre in obligatoires)
                {
                    parametre.Required = true;
                }

                return Task.CompletedTask;
            });
        });
}
