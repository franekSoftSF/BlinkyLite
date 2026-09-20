using System.Security.Cryptography.X509Certificates;
using BlinkyLite.Contracts;
using BlinkyLite.Issuance.Api;
using BlinkyLite.Issuance.Eobo;
using BlinkyLite.Piv;

namespace BlinkyLite.Issuance;

/// <summary>What one issuance produced, and how far it got.</summary>
public sealed record IssuanceOutcome(
    Guid IssuanceId,
    uint Serial,
    bool Issued,
    bool PendingAtCa,
    int? CaRequestId,
    X509Certificate2? Certificate,
    string? Problem)
{
    /// <summary>True when the certificate is on the card and the server knows it.</summary>
    public bool IsComplete => Issued && Certificate is not null;
}

/// <summary>
/// The whole issuance, in one place: server, card, CA, card again, server.
/// </summary>
/// <remarks>
/// <para>
/// This is the "one issuance logic" the repository is built around. WPF and
/// PowerShell collect the input and show the progress; neither of them decides
/// anything about a card or a CA. If an <c>if</c> about either appears in a
/// shell, it is in the wrong place.
/// </para>
/// <para>
/// The order is not arbitrary and every step of it is a place the process can
/// die: the agent certificate is checked before the card is touched, the
/// secrets exist on the server before they exist on the token, the request id
/// is written down before the CA's answer is known, and the certificate is
/// read back off the card before the server is told the issuance is done.
/// </para>
/// </remarks>
public sealed class IssuanceRunner(ServerClient server, CardPersonaliser? personaliser = null)
{
    private readonly CardPersonaliser personaliser = personaliser ?? new CardPersonaliser();

    /// <summary>
    /// Issues one certificate onto one token.
    /// </summary>
    /// <param name="target">Who the certificate is for; only the SID travels.</param>
    /// <param name="agent">The operator's enrolment agent certificate, already checked.</param>
    public async Task<IssuanceOutcome> RunAsync(
        PivSession session,
        DirectoryUser target,
        IssuanceProfile profile,
        X509Certificate2 agent,
        IPinPrompt pin,
        string workstation,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("issuance.step.read-card");
        session.Select();

        var serial = session.GetSerialNumber()
            ?? throw new PersonalisationRefusedException("error.card.not-a-yubikey",
                "the card has no serial number, so it is not a YubiKey");

        var management = session.GetManagementKeyMetadata()
            ?? throw new PersonalisationRefusedException("error.card.too-old",
                "this firmware cannot report its management key algorithm");

        var puk = session.GetPukMetadata();

        progress?.Report("issuance.step.reserve");
        var reservation = await server.ReserveAsync(new StartIssuanceRequest(
            serial,
            session.GetFirmwareVersion().ToString(),
            !puk.IsUnrecoverable,
            management.Algorithm.ToString(),
            profile.Name,
            target.Sid,
            workstation), ct);

        var id = reservation.IssuanceId;

        try
        {
            var request = new PersonalisationRequest(
                Convert.FromBase64String(reservation.ManagementKey),
                reservation.Puk ?? PinComplexityPolicy.FactoryPuk,
                KeyAlgorithm(profile.KeyAlgorithm),
                Policy(profile.PinPolicy, PinPolicy.Once),
                Touch(profile.TouchPolicy));

            // The personaliser reports its own steps; two of them would read
            // as lies here - it has already been said that the card is being
            // read, and "done" is not true until the certificate is on it.
            var result = await personaliser.PersonaliseAsync(
                session, request, pin, Subject(target),
                new Steps(progress, "issuance.step.read-card", "issuance.step.done"), ct);

            // From here the card is known: the server marks the envelopes
            // active, and a later failure is recoverable rather than a token
            // nobody can open.
            await server.CustomisedAsync(id, ct);

            progress?.Report("issuance.step.server-check");
            await server.AttestedAsync(id, new AttestationUpload(
                result.AttestationDer, result.IntermediateDer, result.CsrDer), ct);

            progress?.Report("issuance.step.cmc");
            var attempt = CertEnrollCmc.Build(result.CsrDer, target.SamAccount, agent);
            if (!attempt.Succeeded)
            {
                throw new IssuanceFailedException("error.ca.cmc-failed",
                    $"{attempt.Failure!.Step}: {attempt.Failure.Message} ({attempt.Failure.HResultText})");
            }

            progress?.Report("issuance.step.submit");
            var answer = new CertificateAuthority(profile.CaConfig).Submit(attempt.Cmc!, profile.Template);

            // Written down before the answer is acted on: a certificate the CA
            // issued and nobody recorded cannot be found again (docs/02, 18).
            if (answer.RequestId > 0)
            {
                await server.SubmittedAsync(id, new SubmittedRequest(answer.RequestId, agent.Thumbprint), ct);
            }

            if (answer.IsPending)
            {
                await server.PendingAsync(id, ct);
                return new IssuanceOutcome(id, serial, false, true, answer.RequestId, null, answer.Message);
            }

            if (!answer.IsIssued)
            {
                throw new IssuanceFailedException("error.ca.refused",
                    $"{answer.Disposition}: {answer.Message} [{answer.StatusText} {CertificateAuthority.Explain(answer.Status)}]");
            }

            progress?.Report("issuance.step.write-certificate");
            var certificate = WriteToCard(session, answer.Certificate!);

            progress?.Report("issuance.step.done");
            await server.CompleteAsync(id, answer.Certificate!, ct);

            return new IssuanceOutcome(id, serial, true, false, answer.RequestId, certificate, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The server hears about every failure, and in the words that
            // caused it: an issuance that failed is something somebody will
            // have to explain months later.
            await Report(id, e, ct);
            throw;
        }
    }

    /// <summary>
    /// Writes the certificate and reads it back.
    /// </summary>
    /// <remarks>
    /// Read back because a PUT DATA that answers 9000 has been believed before
    /// and was wrong: the certificate runs to about a kilobyte and travels in
    /// chained APDUs, and a card that took only part of it still says yes.
    /// </remarks>
    private static X509Certificate2 WriteToCard(PivSession session, byte[] der)
    {
        session.PutCertificate(PivSlot.Authentication, der);

        var stored = session.GetCertificate(PivSlot.Authentication)
            ?? throw new IssuanceFailedException("error.card.certificate-not-written",
                "the card has no certificate in 9A after the write");

        var written = X509CertificateLoader.LoadCertificate(der);
        var readBack = X509CertificateLoader.LoadCertificate(stored);

        if (!string.Equals(written.Thumbprint, readBack.Thumbprint, StringComparison.Ordinal))
        {
            throw new IssuanceFailedException("error.card.certificate-not-written",
                $"the card holds {readBack.Thumbprint}, the CA issued {written.Thumbprint}");
        }

        return readBack;
    }

    private async Task Report(Guid id, Exception e, CancellationToken ct)
    {
        try
        {
            await server.FailedAsync(id, e.Message, ct);
        }
        catch (Exception reporting) when (reporting is ServerException or HttpRequestException)
        {
            // The original failure is the one worth having. Losing the note
            // about it is bad; replacing it with "could not write the note" is
            // worse.
        }
    }

    /// <summary>
    /// The subject of the request the card signs.
    /// </summary>
    /// <remarks>
    /// The CA builds the real subject from AD (docs/11), so this is not what
    /// ends up in the certificate. It still names the right person, because a
    /// request that says something else is confusing in every log it passes
    /// through.
    /// </remarks>
    private static string Subject(DirectoryUser target) =>
        $"CN={target.DisplayName.Replace(",", "\\,")}";

    private static PivAlgorithm KeyAlgorithm(string name) =>
        Enum.TryParse<PivAlgorithm>(name, ignoreCase: true, out var parsed) ? parsed : PivAlgorithm.Rsa2048;

    private static PinPolicy Policy(string name, PinPolicy fallback) =>
        Enum.TryParse<PinPolicy>(name, ignoreCase: true, out var parsed) ? parsed : fallback;

    private static TouchPolicy Touch(string name) =>
        Enum.TryParse<TouchPolicy>(name, ignoreCase: true, out var parsed) ? parsed : TouchPolicy.Never;
}

/// <summary>Passes the engine's steps on, minus the ones already said here.</summary>
internal sealed class Steps(IProgress<string>? inner, params string[] hidden) : IProgress<string>
{
    public void Report(string value)
    {
        if (!hidden.Contains(value, StringComparer.Ordinal))
        {
            inner?.Report(value);
        }
    }
}

/// <summary>An issuance that got past the card and failed anyway, with the key to show.</summary>
public sealed class IssuanceFailedException(string messageKey, string detail)
    : Exception($"{messageKey}: {detail}")
{
    public string MessageKey { get; } = messageKey;
}
