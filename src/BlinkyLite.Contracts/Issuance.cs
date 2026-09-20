namespace BlinkyLite.Contracts;

/// <summary>
/// What may be issued and how: one entry of <c>Issuance:Profiles</c> on the
/// server (D-21).
/// </summary>
/// <remarks>
/// The station has no file of its own with template names - it asks
/// <c>GET /api/profiles</c> and gets this. The template and the CA are in here
/// because the station is what talks to the CA: it builds the CMC and calls
/// <c>ICertRequest3.Submit</c>. What the station cannot do is decide what the
/// <i>record</i> says - the server writes the template from its own
/// configuration, never from the request.
/// </remarks>
public sealed record IssuanceProfile(
    string Name,
    string Template,
    string CaConfig,
    string KeyAlgorithm,
    string PinPolicy,
    string TouchPolicy);

/// <summary>
/// Body of <c>POST /api/issuances</c>: what the station read from the token
/// and who the key is for.
/// </summary>
/// <param name="TargetSid">
/// The only thing that names the person. The server looks the account up in AD
/// by this SID and writes down what AD says - a station cannot hand it a UPN
/// and a SID that belong to two different people.
/// </param>
public sealed record StartIssuanceRequest(
    long CardSerial,
    string Firmware,
    bool HasPuk,
    string ManagementKeyAlgorithm,
    string ProfileName,
    string TargetSid,
    string Workstation);

/// <summary>
/// What the station gets back: the secrets it is about to write to the card,
/// sealed in the database first (D-03).
/// </summary>
/// <param name="Puk">Null for a token with no PUK - a Bio, where only the management key is kept.</param>
public sealed record ReservationResponse(
    Guid IssuanceId,
    string? Puk,
    string ManagementKey,
    IssuanceProfile Profile,
    DirectoryUser Target);

/// <summary>Body of <c>POST /api/issuances/{id}/attestation</c>: the proof the card made about itself.</summary>
public sealed record AttestationUpload(byte[] Attestation, byte[] Intermediate, byte[] Csr);

/// <summary>Body of <c>POST /api/issuances/{id}/submitted</c>, written before the CA answers.</summary>
public sealed record SubmittedRequest(int CaRequestId, string EnrolmentAgentThumbprint);

/// <summary>Body of <c>POST /api/issuances/{id}/complete</c>: what the CA issued.</summary>
public sealed record CompleteRequest(byte[] Certificate);

/// <summary>Body of <c>POST /api/issuances/{id}/failed</c>.</summary>
public sealed record FailedRequest(string Error);

/// <summary>What an issuance looks like after a step; the state comes from the database.</summary>
public sealed record IssuanceStatus(Guid IssuanceId, IssuanceState State);
