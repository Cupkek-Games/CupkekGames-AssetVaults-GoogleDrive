using System;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace CupkekGames.AssetVaults.GoogleDrive
{
  /// <summary>
  /// The Localization-style setup panel: paste the client once, press Authorize,
  /// the browser handles the rest.
  ///
  /// <para>Lives in its own assembly so the generic vault window never references
  /// anything Drive-shaped. That separation is the whole reason the backend seam
  /// is worth having.</para>
  /// </summary>
  [CustomEditor(typeof(GoogleDriveSettings))]
  public class GoogleDriveSettingsInspector : UnityEditor.Editor
  {
    private enum Outcome { None, Working, Success, Failure }

    // Same reason as AssetVaultWindow: an Editor keeps its private fields across
    // a domain reload, so a recompile during sign-in would leave _busy true and
    // every button disabled with no way back. The awaited call is gone by then.
    [NonSerialized] private string _status;
    [NonSerialized] private Outcome _outcome = Outcome.None;
    [NonSerialized] private bool _busy;
    [NonSerialized] private bool _pumping;

    private static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

    private void OnDisable() => StopPump();

    // An async continuation cannot repaint an inspector that Unity is not
    // already drawing, which is why a completed sign-in appeared to do nothing.
    // Pumping repaints for the duration keeps the panel live and lets the
    // "waiting for browser" state actually animate.
    private void StartPump()
    {
      if (_pumping) return;
      _pumping = true;
      EditorApplication.update += Pump;
    }

    private void StopPump()
    {
      if (!_pumping) return;
      _pumping = false;
      EditorApplication.update -= Pump;
    }

    private void Pump() => Repaint();

    public override void OnInspectorGUI()
    {
      DrawDefaultInspector();
      var settings = (GoogleDriveSettings)target;

      string rclone = RcloneProvisioner.Resolve(ProjectRoot);
      bool hasToken = GoogleDriveCredentials.TryLoadToken(ProjectRoot, out string token);

      EditorGUILayout.Space(10);
      EditorGUILayout.LabelField("Setup", EditorStyles.boldLabel);
      DrawStep(1, "rclone", rclone != null,
        rclone != null ? "installed" : "not installed");
      DrawStep(2, "OAuth client", settings.HasClient,
        settings.HasClient ? "id and secret set" : "paste id and secret above");
      DrawStep(3, "Google account", hasToken,
        hasToken ? DescribeToken(token) : "not signed in");

      EditorGUILayout.Space(6);

      if (!settings.HasClient)
      {
        EditorGUILayout.HelpBox(
          "One manual step, once: Google Cloud Console > new project > enable the "
          + "Drive API > Credentials > OAuth client ID > application type DESKTOP APP. "
          + "Google has no API to create this; everything after it is buttons.",
          MessageType.Info);
      }

      if (rclone == null)
      {
        EditorGUILayout.HelpBox(
          "rclone is not installed yet. It is a single self-contained binary, so "
          + "this needs no admin rights and touches nothing outside the project.",
          MessageType.Warning);
        using (new EditorGUI.DisabledScope(_busy))
        {
          if (GUILayout.Button($"Download rclone {settings.RcloneVersion}"))
          {
            DownloadRclone(settings);
          }
        }
      }

      using (new EditorGUI.DisabledScope(_busy || rclone == null || !settings.HasClient))
      {
        using (new EditorGUILayout.HorizontalScope())
        {
          if (GUILayout.Button(hasToken ? "Re-authorize..." : "Authorize with Google...",
                GUILayout.Height(24)))
          {
            Authorize(settings);
          }

          using (new EditorGUI.DisabledScope(!hasToken))
          {
            if (GUILayout.Button("Test connection", GUILayout.Height(24), GUILayout.Width(130)))
            {
              TestConnection(settings);
            }
          }
        }
      }

      using (new EditorGUI.DisabledScope(_busy || !hasToken))
      {
        if (GUILayout.Button("Sign out (delete the local token)"))
        {
          GoogleDriveCredentials.Clear(ProjectRoot);
          Set(Outcome.None, "Token deleted. Authorize again when you need it.");
        }
      }

      DrawOutcome();

      EditorGUILayout.Space(6);
      EditorGUILayout.HelpBox(
        "The refresh token is written to UserSettings/ (gitignored) and never to "
        + "this asset. The client id and secret above ARE committed, which is fine "
        + "while the repo is private; rotate the client before making it public.",
        MessageType.None);
    }

    private static void DrawStep(int index, string label, bool done, string detail)
    {
      using (new EditorGUILayout.HorizontalScope())
      {
        Color previous = GUI.color;
        GUI.color = done ? new Color(0.45f, 0.85f, 0.45f) : new Color(0.95f, 0.75f, 0.3f);
        EditorGUILayout.LabelField(done ? "OK" : "..", GUILayout.Width(24));
        GUI.color = previous;
        EditorGUILayout.LabelField($"{index}. {label}", GUILayout.Width(120));
        EditorGUILayout.LabelField(detail, EditorStyles.miniLabel);
      }
    }

    private static string DescribeToken(string token)
    {
      // The token is JSON; surface only when it expires, never its contents.
      const string key = "\"expiry\":\"";
      int at = token.IndexOf(key, StringComparison.Ordinal);
      if (at < 0) return "signed in";

      int start = at + key.Length;
      int end = token.IndexOf('"', start);
      if (end <= start) return "signed in";

      if (DateTime.TryParse(token.Substring(start, end - start), out DateTime expiry))
      {
        TimeSpan left = expiry.ToUniversalTime() - DateTime.UtcNow;
        if (left.TotalMinutes < 0) return "signed in (access token expired, refreshes on use)";
        return $"signed in (access token valid {(int)left.TotalMinutes} min; auto-refreshes)";
      }

      return "signed in";
    }

    private void DrawOutcome()
    {
      if (string.IsNullOrEmpty(_status)) return;
      EditorGUILayout.Space(4);
      MessageType type;
      switch (_outcome)
      {
        case Outcome.Success: type = MessageType.Info; break;
        case Outcome.Failure: type = MessageType.Error; break;
        default: type = MessageType.None; break;
      }

      EditorGUILayout.HelpBox(_status, type);
    }

    private void Set(Outcome outcome, string message)
    {
      _outcome = outcome;
      _status = message;
      Repaint();
    }

    private async void DownloadRclone(GoogleDriveSettings settings)
    {
      _busy = true;
      StartPump();
      Set(Outcome.Working, "Downloading rclone " + settings.RcloneVersion + "...");
      try
      {
        string exe = await RcloneProvisioner.DownloadAsync(
          ProjectRoot, settings.RcloneVersion, null, CancellationToken.None);
        Set(Outcome.Success, "rclone is ready.\n" + exe);
        Debug.Log("[AssetVault] rclone ready at " + exe);
      }
      catch (Exception e)
      {
        Set(Outcome.Failure, "rclone download failed:\n" + e.Message);
        Debug.LogError("[AssetVault] " + e);
      }
      finally
      {
        _busy = false;
        StopPump();
        Repaint();
      }
    }

    private async void Authorize(GoogleDriveSettings settings)
    {
      _busy = true;
      StartPump();
      Set(Outcome.Working,
        "A browser window is opening. Approve access there, then come back.\n"
        + "If Google says access_denied, add your account under "
        + "OAuth consent screen > Test users.");
      try
      {
        var backend = new GoogleDriveBackend(settings, ProjectRoot);
        await backend.AuthorizeAsync(CancellationToken.None);
        Set(Outcome.Success,
          "Signed in. The token is saved to UserSettings/ on this machine only.\n"
          + "Next: press Test connection.");
        Debug.Log("[AssetVault] Google Drive authorised.");
      }
      catch (Exception e)
      {
        Set(Outcome.Failure, "Authorisation failed:\n" + e.Message);
        Debug.LogError("[AssetVault] " + e);
      }
      finally
      {
        _busy = false;
        StopPump();
        Repaint();
      }
    }

    private async void TestConnection(GoogleDriveSettings settings)
    {
      _busy = true;
      StartPump();
      Set(Outcome.Working, "Listing " + settings.RemoteFolder + "...");
      try
      {
        var backend = new GoogleDriveBackend(settings, ProjectRoot);
        var objects = await backend.ListAsync(CancellationToken.None);
        long total = 0L;
        foreach (RemoteObject o in objects) total += o.Bytes;

        // An empty result is the normal state before the first push: the folder
        // is created by the first upload.
        Set(Outcome.Success, objects.Count == 0
          ? $"Connected. '{settings.RemoteFolder}' is empty or not created yet; "
            + "the first push creates it."
          : $"Connected. {objects.Count} archive(s), "
            + $"{AssetVaults.VaultStatus.Describe(total)} in '{settings.RemoteFolder}'.");
      }
      catch (Exception e)
      {
        Set(Outcome.Failure, "Connection failed:\n" + e.Message);
        Debug.LogError("[AssetVault] " + e);
      }
      finally
      {
        _busy = false;
        StopPump();
        Repaint();
      }
    }
  }
}
