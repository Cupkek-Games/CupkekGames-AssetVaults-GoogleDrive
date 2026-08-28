using System.Linq;
using CupkekGames.AssetVaults.Editor;
using UnityEditor;
using UnityEngine;

namespace CupkekGames.AssetVaults.GoogleDrive
{
  /// <summary>
  /// Announces the Drive backend to the vault window.
  ///
  /// <para>This file is the entire coupling between the two packages, and it
  /// points from Drive to the core rather than the other way round. Delete this
  /// assembly and the vault still opens, reports that no backend is installed,
  /// and keeps working as a list of what is on disk.</para>
  /// </summary>
  [InitializeOnLoad]
  internal static class GoogleDriveBackendRegistration
  {
    static GoogleDriveBackendRegistration()
    {
      VaultBackends.Register(new VaultBackendRegistration
      {
        Id = "gdrive",
        DisplayName = "Google Drive",
        CreateBackend = projectRoot =>
        {
          GoogleDriveSettings settings = FindSettings();
          return settings == null ? null : new GoogleDriveBackend(settings, projectRoot);
        },
        FindSettingsAsset = () => FindSettings(),
        CreateSettingsAsset = CreateSettings,
      });
    }

    internal static GoogleDriveSettings FindSettings()
    {
      return AssetDatabase.FindAssets("t:GoogleDriveSettings")
        .Select(AssetDatabase.GUIDToAssetPath)
        .Select(AssetDatabase.LoadAssetAtPath<GoogleDriveSettings>)
        .FirstOrDefault(s => s != null);
    }

    // Asked for rather than assumed: the old code wrote to a folder that only
    // existed in one project, which is exactly the kind of thing that has to go
    // before this ships to anyone else.
    private static void CreateSettings()
    {
      string path = EditorUtility.SaveFilePanelInProject(
        "Where should the Google Drive settings live?",
        "GoogleDriveVaultSettings", "asset",
        "This asset holds the OAuth client id and secret. It is committed, so keep "
        + "it out of a public repository.");
      if (string.IsNullOrEmpty(path)) return;

      var asset = ScriptableObject.CreateInstance<GoogleDriveSettings>();
      AssetDatabase.CreateAsset(asset, path);
      AssetDatabase.SaveAssets();
      Selection.activeObject = asset;
      EditorGUIUtility.PingObject(asset);
    }
  }
}
