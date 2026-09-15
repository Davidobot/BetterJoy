# Why there's a `Nefarius\` subfolder in the build output

If you've looked at `BetterJoyForCemu\bin\x64\Release\` (or an installed copy under
`C:\Program Files\BetterJoy\`) and wondered why there are over 100 DLLs for an app with a
handful of real dependencies, this is why - and why they're split the way they are.

## The root cause

`Nefarius.Drivers.HidHide` and `Nefarius.Utilities.DeviceManagement` have never published a
.NET Framework build at any version - checked every release of `Nefarius.Drivers.HidHide` from
1.0.3 through 1.12.2 on NuGet directly; only `net6.0-windows7.0`/`net7.0-windows7.0`/
`netstandard2.0` ever existed. Since this project targets `net461`, NuGet has no native option
for either package and falls back to their `netstandard2.0` build. Referencing *any*
netstandard2.0 package from a .NET Framework project pulls in the full "NETStandard.Library"
compatibility facade set - roughly 100 small assemblies (`netstandard.dll`,
`System.Buffers.dll`, `System.Security.Cryptography.*.dll`, ...), most of them empty
type-forwarders that exist purely so netstandard-compiled code can resolve types .NET Framework
already has natively under a different assembly name.

Retargeting to a newer .NET Framework version does **not** fix this - `Nefarius.Drivers.HidHide`
has no net4x build at *any* version, so it always falls back to netstandard2.0 regardless of
what this project targets. (`Nefarius.Utilities.DeviceManagement` does have a native `net462`+
build, but every facade it needs is already a subset of what HidHide alone requires, so fixing
its resolution wouldn't remove a single DLL either.)

## What actually happens here

Rather than accept ~100 unexplained DLLs sitting flat in the app's own install root, or spend
real engineering effort replacing a working, actively-used dependency (HidHide is what lets
BetterJoy hide the physical controller from other programs like Steam - genuinely load-bearing,
not something to drop), the facade set is kept but organized:

- **App root**: `BetterJoy2.exe` plus its real, direct dependencies -
  `Concentus.dll`, `Crc32.NET.dll`, `NAudio.dll`, `Nefarius.Drivers.HidHide.dll`,
  `Nefarius.Utilities.DeviceManagement.dll`, `Nefarius.ViGEm.Client.dll`, `WindowsInput.dll`.
  Anyone looking at the root sees exactly BetterJoy's own dependency list, nothing else.
- **`Nefarius\` subfolder**: every netstandard2.0 compatibility facade that exists *only*
  because of the two Nefarius packages above - moved there by `BetterJoy.csproj`'s
  `OrganizeDependencyDlls` post-build target, which runs after every build and relocates
  everything in the output folder that isn't one of BetterJoy's own direct dependencies. It's a
  keep-list, not a move-list: BetterJoy's own references almost never change, while the exact
  facade set NuGet resolves can shift slightly between package version bumps - a keep-list is
  far less likely to silently go stale.
- **`x64\`/`x86\` subfolders**: unrelated to the above - these are the project's own *native*
  (unmanaged) P/Invoke dependencies (`hidapi.dll`, `libsbc.dll`, `samplerate.dll`,
  `libVIIPER.dll`), resolved via `EntryPoint.SetupDlls()` adding the matching architecture
  folder to the process's DLL search path at startup. Each is now only included in the build
  matching its own architecture (`Condition="'$(Platform)' == 'x64'"` etc. in the csproj) -
  previously both architectures' natives shipped in every build regardless of which one could
  actually be loaded by that process.

## How the CLR actually finds the moved DLLs

`App.config`'s `<runtime><assemblyBinding><probing privatePath="Nefarius" /></assemblyBinding>`
tells the .NET Framework CLR to also search the `Nefarius\` subfolder (relative to the app's own
base directory) when resolving an assembly reference it can't find in the root - the standard,
supported mechanism for exactly this, not a custom resolver. Verified with a standalone
isolated .NET Framework 4.6.1 test harness (same file layout, same `<probing>` config) that
successfully constructed `HidHideControlService` and called a real property (`.IsInstalled`,
correctly detecting the driver on the test machine) - not just "did it load", genuine
functional exercise of the actual moved dependency chain.

## What this doesn't fix

The *total* DLL count in the shipped install is unchanged - this is an organization fix, not a
size fix. `JetBrains.Annotations` (confirmed completely unused anywhere in this codebase) was
removed as a separate, genuine reduction. `*.xml` files (NuGet packages' Visual Studio
IntelliSense documentation, meaningless for an installed end-user app) are excluded from the
installer in `Installer\BetterJoy.iss`.
