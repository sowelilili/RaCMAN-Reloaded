using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RaCMAN.App;

/// <summary>
/// Reads the Windows Firewall rule list, so the client can tell whether the console's telemetry will
/// actually arrive instead of assuming it from a flag it set once.
/// <para>
/// Reading needs no administrator rights: <c>HNetCfg.FwPolicy2</c> hands the rules to any user, and
/// only writing one is privileged. It is asked for through late-bound COM rather than an interop
/// assembly because the whole of what is wanted is five properties, and because the app targets a
/// cross-platform framework where a Windows-only reference would have to be conditional.
/// </para>
/// <para>
/// Every failure is the same answer: null, meaning "not known", which
/// <see cref="FirewallHelper.Decide"/> treats as a reason to stay quiet rather than to nag. Nothing
/// here is reached by a test — the decision that uses it is pure and is what the tests exercise.
/// </para>
/// </summary>
public static class FirewallRules
{
    /// <summary>
    /// Every inbound rule that names <paramref name="executable"/>, or null when the list could not
    /// be read. Filtered inside the loop: a PC can carry thousands of rules and only the handful
    /// that name this program are worth the round trips to fetch in full.
    /// </summary>
    public static IReadOnlyList<FirewallRule>? ForProgram(string executable)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(executable)) return null;

        return Read(executable);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<FirewallRule>? Read(string executable)
    {
        object? policy = null;
        try
        {
            if (Type.GetTypeFromProgID("HNetCfg.FwPolicy2") is not { } type) return null;

            policy = Activator.CreateInstance(type);
            if (policy is null || Property(policy, "Rules") is not IEnumerable rules) return null;

            var found = new List<FirewallRule>();
            foreach (object? entry in rules)
            {
                if (entry is null) continue;

                try
                {
                    // The cheap discriminator first: a port rule, a service rule and every rule for
                    // some other program cost one property read and nothing else.
                    if (Property(entry, "ApplicationName") is not string program) continue;
                    if (!FirewallHelper.SamePath(program, executable)) continue;

                    found.Add(new FirewallRule(
                        program,
                        Inbound: Number(Property(entry, "Direction")) == DirectionIn,
                        Allow: Number(Property(entry, "Action")) == ActionAllow,
                        Enabled: Property(entry, "Enabled") is bool enabled && enabled,
                        Protocol: Number(Property(entry, "Protocol"))));
                }
                finally
                {
                    Release(entry);
                }
            }

            return found;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException
                                       or PlatformNotSupportedException or UnauthorizedAccessException
                                       or TargetInvocationException or MemberAccessException
                                       or InvalidOperationException)
        {
            // No firewall service, COM switched off, a policy store that will not open: the client
            // simply does not know, and says nothing.
            return null;
        }
        finally
        {
            Release(policy);
        }
    }

    /// <summary>NET_FW_RULE_DIR_IN.</summary>
    private const int DirectionIn = 1;

    /// <summary>NET_FW_ACTION_ALLOW.</summary>
    private const int ActionAllow = 1;

    [SupportedOSPlatform("windows")]
    private static object? Property(object target, string name)
    {
        try
        {
            return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null,
                CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is COMException or MissingMemberException or TargetInvocationException
                                       or MethodAccessException or InvalidOperationException)
        {
            // A rule the firewall will not describe is a rule that decides nothing.
            return null;
        }
    }

    private static int Number(object? value)
    {
        try
        {
            return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return 0;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Release(object? value)
    {
        try
        {
            if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidComObjectException)
        {
            // Already released, or never a COM object at all. The collector gets the rest.
        }
    }
}
