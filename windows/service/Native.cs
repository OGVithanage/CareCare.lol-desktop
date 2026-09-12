using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CareCare;

internal static class Native
{
    [DllImport("CareCareWfp.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint ApplyPolicy(string proxyExecutable, string resolvers);
    [DllImport("CareCareWfp.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint RemovePolicy();
    internal static void Check(uint error)
    {
        if (error != 0) throw new Win32Exception(unchecked((int)error));
    }

}
