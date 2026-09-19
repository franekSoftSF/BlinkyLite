using BlinkyLite.Contracts;
using FluentNHibernate.Mapping;
using NHibernate.Type;

namespace BlinkyLite.Server.Data;

// SQL types are spelled out on every column: the schema is written by hand in
// db/migrations and SchemaValidator compares these names against it. Left to
// its defaults NHibernate would expect varchar(255) where the table has text.

public sealed class CardMap : ClassMap<Card>
{
    public CardMap()
    {
        Table("cards");
        ReadOnly();
        Not.LazyLoad();

        Id(x => x.Serial).Column("serial").CustomSqlType(Sql.Int8).GeneratedBy.Assigned();
        Map(x => x.Firmware).Column("firmware").CustomSqlType("text").Not.Nullable();
        Map(x => x.FormFactor).Column("form_factor").CustomSqlType(Sql.Int2);
        Map(x => x.HasPuk).Column("has_puk").CustomSqlType("boolean").Not.Nullable();
        Map(x => x.FirstSeenAt).Column("first_seen_at").CustomSqlType(Sql.Timestamp).Not.Nullable();
        Map(x => x.CurrentIssuanceId).Column("current_issuance_id").CustomSqlType("uuid");
    }
}

public sealed class IssuanceMap : ClassMap<Issuance>
{
    public IssuanceMap()
    {
        Table("issuances");
        ReadOnly();
        Not.LazyLoad();

        Id(x => x.Id).Column("id").CustomSqlType("uuid").GeneratedBy.Assigned();
        Map(x => x.CardSerial).Column("card_serial").CustomSqlType(Sql.Int8).Not.Nullable();
        Map(x => x.State).Column("state").CustomType<EnumStringType<IssuanceState>>().CustomSqlType("text").Not.Nullable();
        Map(x => x.TargetSam).Column("target_sam").CustomSqlType("text").Not.Nullable();
        Map(x => x.TargetUpn).Column("target_upn").CustomSqlType("text").Not.Nullable();
        Map(x => x.TargetSid).Column("target_sid").CustomSqlType("text").Not.Nullable();
        Map(x => x.TargetDisplayName).Column("target_display_name").CustomSqlType("text").Not.Nullable();
        Map(x => x.ProfileName).Column("profile_name").CustomSqlType("text").Not.Nullable();
        Map(x => x.TemplateName).Column("template_name").CustomSqlType("text").Not.Nullable();
        Map(x => x.CaConfig).Column("ca_config").CustomSqlType("text").Not.Nullable();
        Map(x => x.CaRequestId).Column("ca_request_id").CustomSqlType(Sql.Int4);
        Map(x => x.OperatorUpn).Column("operator_upn").CustomSqlType("text").Not.Nullable();
        Map(x => x.OperatorSid).Column("operator_sid").CustomSqlType("text").Not.Nullable();
        Map(x => x.WindowsIdentity).Column("windows_identity").CustomSqlType("text").Not.Nullable();
        Map(x => x.EaThumbprint).Column("ea_thumbprint").CustomSqlType("text");
        Map(x => x.Workstation).Column("workstation").CustomSqlType("text").Not.Nullable();
        Map(x => x.KeyAlgorithm).Column("key_algorithm").CustomSqlType("text");
        Map(x => x.PinPolicy).Column("pin_policy").CustomSqlType(Sql.Int2);
        Map(x => x.TouchPolicy).Column("touch_policy").CustomSqlType(Sql.Int2);
        Map(x => x.AttestationDer).Column("attestation_der").CustomSqlType("bytea");
        Map(x => x.AttestationIntermediateDer).Column("attestation_intermediate_der").CustomSqlType("bytea");
        Map(x => x.CsrDer).Column("csr_der").CustomSqlType("bytea");
        Map(x => x.CertificateDer).Column("certificate_der").CustomSqlType("bytea");
        Map(x => x.CertSerial).Column("cert_serial").CustomSqlType("text");
        Map(x => x.CertThumbprint).Column("cert_thumbprint").CustomSqlType("text");
        Map(x => x.CertNotBefore).Column("cert_not_before").CustomSqlType(Sql.Timestamp);
        Map(x => x.CertNotAfter).Column("cert_not_after").CustomSqlType(Sql.Timestamp);
        Map(x => x.Error).Column("error").CustomSqlType("text");
        Map(x => x.CreatedAt).Column("created_at").CustomSqlType(Sql.Timestamp).Not.Nullable();
        Map(x => x.UpdatedAt).Column("updated_at").CustomSqlType(Sql.Timestamp).Not.Nullable();
        Map(x => x.CompletedAt).Column("completed_at").CustomSqlType(Sql.Timestamp);
    }
}

public sealed class CardSecretMap : ClassMap<CardSecret>
{
    public CardSecretMap()
    {
        Table("card_secrets");
        ReadOnly();
        Not.LazyLoad();

        // puk_envelope and mgmt_key_envelope are deliberately not mapped: the
        // application role cannot select them, and a mapping that named them
        // would fail on the first query - which is the point.
        Id(x => x.Id).Column("id").CustomSqlType("uuid").GeneratedBy.Assigned();
        Map(x => x.IssuanceId).Column("issuance_id").CustomSqlType("uuid").Not.Nullable();
        Map(x => x.CardSerial).Column("card_serial").CustomSqlType(Sql.Int8).Not.Nullable();
        Map(x => x.State).Column("state").CustomType<EnumStringType<CardSecretState>>().CustomSqlType("text").Not.Nullable();
        Map(x => x.MgmtKeyAlgorithm).Column("mgmt_key_algorithm").CustomSqlType(Sql.Int2).Not.Nullable();
        Map(x => x.KekVersion).Column("kek_version").CustomSqlType(Sql.Int2).Not.Nullable();
        Map(x => x.PukDisclosedCount).Column("puk_disclosed_count").CustomSqlType(Sql.Int4).Not.Nullable();
        Map(x => x.CreatedAt).Column("created_at").CustomSqlType(Sql.Timestamp).Not.Nullable();
        Map(x => x.ActivatedAt).Column("activated_at").CustomSqlType(Sql.Timestamp);
        Map(x => x.RetiredAt).Column("retired_at").CustomSqlType(Sql.Timestamp);
    }
}

public sealed class AuditEventMap : ClassMap<AuditEvent>
{
    public AuditEventMap()
    {
        Table("audit_events");
        ReadOnly();
        Not.LazyLoad();

        Id(x => x.Id).Column("id").CustomSqlType(Sql.Int8).GeneratedBy.Assigned();
        Map(x => x.At).Column("at").CustomSqlType(Sql.Timestamp).Not.Nullable();
        Map(x => x.ActorUpn).Column("actor_upn").CustomSqlType("text").Not.Nullable();
        Map(x => x.ActorSid).Column("actor_sid").CustomSqlType("text");
        // text[] and inet have no NHibernate types; the database turns them into
        // text on the way out, which is all a read-only viewer needs.
        Map(x => x.ActorRoles).Formula("array_to_string(actor_roles, ',')");
        Map(x => x.Action).Column("action").CustomSqlType("text").Not.Nullable();
        Map(x => x.CardSerial).Column("card_serial").CustomSqlType(Sql.Int8);
        Map(x => x.IssuanceId).Column("issuance_id").CustomSqlType("uuid");
        Map(x => x.Data).Column("data").CustomSqlType("jsonb").Not.Nullable();
        Map(x => x.SourceIp).Formula("host(source_ip)");
    }
}

internal static class Sql
{
    /// <summary>
    /// Every timestamp is timestamptz, read back with DateTimeKind.Utc; Npgsql
    /// refuses a local DateTime for it, so a local time fails loudly.
    /// </summary>
    public const string Timestamp = "timestamptz";

    // Integer columns by their PostgreSQL type names: SchemaValidator compares
    // against udt_name, which says int8 for a column declared bigint, and
    // reports every bigint column as drift otherwise.
    public const string Int8 = "int8";
    public const string Int4 = "int4";
    public const string Int2 = "int2";
}
