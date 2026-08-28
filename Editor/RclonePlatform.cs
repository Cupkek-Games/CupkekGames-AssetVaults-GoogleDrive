using System.Runtime.InteropServices;
using UnityEngine;

namespace CupkekGames.AssetVaults.GoogleDrive
{
  /// <summary>
  /// Which rclone build this machine needs.
  ///
  /// <para>Split out because the provisioner used to hardcode
  /// <c>windows-amd64</c> and <c>rclone.exe</c>. In one Windows project that is
  /// invisible; in a package it means a Mac quietly downloads a Windows
  /// executable and fails later with something unrecognisable. The names below
  /// are rclone's published release names.</para>
  ///
  /// <para><b>Only the Windows path has actually been run.</b> The others are
  /// the documented archive names and the standard execute-bit step; treat a
  /// first run on macOS or Linux as unproven rather than supported.</para>
  /// </summary>
  internal static class RclonePlatform
  {
    public static bool IsWindows =>
      Application.platform == RuntimePlatform.WindowsEditor
      || Application.platform == RuntimePlatform.WindowsPlayer;

    private static bool IsMac =>
      Application.platform == RuntimePlatform.OSXEditor
      || Application.platform == RuntimePlatform.OSXPlayer;

    private static bool IsLinux =>
      Application.platform == RuntimePlatform.LinuxEditor
      || Application.platform == RuntimePlatform.LinuxPlayer;

    public static string ExeName => IsWindows ? "rclone.exe" : "rclone";

    /// <summary>True where the extracted binary needs its execute bit set.</summary>
    public static bool NeedsExecuteBit => !IsWindows;

    private static bool IsArm64 => RuntimeInformation.OSArchitecture == Architecture.Arm64;

    /// <summary>
    /// The suffix in <c>rclone-v1.2.3-&lt;suffix&gt;.zip</c>, or null when this
    /// platform has no mapping. Callers must report that rather than guessing.
    /// </summary>
    public static string ArchiveSuffix
    {
      get
      {
        if (IsWindows) return IsArm64 ? "windows-arm64" : "windows-amd64";
        if (IsMac) return IsArm64 ? "osx-arm64" : "osx-amd64";
        if (IsLinux) return IsArm64 ? "linux-arm64" : "linux-amd64";
        return null;
      }
    }

    public static string Describe() =>
      $"{Application.platform} / {RuntimeInformation.OSArchitecture}";
  }
}
