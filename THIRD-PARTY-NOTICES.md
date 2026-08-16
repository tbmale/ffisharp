# Third-Party Notices

## libffi

This project depends on the [libffi](https://github.com/libffi/libffi) library as
its runtime ABI invocation engine (function calls, aggregate/struct ABI handling,
and callbacks/closures).

- **Version vendored:** 3.8.0
- **License:** MIT License
- **Vendored binaries:**
  - `runtimes/win-x64/native/libffi-8.dll` (Windows x64, cross-compiled with mingw-w64)
  - `runtimes/linux-x64/native/libffi.so.8` (Linux x64, SONAME `libffi.so.8`)

### License

libffi is distributed under the MIT License. The full license text can be found
in the upstream repository at
<https://github.com/libffi/libffi/blob/master/LICENSE>:

> libffi - Copyright (c) 1996-2026 Anthony Green, Red Hat, Inc and others.
> See source files for details.
>
> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in
> all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
> THE SOFTWARE.

### Rebuilding the vendored binaries

```
scripts/build-libffi-win.sh     # Windows x64 (mingw-w64 cross-compile)
scripts/build-libffi-linux.sh   # Linux x64 (native build)
```

These download the libffi 3.8.0 source, build it, and write the result into the
appropriate `runtimes/<rid>/native/` directory.
