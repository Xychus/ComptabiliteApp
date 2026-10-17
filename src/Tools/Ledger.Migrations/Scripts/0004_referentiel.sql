-- Données de référence (pas des projections) : lues par les décisions d'écriture.

-- Plan comptable par entité. no_negative_balance : le compte a un flux account-{entité}-{compte}
-- et son solde ne peut pas devenir négatif dans son sens normal (ADR 0004, ADR 0008 H7).
CREATE TABLE ledger.accounts (
    entity_id           TEXT    NOT NULL,
    account_id          TEXT    NOT NULL,
    name                TEXT    NOT NULL,
    currency            CHAR(3) NOT NULL,
    normal_side         TEXT    NOT NULL,
    is_active           BOOLEAN NOT NULL DEFAULT TRUE,
    no_negative_balance BOOLEAN NOT NULL DEFAULT FALSE,
    CONSTRAINT accounts_pkey PRIMARY KEY (entity_id, account_id),
    CONSTRAINT accounts_currency_format CHECK (currency ~ '^[A-Z]{3}$'),
    CONSTRAINT accounts_normal_side CHECK (normal_side IN ('Debit', 'Credit'))
);

-- Calendrier fiscal par entité (ADR 0008 H4 : périodes mensuelles, à valider).
-- Le statut d'une période (ouverte, scellée) n'est pas ici : il vient de son flux d'événements.
CREATE TABLE ledger.periods (
    entity_id TEXT           NOT NULL,
    period    TEXT COLLATE "C" NOT NULL,
    starts_on DATE           NOT NULL,
    ends_on   DATE           NOT NULL,
    CONSTRAINT periods_pkey PRIMARY KEY (entity_id, period),
    CONSTRAINT periods_period_format CHECK (period ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'),
    CONSTRAINT periods_bounds CHECK (starts_on <= ends_on)
);
