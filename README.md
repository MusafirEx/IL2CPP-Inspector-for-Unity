# IL2CPP Inspector for Unity

An **Editor-only UPM package** for investigating Unity IL2CPP Android builds from inside Unity.

It can extract IL2CPP files from APK/XAPK, invoke [Il2CppDumper](https://github.com/Perfare/Il2CppDumper), search recovered type declarations, and launch [Ghidra](https://github.com/NationalSecurityAgency/ghidra) headless to export selected native pseudocode.

**This is a workflow integration, not a native substitute for Il2CppDumper or Ghidra.** It does not recover exact original C# source code.

## Install using Unity Package Manager

Compatible target: **Unity 2022.3** on **Windows**.

1. Open **Window > Package Manager**.
2. Click **+ > Add package from git URL...**
3. Paste:

~~~text
https://github.com/MusafirEx/IL2CPP-Inspector-for-Unity.git
~~~

The repository root contains a valid UPM package.json, so no subfolder path is required.

## Configure external tools

Download and install the tools separately:

- **Il2CppDumper**: https://github.com/Perfare/Il2CppDumper/releases
- **Ghidra**: https://github.com/NationalSecurityAgency/ghidra/releases

Make sure Il2CppDumper's config.json has **RequireAnyKey = false**, **GenerateScript = true** and produces dump.cs and script.json. Set up a Java runtime compatible with your installed Ghidra release.

Open **Tools > IL2CPP Inspector > Analyzer** in the Unity Editor.

1. Select an APK or XAPK archive.
2. Set an analysis workspace **outside the project's Assets folder**.
3. Select Il2CppDumper.exe and Ghidra/support/analyzeHeadless.bat.
4. Press **1. Extract**, **2. Il2CppDumper**, **3. Ghidra**, or **Run all stages**.
5. Search class names in dump.cs or open the pseudocode report.

## Outputs

~~~text
<workspace>/Output/
  extracted/
    libil2cpp.so
    global-metadata.dat
  dumper/
    dump.cs
    script.json
    DummyDll/
  ghidra_project/
  ghidra_export/
    targets_pseudocode.txt
    ghidra_headless.log
~~~

The output names under dumper/ depend on the installed Il2CppDumper version and its configuration.

## Limitations

- Version 0.1.0 targets Windows Editor. Android APK/XAPK containing standard IL2CPP filenames is the supported input; some split APKs or protected builds are unsupported.
- XAPK scanning examines embedded APK entries. It does not restore data hosted online or guarantee OBB completeness.
- Ghidra output is approximate **native C-like pseudocode**, not restored Unity C# scripts. Offsets and mappings can require manual validation.
- The plugin does not bypass obfuscation, DRM, access control, or encryption.
- Both external tools must be installed locally and are **not bundled or downloaded by this package**.
- Large game binaries may take substantial CPU, memory and disk space. Only inspect software you are permitted to analyze.

## Third-party notices

Il2CppDumper is copyright Perfare and licensed under MIT. Ghidra is provided by NSA under Apache License 2.0. Their projects are linked above. This package calls their external executables; it does not distribute their source or binaries.

## Development

Package ID: com.musafirex.il2cpp-inspector

- Editor/IL2CPPInspectorWindow.cs — Unity Editor user interface
- Editor/InspectorPipeline.cs — bounded archive extraction, process invocation, and output handling
- Editor/GhidraScripts/ExportIl2CppTargets.py — Ghidra Jython post-analysis script

MIT license: see LICENSE.

### Roadmap

- Class/field/method tree and JSON export
- Smarter IL2CPP address mapping diagnostics
- Native offline search and a C# scaffold generator
- Optional asset/prefab script-reference exploration
- Tests using Unity Test Framework and CI validation
