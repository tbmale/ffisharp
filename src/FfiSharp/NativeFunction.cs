using FfiSharp.Bindings;

namespace FfiSharp
{
    /// <summary>
    /// A bound native function obtained via <see cref="FfiLibrary.GetFunction"/>.
    /// Provides a non-dynamic invocation API for callers who prefer explicit objects.
    /// </summary>
    public sealed class NativeFunction
    {
        private readonly NativeFunctionBinding _binding;

        internal NativeFunction(NativeFunctionBinding binding) => _binding = binding;

        public string Name => _binding.Name;

        public object Invoke(params object[] arguments) => _binding.Invoke(arguments);
    }
}
