# Building the client with CMake

`Client/CMakeLists.txt` builds the same six projects as `TClient.sln` (TClient, Engine Lib, TChart,
TCML, TComp, HwidLib) with the same Release|Win32 flags. The resulting `TClient.exe` matches the
MSBuild one (same objects, same link options, same manifest). `TClient.sln` still works and is
unchanged.

## Requirements

Visual Studio 2026 (or its Build Tools) with Desktop development with C++, x86/x64 tools and the
Windows 11 SDK (10.0.26100). The CMake client uses its own window layer and SDK resource headers,
so it does not require MFC.

The repo-root `.vsconfig` lists exactly these components: in the Visual Studio Installer, use
*More > Import configuration* and pick it (Visual Studio also offers to install them when it opens
the repo). ATL is there for the servers.

CMake and Ninja come with Visual Studio. Everything else (DirectX 9 SDK, zlib, HShield) is in
`Lib/3rdParty`.

## Build

```powershell
.\Client\build.ps1            # Ninja + MSVC x86 -> build\msvc-x86\bin\TClient.exe
.\Client\build.ps1 -Deploy    # also copies TClient.exe into Game\
.\Client\build.ps1 -Clean     # from scratch
```

`build.ps1` enters the x86 developer environment itself and temporarily uses UTF-8 console output
so Ninja tracks header dependencies on localized Visual Studio installations. When upgrading a
build created with a different console encoding, run once with `-Clean` to regenerate dependencies.
From an existing x86 developer prompt:

```powershell
cd Client
cmake --preset msvc-x86
cmake --build --preset msvc-x86
```

Or, without a developer prompt, with the Visual Studio generator (opens in Visual Studio too):

```powershell
cd Client
cmake --preset vs-x86
cmake --build --preset vs-x86     # -> build\vs-x86\bin\Release\TClient.exe
```

## clang-cl

```powershell
.\Client\build.ps1 -Preset clang-x86    # -> build\clang-x86\bin\TClient.exe (lld-link)
```

Needs the *C++ Clang Compiler for Windows* component (in `.vsconfig`). clang is the compiler of
the Linux/macOS port, so keep this preset building. What it requires from the code:

- No `&Type(...)` on temporaries: use `TTEMP(Type(...))` (T3D.h), valid until the end of the
  full expression like before.
- No `CString` passed to `...` (Format, printf, CTChart::Format): cast it, `(LPCTSTR)str`. With
  clang the call would abort at runtime.
- No non-ASCII narrow string literals that reach the game: clang only has a UTF-8 execution
  charset, so write CP949 bytes as `\x` escapes (TRACE/ASSERT texts and comments may stay).
- No temporaries bound to non-const references, no narrowing in `{...}` initializers,
  `&Class::Method` in message maps, no `struct X()` constructors.

## Notes

- 32-bit only for now; configuring for x64 stops with an error.
- The old DirectX SDK include folder must come after the Windows SDK one (its dxgi headers are
  older). `cmake/TClientCommon.cmake` handles that for both generators.
- Do not add `/Gy-`: `/O2` implies `/Gy`, which `/OPT:REF` needs to drop unused functions
  (without it the exe grows by about 490 KB).
- Source files are listed explicitly in each `CMakeLists.txt`. When adding a file to the
  `.vcxproj`, add it there too.
