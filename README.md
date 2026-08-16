# FfiSharp

A small, self-contained runtime **C FFI** library for C#, built on
[**libffi**](https://github.com/libffi/libffi). Give it a native library and a
restricted C header, and it turns the declarations into a safe, dynamically
callable .NET API:

```csharp
dynamic ffi = Ffi.Load("libexample.so", "example.h");

int result = ffi.add(10, 20);

Point point = ffi.make_point(10, 20.5);
ffi.mutate_point(point);          // mutated in place

ffi.set_callback((Action<int>)(v => Console.WriteLine(v)));
```

No code generation, no C compiler, no Clang/libclang — all native ABI work is
delegated to libffi.

## Goals & design rules

- **Never reimplement the ABI.** Register/stack argument placement, calling
  conventions, aggregate (struct) passing/returning, and callbacks/trampolines are
  all libffi's job. FfiSharp's job is turning a useful subset of C declarations
  into a safe, ergonomic, dynamically callable .NET API.
- **Independent type model.** C types are *not* mapped to `System.Type`. A C
  `long` stays `long`; the platform ABI decides whether it is 32- or 64-bit
  (Windows LLP64 vs. Linux/macOS LP64).
- **Small, layered, testable.** Lexer → parser → type model → marshaller → libffi
  backend, with every OS-native and libffi call isolated behind a single layer.
- **Cross-runtime.** The core targets `netstandard2.0`, so it runs on modern .NET
  and .NET Framework 4.7.2+.

## Supported C subset

This is a *deliberately restricted* FFI-oriented subset — the parser is not a
general-purpose C compiler and **fails loudly** on anything outside it.

- **Primitives:** `void`, `char`, `signed/unsigned char/short/int/long/long long`,
  `float`, `double`, `wchar_t`, plus `size_t`, `ssize_t`, `ptrdiff_t`,
  `intptr_t`, `uintptr_t`, `intN_t`, `uintN_t`.
- **Type constructs:** `const`, `volatile`, `typedef`, pointers (`void*`,
  `const char*`, `Point*`, …).
- **Structs:** `typedef struct { ... } Name;`, tagged structs, nested structs,
  fixed-size array fields. Sequential layout only.
- **Functions:** declarations with named/anonymous parameters; `cdecl` and
  `stdcall` calling conventions (`__cdecl`/`__stdcall`, and a subset of
  `__attribute__`/`__declspec`).
- **Callbacks:** `typedef void (*Callback)(int);` and function-pointer parameters.

**Explicitly unsupported** (parse error, never silently misinterpreted):
unions, bitfields, packed/alignment attributes, variadics (`...`), enums, global
variables, and function definitions with bodies.

The preprocessor is intentionally minimal: comments are stripped, harmless
`#if/#endif` guards and `#include` lines are skipped, and user-supplied type
aliases (`FfiLoadOptions.TypeAliases`) cover the rest. It does not recursively
parse system headers.

## Quick start

```csharp
using FfiSharp;

// C header: example.h
//   typedef struct { int x; double y; } Point;
//   int add(int a, int b);
//   Point make_point(int x, double y);
//   void mutate_point(Point* p);
//   typedef void (*Callback)(int value);
//   void set_callback(Callback callback);

// Dynamic API (backed by DynamicObject):
using (dynamic ffi = Ffi.Load("libexample.so", "example.h"))
{
    int sum = ffi.add(10, 20);                 // 30

    FfiStruct p = ffi.make_point(10, 20.5);    // struct return
    ffi.mutate_point(p);                        // in-place mutation via Point*

    ffi.set_callback((Action<int>)(v => Console.WriteLine(v)));
}

// Explicit (non-dynamic) API:
using (FfiLibrary lib = Ffi.LoadLibrary("libexample.so", "example.h"))
{
    NativeFunction add = lib.GetFunction("add");
    object result = add.Invoke(10, 20);

    FfiStruct p = lib.CreateStruct("Point");
    p["x"] = 10;
    p["y"] = 20.5;
}
```

### Structs

Structs are represented as boxed `FfiStruct` values (no CLR layout):

```csharp
FfiStruct point = ffi.make_point(3, 4.5);
int x = (int)point["x"];
double y = (double)point["y"];

FfiStruct inner = lib.CreateStruct("Point");
inner["x"] = 5;
inner["y"] = 6.5;
```

### Callbacks

Callbacks become libffi closures. `Ffi.LoadOptions.CallbackExceptionPolicy`
controls what happens when a callback throws (`Ignore`, `Store`, or
`RethrowOnManagedBoundary`); exceptions never unwind through native frames.

```csharp
using (FfiLibrary lib = Ffi.LoadLibrary("libexample.so", "example.h"))
{
    CallbackHandle handle = lib.RegisterCallback(
        "set_callback",
        (Action<int>)(v => Console.WriteLine(v)));

    lib.GetFunction("fire_callback").Invoke(42);

    handle.Dispose(); // frees the closure; native must not call it after this
}
```

### Strings & buffers

`const char*` / `const wchar_t*` convert to/from `string` automatically
(`FfiLoadOptions.StringEncoding` defaults to UTF-8). Non-`const` character
pointers and `void*` are treated as opaque and accept `IntPtr` or `byte[]`.

## Configuration (`FfiLoadOptions`)

| Option | Default | Meaning |
|---|---|---|
| `LibFfiPath` | `null` | Explicit path to a libffi shared library. |
| `Platform` | auto-detect | Explicit target ABI (`FfiPlatform`). |
| `TypeAliases` | `null` | Extra `typedef` aliases (name → C type text). |
| `StringEncoding` | `Utf8` | `Utf8` / `Ansi` / `Utf16` / `RawPointer`. |
| `CallbackExceptionPolicy` | `Store` | `Ignore` / `Store` / `RethrowOnManagedBoundary`. |

## Initialization & choosing a libffi

By default FfiSharp loads libffi from a vendored copy next to the assembly, then
falls back to the system library. To load libffi from an arbitrary path at
initialization time, use `FfiLoadOptions.LibFfiPath`:

```csharp
using FfiSharp;

var options = new FfiLoadOptions
{
    LibFfiPath = "/opt/custom/libffi.so.8"   // or "C:\\libs\\libffi-8.dll"
};

using (dynamic ffi = Ffi.Load("libexample.so", "example.h", options))
{
    int sum = ffi.add(10, 20);
}
```

For full control, `LibFfiBackend` is a public entry point that lets you load a
specific libffi and drive it directly (the same object `Ffi.Load` builds
internally):

```csharp
using System;
using FfiSharp.Abi;
using FfiSharp.Backend;
using FfiSharp.Bindings;
using FfiSharp.Interop;

// 1. Load libffi from an arbitrary path (does NOT rely on search paths).
using (var backend = new LibFfiBackend("/opt/custom/libffi.so.8"))
{
    Console.WriteLine("libffi " + backend.LibFfiVersion);

    // 2. Load the target library and resolve a function symbol.
    using (var lib = PlatformNativeLibrary.Load("libexample.so"))
    {
        IntPtr add = lib.GetSymbolOrThrow("add");

        // 3. Describe the signature and invoke through libffi.
        FfiType intType = backend.CreatePrimitiveType(FfiPrimitive.Int);
        using (FfiCallPlan plan = backend.CreateCallPlan(
            FfiCallingConvention.Cdecl, intType, new[] { intType, intType }))
        {
            int result = Convert.ToInt32(backend.Invoke(plan, add, new object[] { 10, 20 }));
            Console.WriteLine(result); // 30
        }
    }
}
```

> **Note:** when you hand `LibFfiBackend` (or `FfiLoadOptions.LibFfiPath`) a path,
> FfiSharp loads and therefore owns/disposes that libffi handle. To *borrow* an
> already-loaded handle instead, wrap it in your own `INativeLibrary`
> implementation and pass that to the `LibFfiBackend(INativeLibrary, ...)`
> constructor overload — the caller then retains ownership and disposes the handle.

## Performance & caching

FfiSharp never reparses the header, re-resolves symbols, or rebuilds call
descriptions on the hot path. Everything is cached and shared across threads:

- **Header** — parsed once per `Ffi.Load` / `Ffi.LoadLibrary`.
- **Symbols** — resolved once and cached per `NativeFunctionBinding`.
- **`FfiType` / `ffi_type`** — canonical, cached per type (incl. aggregate struct
  types and built-in typedefs).
- **Call plans** — each binding builds its `ffi_cif` once and reuses it.
- **Callbacks** — closures are allocated once and retained by the callback
  registry until disposed.

On libffi 3.7.0+ the backend additionally builds a **reusable call plan**
(`ffi_call_plan_alloc` / `ffi_call_plan_invoke` / `ffi_call_plan_free`) per
signature, avoiding libffi's per-call argument classification. When that API is
absent (older libffi), it transparently falls back to `ffi_call`.

## Supported platforms

Validated end-to-end (native calls, structs, callbacks, strings):

| Platform | libffi source | Status |
|---|---|---|
| Linux x64 | system libffi / vendored `.so` | ✅ tested (mono + .NET 8+) |
| Windows x64 | mingw-w64 cross-compiled `libffi-8.dll` | ✅ tested (.NET Framework 4.8 under Wine) |
| Windows x86 | mingw-w64 (i686) `libffi-8.dll` | ✅ tested (.NET Framework 4.8 under Wine) |

The `runtimes/<rid>/native/` convention and the loader are structured for
`linux-arm64`, `osx-x64`, and `osx-arm64` — those need platform-specific libffi
builds (not possible on this x86-64 host).

> **Note:** only these platforms are *claimed* to work. A platform compiling does
> not mean it is supported until its libffi build is validated there.

## Native libffi dependency

libffi is a runtime native dependency and is **vendored** (not assumed installed):

```
runtimes/
  win-x64/native/libffi-8.dll
  win-x86/native/libffi-8.dll
  linux-x64/native/libffi.so.8
```

The loader prefers a vendored copy next to the assembly and falls back to the
system library. Version and MIT license are documented in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md). Rebuild scripts:
`scripts/build-libffi-win.sh`, `scripts/build-libffi-win-x86.sh`,
`scripts/build-libffi-linux.sh`.

## Building & testing

```bash
# Linux
dotnet build
dotnet test tests/FfiSharp.Tests          # 92 xunit tests
bash tests/native/build.sh                 # native test lib (example.so)

# .NET Framework smoke (Linux, via mono)
mono tests/FfiSharp.Smoke.NetFx/bin/Debug/net472/FfiSharp.Smoke.NetFx.exe

# Windows .NET Framework (cross-compiled, via wine)
bash tests/native/build-win.sh             # example.dll (x64)
bash tests/native/build-win-x86.sh         # example-x86.dll (x86)
bash scripts/build-libffi-win.sh           # libffi-8.dll (x64)
bash scripts/build-libffi-win-x86.sh       # libffi-8.dll (x86)
WINEPREFIX=~/.wine-ffi wine tests/FfiSharp.Smoke.NetFx/bin/Debug/net472/FfiSharp.Smoke.NetFx.exe
WINEPREFIX=~/.wine-ffi wine tests/FfiSharp.Smoke.NetFxX86/bin/Debug/net472/FfiSharp.Smoke.NetFxX86.exe
```

## Project layout

```
src/FfiSharp/                core library (netstandard2.0)
  Ffi.cs, FfiLibrary.cs      public entry points
  Abi/                       FfiType model + FfiPlatform (type system)
  Parsing/                   CLexer, CParser, CTypeResolver, HeaderModel
  Marshal/                   FfiMarshaller (managed ↔ native storage)
  Backend/                   LibFfiBackend, NativeTypeResolver
  Bindings/                  NativeFunctionBinding, FfiCallback, FfiCallPlan
  Interop/                   INativeLibrary, NativeMethods, LibFfiNative (only P/Invoke here)
  Dynamic/                   FfiDynamicObject (DynamicObject)
tests/FfiSharp.Tests/        xunit test suite (net8.0)
tests/FfiSharp.Smoke(.NetFx)/ smoke apps (net8.0 / net472)
tests/native/                C test library + build scripts
runtimes/<rid>/native/       vendored libffi
scripts/                     libffi build scripts
```

## Security

`Ffi.Load(...)` loads and executes arbitrary native code — treat the native
library **and** the header as fully trusted input. Malformed headers produce
parser errors (never code execution), but a loaded library is indistinguishable
from running that library's code directly. There is no sandboxing.

## License

FfiSharp is MIT-licensed. It depends on libffi (MIT) — see
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
