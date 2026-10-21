using Ledger.Query.Infrastructure.Memoire;
using Ledger.Query.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Query.Infrastructure.Tests.Contrat;

/// <summary>Suite de contrat contre l'implémentation en mémoire.</summary>
public sealed class LecteursEnMemoireTests : ContratLecteurs
{
    protected override MagasinLectureEssai CreerMagasin() => new MagasinEnMemoire();

    private sealed class MagasinEnMemoire : MagasinLectureEssai
    {
        private readonly DonneesLectureEnMemoire _donnees = new();
        private long _sequence;

        public override Task AmorcerAsync(JeuEssai jeu)
        {
            _donnees.Modifier(jeu.AppliquerA);
            return Task.CompletedTask;
        }

        public override Task<long> EnregistrerEvenementAsync(DateTimeOffset enregistreLe)
        {
            var sequence = ++_sequence;
            _donnees.Modifier(t => t.AvecEvenement(sequence, enregistreLe));
            return Task.FromResult(sequence);
        }

        public override Task PositionnerAsync(string projection, long sequence)
        {
            _donnees.Modifier(t => t.AvecPosition(projection, sequence));
            return Task.CompletedTask;
        }

        protected override void AjouterLecture(IServiceCollection services) => services.AjouterLectureEnMemoire(_donnees);
    }
}
