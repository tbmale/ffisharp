using System;
using System.Collections.Generic;
using FfiSharp.Abi;
using FfiSharp.Bindings;

namespace FfiSharp.Backend
{
    /// <summary>
    /// Abstraction over the native ABI invocation engine (libffi). Managed code
    /// depends only on this interface; all libffi specifics live behind it.
    /// </summary>
    public interface IFfiBackend
    {
        FfiCallPlan CreateCallPlan(
            FfiCallingConvention callingConvention,
            FfiType returnType,
            IReadOnlyList<FfiType> argumentTypes);

        object Invoke(FfiCallPlan plan, IntPtr function, object[] arguments);
    }
}
