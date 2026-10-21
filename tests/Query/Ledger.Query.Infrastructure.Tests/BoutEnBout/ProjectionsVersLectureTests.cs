using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application;
using Ledger.Projections.Application.Processing;
using Ledger.Projections.Application.Rebuild;
using Ledger.Projections.Application.Taxes;
using Ledger.Projections.Infrastructure;
using Ledger.Projections.Tests.Support;
using Ledger.Query.Application;
using Ledger.Query.Application.Consultations;
using Ledger.Query.Application.Fraicheur;
using Ledger.Query.Infrastructure.Tests.Postgres;
using Ledger.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using static Ledger.Projections.Tests.Support.JournalBuilder;

namespace Ledger.Query.Infrastructure.Tests.BoutEnBout;

/// <summary>
/// De bout en bout sur PostgreSQL : événements du journal → worker de projection réel
/// (<c>ledger_projection</c>) → consultations lues avec le rôle <c>ledger_query</c>. Vérifie que le
/// schéma lu par l'API de requête est celui qu'écrivent les projections, droits compris.
/// </summary>
[Trait("Category", "Integration")]
[Collection(GroupePostgres.Nom)]
public sealed class ProjectionsVersLectureTests(PostgresLecture base_) : IAsyncLifetime
{
    private const string Mars = "2026-03";
    private static readonly DateOnly _le10Mars = new(2026, 3, 10);
    private static readonly DateOnly _le20Mars = new(2026, 3, 20);
    private static readonly DateOnly _le25Mars = new(2026, 3, 25);

    private readonly JournalBuilder _journal = new("E2E-" + Guid.CreateVersion7().ToString("N"));
    private readonly Guid _vente = Guid.CreateVersion7();
    private ServiceProvider _projections = null!;
    private ServiceProvider _lecture = null!;
    private long _offset;
    private long _derniereSequence;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Entite => _journal.EntityId;

    public ValueTask InitializeAsync()
    {
        var projections = new ServiceCollection().AddLogging();
        projections.AddLedgerProjections(new TaxAccountMap(
        [
            new TaxAccount(Entite, "2310", TaxKind.GstCollected),
            new TaxAccount(Entite, "2320", TaxKind.QstCollected),
            new TaxAccount(Entite, "1350", TaxKind.GstPaid),
            new TaxAccount(Entite, "1360", TaxKind.QstPaid),
        ]));
        projections.AddPostgresProjectionStore(_ => base_.ConnexionProjections);
        _projections = projections.BuildServiceProvider();

        var lecture = new ServiceCollection();
        lecture.AddSingleton<IClock, SystemClock>();
        lecture.AjouterConsultations(new OptionsFraicheur());
        lecture.AjouterLecturePostgres(_ => base_.ConnexionLecture);
        _lecture = lecture.BuildServiceProvider();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Les_consultations_lisent_ce_que_les_projections_ont_ecrit()
    {
        await LivrerAsync(
            _journal.AccountDefined("1000", "Encaisse"),
            _journal.AccountDefined("1350", "CTI", "Debit"),
            _journal.AccountDefined("1360", "RTI", "Debit"),
            _journal.AccountDefined("2310", "TPS à percevoir", "Credit"),
            _journal.AccountDefined("2320", "TVQ à percevoir", "Credit"),
            _journal.AccountDefined("4000", "Ventes", "Credit"),
            _journal.AccountDefined("5000", "Achats"),
            _journal.Opened(Mars),
            _journal.Posted(_vente, _le10Mars, Debit("1000", 114.98m), Credit("4000", 100m), Credit("2310", 5m), Credit("2320", 9.98m)),
            _journal.Posted(_le20Mars, Debit("5000", 50m), Debit("1350", 2.5m), Debit("1360", 4.99m), Credit("1000", 57.49m)),
            _journal.Posted(_le20Mars, Debit("1000", 200m), Credit("4000", 200m)));
        var contrePassation = Guid.CreateVersion7();
        await LivrerAsync(_journal.Reversed(_vente, _le25Mars, contrePassation), _journal.Sealed(Mars));

        var encaisse = (await Consultation<ConsulterSoldeCompte>().ExecuterAsync(Entite, "1000", null, Ct)).Value;
        var encaisseLe15 = (await Consultation<ConsulterSoldeCompte>().ExecuterAsync(Entite, "1000", new DateOnly(2026, 3, 15), Ct)).Value;
        var balance = await Consultation<ConsulterBalanceVerification>().ExecuterAsync(Entite, null, Ct);
        var page = (await Consultation<ConsulterLignesCompte>().ExecuterAsync(Entite, "1000", null, 2, Ct)).Value;
        var suite = (await Consultation<ConsulterLignesCompte>().ExecuterAsync(Entite, "1000", page.CurseurSuivant, 2, Ct)).Value;
        var vente = (await Consultation<ConsulterEcriture>().ExecuterAsync(_vente, Ct)).Value;
        var periode = (await Consultation<ConsulterPeriode>().ExecuterAsync(Entite, Mars, Ct)).Value;
        var taxes = await Consultation<ConsulterDeclarationTpsTvq>().ExecuterAsync(Entite, Mars, Ct);

        // 114,98 − 57,49 + 200 − 114,98 ; au 15 mars, seule la vente est passée.
        Assert.Equal(142.51m, Assert.Single(encaisse.Soldes).Solde);
        Assert.Equal(114.98m, Assert.Single(encaisseLe15.Soldes).Solde);
        Assert.True(balance.Equilibree);
        Assert.Equal("Encaisse", balance.Lignes[0].Nom);
        Assert.Equal(4, page.Lignes.Count + suite.Lignes.Count);
        Assert.Null(suite.CurseurSuivant);
        Assert.Equal(contrePassation, vente.Ecriture.ContrePasseePar);
        Assert.Equal(("Sealed", 2L), (periode.Periode.Statut, periode.Periode.Version));
        Assert.NotNull(periode.Instantane);
        Assert.Equal(periode.Instantane.Mouvements.Sum(m => m.Debits), periode.Instantane.Mouvements.Sum(m => m.Credits));

        // La contre-passation annule la TPS/TVQ de la vente dans la même période (ADR 0018).
        var montants = Assert.Single(taxes.Montants);
        Assert.Equal((0m, 2.5m, -2.5m, 0m, 4.99m, -4.99m), (montants.TpsPercue, montants.TpsPayee, montants.TpsNette, montants.TvqPercue, montants.TvqPayee, montants.TvqNette));
        Assert.Equal("Sealed", taxes.StatutPeriode);

        // Toutes les projections ont vu le dernier événement : la réponse n'est pas périmée.
        Assert.False(balance.Fraicheur.Perimee);
        Assert.True(balance.Fraicheur.JusquaSequence >= _derniereSequence);
    }

    [Fact]
    public async Task Une_projection_reconstruite_reste_lisible_et_donne_les_memes_soldes()
    {
        await LivrerAsync(
            _journal.AccountDefined("1000", "Encaisse"),
            _journal.AccountDefined("4000", "Ventes", "Credit"),
            _journal.Opened(Mars),
            _journal.Posted(_le10Mars, Debit("1000", 80m), Credit("4000", 80m)));
        var avant = await Consultation<ConsulterBalanceVerification>().ExecuterAsync(Entite, null, Ct);

        await _projections.GetRequiredService<ProjectionRebuilder>().RebuildAsync(ProjectionNames.AccountBalances, Ct);
        var apres = await Consultation<ConsulterBalanceVerification>().ExecuterAsync(Entite, null, Ct);

        Assert.Equal(avant.Lignes, apres.Lignes);
        Assert.Equal(avant.Totaux, apres.Totaux);
    }

    public async ValueTask DisposeAsync()
    {
        await _projections.DisposeAsync();
        await _lecture.DisposeAsync();
    }

    private T Consultation<T>()
        where T : notnull => _lecture.GetRequiredService<T>();

    /// <summary>Valide chaque événement dans le journal, puis le livre au worker comme le ferait Kafka.</summary>
    private async Task LivrerAsync(params EventEnvelopeV1[] evenements)
    {
        var pompe = _projections.GetRequiredService<ProjectionPump>();
        foreach (var evenement in evenements)
        {
            var stocke = await base_.AjouterAuJournalAsync(evenement);
            _derniereSequence = stocke.GlobalSequence;
            await pompe.ProcessAsync(new SourceMessage(stocke, new SourcePosition("bout-en-bout-" + Entite, "ledger.events", 0, _offset++)), Ct);
        }
    }
}
