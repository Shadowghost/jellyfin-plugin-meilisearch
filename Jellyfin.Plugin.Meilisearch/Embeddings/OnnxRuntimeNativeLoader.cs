using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace Jellyfin.Plugin.Meilisearch.Embeddings;

/// <summary>
/// Teaches ONNX Runtime where to find its native library when it is loaded as part of a Jellyfin
/// plugin.
/// </summary>
/// <remarks>
/// The RID-based <c>runtimes/{rid}/native</c> probing that works in a normal application is driven by
/// the host application's <c>.deps.json</c>. A plugin is loaded into its own context and contributes
/// nothing to that file, so the default <c>DllImport</c> resolution never looks inside the plugin's
/// own directory and the P/Invoke fails with a bare "unable to load onnxruntime". Registering a
/// resolver removes the guesswork.
/// <para>
/// The packaged layout is <c>native/{rid}/</c> rather than NuGet's <c>runtimes/{rid}/native/</c>, and
/// the Windows libraries are staged as <c>.nativelib</c>. That is deliberate: Jellyfin discovers
/// plugin assemblies by globbing <c>*.dll</c> through every subdirectory and calling
/// <c>LoadFromAssemblyPath</c> on each result, which throws on a native Windows DLL and disables the
/// whole plugin. Loading by full path here does not care about the extension. NuGet's layout is still
/// probed so an install assembled straight from the build output keeps working.
/// </para>
/// </remarks>
internal static class OnnxRuntimeNativeLoader
{
    private const string LibraryName = "onnxruntime";

    private static readonly object _gate = new();
    private static bool _registered;

    /// <summary>
    /// Registers the resolver. Safe to call repeatedly; only the first call has an effect.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public static void EnsureRegistered(ILogger logger)
    {
        lock (_gate)
        {
            if (_registered)
            {
                return;
            }

            _registered = true;

            try
            {
                NativeLibrary.SetDllImportResolver(
                    typeof(InferenceSession).Assembly,
                    (name, assembly, searchPath) => Resolve(name, logger));
            }
#pragma warning disable CA1031 // A resolver already installed by someone else is not fatal.
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not register the ONNX Runtime native resolver");
            }
#pragma warning restore CA1031
        }
    }

    private static IntPtr Resolve(string libraryName, ILogger logger)
    {
        if (!libraryName.Contains(LibraryName, StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in GetCandidatePaths())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                logger.LogInformation("Loaded ONNX Runtime native library from {Path}", candidate);
                return handle;
            }

            logger.LogWarning("Found but could not load ONNX Runtime native library at {Path}", candidate);
        }

        // Returning zero lets the default resolution run, which succeeds when the library happens to
        // be installed system-wide.
        logger.LogDebug("No bundled ONNX Runtime native library found; falling back to default resolution");
        return IntPtr.Zero;
    }

    private static IEnumerable<string> GetCandidatePaths()
    {
        var pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrEmpty(pluginDirectory))
        {
            yield break;
        }

        var fileName = GetNativeFileName();
        if (fileName is null)
        {
            yield break;
        }

        // Flat next to the plugin assembly first: that is where a manual install or a flattened
        // package puts it.
        yield return Path.Combine(pluginDirectory, fileName);

        var stagedName = OperatingSystem.IsWindows() ? "onnxruntime.nativelib" : fileName;

        foreach (var rid in GetRuntimeIdentifiers())
        {
            // The layout this plugin actually ships.
            yield return Path.Combine(pluginDirectory, "native", rid, stagedName);

            // NuGet's own layout, for an install assembled straight from the build output.
            yield return Path.Combine(pluginDirectory, "runtimes", rid, "native", fileName);
        }
    }

    private static string? GetNativeFileName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "onnxruntime.dll";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "libonnxruntime.dylib";
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            return "libonnxruntime.so";
        }

        return null;
    }

    private static IEnumerable<string> GetRuntimeIdentifiers()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => null
        };

        if (architecture is null)
        {
            yield break;
        }

        if (OperatingSystem.IsWindows())
        {
            yield return "win-" + architecture;
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "osx-" + architecture;

            // The package ships a single universal dylib under osx-arm64 for some versions.
            yield return "osx-arm64";
            yield return "osx-x64";
        }
        else
        {
            yield return "linux-" + architecture;

            // musl-based images (Alpine) use a distinct RID.
            yield return "linux-musl-" + architecture;
        }
    }
}
