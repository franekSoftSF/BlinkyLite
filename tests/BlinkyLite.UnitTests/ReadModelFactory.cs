using System.Reflection;
using BlinkyLite.Contracts;
using ServerIssuance = BlinkyLite.Server.Data.Issuance;

namespace BlinkyLite.UnitTests;

/// <summary>
/// Builds read-model rows the way the database would.
/// </summary>
/// <remarks>
/// The entities have protected setters because nothing in the server may write
/// through them - every write is a bl_* function (docs/07). A test still needs
/// rows to read, so it sets them the only way left: reflection, here and
/// nowhere else, so the rule stays visible everywhere it matters.
/// </remarks>
internal static class ReadModelFactory
{
    public static ServerIssuance Issuance(Guid id, long serial, string targetUpn, byte[]? csr)
    {
        var row = new ServerIssuance();

        Set(row, nameof(ServerIssuance.Id), id);
        Set(row, nameof(ServerIssuance.CardSerial), serial);
        Set(row, nameof(ServerIssuance.TargetUpn), targetUpn);
        Set(row, nameof(ServerIssuance.State), csr is null ? IssuanceState.Reserved : IssuanceState.Attested);

        if (csr is not null)
        {
            Set(row, nameof(ServerIssuance.CsrDer), csr);
        }

        return row;
    }

    /// <summary>Any read-model row, property by property.</summary>
    public static T Row<T>(params (string Property, object? Value)[] values) where T : new()
    {
        var row = new T();
        foreach (var (property, value) in values)
        {
            Set(row, property, value);
        }

        return row;
    }

    private static void Set(object target, string property, object? value) =>
        target.GetType()
            .GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!
            .GetSetMethod(nonPublic: true)!
            .Invoke(target, [value]);
}
