using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CareCare;

internal static class Native
{
    [DllImport("CareCareWfp.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint ApplyPolicy(string addresses, string resolvers);
    [DllImport("CareCareWfp.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint RemovePolicy();
    [DllImport("CareCareWfp.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ResolveDomain(string domain, StringBuilder output, uint capacity, out uint ttl);

    internal static void Check(uint error)
    {
        if (error != 0) throw new Win32Exception(unchecked((int)error));
    }

    internal static Resolution Resolve(string domain)
    {
        var buffer = new StringBuilder(65536);
        Check(ResolveDomain(domain, buffer, (uint)buffer.Capacity, out var ttl));
        // One second is the minimum refresh interval, including zero-TTL records.
        return new(buffer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries),
            DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(ttl, 1u, 86400u)));
    }
}

internal sealed record Resolution(string[] Addresses, DateTimeOffset Expires);
