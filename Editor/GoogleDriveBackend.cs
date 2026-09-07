using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CupkekGames.AssetVaults.GoogleDrive
{
  /// <summary>
  /// <see cref="IVaultBackend"/> over Google Drive, driven by rclone.
  ///
  /// <para><b>No rclone.conf, ever.</b> rclone reads a remote's settings from
  /// <c>RCLONE_CONFIG_&lt;NAME&gt;_&lt;KEY&gt;</c> environment variables, so the
  /// whole remote is defined per invocation. That is what lets the editor own the
  /// setup: no interactive <c>rclone config</c> session to drive, and no shared
  /// config file to keep in sync between machines.</para>
  /// </summary>
  public class GoogleDriveBackend : IVaultBackend
  {
    private readonly GoogleDriveSettings _settings;
    private readonly string _projectRoot;

    public GoogleDriveBackend(GoogleDriveSettings settings, string projectRoot)
    {
      _settings = settings;
      _projectRoot = projectRoot;
    }

    public string Id => "gdrive";
    public string DisplayName => "Google Drive";

    /// <summary>
    /// Every failure here is something the readiness checklist can offer a button
    /// for, so the text names the fix rather than the fault.
    /// </summary>
    public bool IsConfigured(out string problem)
    {
      if (_settings == null)
      {
        problem = "No GoogleDriveSettings asset assigned.";
        return false;
      }

      if (RcloneProvisioner.Resolve(_projectRoot) == null)
      {
        problem = "rclone is not installed. Press Download rclone.";
        return false;
      }

      if (!_settings.HasClient)
      {
        problem = "No OAuth client. Create one in Google Cloud Console (Desktop app) "
                  + "and paste the id and secret into the settings asset.";
        return false;
      }

      if (!GoogleDriveCredentials.TryLoadToken(_projectRoot, out _))
      {
        problem = "Not authorised yet. Press Authorize to sign in with Google.";
        return false;
      }

      problem = null;
      return true;
    }

    // rclone reads a remote's settings from RCLONE_CONFIG_<NAME>_<KEY>, so the
    // whole remote is defined per invocation with nothing persisted outside the
    // project and no rclone.conf.
    private const string RemoteName = "ASSETVAULT";

    /// <summary>
    /// The remote as an argument: just "ASSETVAULT:Folder/object". All the
    /// configuration travels in the environment (see <see cref="ConfigEnvironment"/>).
    ///
    /// <para>This replaced an inline ":drive,key=value,...:" connection string,
    /// which could not work: those are comma and colon delimited, and an OAuth
    /// token is JSON full of both, so rclone truncated it and reported
    /// "unexpected end of JSON input". Quoting would have patched that, but the
    /// environment also keeps the refresh token off a command line that any
    /// process listing can read.</para>
    /// </summary>
    private string Remote(string objectName)
    {
      string path = _settings.RemoteFolder;
      if (!string.IsNullOrEmpty(objectName))
      {
        path += "/" + objectName;
      }

      return RemoteName + ":" + path;
    }

    private IReadOnlyDictionary<string, string> ConfigEnvironment()
    {
      if (!GoogleDriveCredentials.TryLoadToken(_projectRoot, out string token))
      {
        throw new VaultException("Not authorised. Press Authorize in the vault settings.");
      }

      return new Dictionary<string, string>
      {
        { "RCLONE_CONFIG_" + RemoteName + "_TYPE", "drive" },
        { "RCLONE_CONFIG_" + RemoteName + "_SCOPE", _settings.ScopeArgument },
        { "RCLONE_CONFIG_" + RemoteName + "_CLIENT_ID", _settings.ClientId },
        { "RCLONE_CONFIG_" + RemoteName + "_CLIENT_SECRET", _settings.ClientSecret },
        { "RCLONE_CONFIG_" + RemoteName + "_TOKEN", token },
      };
    }

    private string Exe()
    {
      string exe = RcloneProvisioner.Resolve(_projectRoot);
      if (exe == null)
      {
        throw new VaultException("rclone is not available. Press Download rclone in the vault window.");
      }

      return exe;
    }

    private List<string> CommonArgs()
    {
      return new List<string>
      {
        "--drive-chunk-size", _settings.ChunkSize,
        "--transfers", _settings.ParallelTransfers.ToString(),
        "--retries", "5",
        "--stats", "1s",
        "--stats-one-line",
      };
    }

    public async Task<IReadOnlyList<RemoteObject>> ListAsync(CancellationToken ct)
    {
      // "ps" is path-then-size, matching the order parsed below. It was "sp",
      // which is size-then-path, so every object was named after its own byte
      // count and every size parsed as 0. Counts were right, which is why the
      // connection test looked healthy for as long as it did.
      var args = new List<string> { "lsf", Remote(null), "--format", "ps", "--separator", "|" };
      string output;
      try
      {
        output = await RcloneProcess.CaptureAsync(Exe(), args, ct, ConfigEnvironment())
          .ConfigureAwait(false);
      }
      catch (VaultException e) when (e.Message.Contains("directory not found"))
      {
        // The vault folder is created by the first upload. Before that Drive
        // reports it missing, which means "empty", not "broken" - surfacing it as
        // a failure made a perfectly good connection look like one.
        return new List<RemoteObject>();
      }
      catch (VaultException e)
      {
        throw Translate(e);
      }

      var results = new List<RemoteObject>();
      foreach (string raw in output.Split('\n'))
      {
        string line = raw.Trim();
        if (line.Length == 0) continue;
        string[] parts = line.Split('|');
        if (parts.Length < 2) continue;
        long.TryParse(parts[1], out long bytes);
        results.Add(new RemoteObject(parts[0], bytes));
      }

      return results;
    }

    public async Task DownloadAsync(string remoteName, string localFile,
      IProgress<TransferProgress> progress, CancellationToken ct)
    {
      string parent = Path.GetDirectoryName(localFile);
      if (!string.IsNullOrEmpty(parent))
      {
        Directory.CreateDirectory(parent);
      }

      var args = new List<string> { "copyto", Remote(remoteName), localFile };
      args.AddRange(CommonArgs());
      await Transfer(args, progress, ct).ConfigureAwait(false);
    }

    public async Task UploadAsync(string localFile, string remoteName,
      IProgress<TransferProgress> progress, CancellationToken ct)
    {
      if (!File.Exists(localFile))
      {
        throw new VaultException($"Nothing to upload at {localFile}");
      }

      var args = new List<string> { "copyto", localFile, Remote(remoteName) };
      args.AddRange(CommonArgs());
      await Transfer(args, progress, ct).ConfigureAwait(false);
    }

    private async Task Transfer(List<string> args, IProgress<TransferProgress> progress,
      CancellationToken ct)
    {
      try
      {
        await RcloneProcess.RunAsync(Exe(), args, progress, null, ct, ConfigEnvironment())
          .ConfigureAwait(false);
      }
      catch (VaultException e)
      {
        throw Translate(e);
      }
    }

    /// <summary>
    /// rclone reports Google's failures verbatim, and the one that recurs reads
    /// like a machine fault: <c>invalid_grant</c> on a refresh means Google has
    /// revoked the refresh token. That is routine for an OAuth consent screen
    /// left in Testing status, which expires every refresh token after 7 days
    /// no matter which scope it carries. Name the fix, not the fault.
    /// </summary>
    private static VaultException Translate(VaultException e)
    {
      if (e.Message.Contains("invalid_grant"))
      {
        return new VaultException(
          "Google sign-in has expired. Press Re-authorize in the vault settings (Set up). "
          + "This repeats every 7 days while the OAuth consent screen is in Testing status; "
          + "publish it (OAuth consent screen > Publishing status > In production) to stop that.",
          e);
      }

      return e;
    }

    // Folder ids never change for the life of a folder, so one lookup per backend
    // instance is plenty: the window keeps one backend per reload, the inspector
    // makes a fresh one per click. Not static on purpose - a static cache would
    // owe the lifecycle analyzer an answer, and this is derived state anyway.
    private string _folderId;

    /// <summary>
    /// The vault folder in the Drive web UI. rclone's <c>lsf</c> can print each
    /// entry's Drive id (<c>--format i</c>), so the parent is listed once and the
    /// folder's id is picked out of it; Drive's folder URL is just that id.
    /// </summary>
    public async Task<string> GetWebUrlAsync(CancellationToken ct)
    {
      if (_folderId == null)
      {
        _folderId = await LookUpFolderIdAsync(ct).ConfigureAwait(false);
        if (_folderId == null)
        {
          return null;
        }
      }

      return "https://drive.google.com/drive/folders/" + _folderId;
    }

    private async Task<string> LookUpFolderIdAsync(CancellationToken ct)
    {
      // RemoteFolder may be nested ("Backups/Vault"): list the parent, match the leaf.
      string folder = _settings.RemoteFolder;
      int slash = folder.LastIndexOf('/');
      string parent = slash < 0 ? string.Empty : folder.Substring(0, slash);
      string leaf = slash < 0 ? folder : folder.Substring(slash + 1);

      var args = new List<string>
      {
        "lsf", RemoteName + ":" + parent, "--dirs-only", "--format", "pi", "--separator", "|",
      };
      string output;
      try
      {
        output = await RcloneProcess.CaptureAsync(Exe(), args, ct, ConfigEnvironment())
          .ConfigureAwait(false);
      }
      catch (VaultException e) when (e.Message.Contains("directory not found"))
      {
        return null;
      }
      catch (VaultException e)
      {
        throw Translate(e);
      }

      foreach (string raw in output.Split('\n'))
      {
        string line = raw.Trim();
        if (line.Length == 0) continue;
        string[] parts = line.Split('|');
        if (parts.Length < 2) continue;
        // lsf marks directories with a trailing slash.
        if (parts[0].TrimEnd('/') == leaf && parts[1].Length > 0)
        {
          return parts[1];
        }
      }

      return null;
    }

    /// <summary>
    /// The browser sign-in. <c>rclone authorize</c> runs a loopback listener on
    /// 127.0.0.1:53682, opens the browser and prints the token to stdout, so the
    /// whole OAuth dance is one non-interactive call and the editor just captures
    /// the result.
    /// </summary>
    public async Task<string> AuthorizeAsync(CancellationToken ct)
      => await AuthorizeAsync(null, ct).ConfigureAwait(false);

    private const int SignInTimeoutMinutes = 10;

    /// <param name="onSignInUrl">
    /// Receives the sign-in link the moment rclone prints it, while its listener is
    /// still alive. Before this existed the link was only visible in the failure
    /// text, i.e. after the listener had already gone, so "open it by hand" was
    /// never an option when the browser did not come up.
    /// </param>
    public async Task<string> AuthorizeAsync(Action<string> onSignInUrl, CancellationToken ct)
    {
      if (!_settings.HasClient)
      {
        throw new VaultException("Set the OAuth client id and secret before authorising.");
      }

      // An abandoned sign-in leaves rclone listening on the callback port for
      // ever, and the next attempt then fails with a bind error that reads like
      // a machine problem. Clear our own leftovers first.
      int stale = RcloneProcess.KillStaleAuthProcesses(_projectRoot);
      if (stale > 0)
      {
        UnityEngine.Debug.Log(
          $"[AssetVault] closed {stale} unfinished rclone sign-in(s) still holding port "
          + $"{RcloneProcess.AuthPort}.");
      }

      // The scope must match the one the transfers use, or the token Google
      // issues will not cover the calls that follow.
      var args = new List<string>
      {
        "authorize", "drive", _settings.ClientId, _settings.ClientSecret,
        "--drive-scope", _settings.ScopeArgument,
      };

      // rclone waits for the browser callback indefinitely. Without a deadline a
      // refused or abandoned consent hangs the process for the rest of the
      // session, which is exactly how the port got stuck. Ten minutes: the old
      // three ran out on a tab that opened behind other windows, and the user
      // then found a dead listener and a blank page.
      using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(SignInTimeoutMinutes)))
      using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
      {
        var output = new System.Text.StringBuilder();
        bool announced = false;
        // The link rclone opens; only this page redirects on to Google.
        string signInUrlPrefix = "http://127.0.0.1:" + RcloneProcess.AuthPort + "/auth?state=";
        void OnLine(string line)
        {
          output.AppendLine(line);
          if (announced) return;
          int at = line.IndexOf(signInUrlPrefix, StringComparison.Ordinal);
          if (at < 0) return;
          announced = true;
          string url = line.Substring(at).Trim();
          UnityEngine.Debug.Log(
            "[AssetVault] Google sign-in page: " + url
            + "\nIf no browser tab appeared, open that link yourself within "
            + $"{SignInTimeoutMinutes} minutes.");
          onSignInUrl?.Invoke(url);
        }

        try
        {
          await RcloneProcess.RunAsync(Exe(), args, null, OnLine, linked.Token)
            .ConfigureAwait(false);
        }
        catch (VaultException e) when (e.Message.Contains("bind"))
        {
          throw new VaultException(
            $"Port {RcloneProcess.AuthPort} is still in use, so the sign-in server could not "
            + "start. Another rclone is holding it; close it and try again.", e);
        }
        catch (VaultException e) when (e.Message.Contains("state doesn't match")
                                       || e.Message.Contains("State did not match"))
        {
          throw new VaultException(
            "The browser page belonged to an earlier sign-in attempt, so Google's answer "
            + "was refused. Press Re-authorize again and use the tab it opens this time.", e);
        }

        if (timeout.IsCancellationRequested)
        {
          throw new VaultException(
            $"Sign-in timed out after {SignInTimeoutMinutes} minutes. If the Google "
            + "page showed \"access_denied\", add your account under OAuth consent screen > "
            + "Test users, then try again.");
        }

        return FinishAuthorize(output.ToString());
      }
    }

    private string FinishAuthorize(string output)
    {
      // rclone frames the token between banner lines; the payload is the JSON object.
      int start = output.IndexOf('{');
      int end = output.LastIndexOf('}');
      if (start < 0 || end <= start)
      {
        throw new VaultException("Authorisation did not return a token.\n" + output.Trim());
      }

      string token = output.Substring(start, end - start + 1).Trim();
      GoogleDriveCredentials.SaveToken(_projectRoot, token);
      return token;
    }
  }
}
