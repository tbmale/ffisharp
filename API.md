# FfiSharp API Reference

This document is the authoritative reference for the **public** FfiSharp API.
Everything here is part of the stable surface. Internal types (the libffi interop
layer, marshaller internals, the callback registry, the invocation frame, and the
C parser internals) are intentionally **not** listed — they are implementation
details and may change without notice.

- **Namespace root:** `FfiSharp`
- **Type system / ABI:** `FfiSharp.Abi`
- **Core target:** `netstandard2.0` (compatible with modern .NET and .NET Framework 4.7.2+)

---

## Table of contents

1. [Entry points](#entry-points)
2. [Library & functions](#library--functions)
3. [Structs](#structs)
4. [Callbacks](#callbacks)
5. [Type model](#type-model)
6. [Platform / ABI](#platform--abi)
7. [Type system cache](#type-system-cache)
8. [Backend (low-level)](#backend-low-level)
9. [Native library abstraction](#native-library-abstraction)
10. [Options & enums](#options--enums)
11. [Exceptions](#exceptions)

---

## Entry points

### `Ffi` (static)

The primary way to load a native library and a restricted C header.

| Member | Signature | Description |
|---|---|---|
| `Load` | `static dynamic Load(string libraryPath, string headerPath, FfiLoadOptions options = null)` | Loads a library + header and returns a `dynamic` caller (backed by `FfiDynamicObject`). |
| `LoadLibrary` | `static FfiLibrary LoadLibrary(string libraryPath, string headerPath, FfiLoadOptions options = null)` | Loads a library + header and returns the explicit, non-dynamic `FfiLibrary`. |

```csharp
using FfiSharp;

dynamic ffi = Ffi.Load("libexample.so", "example.h");
int result = ffi.add(10, 20);

FfiLibrary lib = Ffi.LoadLibrary("libexample.so", "example.h");
NativeFunction add = lib.GetFunction("add");
object r = add.Invoke(10, 20);
```

> **Security:** `Ffi.Load` / `Ffi.LoadLibrary` execute arbitrary native code. Both
> the library and the header must be treated as fully trusted input.

---

## Library & functions

### `FfiLibrary` (sealed, `IDisposable`)

A loaded native library + parsed header. Owns the native library handle, the libffi
backend, the type resolver, the function-binding cache, and the callback registry.

| Member | Signature | Description |
|---|---|---|
| `Platform` | `FfiPlatform Platform { get; }` | The target ABI configuration. |
| `Load` | `static FfiLibrary Load(string libraryPath, string headerPath, FfiLoadOptions options = null)` | Loads and parses; see `Ffi`. |
| `GetFunction` | `NativeFunction GetFunction(string name)` | Returns a bound function; throws `MissingSymbolException` if unknown/missing. |
| `GetStructType` | `FfiStructType GetStructType(string name)` | Resolves a struct type by typedef name or tag. |
| `CreateStruct` | `FfiStruct CreateStruct(string name)` | Creates an empty boxed struct value for a struct type name. |
| `RegisterCallback` | `CallbackHandle RegisterCallback(string functionName, Delegate callback)` | Registers a managed callback for a native function whose single parameter is a function pointer. |
| `Dispose` | `void Dispose()` | Rejects new operations, waits for in-flight ones, then releases callbacks/backend/library. Idempotent. |

**Thread-safety / disposal:** invocations of the same function are concurrent (no
global lock around `ffi_call`). `Dispose` drains in-flight operations before
releasing resources; further use throws `ObjectDisposedException`. Do not call
`Dispose` from *inside* a callback on the same thread (it is deferred in that case
and completed by a later non-reentrant dispose).

### `NativeFunction` (sealed)

A bound native function (obtained via `FfiLibrary.GetFunction`).

| Member | Signature | Description |
|---|---|---|
| `Name` | `string Name { get; }` | The function name. |
| `Invoke` | `object Invoke(params object[] arguments)` | Invokes the function; returns the marshalled result. |

```csharp
NativeFunction add = lib.GetFunction("add");
int result = (int)add.Invoke(10, 20);
```

### `FfiDynamicObject` (sealed, `DynamicObject`, `IDisposable`)

The `dynamic` view of an `FfiLibrary`. Member calls are routed to the parsed
header's function bindings via `TryInvokeMember`.

| Member | Signature | Description |
|---|---|---|
| `Library` | `FfiLibrary Library { get; }` | The underlying library. |
| `Dispose` | `void Dispose()` | Disposes the underlying library. |

---

## Structs

### `FfiStruct` (sealed)

A boxed C struct value, accessed by field name. This is the managed representation
returned by struct-returning functions and accepted by struct / struct-pointer
parameters. It does **not** use CLR struct layout.

| Member | Signature | Description |
|---|---|---|
| `Type` | `FfiStructType Type { get; }` | The struct type (authoritative for field validation). |
| indexer | `object this[string name] { get; set; }` | Reads/writes a field; setter throws `KeyNotFoundException` for an unknown field name. |
| `SetField` | `void SetField(string name, object value)` | Sets a field (validated immediately against `FfiStructType`). |
| `GetField` | `object GetField(string name)` | Reads a field; throws `KeyNotFoundException` if unset/unknown. |
| `TryGetField` | `bool TryGetField(string name, out object value)` | Non-throwing field read. |
| `Fields` | `IReadOnlyDictionary<string, object> Fields { get; }` | The backing field map. |

```csharp
FfiStruct p = lib.CreateStruct("Point");
p["x"] = 10;
p["y"] = 20.5;
int x = (int)p["x"];
```

> **Type compatibility:** a struct value must belong to the *exact* struct type the
> native signature expects; passing an unrelated `FfiStruct` throws
> `FfiMarshallingException` (never silently reinterpreted).

---

## Callbacks

### `CallbackHandle` (sealed, `IDisposable`)

A handle to a registered callback. Disposing it frees the libffi closure.

| Member | Signature | Description |
|---|---|---|
| `FunctionPointer` | `IntPtr FunctionPointer { get; }` | The native function pointer (may be passed to C code). |
| `LastException` | `Exception LastException { get; }` | The last exception captured by the callback (`Store`/`RethrowOnManagedBoundary` policies). |
| `Dispose` | `void Dispose()` | Frees the closure and removes it from the registry. |

> **Ownership rule:** the native library must not invoke the callback after its
> handle is disposed. Doing so is undefined native behavior.

```csharp
CallbackHandle h = lib.RegisterCallback("set_callback", (Action<int>)(v => Console.WriteLine(v)));
lib.GetFunction("fire_callback").Invoke(42);
h.Dispose();
```

---

## Type model

All types live in `FfiSharp.Abi`. The model is **independent of `System.Type`**;
the native representation (via libffi) is authoritative.

### `FfiType` (abstract)

| Member | Signature | Description |
|---|---|---|
| `Kind` | `abstract FfiTypeKind Kind { get; }` | The type category. |
| `Size` | `abstract int Size { get; }` | Native size in bytes (0 for `void`). |
| `Alignment` | `abstract int Alignment { get; }` | Native alignment in bytes. |

### `FfiPrimitiveType` (sealed, `: FfiType`)

| Member | Signature | Description |
|---|---|---|
| `Primitive` | `FfiPrimitive Primitive { get; }` | The logical C identity (e.g. `Long`). |
| `Storage` | `FfiPrimitive Storage { get; }` | The fixed-width storage kind actually marshalled (e.g. `LongLong` on LP64). |

### `FfiPointerType` (sealed, `: FfiType`)

| Member | Signature | Description |
|---|---|---|
| `Pointee` | `FfiType Pointee { get; }` | The pointed-to type, or `null` for `void*`. |
| `IsConst` | `bool IsConst { get; }` | Whether the pointee is `const` (e.g. `const char*`). |

### `FfiStructType` (sealed, `: FfiType`)

| Member | Signature | Description |
|---|---|---|
| `Name` | `string Name { get; }` | The struct name. |
| `Fields` | `IReadOnlyList<FfiStructField> Fields { get; }` | The fields. |
| `GetField` | `FfiStructField GetField(string name)` | Resolves a field by name (throws `KeyNotFoundException`). |

### `FfiStructField` (sealed)

| Member | Signature | Description |
|---|---|---|
| `Name` | `string Name { get; }` | Field name. |
| `Type` | `FfiType Type { get; }` | The element type (one element of an array field). |
| `ArrayLength` | `int ArrayLength { get; }` | `1` for scalar, `>1` for fixed-size array. |
| `Offset` | `int Offset { get; }` | Byte offset within the struct (computed by layout). |

### `FfiFunctionType` (sealed, `: FfiType`)

A C function-pointer type (return type + parameter types + calling convention).
Passed at the ABI level as `ffi_type_pointer`; the signature is retained to build
libffi closures.

| Member | Signature | Description |
|---|---|---|
| `ReturnType` | `FfiType ReturnType { get; }` | The return type. |
| `ParameterTypes` | `IReadOnlyList<FfiType> ParameterTypes { get; }` | The parameter types. |
| `CallingConvention` | `FfiCallingConvention CallingConvention { get; }` | The calling convention. |

### Enums

- **`FfiTypeKind`** — `Primitive`, `Pointer`, `Struct`, `Array`, `Function`.
- **`FfiPrimitive`** — `Void`, `Char`, `SChar`, `UChar`, `Short`, `UShort`, `Int`,
  `UInt`, `Long`, `ULong`, `LongLong`, `ULongLong`, `Float`, `Double`, `WChar`.

---

## Platform / ABI

### `FfiPlatform` (sealed)

Explicit target ABI configuration (managed metadata only; the actual ABI mechanics
are delegated to libffi).

| Member | Signature | Description |
|---|---|---|
| ctor | `FfiPlatform(FfiOS os, FfiArchitecture architecture)` | Constructs a platform model. |
| `OS` | `FfiOS OS { get; }` | Operating system. |
| `Architecture` | `FfiArchitecture Architecture { get; }` | CPU architecture. |
| `PointerSize` | `int PointerSize { get; }` | Native pointer size in bytes (4/8). |
| `CLongSize` | `int CLongSize { get; }` | Native `long` size (4 on LLP64, 8 on LP64). |
| `IsCharSigned` | `bool IsCharSigned { get; }` | Whether plain `char` is signed. |
| `WCharSize` | `int WCharSize { get; }` | Native `wchar_t` size (2 or 4). |
| `DefaultCallingConvention` | `FfiCallingConvention DefaultCallingConvention { get; }` | The platform default convention. |
| `Is64Bit` | `bool Is64Bit { get; }` | `PointerSize == 8`. |
| `ResolveStorage` | `FfiPrimitive ResolveStorage(FfiPrimitive logical)` | Maps a logical primitive to its fixed-width storage kind. |
| `PointerSizedSigned` | `FfiPrimitive PointerSizedSigned { get; }` | Signed integer matching the pointer size. |
| `PointerSizedUnsigned` | `FfiPrimitive PointerSizedUnsigned { get; }` | Unsigned integer matching the pointer size. |
| `Detect` | `static FfiPlatform Detect()` | Detects the running platform. |

### Enums

- **`FfiOS`** — `Windows`, `Linux`, `OSX`, `Unknown`.
- **`FfiArchitecture`** — `X86`, `X64`, `Arm64`, `Unknown`.
- **`FfiCallingConvention`** — `Cdecl`, `Stdcall`.

---

## Type system cache

### `FfiTypeSystem` (sealed)

Canonical, thread-safe cache of `FfiType` instances and C typedef aliases.

| Member | Signature | Description |
|---|---|---|
| `Platform` | `FfiPlatform Platform { get; }` | The platform this system models. |
| `GetPrimitive` | `FfiPrimitiveType GetPrimitive(FfiPrimitive primitive)` | Canonical primitive type. |
| `GetPointer` | `FfiPointerType GetPointer(FfiType pointee, bool pointeeIsConst = false)` | Canonical pointer type. |
| `AddTypedef` | `void AddTypedef(string name, FfiType type)` | Adds a typedef alias. |
| `TryResolveTypedef` | `bool TryResolveTypedef(string name, out FfiType type)` | Resolves a typedef non-throwing. |
| `ResolveTypedef` | `FfiType ResolveTypedef(string name)` | Resolves a typedef (throws `KeyNotFoundException`). |

Built-in typedefs: `int8_t`…`uint64_t`, `intptr_t`, `uintptr_t`, `size_t`,
`ssize_t`, `ptrdiff_t`, `wchar_t`.

> **Note:** `FfiTypeSystem`'s constructor takes platform-specific resolver
> delegates and is primarily obtained via `LibFfiBackend.Types`; most users will not
> construct it directly.

---

## Backend (low-level)

### `IFfiBackend` (interface)

Abstraction over the native ABI invocation engine (libffi).

| Member | Signature | Description |
|---|---|---|
| `CreateCallPlan` | `FfiCallPlan CreateCallPlan(FfiCallingConvention cc, FfiType returnType, IReadOnlyList<FfiType> argumentTypes)` | Prepares a reusable call plan. |
| `Invoke` | `object Invoke(FfiCallPlan plan, IntPtr function, object[] arguments)` | Invokes a function through the plan. |

### `LibFfiBackend` (sealed, `IFfiBackend`, `IDisposable`)

The libffi-backed implementation.

| Member | Signature | Description |
|---|---|---|
| ctor | `LibFfiBackend(string libFfiPath = null, FfiPlatform platform = null, StringEncoding stringEncoding = StringEncoding.Utf8)` | Loads (and owns) libffi from `libFfiPath` or auto-discovery. |
| ctor | `LibFfiBackend(INativeLibrary ffiLibrary, FfiPlatform platform = null, StringEncoding stringEncoding = StringEncoding.Utf8)` | **Borrows** an already-loaded libffi handle (caller retains ownership). |
| `DefaultAbi` | `int DefaultAbi { get; }` | libffi's default ABI id. |
| `LibFfiVersion` | `ulong LibFfiVersion { get; }` | libffi version number (`x*10000 + y*100 + z`). |
| `Platform` | `FfiPlatform Platform { get; }` | Target ABI configuration. |
| `Types` | `FfiTypeSystem Types { get; }` | The type-system cache. |
| `CreatePrimitiveType` | `FfiPrimitiveType CreatePrimitiveType(FfiPrimitive primitive)` | Canonical primitive type. |
| `CreatePointerType` | `FfiPointerType CreatePointerType(FfiType pointee)` | Canonical pointer type. |
| `CreateCallPlan` | `FfiCallPlan CreateCallPlan(...)` | Prepares a reusable call plan. |
| `Invoke` | `object Invoke(FfiCallPlan plan, IntPtr function, object[] arguments)` | Invokes through a plan. |
| `Dispose` | `void Dispose()` | Drains in-flight ops then unloads libffi (if owned). |

### `FfiCallPlan` (sealed, `IDisposable`)

A prepared, reusable call description for a specific signature. Immutable after
creation and shareable across threads. Owns the native `ffi_cif`, the `ffi_type**`
array, and (on libffi 3.7.0+) a reusable call plan.

| Member | Signature | Description |
|---|---|---|
| `ArgumentTypes` | `IReadOnlyList<FfiType> ArgumentTypes { get; }` | The argument types. |
| `ReturnType` | `FfiType ReturnType { get; }` | The return type. |
| `Dispose` | `void Dispose()` | Frees the native call description. |

---

## Native library abstraction

### `INativeLibrary` (interface, `IDisposable`)

Minimal abstraction over a loaded native library.

| Member | Signature | Description |
|---|---|---|
| `GetSymbol` | `IntPtr GetSymbol(string name)` | Resolves an exported symbol (`IntPtr.Zero` if not found). |

### `PlatformNativeLibrary` (sealed, `INativeLibrary`)

The portable implementation using `LoadLibrary`/`GetProcAddress`/`FreeLibrary`
(Windows) or `dlopen`/`dlsym`/`dlclose` (Unix). Works on every target.

| Member | Signature | Description |
|---|---|---|
| `Load` | `static PlatformNativeLibrary Load(string path)` | Loads a library. |
| `GetSymbol` | `IntPtr GetSymbol(string name)` | Resolves a symbol. |
| `GetSymbolOrThrow` | `IntPtr GetSymbolOrThrow(string name)` | Resolves or throws `MissingSymbolException`. |
| `Dispose` | `void Dispose()` | Unloads the library. |

---

## Options & enums

### `FfiLoadOptions` (sealed)

| Member | Default | Description |
|---|---|---|
| `LibFfiPath` | `null` | Explicit path to the libffi shared library (auto-discovered when null). |
| `Platform` | `null` | Explicit target ABI (auto-detected when null). |
| `TypeAliases` | `null` | Extra `typedef` aliases (`name` → C type text). |
| `StringEncoding` | `Utf8` | `const char*`↔`string` encoding. |
| `CallbackExceptionPolicy` | `Store` | How callback exceptions are handled. |

### `StringEncoding` (enum)

`Utf8` (default), `Ansi`, `Utf16`, `RawPointer` (no automatic conversion).

### `CallbackExceptionPolicy` (enum)

| Value | Description |
|---|---|
| `Ignore` | Swallow callback exceptions. |
| `Store` | Record the exception (inspect via `CallbackHandle.LastException`). |
| `RethrowOnManagedBoundary` | Record and rethrow on the next managed call. |

All policies catch at the native boundary — a callback exception never unwinds
through native frames.

---

## Exceptions

All derive from `FfiException` (which derives from `Exception`).

| Type | Description |
|---|---|
| `FfiException` | Base type for all FfiSharp errors. |
| `NativeLibraryLoadException` | A native library could not be loaded. |
| `MissingSymbolException` | A required native symbol was not found. |
| `FfiInvocationException` | A native invocation failed (e.g. bad cif/ABI). |
| `FfiMarshallingException` | A managed value could not be converted to/from native memory. |
| `FfiParseException` | A C header failed to lex/parse (`Line`/`Column` exposed). |
