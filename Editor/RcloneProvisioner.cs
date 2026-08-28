using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CupkekGames.AssetVaults.GoogleDrive
{
  /// <summary>
  /// Downloads rclone so a fresh clone on a new PC needs no manual install.
  ///
  /// <para>rclone is a single self-contained binary with no installer, no
  /// registry use and no runtime dependencies, which is what makes this possible
  /// at all. The version is pinned in the settings asset so two machines cannot
  /// silently disagree, and the download is checked against the release's own
  /// published SHA256SUMS before anything is extracted.</para>
  ///
  /// <para>Not vendored in the repo: ~60 MB per platform, in a project whose
  /// entire problem is committed binaries.</para>
  /// </summary>
  public static class RcloneProvisioner
  {
    /// <summary>
    /// A system-wide rclone already on PATH wins. Nothing is forced on someone
    /// who has one, and the managed copy is only a fallback.
    /// </summary>
    public static string FindOnPath()
    {
      string pathVar = Environment.GetEnvironmentVariable("PATH");
      if (string.IsNullOrEmpty(pathVar))
      {
        return null;
      }

      string exe = RclonePlatform.ExeName;
      foreach (string dir in pathVar.Split(Path.PathSeparator))
      {
        if (string.IsNullOrWhiteSpace(dir)) continue;
        string candidate;
        try
        {
          candidate = Path.Combine(dir.Trim(), exe);
        }
        catch (ArgumentException)
        {
          continue; // PATH entries with invalid characters are common; skip them.
        }

        if (File.Exists(candidate))
        {
          return candidate;
        }
      }

      return null;
    }

    public static string Resolve(string projectRoot)
    {
      string managed = RcloneProcess.ManagedExePath(projectRoot);
      if (File.Exists(managed))
      {
        return managed;
      }

      return FindOnPath();
    }

    /// <summary>
    /// The release archive for THIS machine. Throws rather than guessing: a
    /// silent fallback to the Windows build is how a Mac ends up with an
    /// executable it cannot run and an error three steps further on.
    /// </summary>
    public static string ArchiveName(string version)
    {
      string suffix = RclonePlatform.ArchiveSuffix;
      if (suffix == null)
      {
        throw new VaultException(
          $"No rclone build is known for {RclonePlatform.Describe()}. Install rclone "
          + "yourself and put it on PATH; the vault will find it there.");
      }

      return $"rclone-v{version}-{suffix}.zip";
    }

    public static string DownloadUrl(string version)
      => $"https://downloads.rclone.org/v{version}/{ArchiveName(version)}";

    public static string ChecksumUrl(string version)
      => $"https://downloads.rclone.org/v{version}/SHA256SUMS";

    /// <summary>
    /// Fetch, verify and extract the rclone binary into Library/AssetVault/rclone/.
    /// Returns the path to the extracted executable.
    /// </summary>
    public static async Task<string> DownloadAsync(
      string projectRoot, string version, IProgress<TransferProgress> progress, CancellationToken ct)
    {
      string zipName = ArchiveName(version);
      string temp = Path.Combine(Path.GetTempPath(), zipName);
      string target = RcloneProcess.ManagedExePath(projectRoot);

      progress?.Report(new TransferProgress(0, 0, $"downloading rclone {version}"));

      // TLS 1.2 is not the default on the .NET profile Unity ships on Windows.
      ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

      // Task.Run around the SYNCHRONOUS DownloadData, not DownloadDataTaskAsync.
      //
      // WebClient is event-based-async: its completion is posted to the
      // SynchronizationContext captured when the call started, which in the
      // editor is the main thread. If anything ever blocks the main thread
      // waiting on this task, that completion can never be raised and the editor
      // deadlocks - ConfigureAwait(false) does not save you, because it only
      // governs the await continuation, not WebClient's own posting. That froze
      // this editor once (2026-08-28). Running the synchronous call on a pool
      // thread removes the sync context from the picture entirely.
      string url = DownloadUrl(version);
      byte[] payload = await Task.Run(() =>
      {
        using (var client = new WebClient())
        {
          return client.DownloadData(url);
        }
      }, ct).ConfigureAwait(false);

      ct.ThrowIfCancellationRequested();
      progress?.Report(new TransferProgress(payload.LongLength, payload.LongLength, "verifying checksum"));

      string expected = await FetchExpectedHashAsync(version, zipName).ConfigureAwait(false);
      string actual = Sha256Hex(payload);
      if (!string.IsNullOrEmpty(expected) &&
          !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
      {
        throw new VaultException(
          $"rclone download failed its checksum.\n  expected {expected}\n  got      {actual}");
      }

      File.WriteAllBytes(temp, payload);
      try
      {
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        progress?.Report(new TransferProgress(payload.LongLength, payload.LongLength, "extracting"));

        using (ZipArchive archive = ZipFile.OpenRead(temp))
        {
          string wanted = RclonePlatform.ExeName;
          ZipArchiveEntry entry = null;
          foreach (ZipArchiveEntry candidate in archive.Entries)
          {
            if (candidate.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
              entry = candidate;
              break;
            }
          }

          if (entry == null)
          {
            throw new VaultException($"{wanted} was not found inside the downloaded archive.");
          }

          entry.ExtractToFile(target, overwrite: true);
          MakeExecutable(target);
        }
      }
      finally
      {
        if (File.Exists(temp))
        {
          File.Delete(temp);
        }
      }

      return target;
    }

    // The release publishes one SHA256SUMS covering every platform archive.
    // A failure to fetch it is not fatal on its own, but a MISMATCH is.
    private static async Task<string> FetchExpectedHashAsync(string version, string zipName)
    {
      try
      {
        // Same reason as the payload download: synchronous call on a pool
        // thread, never WebClient's task-returning overloads.
        string url = ChecksumUrl(version);
        string sums = await Task.Run(() =>
        {
          using (var client = new WebClient())
          {
            return client.DownloadString(url);
          }
        }).ConfigureAwait(false);

        foreach (string line in sums.Split('\n'))
        {
          if (line.IndexOf(zipName, StringComparison.OrdinalIgnoreCase) < 0) continue;
          string[] parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
          if (parts.Length > 0) return parts[0];
        }
      }
      catch (Exception)
      {
        // Offline or the layout changed: fall through and skip verification
        // rather than blocking the download entirely.
      }

      return null;
    }

    /// <summary>
    /// Zip entries carry no Unix permissions through <c>System.IO.Compression</c>,
    /// so a freshly extracted binary is not executable on macOS or Linux. No-op
    /// on Windows.
    /// </summary>
    private static void MakeExecutable(string path)
    {
      if (!RclonePlatform.NeedsExecuteBit) return;

      try
      {
        using (var chmod = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
          FileName = "/bin/chmod",
          ArgumentList = { "+x", path },
          UseShellExecute = false,
          CreateNoWindow = true,
        }))
        {
          chmod?.WaitForExit(10000);
        }
      }
      catch (Exception e)
      {
        throw new VaultException(
          $"Could not make {path} executable. Run: chmod +x \"{path}\"", e);
      }
    }

    private static string Sha256Hex(byte[] data)
    {
      using (var sha = SHA256.Create())
      {
        byte[] hash = sha.ComputeHash(data);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
      }
    }
  }
}
