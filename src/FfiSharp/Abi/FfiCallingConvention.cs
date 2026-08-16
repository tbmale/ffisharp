namespace FfiSharp.Abi
{
    /// <summary>
    /// Calling conventions the parser can express. On 64-bit targets (and most
    /// platforms) there is a single convention, so <see cref="Cdecl"/> and
    /// <see cref="Stdcall"/> are equivalent there; on 32-bit x86 they differ and
    /// are mapped to distinct libffi ABIs.
    /// </summary>
    public enum FfiCallingConvention
    {
        Cdecl = 0,
        Stdcall = 1
    }
}
