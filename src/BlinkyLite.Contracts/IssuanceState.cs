namespace BlinkyLite.Contracts;

/// <summary>
/// States of one issuance; the transitions are enforced by the bl_* functions
/// in the database, not here. See docs/01-architecture.md.
/// </summary>
public enum IssuanceState
{
    Reserved,
    Customised,
    Attested,
    PendingCa,
    Issued,
    Failed,
    Superseded,

    /// <summary>
    /// Wycofane z użycia (0058): klucz skasowano, zgubiono albo oddano poza
    /// BlinkyLite, więc zapisany PUK i management key już do niczego nie
    /// pasują. Stan, nie usunięcie - historia jest tylko dopisywana.
    /// </summary>
    Withdrawn,
}
