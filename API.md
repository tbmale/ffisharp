# FfiSharp API Reference

FfiSharp is a runtime C FFI for C#.

It loads a native shared library, parses a deliberately restricted C header,
constructs a runtime representation of the declared ABI, and invokes native
functions through libffi.

It does not generate C# code, does not use a C compiler, and does not require
Clang/libclang.

The public API is divided into two levels:

- High-level API — normally all an application needs:
  `Ffi`, `FfiLibrary`, `NativeFunction`, `FfiStruct`, and `CallbackHandle`.
- Low-level API — for applications or libraries that need direct control over
  the FFI type model, libffi call plans, and native-library loading:
  `FfiType`, `FfiTypeSystem`, `FfiPlatform`, `LibFfiBackend`, `FfiCallPlan`,
  `INativeLibrary`, and related types.

---

## Table of contents

1. [Compatibility](#compatibility)
2. [Quick start](#quick-start)
3. [Core concepts](#core-concepts)
4. [Entry points](#entry-points)
5. [Library and functions](#library-and-functions)
6. [C type mapping](#c-type-mapping)
7. [Structs](#structs)
8. [Pointers, strings, and buffers](#pointers-strings-and-buffers)
9. [Callbacks](#callbacks)
10. [FFI type model](#ffi-type-model)
11. [Platform and ABI](#platform-and-abi)
12. [Type system](#type-system)
13. [Low-level libffi API](#low-level-libffi-api)
14. [Native library abstraction](#native-library-abstraction)
15. [Loading options](#loading-options)
16. [C language subset](#c-language-subset)
17. [ABI and platform semantics](#abi-and-platform-semantics)
18. [Ownership and lifetime](#ownership-and-lifetime)
19. [Thread safety and reentrancy](#thread-safety-and-reentrancy)
20. [Exception handling](#exception-handling)
21. [Caching and performance](#caching-and-performance)
22. [Unsupported C constructs](#unsupported-c-constructs)
23. [Security](#security)
24. [Supported platforms](#supported-platforms)
25. [Complete examples](#complete-examples)
26. [Exceptions](#exceptions)

---

# Compatibility

The core library targets:

```text
netstandard2.0
```

This makes the managed library usable from modern .NET and compatible .NET
Framework applications, including the project's validated .NET Framework
targets.

The FFI additionally requires a native `libffi` runtime library.

FfiSharp does not implement the native ABI itself. libffi is responsible for
ABI-specific argument passing, return values, aggregate handling, calling
conventions, and callback trampolines.

---

# Quick start

Suppose `example.h` contains:

```c
typedef struct {
    int x;
    double y;
} Point;

int add(int a, int b);

Point make_point(int x, double y);

void mutate_point(Point* point);

typedef void (*Callback)(int value);

void set_callback(Callback callback);
```

Load the library and header:

```csharp
using FfiSharp;

using (dynamic ffi = Ffi.Load("libexample.so", "example.h"))
{
    int sum = ffi.add(10, 20);

    FfiStruct point = ffi.make_point(10, 20.5);

    ffi.mutate_point(point);

    ffi.set_callback(
        (Action<int>)(value => Console.WriteLine(value)));
}
```

The same library can be accessed without `dynamic`:

```csharp
using (FfiLibrary library =
       Ffi.LoadLibrary("libexample.so", "example.h"))
{
    NativeFunction add = library.GetFunction("add");

    int result = (int)add.Invoke(10, 20);

    FfiStruct point = library.CreateStruct("Point");

    point["x"] = 10;
    point["y"] = 20.5;
}
```

The dynamic API is only a convenience layer. The explicit API is the underlying
object model.

---

# Core concepts

FfiSharp has five important concepts.

## Native library

`FfiLibrary` owns a loaded native library and the runtime state associated with
its parsed header.

## Native function

`NativeFunction` represents one resolved native function.

It has a boxed invocation interface:

```csharp
object result = function.Invoke(arguments);
```

The actual native signature comes from the parsed C header.

## FFI type

`FfiType` represents a C ABI type.

It is independent of `System.Type`.

For example, C `long` remains a C `long`; its native representation depends on
the selected ABI.

## Boxed struct

`FfiStruct` represents a C struct without requiring a generated CLR struct.

Fields are accessed by name:

```csharp
point["x"] = 10;

object x = point["x"];
```

## Callback

`CallbackHandle` represents a managed delegate exposed to native code through a
libffi closure.

The native code receives a function pointer.

---

# Entry points

## `Ffi`

Namespace:

```text
FfiSharp
```

Static entry point for the high-level API.

### `Load`

```csharp
static dynamic Load(
    string libraryPath,
    string headerPath,
    FfiLoadOptions options = null)
```

Loads a native library and parses the specified C header.

Returns an `FfiDynamicObject` through the `dynamic` interface.

Example:

```csharp
dynamic ffi = Ffi.Load(
    "libexample.so",
    "example.h");

int result = ffi.add(10, 20);
```

The returned object should normally be disposed:

```csharp
using (dynamic ffi =
       Ffi.Load("libexample.so", "example.h"))
{
    int result = ffi.add(10, 20);
}
```

### `LoadLibrary`

```csharp
static FfiLibrary LoadLibrary(
    string libraryPath,
    string headerPath,
    FfiLoadOptions options = null)
```

Loads a native library and parses the specified C header.

Returns the explicit `FfiLibrary` API.

Example:

```csharp
using (FfiLibrary library =
       Ffi.LoadLibrary("libexample.so", "example.h"))
{
    NativeFunction function =
        library.GetFunction("add");

    int result =
        (int)function.Invoke(10, 20);
}
```

### Loading behavior

The header is read and parsed when the library is loaded.

The native library is loaded separately.

Failure to load the native library results in a
`NativeLibraryLoadException`.

Failure to parse the header results in an `FfiParseException`.

---

# Library and functions

## `FfiLibrary`

Namespace:

```text
FfiSharp
```

Declaration:

```csharp
public sealed class FfiLibrary : IDisposable
```

Represents:

- the loaded native library;
- the parsed header;
- the C type resolver;
- the FFI type system;
- resolved function bindings;
- callback registrations;
- the libffi backend.

### `Platform`

```csharp
FfiPlatform Platform { get; }
```

Returns the ABI configuration used by the library.

### `Load`

```csharp
static FfiLibrary Load(
    string libraryPath,
    string headerPath,
    FfiLoadOptions options = null)
```

Equivalent to `Ffi.LoadLibrary`.

### `GetFunction`

```csharp
NativeFunction GetFunction(string name)
```

Returns a native function binding.

The function must be declared in the parsed header and its native symbol must
be resolvable.

Throws:

- `ArgumentNullException` for a null name;
- `MissingSymbolException` when the function is not declared or its native
  symbol cannot be resolved;
- `ObjectDisposedException` when the library has been disposed.

The returned `NativeFunction` can be reused and invoked concurrently.

Example:

```csharp
NativeFunction add =
    library.GetFunction("add");

int result =
    (int)add.Invoke(1, 2);
```

### `GetStructType`

```csharp
FfiStructType GetStructType(string name)
```

Resolves a struct type by typedef name or struct tag.

Example:

```csharp
FfiStructType pointType =
    library.GetStructType("Point");
```

Throws when the struct cannot be resolved.

### `CreateStruct`

```csharp
FfiStruct CreateStruct(string name)
```

Creates an empty boxed `FfiStruct` associated with the named struct type.

Example:

```csharp
FfiStruct point =
    library.CreateStruct("Point");

point["x"] = 10;
point["y"] = 20;
```

### `RegisterCallback`

```csharp
CallbackHandle RegisterCallback(
    string functionName,
    Delegate callback)
```

Registers a managed callback with a native function whose signature contains
exactly one parameter and that parameter is a function-pointer type.

The callback is converted into a libffi closure.

The resulting closure's native function pointer is passed to the named native
function.

Example:

```csharp
CallbackHandle handle =
    library.RegisterCallback(
        "set_callback",
        (Action<int>)(value =>
            Console.WriteLine(value)));
```

The callback handle owns the closure.

Keep the handle alive for as long as native code may invoke the callback.

### `Dispose`

```csharp
void Dispose()
```

Logically disposes the library.

After disposal:

- new operations are rejected;
- in-flight operations are allowed to finish;
- callback registrations are drained;
- call bindings are disposed;
- the libffi backend is disposed;
- the native target library is unloaded.

`Dispose` is idempotent.

Using a disposed library throws `ObjectDisposedException`.

> Reentrancy: do not dispose the owning library from inside an active callback
> or from an invocation that would wait for itself to complete. Native callbacks
> must be stopped/unregistered before their native resources are destroyed.

---

## `NativeFunction`

Namespace:

```text
FfiSharp
```

Declaration:

```csharp
public sealed class NativeFunction
```

Represents a resolved native function.

A `NativeFunction` is obtained from:

```csharp
FfiLibrary.GetFunction(...)
```

### `Name`

```csharp
string Name { get; }
```

The native function name used by the binding.

### `Invoke`

```csharp
object Invoke(params object[] arguments)
```

Invokes the native function.

Arguments are boxed managed values.

The return value is boxed as an `object`.

Example:

```csharp
NativeFunction add =
    library.GetFunction("add");

int result =
    (int)add.Invoke(10, 20);
```

For a `void` function, the result is `null`.

The arguments must be compatible with the C signature declared in the header.

Incorrect arguments can result in `FfiMarshallingException`.

---

## `FfiDynamicObject`

Namespace:

```text
FfiSharp
```

Declaration:

```csharp
public sealed class FfiDynamicObject :
    DynamicObject,
    IDisposable
```

Provides the dynamic form of an `FfiLibrary`.

A call such as:

```csharp
ffi.add(10, 20)
```

is resolved against the parsed header and routed to the corresponding
`NativeFunction`.

### `Library`

```csharp
FfiLibrary Library { get; }
```

The underlying library.

### `Dispose`

```csharp
void Dispose()
```

Disposes the underlying `FfiLibrary`.

---

# C type mapping

FfiSharp does not map C types directly to CLR types.

The C declaration determines the native ABI type, while the marshaller determines
how managed values are represented during a call.

Typical mappings include:

| C type | Typical managed value |
|---|---|
| `char` | numeric primitive |
| `signed char` | numeric primitive |
| `unsigned char` | numeric primitive |
| `short` | numeric primitive |
| `unsigned short` | numeric primitive |
| `int` | `int` |
| `unsigned int` | `uint` |
| `long` | ABI-dependent integer |
| `unsigned long` | ABI-dependent integer |
| `long long` | `long` |
| `unsigned long long` | `ulong` |
| `float` | `float` |
| `double` | `double` |
| `wchar_t` | ABI-dependent character representation |
| `void*` | `IntPtr` or supported buffer representation |
| `T*` | pointer representation appropriate to the marshaller |
| `const char*` | `string` or pointer representation |
| `const wchar_t*` | `string` or pointer representation |
| `struct T` | `FfiStruct` |
| `struct T*` | `FfiStruct` with pointer semantics |
| function pointer | managed `Delegate` when used as a callback |

The exact accepted managed representations are determined by the marshaller
and the native type.

---

# Structs

FfiSharp deliberately does not generate CLR types for C structs.

A C struct is represented by:

```text
FfiStructType
        +
FfiStruct
```

`FfiStructType` describes the ABI layout.

`FfiStruct` contains the managed field values.

This avoids requiring:

- generated CLR types;
- `StructLayout`;
- Reflection.Emit;
- compile-time C# definitions.

## `FfiStruct`

Namespace:

```text
FfiSharp
```

Declaration:

```csharp
public sealed class FfiStruct
```

A boxed runtime representation of a C struct.

It is identified by its exact `FfiStructType`.

It does not have CLR struct layout.

### `Type`

```csharp
FfiStructType Type { get; }
```

The exact C struct type represented by the object.

### Indexer

```csharp
object this[string name] { get; set; }
```

Reads or writes a named field.

Example:

```csharp
FfiStruct point =
    library.CreateStruct("Point");

point["x"] = 10;
point["y"] = 20.5;

int x = (int)point["x"];
double y = (double)point["y"];
```

The setter validates that the field name exists in the associated
`FfiStructType`.

An unknown field name throws `KeyNotFoundException`.

### `SetField`

```csharp
void SetField(
    string name,
    object value)
```

Sets a named field.

### `GetField`

```csharp
object GetField(string name)
```

Returns the value of a field.

### `TryGetField`

```csharp
bool TryGetField(
    string name,
    out object value)
```

Attempts to retrieve a field without throwing for an absent value.

### `Fields`

```csharp
IReadOnlyDictionary<string, object> Fields { get; }
```

Provides the managed field map.

The dictionary represents managed values, not native memory.

Changes to an `FfiStruct` are marshalled when the struct participates in a
native call.

### Struct type identity

A struct value must correspond to the exact `FfiStructType` expected by the
native signature.

An unrelated struct with identical-looking fields is not interchangeable merely
because both contain fields with the same names.

Passing an incompatible struct results in `FfiMarshallingException`.

---

## `FfiStructType`

Namespace:

```text
FfiSharp.Abi
```

Declaration:

```csharp
public sealed class FfiStructType : FfiType
```

Describes a C struct's native layout.

### `Name`

```csharp
string Name { get; }
```

The struct name.

### `Fields`

```csharp
IReadOnlyList<FfiStructField> Fields { get; }
```

The fields in declaration/layout order.

### `GetField`

```csharp
FfiStructField GetField(string name)
```

Returns the field definition.

Throws `KeyNotFoundException` if the field does not exist.

### Layout

FfiSharp uses sequential C-style struct layout.

Field offsets and total size are determined from the native type model and
platform alignment rules.

Packing directives and bit-fields are not supported.

---

## `FfiStructField`

Namespace:

```text
FfiSharp.Abi
```

Describes one field of a struct.

### `Name`

```csharp
string Name { get; }
```

Field name.

### `Type`

```csharp
FfiType Type { get; }
```

The element type.

For a scalar field, this is the field's type.

For a fixed-size array field, this is the array element type.

### `ArrayLength`

```csharp
int ArrayLength { get; }
```

Interpretation:

```text
1       scalar
> 1     fixed-size C array
```

For example:

```c
struct Packet {
    int length;
    unsigned char data[16];
};
```

is represented conceptually as:

```text
length:
    Type = int type
    ArrayLength = 1

data:
    Type = unsigned char
    ArrayLength = 16
```

### `Offset`

```csharp
int Offset { get; }
```

The native byte offset of the field within the containing struct.

---

# Pointers, strings, and buffers

Pointers are represented by `FfiPointerType`.

A pointer is not automatically converted into a CLR object merely because its
pointee type is known.

Raw pointer values are represented using `IntPtr` where the marshaller requires
an opaque pointer.

## `void*`

`void*` is an opaque native pointer.

It can be represented by an `IntPtr`.

Example:

```csharp
NativeFunction function =
    library.GetFunction("accept_pointer");

function.Invoke(IntPtr.Zero);
```

## Character pointers

`const char*` and `const wchar_t*` can use the configured string conversion.

The default string encoding is UTF-8.

See `StringEncoding`.

## `byte[]`

A `byte[]` can be used where the marshaller supports a native byte-buffer
representation.

The normal copy semantics are:

```text
managed byte[]
       |
       | copy into native storage
       v
native buffer
       |
       | native function mutates buffer
       v
native buffer
       |
       | copy back
       v
managed byte[]
```

The native function must not access beyond the managed array's length.

The native buffer exists only for the duration of the call.

## Struct pointers

When an `FfiStruct` is passed to a native function expecting a pointer to the
same struct type, FfiSharp marshals the struct into native storage.

For a mutable struct pointer, modifications made by native code are copied back
into the `FfiStruct`.

Example:

```c
typedef struct {
    int x;
    int y;
} Point;

void move_point(Point* point);
```

Usage:

```csharp
FfiStruct point =
    library.CreateStruct("Point");

point["x"] = 10;
point["y"] = 20;

library.GetFunction("move_point")
       .Invoke(point);
```

After the call, the managed fields reflect native mutations.

## Raw returned pointers

Raw returned pointers are caller-owned.

FfiSharp does not infer ownership and does not automatically free arbitrary native
pointers returned by a function.

If a C API returns memory that must be freed with a particular native function,
the caller is responsible for invoking that API correctly.

---

# Callbacks

A C callback is represented by a managed delegate and exposed to native code
through a libffi closure.

Example C API:

```c
typedef void (*Callback)(int value);

void set_callback(Callback callback);
void fire_callback(int value);
```

Register:

```csharp
CallbackHandle handle =
    library.RegisterCallback(
        "set_callback",
        (Action<int>)(value =>
            Console.WriteLine(value)));
```

Then:

```csharp
library.GetFunction("fire_callback")
       .Invoke(42);
```

Finally:

```csharp
handle.Dispose();
```

## `CallbackHandle`

Namespace:

```text
FfiSharp
```

Declaration:

```csharp
public sealed class CallbackHandle : IDisposable
```

Owns a registered native callback closure.

### `FunctionPointer`

```csharp
IntPtr FunctionPointer { get; }
```

The native function pointer associated with the libffi closure.

### `LastException`

```csharp
Exception LastException { get; }
```

The most recently captured callback exception when the configured callback
exception policy stores exceptions.

### `Dispose`

```csharp
void Dispose()
```

Logically disposes the callback and removes it from the callback registry.

Native code must stop invoking the callback before its handle is disposed.

Invoking a callback after its handle has been disposed is undefined behavior from
the native caller's perspective.

When disposal occurs while the callback is executing, physical closure destruction
may be deferred until the callback has safely returned.

---

# FFI type model

All ABI type classes are in:

```text
FfiSharp.Abi
```

The type model describes C types independently from CLR `System.Type`.

## `FfiType`

Abstract base class:

```csharp
public abstract class FfiType
```

### `Kind`

```csharp
abstract FfiTypeKind Kind { get; }
```

### `Size`

```csharp
abstract int Size { get; }
```

Native size in bytes.

`void` has size zero.

### `Alignment`

```csharp
abstract int Alignment { get; }
```

Native alignment in bytes.

## `FfiPrimitiveType`

Represents a C primitive.

```csharp
public sealed class FfiPrimitiveType : FfiType
```

### `Primitive`

```csharp
FfiPrimitive Primitive { get; }
```

The logical C primitive.

### `Storage`

```csharp
FfiPrimitive Storage { get; }
```

The concrete fixed-width storage representation selected for the current ABI.

This distinction matters for C types whose size varies by platform.

## `FfiPointerType`

Represents a C pointer.

```csharp
public sealed class FfiPointerType : FfiType
```

### `Pointee`

```csharp
FfiType Pointee { get; }
```

The pointed-to type.

`null` represents `void*`.

### `IsConst`

```csharp
bool IsConst { get; }
```

Indicates whether the pointee was declared `const`.

This is type metadata; it does not by itself provide memory protection.

## `FfiFunctionType`

Represents a C function-pointer type.

```csharp
public sealed class FfiFunctionType : FfiType
```

### `ReturnType`

```csharp
FfiType ReturnType { get; }
```

### `ParameterTypes`

```csharp
IReadOnlyList<FfiType> ParameterTypes { get; }
```

### `CallingConvention`

```csharp
FfiCallingConvention CallingConvention { get; }
```

## `FfiTypeKind`

```csharp
public enum FfiTypeKind
```

Values:

```text
Primitive
Pointer
Struct
Array
Function
```

## `FfiPrimitive`

```csharp
public enum FfiPrimitive
```

Values:

```text
Void
Char
SChar
UChar
Short
UShort
Int
UInt
Long
ULong
LongLong
ULongLong
Float
Double
WChar
```

---

# Platform and ABI

## `FfiPlatform`

Namespace:

```text
FfiSharp.Abi
```

Represents target ABI metadata used by the type system.

### Constructor

```csharp
FfiPlatform(
    FfiOS os,
    FfiArchitecture architecture)
```

### `OS`

```csharp
FfiOS OS { get; }
```

### `Architecture`

```csharp
FfiArchitecture Architecture { get; }
```

### `PointerSize`

```csharp
int PointerSize { get; }
```

Native pointer size in bytes.

### `CLongSize`

```csharp
int CLongSize { get; }
```

Native C `long` size.

### `IsCharSigned`

```csharp
bool IsCharSigned { get; }
```

### `WCharSize`

```csharp
int WCharSize { get; }
```

Native `wchar_t` size.

### `DefaultCallingConvention`

```csharp
FfiCallingConvention DefaultCallingConvention { get; }
```

### `Is64Bit`

```csharp
bool Is64Bit { get; }
```

Equivalent to:

```text
PointerSize == 8
```

### `ResolveStorage`

```csharp
FfiPrimitive ResolveStorage(
    FfiPrimitive logical)
```

Maps a logical C primitive to concrete storage required by the target ABI.

### `PointerSizedSigned`

```csharp
FfiPrimitive PointerSizedSigned { get; }
```

### `PointerSizedUnsigned`

```csharp
FfiPrimitive PointerSizedUnsigned { get; }
```

### `Detect`

```csharp
static FfiPlatform Detect()
```

Detects the current runtime platform.

## `FfiOS`

Values:

```text
Windows
Linux
OSX
Unknown
```

## `FfiArchitecture`

Values:

```text
X86
X64
Arm64
Unknown
```

## `FfiCallingConvention`

Values:

```text
Cdecl
Stdcall
```

`Stdcall` has ABI significance primarily on 32-bit Windows x86.

---

# Type system

## `FfiTypeSystem`

Namespace:

```text
FfiSharp.Abi
```

Represents the canonical runtime C type system.

### `Platform`

```csharp
FfiPlatform Platform { get; }
```

### `GetPrimitive`

```csharp
FfiPrimitiveType GetPrimitive(
    FfiPrimitive primitive)
```

### `GetPointer`

```csharp
FfiPointerType GetPointer(
    FfiType pointee,
    bool pointeeIsConst = false)
```

For `void*`, `pointee` is `null`.

### `AddTypedef`

```csharp
void AddTypedef(
    string name,
    FfiType type)
```

### `TryResolveTypedef`

```csharp
bool TryResolveTypedef(
    string name,
    out FfiType type)
```

### `ResolveTypedef`

```csharp
FfiType ResolveTypedef(
    string name)
```

Throws `KeyNotFoundException` if the alias does not exist.

### Built-in typedefs

The type system recognizes platform-appropriate forms of:

```text
int8_t
uint8_t
int16_t
uint16_t
int32_t
uint32_t
int64_t
uint64_t
intptr_t
uintptr_t
size_t
ssize_t
ptrdiff_t
wchar_t
```

---

# Low-level libffi API

The following API is public but intended primarily for advanced users.

Most applications should use `Ffi.Load` or `Ffi.LoadLibrary`.

## `IFfiBackend`

Namespace:

```text
FfiSharp.Backend
```

Abstraction over the native ABI invocation engine.

### `CreateCallPlan`

```csharp
FfiCallPlan CreateCallPlan(
    FfiCallingConvention callingConvention,
    FfiType returnType,
    IReadOnlyList<FfiType> argumentTypes)
```

### `Invoke`

```csharp
object Invoke(
    FfiCallPlan plan,
    IntPtr function,
    object[] arguments)
```

## `LibFfiBackend`

Namespace:

```text
FfiSharp.Backend
```

Declaration:

```csharp
public sealed class LibFfiBackend :
    IFfiBackend,
    IDisposable
```

The libffi implementation of `IFfiBackend`.

### Constructor

```csharp
LibFfiBackend(
    string libFfiPath = null,
    FfiPlatform platform = null,
    StringEncoding stringEncoding = StringEncoding.Utf8)
```

### Borrowed-library constructor

```csharp
LibFfiBackend(
    INativeLibrary ffiLibrary,
    FfiPlatform platform = null,
    StringEncoding stringEncoding = StringEncoding.Utf8)
```

The supplied `INativeLibrary` remains owned by the caller.

### `DefaultAbi`

```csharp
int DefaultAbi { get; }
```

### `LibFfiVersion`

```csharp
ulong LibFfiVersion { get; }
```

Version encoding:

```text
major * 10000 + minor * 100 + patch
```

### `Platform`

```csharp
FfiPlatform Platform { get; }
```

### `Types`

```csharp
FfiTypeSystem Types { get; }
```

### `CreatePrimitiveType`

```csharp
FfiPrimitiveType CreatePrimitiveType(
    FfiPrimitive primitive)
```

### `CreatePointerType`

```csharp
FfiPointerType CreatePointerType(
    FfiType pointee)
```

### `CreateCallPlan`

```csharp
FfiCallPlan CreateCallPlan(...)
```

### `Invoke`

```csharp
object Invoke(
    FfiCallPlan plan,
    IntPtr function,
    object[] arguments)
```

### `Dispose`

```csharp
void Dispose()
```

Stops new operations, drains active operations, and releases libffi resources
owned by this backend.

## `FfiCallPlan`

Namespace:

```text
FfiSharp.Bindings
```

Represents a prepared native call description.

A call plan is immutable after creation and reusable.

### `ArgumentTypes`

```csharp
IReadOnlyList<FfiType> ArgumentTypes { get; }
```

### `ReturnType`

```csharp
FfiType ReturnType { get; }
```

### `Dispose`

```csharp
void Dispose()
```

---

# Native library abstraction

## `INativeLibrary`

Namespace:

```text
FfiSharp.Interop
```

Minimal abstraction over a loaded native library.

### `GetSymbol`

```csharp
IntPtr GetSymbol(string name)
```

Returns `IntPtr.Zero` when the symbol is not found.

Implementations are `IDisposable`.

## `PlatformNativeLibrary`

Namespace:

```text
FfiSharp.Interop
```

Declaration:

```csharp
public sealed class PlatformNativeLibrary :
    INativeLibrary
```

Provides the platform-specific native loader.

Windows uses:

```text
LoadLibrary
GetProcAddress
FreeLibrary
```

Unix-like systems use:

```text
dlopen
dlsym
dlclose
```

### `Load`

```csharp
static PlatformNativeLibrary Load(
    string path)
```

### `GetSymbol`

```csharp
IntPtr GetSymbol(
    string name)
```

### `GetSymbolOrThrow`

```csharp
IntPtr GetSymbolOrThrow(
    string name)
```

### `Dispose`

```csharp
void Dispose()
```

---

# Loading options

## `FfiLoadOptions`

Namespace:

```text
FfiSharp
```

Configuration used by `Ffi.Load` and `FfiLibrary.Load`.

### `LibFfiPath`

```csharp
string LibFfiPath { get; set; }
```

Explicit path to the libffi shared library.

Default: `null`.

### `Platform`

```csharp
FfiPlatform Platform { get; set; }
```

Explicit target ABI.

Default: `null`, meaning automatic detection.

### `TypeAliases`

```csharp
IDictionary<string, string> TypeAliases { get; set; }
```

Additional typedef aliases supplied as C type text.

### `StringEncoding`

```csharp
StringEncoding StringEncoding { get; set; }
```

Default: `Utf8`.

### `CallbackExceptionPolicy`

```csharp
CallbackExceptionPolicy CallbackExceptionPolicy { get; set; }
```

Default: `Store`.

---

# `StringEncoding`

Values:

```text
Utf8
Ansi
Utf16
RawPointer
```

### `Utf8`

Default. Strings are converted using UTF-8.

### `Ansi`

Uses the platform/default ANSI representation.

### `Utf16`

Uses UTF-16 representation.

### `RawPointer`

Disables automatic string conversion and treats the relevant native pointer as
a raw pointer.

Use `IntPtr` when working with raw pointer semantics.

---

# `CallbackExceptionPolicy`

Values:

```text
Ignore
Store
RethrowOnManagedBoundary
```

## `Ignore`

Exceptions thrown by managed callbacks are swallowed.

## `Store`

The exception is captured and exposed through:

```csharp
CallbackHandle.LastException
```

## `RethrowOnManagedBoundary`

The exception is captured and rethrown when execution next crosses a managed
FfiSharp invocation boundary.

In all policies:

> A managed callback exception never unwinds through native stack frames.

---

# C language subset

The header parser is intentionally not a general-purpose C compiler.

It accepts a restricted FFI-oriented subset.

## Supported primitives

```text
void
char
signed char
unsigned char
short
unsigned short
int
unsigned int
long
unsigned long
long long
unsigned long long
float
double
wchar_t
```

The type system also supports platform-defined integer typedefs such as:

```text
int8_t
uint8_t
int16_t
uint16_t
int32_t
uint32_t
int64_t
uint64_t
intptr_t
uintptr_t
size_t
ssize_t
ptrdiff_t
```

## Supported qualifiers

```text
const
volatile
```

## Supported typedefs

C typedef declarations are supported.

Example:

```c
typedef unsigned long Handle;
```

## Supported pointers

Examples:

```c
void*
int*
const char*
Point*
const Point*
```

## Supported structs

Examples:

```c
typedef struct {
    int x;
    double y;
} Point;
```

and tagged structs:

```c
struct Point {
    int x;
    double y;
};
```

Nested structs and fixed-size array fields are supported.

Struct layout is sequential.

## Supported functions

Function declarations with named or anonymous parameters are supported.

Examples:

```c
int add(int a, int b);

void process(void* data);

Point create_point(int x, double y);
```

## Supported calling conventions

```text
cdecl
stdcall
```

The parser accepts the supported forms of:

```text
__cdecl
__stdcall
```

## Supported function pointers

Examples:

```c
typedef void (*Callback)(int);

void set_callback(Callback callback);
```

---

# Header preprocessing

FfiSharp intentionally has only a minimal preprocessor.

It does not recursively parse system headers.

The parser can skip:

- comments;
- harmless `#if` / `#endif` guards;
- `#include` lines.

Use `FfiLoadOptions.TypeAliases` for type definitions that would otherwise
normally be supplied through macros or system headers.

The parser is deliberately designed to fail loudly on unsupported C syntax.

---

# ABI and platform semantics

## libffi is authoritative

FfiSharp does not reimplement the native ABI.

libffi is responsible for:

- argument placement;
- register/stack rules;
- calling conventions;
- aggregate argument passing;
- aggregate return values;
- callbacks and trampolines.

FfiSharp's responsibility is:

```text
C declaration
    ↓
runtime C type model
    ↓
managed/native marshalling
    ↓
libffi invocation
```

## C `long`

C `long` is platform-dependent.

Typical ABIs:

```text
Windows x64:
    long = 32 bits

Linux x64:
    long = 64 bits

macOS x64:
    long = 64 bits
```

FfiSharp therefore keeps `FfiPrimitive.Long` as the logical C type and resolves
its concrete storage through `FfiPlatform`.

Do not assume C `long` means CLR `long`.

## `wchar_t`

`wchar_t` is ABI-dependent.

Typical platforms:

```text
Windows:
    16-bit

Linux/macOS:
    32-bit
```

## Plain `char`

The signedness of plain C `char` is implementation-defined.

FfiSharp uses the platform assumptions encoded by `FfiPlatform`.

## `stdcall`

`stdcall` is a distinct ABI primarily on Windows x86.

On Windows x64 and ARM64, there is effectively one standard calling ABI.

On 32-bit Windows, FfiSharp also handles common stdcall export decoration:

```text
name@N
_name@N
```

where `N` is the argument byte count.

Hidden struct-return parameters are not included in this decoration calculation.

Functions requiring such hidden-parameter decoration should provide an
undecorated export.

---

# Ownership and lifetime

FFI code is inherently ownership-sensitive.

FfiSharp does not infer ownership of arbitrary native resources.

## Library ownership

`FfiLibrary` owns:

- the target native library handle;
- its libffi backend;
- function bindings;
- callback registrations;
- associated native call plans.

Dispose the library only after native code has stopped using its functions and
callbacks.

## Native function ownership

A `NativeFunction` is owned by its `FfiLibrary`.

Do not use it after the owning library has been disposed.

## Call-plan ownership

An `FfiCallPlan` owns native libffi call-description resources.

It is reusable and can be shared concurrently.

It must remain alive while calls using it are active.

## Raw pointer ownership

FfiSharp does not automatically free arbitrary native pointers.

If a C API returns memory that must be freed with a particular native function,
the caller is responsible for invoking that API correctly.

## Strings

String arguments are copied into temporary native storage.

For supported returned strings, FfiSharp creates a managed string copy.

The returned native buffer remains owned by the native library/callee.

FfiSharp does not automatically free arbitrary returned string pointers.

## `byte[]`

Managed byte arrays are copied into temporary native storage for the duration
of the call.

Where copy-back semantics apply, modifications are copied back into the managed
array after the native call completes.

## Struct pointers

Struct-pointer arguments use temporary native storage.

For mutable struct-pointer calls, native modifications are copied back into the
managed `FfiStruct`.

## Callback ownership

A `CallbackHandle` owns its managed/native callback registration.

The native side must stop invoking the callback before the handle is disposed.

Calling a disposed callback pointer from native code is undefined behavior.

---

# Thread safety and reentrancy

FfiSharp's core objects are designed for concurrent use.

## Native function invocation

The same `NativeFunction` can be invoked concurrently from multiple threads.

There is no global mutex around the native `ffi_call`.

Per-invocation temporary state is isolated.

## Call plans

`FfiCallPlan` instances are immutable after construction and can be shared by
concurrent calls.

## Type system

`FfiTypeSystem` caches are thread-safe.

Canonical type objects can be shared.

## `FfiStruct`

An individual `FfiStruct` should not be concurrently mutated from multiple
threads without external synchronization.

The struct is a mutable managed value container.

## Callbacks

Callbacks may arrive on native-created threads.

The managed callback is entered through a libffi closure.

Managed exceptions are caught before returning through the native ABI.

## Disposal

Disposal prevents new operations and waits for existing operations to drain
before releasing the native resources they depend on.

This prevents ordinary concurrent disposal from unloading a library while a
native invocation is still executing.

### Reentrant disposal

Do not dispose the same library from inside an active callback:

```csharp
library.RegisterCallback(
    "set_callback",
    (Action)(() =>
    {
        library.Dispose();
    }));
```

A callback that disposes the same library can cause disposal to wait for the
callback that is currently executing.

The safe pattern is to stop/unregister callbacks and dispose from outside the
active callback.

---

# Exception handling

All FfiSharp-specific exceptions derive from:

```text
FfiException
```

which derives from:

```text
Exception
```

## `FfiException`

Base class for FfiSharp failures.

## `NativeLibraryLoadException`

The requested native library could not be loaded.

Typical causes include:

- missing library;
- invalid library;
- incompatible architecture;
- missing dependent native library;
- loader failure.

## `MissingSymbolException`

A required native symbol could not be found.

This can occur when:

- the function is not declared in the header;
- the native library does not export the expected symbol;
- a decorated stdcall symbol could not be resolved.

## `FfiInvocationException`

A native invocation failed at the FFI/ABI level.

## `FfiMarshallingException`

A managed value could not be converted to or from the native representation.

Examples include:

- incompatible argument type;
- incompatible struct;
- invalid pointer representation;
- unsupported conversion;
- invalid native result conversion.

## `FfiParseException`

The C header could not be parsed.

The exception exposes line/column information to help locate the parse failure.

---

# Caching and performance

FfiSharp separates cold-path work from the invocation hot path.

The following are cached:

- parsed header;
- resolved symbols;
- canonical FFI types;
- typedef aliases;
- function call plans;
- callback closures.

A function's ABI description is constructed once and reused.

On libffi versions supporting reusable call plans, FfiSharp uses libffi's
reusable call-plan mechanism.

When that API is unavailable, FfiSharp falls back to the normal libffi
invocation API.

## InvocationFrame

The implementation uses a reusable, nesting-aware invocation frame for
temporary call storage.

The frame holds resources such as:

- argument-value pointers;
- aligned primitive storage;
- return storage;
- cleanup information.

Nested/reentrant calls use separate frames so an inner invocation cannot
overwrite an outer invocation's temporary storage.

## Primitive fast path

Primitive-only signatures can write directly into invocation-frame storage.

This avoids unnecessary per-argument native allocations.

## Cleanup representation

The invocation path uses compact cleanup metadata rather than allocating an
individual managed cleanup delegate for every argument.

## Callback exception fast path

The normal case where no callback exception is pending is handled without
locking and scanning the callback registry.

The slower exception-draining path is only entered when the pending-exception
state indicates that one exists.

---

# Unsupported C constructs

The parser intentionally rejects constructs outside the supported FFI subset.

These are not silently approximated.

Currently unsupported:

- unions;
- bit-fields;
- packed structs;
- explicit struct packing/alignment attributes;
- variadic functions (`...`);
- enums;
- global variables;
- function definitions with bodies;
- arbitrary C preprocessor logic;
- C++ language constructs;
- C++ classes;
- C++ name mangling;
- C++ exceptions;
- templates.

For example:

```c
int printf(const char*, ...);
```

is not a supported variadic function declaration.

FfiSharp will not silently treat it as an ordinary fixed-argument function.

---

# Security

Loading an FfiSharp library executes native code.

For example:

```csharp
Ffi.Load(
    "untrusted.so",
    "untrusted.h");
```

must be treated as equivalent to loading that native code directly.

Neither the native library nor the header should be considered safe merely
because FfiSharp performs parsing.

Important:

- FfiSharp provides no native-code sandbox.
- A malicious native library has the same privileges as the hosting process.
- A malformed header should produce a parser/type error, not native code execution.
- The native library itself must always be treated as trusted input.

---

# Supported platforms

The repository's validated configurations should be treated as authoritative;
do not infer support merely from whether a particular runtime happens to load.

The currently vendored native runtime layouts include:

```text
runtimes/
    win-x64/native/libffi-8.dll
    win-x86/native/libffi-8.dll
    linux-x64/native/libffi.so.8
```

Additional architectures require corresponding validated libffi builds before
they should be considered supported.

---

# Complete examples

## Basic function

C:

```c
int add(int a, int b);
```

C#:

```csharp
using FfiSharp;

using (FfiLibrary library =
       Ffi.LoadLibrary("example.so", "example.h"))
{
    NativeFunction add =
        library.GetFunction("add");

    int result =
        (int)add.Invoke(10, 20);

    Console.WriteLine(result);
}
```

## Struct return

C:

```c
typedef struct {
    int x;
    double y;
} Point;

Point make_point(int x, double y);
```

C#:

```csharp
FfiStruct point =
    library.GetFunction("make_point")
           .Invoke(10, 20.5) as FfiStruct;

int x = (int)point["x"];
double y = (double)point["y"];
```

## Struct mutation

C:

```c
typedef struct {
    int x;
    int y;
} Point;

void move_point(Point* point);
```

C#:

```csharp
FfiStruct point =
    library.CreateStruct("Point");

point["x"] = 10;
point["y"] = 20;

library.GetFunction("move_point")
       .Invoke(point);

Console.WriteLine(point["x"]);
Console.WriteLine(point["y"]);
```

## Callback

C:

```c
typedef void (*Callback)(int value);

void set_callback(Callback callback);
void fire_callback(int value);
```

C#:

```csharp
CallbackHandle callback =
    library.RegisterCallback(
        "set_callback",
        (Action<int>)(value =>
            Console.WriteLine(
                "Native value: " + value)));

library.GetFunction("fire_callback")
       .Invoke(42);

callback.Dispose();
```

## Callback exception storage

```csharp
var options = new FfiLoadOptions
{
    CallbackExceptionPolicy =
        CallbackExceptionPolicy.Store
};

using (FfiLibrary library =
       Ffi.LoadLibrary(
           "example.so",
           "example.h",
           options))
{
    CallbackHandle callback =
        library.RegisterCallback(
            "set_callback",
            (Action<int>)(value =>
            {
                throw new InvalidOperationException(
                    "callback failed");
            }));

    library.GetFunction("fire_callback")
           .Invoke(42);

    Exception error =
        callback.LastException;

    callback.Dispose();
}
```

The exception does not unwind through the native caller.

## Explicit platform configuration

```csharp
var options = new FfiLoadOptions
{
    Platform = new FfiPlatform(
        FfiOS.Windows,
        FfiArchitecture.X64)
};

using (FfiLibrary library =
       Ffi.LoadLibrary(
           "example.dll",
           "example.h",
           options))
{
    // ...
}
```

## Explicit libffi path

```csharp
var options = new FfiLoadOptions
{
    LibFfiPath = "/opt/custom/libffi.so.8"
};

using (FfiLibrary library =
       Ffi.LoadLibrary(
           "libexample.so",
           "example.h",
           options))
{
    int result =
        (int)library.GetFunction("add")
                    .Invoke(1, 2);
}
```

## Low-level libffi invocation

Advanced applications can bypass the header parser and construct the type and
call-plan objects directly.

```csharp
using FfiSharp.Abi;
using FfiSharp.Backend;
using FfiSharp.Interop;

using (var backend =
       new LibFfiBackend("/opt/lib/libffi.so.8"))
{
    using (var native =
           PlatformNativeLibrary.Load("libexample.so"))
    {
        IntPtr address =
            native.GetSymbolOrThrow("add");

        FfiType intType =
            backend.CreatePrimitiveType(
                FfiPrimitive.Int);

        using (FfiCallPlan plan =
               backend.CreateCallPlan(
                   FfiCallingConvention.Cdecl,
                   intType,
                   new[]
                   {
                       intType,
                       intType
                   }))
        {
            object result =
                backend.Invoke(
                    plan,
                    address,
                    new object[]
                    {
                        10,
                        20
                    });

            int value =
                Convert.ToInt32(result);
        }
    }
}
```

This mode is intended for applications that already possess the native ABI
description and do not need FfiSharp's C parser.

---

# API design guarantees

The following are deliberate properties of the public API.

## No code generation

FfiSharp does not generate C# source or CLR types for native declarations.

## No compiler dependency

The normal FfiSharp runtime does not require:

- GCC;
- Clang;
- MSVC;
- libclang.

The C header is parsed by FfiSharp's restricted parser.

## No ABI reimplementation

FfiSharp delegates native ABI mechanics to libffi.

## Boxed runtime structs

C structs are represented as `FfiStruct` objects rather than generated CLR
struct types.

## Runtime callbacks

Callbacks are implemented using libffi closures.

## Cross-runtime design

The managed core targets `netstandard2.0`.

---

# Native dependency

libffi is required at runtime.

FfiSharp prefers a vendored native libffi build where available and can fall back
to a system library.

The minimum supported libffi version and optional call-plan requirements should
be taken from the repository's current build/runtime configuration.

See `THIRD-PARTY-NOTICES.md` for libffi licensing and version information.

---

# API stability

This document describes the intended public API and its observable behavior.

The following implementation details are deliberately not API contracts:

- parser internals;
- lexer internals;
- native P/Invoke declarations;
- libffi symbol-loading implementation;
- invocation-frame implementation;
- marshaller implementation;
- cleanup-record representation;
- callback-registry implementation;
- symbol-cache implementation;
- call-plan allocation strategy.

Applications should depend on the public types and semantics documented above,
not on internal implementation details.

---

# Summary

The normal high-level usage pattern is:

```text
C header
    |
    v
Ffi.Load / Ffi.LoadLibrary
    |
    v
FfiLibrary
    |
    +---- GetFunction() ------> NativeFunction
    |
    +---- GetStructType() ----> FfiStructType
    |
    +---- CreateStruct() -----> FfiStruct
    |
    +---- RegisterCallback() -> CallbackHandle
```

The runtime type model is:

```text
FfiType
   |
   +-- FfiPrimitiveType
   |
   +-- FfiPointerType
   |
   +-- FfiStructType
   |
   +-- FfiFunctionType
```

The native invocation pipeline is:

```text
C declaration
      |
      v
C parser
      |
      v
FfiType model
      |
      v
NativeFunction / FfiCallPlan
      |
      v
managed -> native marshalling
      |
      v
libffi
      |
      v
native function
      |
      v
native -> managed unmarshalling
```

The central ownership rule is:

> Native resources remain valid until all operations using them have completed,
> and the caller remains responsible for the lifetime of raw pointers and
> callback registrations exposed to native code.
