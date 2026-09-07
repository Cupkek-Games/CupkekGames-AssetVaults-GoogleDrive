using System;
using System.IO;
using UnityEngine;

namespace CupkekGames.AssetVaults.GoogleDrive
{
  /// <summary>
  /// Committed configuration for the Drive backend.
  ///
  /// <para>The client id and secret live here on purpose (owner decision,
  /// 2026-08-28): RFC 8252 treats a desktop-app client secret as
  /// non-confidential, because it cannot be kept secret inside a distributed
  /// application, and Google issues them for exactly this flow.</para>
  ///
  /// <para><b>The refresh token does NOT live here.</b> That one grants access to
  /// the owner's Drive, so it goes to <see cref="GoogleDriveCredentials"/> under
  /// <c>UserSettings/</c>, which is gitignored. This is the single place Unity
  /// Localization is a bad model to copy: it keeps credentials in the asset and
  /// people commit them.</para>
  ///
  /// <para>If this repository ever becomes public, rotate the client in Google
  /// Cloud Console first.</para>
  /// </summary>
  [CreateAssetMenu(
    fileName = "GoogleDriveVaultSettings",
    menuName = "CupkekGames/Asset Vault/Google Drive Settings")]
  public class GoogleDriveSettings : ScriptableObject
  {
    [Header("Drive")]
    [Tooltip("Folder inside your Drive that holds the pack archives. Created on first upload.")]
    [SerializeField] private string _remoteFolder = "AssetVault";

    [Header("OAuth client (Google Cloud Console > Credentials > OAuth client ID > Desktop app)")]
    [Tooltip("Safe to commit. Google has no API to create this, so it is the one manual step.")]
    [SerializeField] private string _clientId = "";

    [Tooltip("Safe to commit WHILE THIS REPO IS PRIVATE. Rotate the client before making it public.")]
    [SerializeField] private string _clientSecret = "";

    [Header("Scope")]
    [Tooltip("Drive = full access; lets you manage the vault folder by hand in the Drive web " +
             "UI. Cost: 'drive' is a RESTRICTED scope, so a production consent screen shows " +
             "Google's 'unverified app' warning on sign-in. " +
             "DriveFile = only files this app created: not restricted, so no warning, but " +
             "rclone cannot then see anything you upload manually. " +
             "Neither scope avoids the 7-day refresh-token expiry of a Testing-status consent " +
             "screen; publishing the consent screen does.")]
    [SerializeField] private DriveScope _scope = DriveScope.Drive;

    [Header("Transfer")]
    [Tooltip("Larger chunks materially improve large-file upload throughput on Drive.")]
    [SerializeField] private string _chunkSize = "64M";

    [SerializeField, Range(1, 16)] private int _parallelTransfers = 4;

    [Header("rclone")]
    [Tooltip("Version downloaded on demand into Library/. Pinned so two machines cannot disagree.")]
    [SerializeField] private string _rcloneVersion = "1.68.2";

    /// <summary>
    /// rclone's scope values. Defaults to <see cref="Drive"/> at the owner's
    /// choice (2026-08-28): full access keeps the vault folder manageable by hand
    /// in the Drive web UI. <see cref="DriveFile"/> stays available because it is
    /// not a restricted scope, so a published consent screen needs no verification
    /// and shows no warning. Neither scope escapes the 7-day refresh-token expiry
    /// of a Testing-status consent screen (learned 2026-09-07: the first token
    /// died on day 10); only publishing the consent screen does.
    /// </summary>
    public enum DriveScope
    {
      DriveFile,
      Drive,
    }

    public string ScopeArgument => _scope == DriveScope.Drive ? "drive" : "drive.file";

    public string RemoteFolder => string.IsNullOrWhiteSpace(_remoteFolder)
      ? "AssetVault"
      : _remoteFolder.Trim().Trim('/');

    public string ClientId => _clientId != null ? _clientId.Trim() : string.Empty;
    public string ClientSecret => _clientSecret != null ? _clientSecret.Trim() : string.Empty;
    public string ChunkSize => string.IsNullOrWhiteSpace(_chunkSize) ? "64M" : _chunkSize.Trim();
    public int ParallelTransfers => Mathf.Clamp(_parallelTransfers, 1, 16);
    public string RcloneVersion => string.IsNullOrWhiteSpace(_rcloneVersion) ? "1.68.2" : _rcloneVersion.Trim();

    public bool HasClient => !string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(ClientSecret);
  }

  /// <summary>
  /// The refresh token, stored per machine and never committed.
  ///
  /// <para><c>UserSettings/</c> is Unity's per-user folder and was added to
  /// .gitignore before any of this was written, precisely so the first commit
  /// could not leak a token.</para>
  /// </summary>
  public static class GoogleDriveCredentials
  {
    private const string FileName = "AssetVault.GoogleDrive.json";

    [Serializable]
    private class Payload
    {
      public string token;
      public string authorizedUtc;
    }

    public static string FilePath(string projectRoot)
      => Path.Combine(projectRoot, "UserSettings", FileName);

    public static bool TryLoadToken(string projectRoot, out string token)
    {
      token = null;
      string path = FilePath(projectRoot);
      if (!File.Exists(path))
      {
        return false;
      }

      try
      {
        var payload = JsonUtility.FromJson<Payload>(File.ReadAllText(path));
        if (payload == null || string.IsNullOrWhiteSpace(payload.token))
        {
          return false;
        }

        token = payload.token;
        return true;
      }
      catch (Exception)
      {
        // A corrupt credentials file is not fatal: re-authorising rewrites it.
        return false;
      }
    }

    public static void SaveToken(string projectRoot, string token)
    {
      string path = FilePath(projectRoot);
      Directory.CreateDirectory(Path.GetDirectoryName(path));
      var payload = new Payload
      {
        token = token,
        authorizedUtc = DateTime.UtcNow.ToString("o"),
      };
      File.WriteAllText(path, JsonUtility.ToJson(payload, true));
    }

    public static void Clear(string projectRoot)
    {
      string path = FilePath(projectRoot);
      if (File.Exists(path))
      {
        File.Delete(path);
      }
    }
  }
}
