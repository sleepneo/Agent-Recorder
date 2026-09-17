using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit.Sdk;

namespace AgentRecorder.Tests;

/// <summary>
/// Owns a private copy of the Fake AudioHelper runtime for one test instance.
/// The source is read-only build output; all process execution and cleanup use
/// the private destination tree.
/// </summary>
internal sealed class FakeAudioHelperDeployment : IDisposable
{
    private readonly string _sourceDirectory;
    private bool _disposed;

    public FakeAudioHelperDeployment(string ownerDirectory)
    {
        if (string.IsNullOrWhiteSpace(ownerDirectory))
            throw new ArgumentException("An owner directory is required.", nameof(ownerDirectory));

        var outputConfiguration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name;
        var targetFramework = new DirectoryInfo(AppContext.BaseDirectory).Name;
        if (string.IsNullOrWhiteSpace(outputConfiguration) || string.IsNullOrWhiteSpace(targetFramework))
            throw new InvalidOperationException($"Cannot resolve test output configuration from '{AppContext.BaseDirectory}'.");

        _sourceDirectory = Path.Combine(
            TestHelper.ProjectRoot,
            "tests",
            "AgentRecorder.AudioHelper.Fake",
            "bin",
            outputConfiguration,
            targetFramework);
        if (!Directory.Exists(_sourceDirectory))
            throw new DirectoryNotFoundException($"Fake AudioHelper build output not found: {_sourceDirectory}");

        Root = Path.Combine(ownerDirectory, "fake-audio-helper");
        Directory.CreateDirectory(Root);

        foreach (var sourcePath in Directory.EnumerateFiles(_sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(_sourceDirectory, sourcePath);
            var destinationPath = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: false);
        }

        ExecutablePath = Path.Combine(Root, "AgentRecorder.AudioHelper.Fake.exe");
        if (!File.Exists(ExecutablePath))
        {
            TestDirectoryCleanup.DeleteOwnedDirectory(Root);
            throw new FileNotFoundException("Private Fake AudioHelper executable was not deployed.", ExecutablePath);
        }
    }

    public string Root { get; }

    public string ExecutablePath { get; }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopOwnedProcesses();
        TestDirectoryCleanup.DeleteOwnedDirectory(Root);
    }

    private void StopOwnedProcesses()
    {
        var owned = FindOwnedProcesses();
        var survivors = new List<int>();

        foreach (var process in owned)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // If the process became inaccessible, the bounded wait below
                // remains the final proof before the directory is touched.
            }
            finally
            {
                try
                {
                    if (!process.WaitForExit(5000) && !process.HasExited)
                        survivors.Add(process.Id);
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        if (survivors.Count != 0)
        {
            throw new XunitException(
                $"Private Fake AudioHelper processes did not exit before cleanup: {string.Join(", ", survivors)}");
        }
    }

    private List<Process> FindOwnedProcesses()
    {
        var owned = new List<Process>();
        var rootPrefix = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (var process in Process.GetProcesses())
        {
            var keepProcessHandle = false;
            try
            {
                if (process.HasExited)
                    continue;

                string? executablePath;
                try
                {
                    executablePath = process.MainModule?.FileName;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    executablePath = null;
                }
                catch (InvalidOperationException)
                {
                    executablePath = null;
                }

                if (executablePath != null &&
                    Path.GetFullPath(executablePath).StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    owned.Add(process);
                    keepProcessHandle = true;
                    continue;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Some unrelated system processes cannot be inspected from the
                // test token. They are not owned unless their private path was
                // positively observed, so leave them untouched.
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                if (!keepProcessHandle)
                    process.Dispose();
            }
        }

        return owned;
    }
}
