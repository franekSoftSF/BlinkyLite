using System.Reflection;
using System.Runtime.InteropServices;

namespace BlinkyLite.Issuance.Eobo;

/// <summary>
/// Late-bound calls into the COM that ships with Windows, with the one piece
/// of information a failure carries kept intact.
/// </summary>
/// <remarks>
/// <para>
/// Late-bound rather than through an interop assembly: interop would tie the
/// build to one Windows SDK and would have to be published for two
/// architectures, while these objects are IDispatch and answer
/// <see cref="Type.InvokeMember(string, BindingFlags, Binder, object, object[])"/>
/// without any of that.
/// </para>
/// <para>
/// The unwrapping matters. <c>InvokeMember</c> wraps everything in a
/// <see cref="TargetInvocationException"/>, so the HRESULT - which is the whole
/// of what CertEnroll and the CA say when they refuse - sits two levels down,
/// under a message about reflection.
/// </para>
/// </remarks>
internal static class Com
{
    public static object Create(string progId)
    {
        var type = Type.GetTypeFromProgID(progId)
            ?? throw new CertEnrollException(progId,
                "not registered on this machine - this component is part of Windows", 0);

        return Activator.CreateInstance(type)
            ?? throw new CertEnrollException(progId, "the object could not be created", 0);
    }

    public static object? Invoke(object target, string member, params object?[] args) =>
        Call(member, () => target.GetType().InvokeMember(
            member, BindingFlags.InvokeMethod, binder: null, target, args));

    public static void Set(object target, string member, object value) =>
        Call(member, () => target.GetType().InvokeMember(
            member, BindingFlags.SetProperty, binder: null, target, [value]));

    public static object? Get(object target, string member, params object?[] args) =>
        Call(member, () => target.GetType().InvokeMember(
            member, BindingFlags.GetProperty, binder: null, target, args));

    public static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            Marshal.FinalReleaseComObject(com);
        }
    }

    private static object? Call(string step, Func<object?> call)
    {
        try
        {
            return call();
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            var inner = e.InnerException;
            throw new CertEnrollException(step, inner.Message,
                inner is COMException com ? com.HResult : 0);
        }
        catch (COMException e)
        {
            throw new CertEnrollException(step, e.Message, e.HResult);
        }
        catch (Exception e) when (e is MissingMethodException or MissingMemberException or InvalidCastException)
        {
            throw new CertEnrollException(step, e.Message, 0);
        }
    }
}
