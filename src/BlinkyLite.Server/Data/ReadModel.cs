using BlinkyLite.Contracts;

namespace BlinkyLite.Server.Data;

// Read-only entities. Nothing in the server writes through them: the mappings
// are ReadOnly(), sessions are DefaultReadOnly, and the application role has no
// INSERT/UPDATE/DELETE anyway. Setters are protected because NHibernate needs
// them and nobody else does.

public enum CardSecretState
{
    Reserved,
    Active,
    Retired,
}

public class Card
{
    public virtual long Serial { get; protected set; }
    public virtual string Firmware { get; protected set; } = "";
    public virtual short? FormFactor { get; protected set; }
    public virtual bool HasPuk { get; protected set; }
    public virtual DateTime FirstSeenAt { get; protected set; }
    public virtual Guid? CurrentIssuanceId { get; protected set; }
}

public class Issuance
{
    public virtual Guid Id { get; protected set; }
    public virtual long CardSerial { get; protected set; }
    public virtual IssuanceState State { get; protected set; }
    public virtual string TargetSam { get; protected set; } = "";
    public virtual string TargetUpn { get; protected set; } = "";
    public virtual string TargetSid { get; protected set; } = "";
    public virtual string TargetDisplayName { get; protected set; } = "";
    public virtual string ProfileName { get; protected set; } = "";
    public virtual string TemplateName { get; protected set; } = "";
    public virtual string CaConfig { get; protected set; } = "";
    public virtual int? CaRequestId { get; protected set; }
    public virtual string OperatorUpn { get; protected set; } = "";
    public virtual string OperatorSid { get; protected set; } = "";
    public virtual string WindowsIdentity { get; protected set; } = "";
    public virtual string? EaThumbprint { get; protected set; }
    public virtual string Workstation { get; protected set; } = "";
    public virtual string? KeyAlgorithm { get; protected set; }
    public virtual short? PinPolicy { get; protected set; }
    public virtual short? TouchPolicy { get; protected set; }
    public virtual byte[]? AttestationDer { get; protected set; }
    public virtual byte[]? AttestationIntermediateDer { get; protected set; }
    public virtual byte[]? CsrDer { get; protected set; }
    public virtual byte[]? CertificateDer { get; protected set; }
    public virtual string? CertSerial { get; protected set; }
    public virtual string? CertThumbprint { get; protected set; }
    public virtual DateTime? CertNotBefore { get; protected set; }
    public virtual DateTime? CertNotAfter { get; protected set; }
    public virtual string? Error { get; protected set; }
    public virtual DateTime CreatedAt { get; protected set; }
    public virtual DateTime UpdatedAt { get; protected set; }
    public virtual DateTime? CompletedAt { get; protected set; }
}

/// <summary>
/// An envelope's bookkeeping. The envelopes themselves are not here and cannot
/// be: the application role has no SELECT on those two columns.
/// </summary>
public class CardSecret
{
    public virtual Guid Id { get; protected set; }
    public virtual Guid IssuanceId { get; protected set; }
    public virtual long CardSerial { get; protected set; }
    public virtual CardSecretState State { get; protected set; }
    public virtual short MgmtKeyAlgorithm { get; protected set; }
    public virtual short KekVersion { get; protected set; }
    public virtual int PukDisclosedCount { get; protected set; }
    public virtual DateTime CreatedAt { get; protected set; }
    public virtual DateTime? ActivatedAt { get; protected set; }
    public virtual DateTime? RetiredAt { get; protected set; }
}

public class AuditEvent
{
    public virtual long Id { get; protected set; }
    public virtual DateTime At { get; protected set; }
    public virtual string ActorUpn { get; protected set; } = "";
    public virtual string? ActorSid { get; protected set; }

    /// <summary>Comma-separated; text[] has no NHibernate type and nothing here needs one.</summary>
    public virtual string ActorRoles { get; protected set; } = "";

    public virtual string Action { get; protected set; } = "";
    public virtual long? CardSerial { get; protected set; }
    public virtual Guid? IssuanceId { get; protected set; }

    /// <summary>The jsonb payload as text.</summary>
    public virtual string Data { get; protected set; } = "{}";

    public virtual string? SourceIp { get; protected set; }
}
