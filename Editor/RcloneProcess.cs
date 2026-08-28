using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CupkekGames.AssetVaults.GoogleDrive
{
  /// <summary>
  /// Runs rclone and turns its output into <see cref="TransferProgress"/>.
  ///
  /// <para>rclone rather than a vendored Google client library because it
  /// already solves OAuth refresh, resumable chunked upload, retry with backoff
  /// and checksums. Reimplementing those is weeks of work and would put an OAuth
  /// secret inside a compiled assembly.</para>
  /// </summary>
  public static class RcloneProcess
  {
    // "Transferred:   1.234 GiB / 2.000 GiB, 61%, 12.345 MiB/s, ETA 1m2s"
    private static readonly Regex StatsLine = new Regex(
      @"Transferred:\s+([\d.]+)\s*(\w+)\s*/\s*([\d.]+)\s*(\w+)",
      RegexOptions.Compiled);

    public static async Task<int> RunAsync(
      string exePath,
      IReadOnlyList<string> args,
      IProgress<TransferProgress> progress,
      Action<string> onLine,
      CancellationToken ct,
      IReadOnlyDictionary<string, string> environment = null)
    {
      var startInfo = new ProcessStartInfo
      {
        FileName = exePath,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
      };
      foreach (string a in args)
      {
        startInfo.ArgumentList.Add(a);
      }

      // Backend configuration arrives through the environment, never the command
      // line. Two reasons, and both bit us: rclone connection strings are
      // comma/colon delimited, so an OAuth token JSON embedded in one is
      // truncated at its first comma ("unexpected end of JSON input"); and a
      // command line is visible to anything that can list processes, which is a
      // poor place for a refresh token.
      if (environment != null)
      {
        foreach (KeyValuePair<string, string> kv in environment)
        {
          startInfo.EnvironmentVariables[kv.Key] = kv.Value;
        }
      }

      using (var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true })
      {
        var stderr = new StringBuilder();
        var done = new TaskCompletionSource<int>();

        process.OutputDataReceived += (_, e) =>
        {
          if (e.Data == null) return;
          onLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
          if (e.Data == null) return;
          // rclone writes its --stats block to stderr, so progress lives here
          // alongside real errors. Keep both: the tail is the error message.
          stderr.AppendLine(e.Data);
          if (stderr.Length > 8000) stderr.Remove(0, 4000);
          TryReportProgress(e.Data, progress);
          onLine?.Invoke(e.Data);
        };
        process.Exited += (_, __) => done.TrySetResult(process.ExitCode);

        if (!process.Start())
        {
          throw new VaultException($"Could not start rclone at {exePath}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (ct.Register(() =>
        {
          try
          {
            if (!process.HasExited) process.Kill();
          }
          catch (Exception)
          {
            // Racing a process that exited on its own is not an error.
          }
        }))
        {
          int exitCode = await done.Task.ConfigureAwait(false);

          // The Exited event fires BEFORE the async readers have necessarily
          // delivered their last lines, so reading stdout or stderr here is a
          // race. The parameterless WaitForExit is the documented way to wait
          // for those handlers to drain; the process has already gone, so it
          // returns almost at once. Losing this race would silently truncate the
          // stderr tail an error message is built from - and, worse, the stdout
          // that AuthorizeAsync parses the OAuth token out of.
          process.WaitForExit();

          if (exitCode != 0 && !ct.IsCancellationRequested)
          {
            throw new VaultException(
              $"rclone exited with code {exitCode}.\n{Tail(stderr.ToString())}");
          }

          return exitCode;
        }
      }
    }

    /// <summary>Runs rclone and returns everything it printed to stdout.</summary>
    public static async Task<string> CaptureAsync(
      string exePath, IReadOnlyList<string> args, CancellationToken ct,
      IReadOnlyDictionary<string, string> environment = null)
    {
      var stdout = new StringBuilder();
      await RunAsync(exePath, args, null, line => stdout.AppendLine(line), ct, environment)
        .ConfigureAwait(false);
      return stdout.ToString();
    }

    private static void TryReportProgress(string line, IProgress<TransferProgress> progress)
    {
      if (progress == null) return;
      Match m = StatsLine.Match(line);
      if (!m.Success) return;

      long done = ParseSize(m.Groups[1].Value, m.Groups[2].Value);
      long total = ParseSize(m.Groups[3].Value, m.Groups[4].Value);
      progress.Report(new TransferProgress(done, total, line.Trim()));
    }

    private static long ParseSize(string value, string unit)
    {
      if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double n))
      {
        return 0L;
      }

      switch (unit.ToUpperInvariant())
      {
        case "B": return (long)n;
        case "KIB": case "KB": return (long)(n * 1024);
        case "MIB": case "MB": return (long)(n * 1024 * 1024);
        case "GIB": case "GB": return (long)(n * 1024L * 1024 * 1024);
        case "TIB": case "TB": return (long)(n * 1024L * 1024 * 1024 * 1024);
        default: return (long)n;
      }
    }

    private static string Tail(string text)
    {
      if (string.IsNullOrEmpty(text)) return "(no output)";
      string[] lines = text.TrimEnd().Split('\n');
      int take = Math.Min(6, lines.Length);
      return string.Join("\n", lines, lines.Length - take, take).Trim();
    }

    /// <summary>
    /// Where an editor-provisioned rclone lives: project-local, so no admin
    /// rights, no PATH edits and no installer are involved.
    ///
    /// <para>Under <c>Library/</c> deliberately. Every Unity .gitignore in
    /// existence excludes that folder, which a package cannot say of anywhere
    /// else - and a package whose default behaviour is to make consumers commit
    /// a 20 MB binary would be a bad neighbour. The cost is that clearing
    /// Library re-downloads it, which takes seconds.</para>
    /// </summary>
    public static string ManagedExePath(string projectRoot)
      => Path.Combine(projectRoot, "Library", "AssetVault", "rclone", RclonePlatform.ExeName);

    /// <summary>
    /// The loopback port rclone hardcodes for the OAuth callback. It is baked
    /// into the redirect URI, so it cannot be moved to a free port.
    /// </summary>
    public const int AuthPort = 53682;

    /// <summary>
    /// Kill any rclone we previously started that is still sitting on the auth
    /// port.
    ///
    /// <para>An abandoned sign-in leaves rclone listening FOREVER: it is waiting
    /// for a callback that will never arrive because the browser tab was closed
    /// or the consent was refused. The next Authorize then dies with "Only one
    /// usage of each socket address", which names the symptom and not the cause.
    /// Only processes launched from <paramref name="projectRoot"/> are touched,
    /// so a system-wide rclone doing real work is never disturbed.</para>
    /// </summary>
    public static int KillStaleAuthProcesses(string projectRoot)
    {
      string managed = ManagedExePath(projectRoot);
      int killed = 0;

      foreach (Process process in Process.GetProcessesByName("rclone"))
      {
        try
        {
          string path = process.MainModule != null ? process.MainModule.FileName : null;
          if (path == null ||
              !path.Equals(managed, StringComparison.OrdinalIgnoreCase))
          {
            continue;
          }

          process.Kill();
          process.WaitForExit(3000);
          killed++;
        }
        catch (Exception)
        {
          // Access denied or the process exited underneath us: neither is worth
          // failing an authorisation over.
        }
        finally
        {
          process.Dispose();
        }
      }

      return killed;
    }
  }
}
