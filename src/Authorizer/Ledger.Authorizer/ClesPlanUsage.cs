using System.Collections.Concurrent;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace Ledger.Authorizer;

/// <summary>Clé API du plan d'usage d'un client (ADR 0031) ; <c>null</c> si le client n'a pas de plan.</summary>
public interface IMagasinClesPlanUsage
{
    Task<string?> TrouverAsync(string clientId, CancellationToken annulation);
}

/// <summary>Paramètres SSM <c>SecureString</c> (CMK <c>ssm</c>) : <c>{préfixe}/{clientId}</c>.</summary>
public sealed class MagasinClesPlanUsageSsm : IMagasinClesPlanUsage
{
    private readonly IAmazonSimpleSystemsManagement _ssm;
    private readonly string _prefixe;

    public MagasinClesPlanUsageSsm(IAmazonSimpleSystemsManagement ssm, string prefixe)
    {
        ArgumentNullException.ThrowIfNull(ssm);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefixe);
        _ssm = ssm;
        _prefixe = prefixe;
    }

    public async Task<string?> TrouverAsync(string clientId, CancellationToken annulation)
    {
        // Un clientId hors de ce jeu de caractères ne peut pas désigner un paramètre : refus sans appel.
        if (!NomClientValide(clientId))
        {
            return null;
        }

        try
        {
            var reponse = await _ssm.GetParameterAsync(
                new GetParameterRequest { Name = $"{_prefixe}/{clientId}", WithDecryption = true },
                annulation).ConfigureAwait(false);
            return reponse.Parameter.Value;
        }
        catch (ParameterNotFoundException)
        {
            return null;
        }
    }

    internal static bool NomClientValide(string clientId) =>
        clientId.Length is > 0 and <= 128 && clientId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

/// <summary>
/// Cache en mémoire devant un autre magasin, pour la durée de vie de l'instance Lambda : évite un
/// appel SSM par requête non mise en cache par API Gateway. Les absences sont aussi retenues.
/// </summary>
public sealed class MagasinClesPlanUsageEnCache : IMagasinClesPlanUsage
{
    private readonly IMagasinClesPlanUsage _source;
    private readonly TimeSpan _duree;
    private readonly TimeProvider _horloge;
    private readonly ConcurrentDictionary<string, (string? Cle, DateTimeOffset Expire)> _entrees = new(StringComparer.Ordinal);

    public MagasinClesPlanUsageEnCache(IMagasinClesPlanUsage source, TimeSpan duree, TimeProvider horloge)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(horloge);
        _source = source;
        _duree = duree;
        _horloge = horloge;
    }

    public async Task<string?> TrouverAsync(string clientId, CancellationToken annulation)
    {
        var maintenant = _horloge.GetUtcNow();
        if (_entrees.TryGetValue(clientId, out var entree) && entree.Expire > maintenant)
        {
            return entree.Cle;
        }

        var cle = await _source.TrouverAsync(clientId, annulation).ConfigureAwait(false);
        _entrees[clientId] = (cle, maintenant + _duree);
        return cle;
    }
}
