using Ledger.Projections.Tests.Support;
using Ledger.Query.Infrastructure.Tests.Postgres;
using Ledger.Query.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Query.Infrastructure.Tests.Contrat;

/// <summary>Suite de contrat contre PostgreSQL, lue avec le rôle <c>ledger_query</c>.</summary>
[Trait("Category", "Integration")]
[Collection(GroupePostgres.Nom)]
public sealed class LecteursNpgsqlTests(PostgresLecture base_) : ContratLecteurs
{
    protected override MagasinLectureEssai CreerMagasin() => new MagasinNpgsql(base_);

    private sealed class MagasinNpgsql(PostgresLecture base_) : MagasinLectureEssai
    {
        public override Task AmorcerAsync(JeuEssai jeu) => base_.AmorcerAsync(jeu);

        public override async Task<long> EnregistrerEvenementAsync(DateTimeOffset enregistreLe)
        {
            var evenement = new JournalBuilder(Entite).Opened(JeuEssai.Mars) with { RecordedAt = enregistreLe };
            return (await base_.AjouterAuJournalAsync(evenement)).GlobalSequence;
        }

        public override Task PositionnerAsync(string projection, long sequence) => base_.PositionnerAsync(projection, sequence);

        protected override void AjouterLecture(IServiceCollection services) => services.AjouterLecturePostgres(_ => base_.ConnexionLecture);
    }
}
