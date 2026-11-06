using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace MusafirEx.IL2CPPInspector.Editor
{
    internal sealed class InspectorSettings
    {
        public string InputPath;
        public string Workspace;
        public string DumperPath;
        public string GhidraPath;
        public string Targets;
        public string AssetsPath;
        public string GhidraScriptsPath;
    }

    internal static class InspectorPipeline
    {
        private const long MaxArchiveMemberBytes = 1024L * 1024 * 1024;
        private const long MaxNativeBytes = 1024L * 1024 * 1024;
        private const long MaxMetadataBytes = 256L * 1024 * 1024;

        internal static string OutputDirectory(string workspace)
        {
            return Path.Combine(string.IsNullOrWhiteSpace(workspace) ? "." : workspace, "Output");
        }

        private static string Native(InspectorSettings s) { return Path.Combine(OutputDirectory(s.Workspace), "extracted", "libil2cpp.so"); }
        private static string Metadata(InspectorSettings s) { return Path.Combine(OutputDirectory(s.Workspace), "extracted", "global-metadata.dat"); }
        private static string DumpDir(InspectorSettings s) { return Path.Combine(OutputDirectory(s.Workspace), "dumper"); }

        internal static void Extract(InspectorSettings s, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(s.InputPath) || !File.Exists(s.InputPath))
                throw new FileNotFoundException("Choose an existing APK/XAPK.", s.InputPath);
            EnsureWorkspace(s);
            var extension = Path.GetExtension(s.InputPath).ToLowerInvariant();
            if (extension != ".apk" && extension != ".xapk")
                throw new InvalidDataException("Only .apk and .xapk archives are supported.");

            Directory.CreateDirectory(Path.GetDirectoryName(Native(s)));
            if (File.Exists(Native(s))) File.Delete(Native(s));
            if (File.Exists(Metadata(s))) File.Delete(Metadata(s));
            bool native = false, metadata = false;

            if (extension == ".apk")
            {
                using (var archive = ZipFile.OpenRead(s.InputPath))
                    ExtractEntries(archive, s, ref native, ref metadata);
            }
            else
            {
                using (var archive = ZipFile.OpenRead(s.InputPath))
                {
                    var nestedApks = archive.Entries
                        .Where(e => e.FullName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(e => e.Length).ToArray();
                    if (nestedApks.Length == 0)
                        throw new InvalidDataException("XAPK has no embedded APK files.");
                    foreach (var apkEntry in nestedApks)
                    {
                        if (apkEntry.Length > MaxArchiveMemberBytes)
                            throw new InvalidDataException("Nested APK exceeds safety limit.");
                        var temp = Path.GetTempFileName();
                        try
                        {
                            using (var input = apkEntry.Open())
                            using (var output = File.Create(temp))
                                CopyBounded(input, output, MaxArchiveMemberBytes);
                            using (var nested = ZipFile.OpenRead(temp))
                                ExtractEntries(nested, s, ref native, ref metadata);
                        }
                        finally
                        {
                            if (File.Exists(temp)) File.Delete(temp);
                        }
                        if (native && metadata) break;
                    }
                }
            }

            if (!native || !metadata)
                throw new InvalidDataException("Could not find both libil2cpp.so and global-metadata.dat. " +
                    "Check for split APKs, external data or a non-IL2CPP build.");
            log("IL2CPP binary: " + Native(s));
            log("Metadata: " + Metadata(s));
        }

        private static void ExtractEntries(ZipArchive archive, InspectorSettings s,
            ref bool native, ref bool metadata)
        {
            if (!native)
            {
                var entry = archive.GetEntry("lib/arm64-v8a/libil2cpp.so")
                    ?? archive.GetEntry("lib/armeabi-v7a/libil2cpp.so");
                if (entry != null)
                {
                    ExtractOne(entry, Native(s), MaxNativeBytes);
                    native = true;
                }
            }
            if (!metadata)
            {
                var entry = archive.GetEntry("assets/bin/Data/Managed/Metadata/global-metadata.dat");
                if (entry != null)
                {
                    ExtractOne(entry, Metadata(s), MaxMetadataBytes);
                    metadata = true;
                }
            }
        }

        private static void ExtractOne(ZipArchiveEntry entry, string destination, long limit)
        {
            if (entry.Length <= 0 || entry.Length > limit)
                throw new InvalidDataException("Invalid or oversized IL2CPP entry: " + entry.FullName);
            using (var input = entry.Open())
            using (var output = File.Create(destination))
                CopyBounded(input, output, limit);
        }

        private static void CopyBounded(Stream input, Stream output, long limit)
        {
            long total = 0;
            var buffer = new byte[65536];
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += count;
                if (total > limit) throw new InvalidDataException("Archive entry exceeded size limit.");
                output.Write(buffer, 0, count);
            }
        }

        internal static void Dump(InspectorSettings s, Action<string> log)
        {
            EnsureWorkspace(s);
            if (string.IsNullOrWhiteSpace(s.DumperPath) || !File.Exists(s.DumperPath))
                throw new FileNotFoundException("Select Il2CppDumper.exe.", s.DumperPath);
            if (!File.Exists(Native(s)) || !File.Exists(Metadata(s)))
                throw new InvalidOperationException("Run Extract first.");
            var dir = DumpDir(s);
            Directory.CreateDirectory(dir);
            var dumped = Path.Combine(dir, "dump.cs");
            var mapping = Path.Combine(dir, "script.json");
            if (File.Exists(dumped)) File.Delete(dumped);
            if (File.Exists(mapping)) File.Delete(mapping);
            log("Running Il2CppDumper...");
            var arguments = Quote(Native(s)) + " " + Quote(Metadata(s)) + " " + Quote(dir);
            var code = Execute(s.DumperPath, arguments, Path.GetDirectoryName(s.DumperPath), 600000, log);
            if (code != 0) throw new InvalidOperationException("Il2CppDumper exited with code " + code);
            if (!File.Exists(dumped))
                throw new InvalidDataException("dump.cs was not produced. Check Il2CppDumper config.json and logs.");
            if (!File.Exists(mapping))
                log("WARNING: script.json missing. Enable GenerateScript in Il2CppDumper config.json for Ghidra.");
            log("Metadata report: " + dumped);
        }

        internal static void Decompile(InspectorSettings s, Action<string> log)
        {
            EnsureWorkspace(s);
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                throw new PlatformNotSupportedException("Ghidra launcher in v0.1 targets Windows.");
            if (string.IsNullOrWhiteSpace(s.GhidraPath) || !File.Exists(s.GhidraPath))
                throw new FileNotFoundException("Select Ghidra support/analyzeHeadless.bat.", s.GhidraPath);
            if (!File.Exists(Native(s))) throw new InvalidOperationException("Run Extract first.");
            var mapping = Path.Combine(DumpDir(s), "script.json");
            if (!File.Exists(mapping)) throw new FileNotFoundException("Run Il2CppDumper with GenerateScript enabled.", mapping);

            if (string.IsNullOrWhiteSpace(s.Targets) ||
                !Regex.IsMatch(s.Targets, @"^[A-Za-z0-9_., ]{1,240}$"))
                throw new InvalidOperationException("Target names may contain only letters, digits, spaces, commas, dots and underscores.");
            var scripts = s.GhidraScriptsPath;
            if (string.IsNullOrWhiteSpace(scripts))
                throw new InvalidOperationException("Cannot locate the installed UPM package Ghidra scripts.");
            if (!File.Exists(Path.Combine(scripts, "ExportIl2CppTargets.py")))
                throw new FileNotFoundException("Ghidra script is missing from the package.");

            var project = Path.Combine(OutputDirectory(s.Workspace), "ghidra_project");
            var output = Path.Combine(OutputDirectory(s.Workspace), "ghidra_export");
            Directory.CreateDirectory(project);
            Directory.CreateDirectory(output);

            var arguments = Quote(project) + " IL2CPPInspector -import " + Quote(Native(s)) +
                " -overwrite -scriptPath " + Quote(scripts) +
                " -postScript ExportIl2CppTargets.py " + Quote(mapping) + " " + Quote(output) +
                " " + Quote(s.Targets) + " -log " + Quote(Path.Combine(output, "ghidra_headless.log"));
            // Windows batch file via cmd, without UseShellExecute.
            var command = "/d /s /c \"\"" + s.GhidraPath + "\" " + arguments + "\"";
            log("Running Ghidra headless; see ghidra_headless.log for details.");
            var code = Execute("cmd.exe", command, Path.GetDirectoryName(s.GhidraPath), 3600000, log);
            if (code != 0) throw new InvalidOperationException("Ghidra exited with code " + code);
            var report = Path.Combine(output, "targets_pseudocode.txt");
            if (!File.Exists(report))
                throw new InvalidDataException("No pseudocode report was produced. Examine Ghidra headless log.");
            log("Pseudocode: " + report);
        }

        private static void EnsureWorkspace(InspectorSettings s)
        {
            if (string.IsNullOrWhiteSpace(s.Workspace))
                throw new InvalidOperationException("Choose a workspace folder outside Unity Assets.");
            var full = Path.GetFullPath(s.Workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var assets = Path.GetFullPath(s.AssetsPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Equals(assets, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(assets + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Workspace must be outside Unity's Assets folder.");
        }

        private static string Quote(string value) { return "\"" + (value ?? "").Replace("\"", "\\\"") + "\""; }

        private static int Execute(string executable, string arguments, string directory, int timeoutMs, Action<string> log)
        {
            var info = new ProcessStartInfo(executable, arguments)
            {
                WorkingDirectory = string.IsNullOrEmpty(directory) ? Environment.CurrentDirectory : directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (var process = new Process { StartInfo = info })
            {
                if (!process.Start()) throw new InvalidOperationException("Could not launch: " + executable);
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { /* best effort */ }
                    throw new TimeoutException("Analysis timed out: " + executable);
                }
                Task.WaitAll(stdout, stderr);
                if (!string.IsNullOrWhiteSpace(stdout.Result)) log(stdout.Result);
                if (!string.IsNullOrWhiteSpace(stderr.Result)) log("STDERR:\n" + stderr.Result);
                return process.ExitCode;
            }
        }
    }
}
