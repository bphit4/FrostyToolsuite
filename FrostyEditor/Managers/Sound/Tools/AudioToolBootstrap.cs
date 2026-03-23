using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FrostyEditor.Managers.Sound.Tools;

internal static class AudioToolBootstrap
{
    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "FrostyToolsuite", "SoundTools");

    internal static async Task<string> StageArchiveAsync(
        string archiveFileName,
        string resourceSuffix,
        string toolFolderName,
        CancellationToken cancellationToken = default)
    {
        string targetRoot = Path.Combine(TempRoot, toolFolderName);
        Directory.CreateDirectory(targetRoot);

        using Stream archiveStream = OpenArchiveStream(archiveFileName, resourceSuffix);
        using ZipArchive archive = new(archiveStream, ZipArchiveMode.Read, leaveOpen: false);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            string outputPath = Path.Combine(targetRoot, relativePath);

            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await using Stream entryStream = entry.Open();
            await using FileStream outputStream = new(outputPath, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous
            });
            await entryStream.CopyToAsync(outputStream, cancellationToken).ConfigureAwait(false);
        }

        return targetRoot;
    }

    internal static async Task RunProcessAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken = default,
        string? workingDirectory = null)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory
            }
        };

        StringBuilder stdout = new();
        StringBuilder stderr = new();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stdout)
                {
                    stdout.AppendLine(e.Data);
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderr)
                {
                    stderr.AppendLine(e.Data);
                }
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start tool process '{fileName}'.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            string errorText;
            lock (stderr)
            {
                errorText = stderr.ToString();
            }

            string outputText;
            lock (stdout)
            {
                outputText = stdout.ToString();
            }

            throw new InvalidOperationException(
                $"Tool '{Path.GetFileName(fileName)}' exited with code {process.ExitCode}.{Environment.NewLine}" +
                $"{(string.IsNullOrWhiteSpace(errorText) ? string.Empty : $"stderr:{Environment.NewLine}{errorText}{Environment.NewLine}")}" +
                $"{(string.IsNullOrWhiteSpace(outputText) ? string.Empty : $"stdout:{Environment.NewLine}{outputText}")}");
        }
    }

    private static Stream OpenArchiveStream(string archiveFileName, string resourceSuffix)
    {
        Stream? embedded = OpenEmbeddedResource(resourceSuffix);
        if (embedded is not null)
        {
            return embedded;
        }

        string? onDisk = LocateArchiveOnDisk(archiveFileName);
        if (onDisk is not null)
        {
            return File.OpenRead(onDisk);
        }

        throw new FileNotFoundException(
            $"Unable to locate '{archiveFileName}'. Expected it as an embedded resource ending with '{resourceSuffix}' " +
            $"or as a file under the user Downloads folder / app directory.");
    }

    private static Stream? OpenEmbeddedResource(string resourceSuffix)
    {
        IEnumerable<Assembly?> assemblies =
        [
            typeof(AudioToolBootstrap).Assembly,
            Assembly.GetEntryAssembly()
        ];

        foreach (Assembly? assembly in assemblies.Where(static a => a is not null))
        {
            string? resourceName = assembly!.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
            if (resourceName is not null)
            {
                return assembly.GetManifestResourceStream(resourceName);
            }
        }

        return null;
    }

    private static string? LocateArchiveOnDisk(string archiveFileName)
    {
        string[] candidateNames = GetArchiveCandidateNames(archiveFileName);
        List<string> roots =
        [
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
            Path.Combine(AppContext.BaseDirectory, "Assets", "SoundTools"),
            Path.Combine(Environment.CurrentDirectory, "Assets", "SoundTools")
        ];

        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads))
        {
            roots.Add(downloads);
        }

        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (string candidateName in candidateNames)
                {
                    string? found = Directory.EnumerateFiles(root, candidateName, SearchOption.AllDirectories).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(found))
                    {
                        return found;
                    }
                }
            }
            catch
            {
                // Ignore locked folders or access issues during best-effort discovery.
            }
        }

        return null;
    }

    private static string[] GetArchiveCandidateNames(string archiveFileName)
    {
        HashSet<string> names = [archiveFileName];
        string baseName = Path.GetFileNameWithoutExtension(archiveFileName);
        if (!string.IsNullOrWhiteSpace(baseName))
        {
            string[] tokens = baseName.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 0)
            {
                names.Add(tokens[^1] + ".zip");
            }

            if (tokens.Length > 1)
            {
                names.Add(tokens[^2] + ".zip");
            }
        }

        return names.ToArray();
    }
}
