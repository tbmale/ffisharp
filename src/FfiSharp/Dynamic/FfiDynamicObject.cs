using System;
using System.Dynamic;

namespace FfiSharp.Dynamic
{
    /// <summary>
    /// The dynamic view of an <see cref="FfiLibrary"/>. Method calls are routed via
    /// <see cref="TryInvokeMember"/> to the parsed header's function bindings.
    /// </summary>
    public sealed class FfiDynamicObject : DynamicObject, IDisposable
    {
        private readonly FfiLibrary _library;

        internal FfiDynamicObject(FfiLibrary library) => _library = library;

        /// <summary>The underlying library (exposed for explicit, non-dynamic use).</summary>
        public FfiLibrary Library => _library;

        public override bool TryInvokeMember(InvokeMemberBinder binder, object[] args, out object result)
        {
            result = _library.Invoke(binder.Name, args);
            return true;
        }

        public void Dispose() => _library.Dispose();
    }
}
