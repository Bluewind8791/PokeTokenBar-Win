using System.Diagnostics;
using System.IO;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// The side effects behind Settings buttons: save export/import, snapshots, problem report, log file,
/// links and updates. The popover is transient, so file dialogs and alerts close it first and report
/// the result in an alert or File Explorer (macOS reveals in Finder / shows an NSAlert the same way).
/// </summary>
internal static class SettingsActions
{
    /// <summary>The developer's public contact (SupportMail.swift).</summary>
    public const string SupportAddress = "parkdongmin123@gmail.com";

    private static string DeviceName => Environment.MachineName;

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Write($"open url failed: {ex.Message}");
        }
    }

    /// <summary>Windows has no in-place upgrade channel: applying an update opens its release page.</summary>
    public static void ApplyUpdate(AppServices s)
    {
        if (s.Updater.UpdateTarget is { } target && UpdateChecker.IsTrustedReleaseURL(target.Url)) OpenUrl(target.Url);
    }

    /// <summary>The export time in the confirmation and snapshot list — short date and time in the app language.</summary>
    public static string ExportedAtText(DateTimeOffset date, AppLanguage language)
    {
        var culture = language.DisplayCulture();
        return date.ToLocalTime().ToString("g", culture);
    }

    public static void ExportSave(AppServices s)
    {
        var l = s.L;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = l.ExportSaveLabel,
            FileName = s.Companion.SuggestedExportFileName,
            Filter = "JSON (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
        };
        s.ClosePopoverForDialog();
        if (dialog.ShowDialog() != true) return;
        var path = dialog.FileName;
        try
        {
            var data = s.Companion.ExportedSaveData(s.Updater.CurrentVersion, DeviceName);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
            RevealInExplorer(path);
        }
        catch (Exception ex)
        {
            AppLog.Write($"save export failed: {ex.Message}");
            MacAlert.Show(l.ExportSaveLabel, l.UserFacingError(ex), [l.Close], warning: true);
        }
    }

    public static void ImportSave(AppServices s)
    {
        var l = s.L;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = l.ImportSaveLabel,
            Filter = "JSON (*.json)|*.json",
            Multiselect = false,
        };
        s.ClosePopoverForDialog();
        if (dialog.ShowDialog() != true) return;

        SaveEnvelope envelope;
        try
        {
            var info = new FileInfo(dialog.FileName);
            // Checked before reading: a wrong multi-gigabyte pick must not be loaded to find out.
            if (info.Length > SaveTransfer.MaxFileBytes)
                throw new SaveTransferException(SaveTransferErrorKind.FileTooLarge, bytes: (int)Math.Min(int.MaxValue, info.Length), limit: SaveTransfer.MaxFileBytes);
            envelope = SaveTransfer.Decode(File.ReadAllBytes(dialog.FileName));
        }
        catch (Exception ex)
        {
            AppLog.Write($"save import read failed: {ex.Message}");
            MacAlert.Show(l.ImportSaveLabel, ImportError(l, ex), [l.Close], warning: true);
            return;
        }
        var incoming = new SaveSummary(envelope.State);
        // Not applied on pick: show what is replaced, in numbers, and ask once more.
        var current = s.Companion.TransferSummary;
        var body = l.ImportConfirmBody(incoming.DexCount, TokenFormatter.Compact(incoming.LifetimeTokens),
            ExportedAtText(envelope.ExportedAt, s.Companion.Language), envelope.SourceDevice,
            current.DexCount, TokenFormatter.Compact(current.LifetimeTokens));
        // The destructive action is never the default button (one Return must not replace progress): ImportConfirmPolicy.
        var choice = MacAlert.Show(l.ImportConfirmTitle, body, [l.ImportConfirmReplace, l.Cancel],
            defaultIndex: ImportConfirmPolicy.CancelButtonIndex, cancelIndex: ImportConfirmPolicy.CancelButtonIndex, warning: true);
        if (choice != ImportConfirmPolicy.ReplaceButtonIndex) return;

        try
        {
            s.Companion.ApplySave(envelope, s.Store.TodayTokensByProvider, LocalUsageReader.LocalDay(DateTimeOffset.Now), s.Store.HasUsageData);
        }
        catch (Exception ex)
        {
            AppLog.Write($"save import apply failed: {ex.Message}");
            MacAlert.Show(l.ImportSaveLabel, ImportError(l, ex), [l.Close], warning: true);
            return;
        }
        MacAlert.Show(l.ImportSaveLabel, l.ImportSaveDone(incoming.DexCount, TokenFormatter.Compact(incoming.LifetimeTokens)), [l.Close]);
    }

    public static void TakeManualSnapshot(AppServices s)
    {
        var l = s.L;
        s.ClosePopoverForDialog();
        try
        {
            s.Companion.CreateManualSnapshot();
            MacAlert.Show(l.CreateSnapshotButton, l.SnapshotCreatedToast, [l.Close]);
        }
        catch (Exception ex)
        {
            AppLog.Write($"manual snapshot creation failed: {ex.Message}");
            MacAlert.Show(l.CreateSnapshotButton, l.UserFacingError(ex), [l.Close], warning: true);
        }
    }

    public static void ConfirmAndRestoreSnapshot(AppServices s, SaveSnapshot snapshot)
    {
        var l = s.L;
        var current = s.Companion.TransferSummary;
        var body = l.RestoreConfirmBody(ExportedAtText(snapshot.Date, s.Companion.Language), snapshot.DexCount,
            TokenFormatter.Compact(snapshot.LifetimeTokens), current.DexCount, TokenFormatter.Compact(current.LifetimeTokens));
        s.ClosePopoverForDialog();
        var choice = MacAlert.Show(l.RestoreConfirmTitle, body, [l.RestoreSnapshotButton, l.Cancel],
            defaultIndex: ImportConfirmPolicy.CancelButtonIndex, cancelIndex: ImportConfirmPolicy.CancelButtonIndex, warning: true);
        if (choice != ImportConfirmPolicy.ReplaceButtonIndex) return;
        try
        {
            s.Companion.RestoreSnapshot(snapshot, s.Store.TodayTokensByProvider, LocalUsageReader.LocalDay(DateTimeOffset.Now), s.Store.HasUsageData);
            MacAlert.Show(l.RestoreSnapshotButton, l.RestoreDoneMessage(snapshot.DexCount, TokenFormatter.Compact(snapshot.LifetimeTokens)), [l.Close]);
        }
        catch (Exception ex)
        {
            AppLog.Write($"snapshot restore failed: {ex.Message}");
            MacAlert.Show(l.RestoreSnapshotButton, ImportError(l, ex), [l.Close], warning: true);
        }
    }

    private static string ImportError(L l, Exception ex) => ex is SaveTransferException t
        ? l.ImportErrorMessage(t.Kind switch
        {
            SaveTransferErrorKind.NotASaveFile => "notASaveFile",
            SaveTransferErrorKind.NewerSchema => "newerSchema",
            SaveTransferErrorKind.FileTooLarge => "fileTooLarge",
            _ => "backupFailed",
        })
        : l.UserFacingError(ex);

    /// <summary>Opens a report mail with diagnostics filled in. False when no mail handler took it (the
    /// view then shows the address to copy).</summary>
    public static bool ReportProblem(AppServices s)
    {
        var l = s.L;
        var version = s.Updater.CurrentVersion;
        var subject = l.ReportMailSubject(version);
        var body = l.ReportMailBody(version, Environment.OSVersion.VersionString);
        // Uri.EscapeDataString encodes '+' too, which mail clients would otherwise read as a space.
        var url = $"mailto:{SupportAddress}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"report mail failed: {ex.Message}");
            return false;
        }
    }

    public static void RevealLogFile() => RevealInExplorer(AppLog.LogFilePath);

    private static void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Write($"reveal failed: {ex.Message}");
        }
    }
}
