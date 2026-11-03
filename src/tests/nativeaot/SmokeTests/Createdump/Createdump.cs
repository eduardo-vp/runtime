// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

internal class Createdump
{
#if LINKED_CREATEDUMP
    private const bool LinkedCreatedump = true;
#else
    private const bool LinkedCreatedump = false;
#endif

    private static int Main(string[] args)
    {
        if (args.Length != 0)
        {
            if (args[0] == "crash")
            {
                Environment.FailFast("Createdump selection test");
            }

            // If bootstrap dispatch is broken, do not crash again on the sentinel argument.
            return 42;
        }

        string directory = Path.Combine(Path.GetTempPath(), "NativeAotCreatedump-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string executable = Path.Combine(directory, "test app");
            File.Copy(Environment.ProcessPath, executable);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // A marker-only helper makes accidental external fallback observable without requiring
            // the standalone tool. Dump-format validation belongs to the diagnostics tests.
            WriteExternalHelper(directory);
            string overrideDirectory = Path.Combine(directory, "external");
            Directory.CreateDirectory(overrideDirectory);
            WriteExternalHelper(overrideDirectory);

            RunScenario(executable, "automatic", expectLinked: LinkedCreatedump);
            RunScenario(executable, "defaultname", expectLinked: LinkedCreatedump, defaultName: true);
            RunScenario(executable, "logging", expectLinked: LinkedCreatedump, logToFile: true);
            RunScenario(executable, "override", expectLinked: false, toolDirectory: overrideDirectory);
            RunScenario(executable, "heap", expectLinked: false, dumpType: "2");
            RunScenario(executable, "crashreport", expectLinked: false, crashReport: true);
            return 100;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteExternalHelper(string directory)
    {
        string helper = Path.Combine(directory, "createdump");
        File.WriteAllText(helper, "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$CREATEDUMP_TEST_MARKER\"\n");
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RunScenario(
        string executable,
        string scenario,
        bool expectLinked,
        bool logToFile = false,
        string toolDirectory = null,
        string dumpType = "4",
        bool crashReport = false,
        bool defaultName = false)
    {
        string directory = Path.Combine(Path.GetDirectoryName(executable), scenario);
        Directory.CreateDirectory(directory);
        string marker = Path.Combine(directory, "external.marker");
        string log = Path.Combine(directory, "createdump.log");
        var startInfo = new ProcessStartInfo(executable, ["crash"])
        {
            InheritedHandles = [],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.Environment["DOTNET_DbgEnableMiniDump"] = "1";
        startInfo.Environment["DOTNET_EnableDiagnostics"] = "0";
        startInfo.Environment["DOTNET_DbgMiniDumpType"] = dumpType;
        startInfo.Environment.Remove("COMPlus_DbgMiniDumpName");
        startInfo.Environment["DOTNET_DbgMiniDumpName"] = defaultName ? null : Path.Combine(directory, "dump.%e.%p");
        startInfo.Environment.Remove("COMPlus_DbgCreateDumpToolPath");
        startInfo.Environment.Remove("COMPlus_CreateDumpLogToFile");
        startInfo.Environment["DOTNET_DbgCreateDumpToolPath"] = toolDirectory;
        startInfo.Environment["DOTNET_CreateDumpLogToFile"] = logToFile ? log : null;
        startInfo.Environment["DOTNET_CreateDumpDiagnostics"] = logToFile ? "1" : "0";
        startInfo.Environment["DOTNET_CreateDumpVerboseDiagnostics"] = logToFile ? "1" : "0";
        startInfo.Environment["DOTNET_EnableCrashReport"] = crashReport ? "1" : "0";
        startInfo.Environment["DOTNET_EnableCrashReportOnly"] = "0";
        startInfo.Environment["DOTNET_DbgDisablePagemapUse"] = "0";
        startInfo.Environment["TMPDIR"] = directory + Path.DirectorySeparatorChar;
        startInfo.Environment["CREATEDUMP_TEST_MARKER"] = marker;

        ProcessTextOutput output = Process.RunAndCaptureText(startInfo, TimeSpan.FromMinutes(2));
        Console.WriteLine($"{scenario}: {output.StandardOutput}{output.StandardError}");
        // SIGABRT is 6 on the supported Linux targets, but has no named PosixSignal value.
        if (output.ExitStatus.Signal != (PosixSignal)6)
        {
            throw new Exception($"{scenario}: expected SIGABRT, got exit code {output.ExitStatus.ExitCode}, signal {output.ExitStatus.Signal}.");
        }

        string dump = Path.Combine(directory, defaultName ? $"coredump.{output.ProcessId}" : $"dump.test app.{output.ProcessId}");
        if (expectLinked)
        {
            string diagnostics = logToFile ? File.ReadAllText(log) : output.StandardOutput;
            if (File.Exists(marker) || !File.Exists(dump) || new FileInfo(dump).Length == 0 ||
                !diagnostics.Contains("Dump successfully written", StringComparison.Ordinal))
            {
                throw new Exception($"{scenario}: linked createdump did not write the dump. Diagnostics: {diagnostics}");
            }
        }
        else
        {
            if (!File.Exists(marker) || File.Exists(dump))
            {
                throw new Exception($"{scenario}: expected the external helper to be invoked.");
            }

            string[] arguments = File.ReadAllLines(marker);
            if (Array.IndexOf(arguments, "--nativeaot") < 0 ||
                Array.IndexOf(arguments, dumpType == "2" ? "--withheap" : "--full") < 0 ||
                (crashReport && Array.IndexOf(arguments, "--crashreport") < 0))
            {
                throw new Exception($"{scenario}: external helper received unexpected arguments: {string.Join(" ", arguments)}");
            }
        }
    }
}
