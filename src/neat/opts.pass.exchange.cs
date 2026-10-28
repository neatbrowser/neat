using System.Diagnostics;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace neat;

/// <summary>
/// The Passwords page, third part: the list of sites never to offer to save for, editing a login,
/// and moving logins in and out as CSV files (the format Chrome, Edge and Firefox use).
///
/// A CSV file holds every password in plain text. NEAT says so each time it makes one or reads
/// one, makes the person pass the gate before exporting, never overwrites an existing file, and
/// offers to delete the file as soon as it has been written or imported.
/// </summary>
public sealed partial class Opts
{
    // A password export is a few hundred kilobytes at most; anything much bigger is not one.
    private const long ImportMaxBytes = 8L * 1024 * 1024;

    // ---- sites never to offer to save for ----

    private UIElement NeverCard()
    {
        var sites = App.Cfg.Cur.NeverSave;

        if (sites.Count == 0)
        {
            return Block.Card(_skin,
                Explain("No sites. When you choose Never for this site on the save question, the site is listed here."));
        }

        var rows = new List<UIElement>();
        foreach (var site in sites.OrderBy(s => s, StringComparer.Ordinal).ToList())
        {
            var origin = site;

            rows.Add(Block.Row(_skin, origin, "NEAT does not offer to save a password here.",
                IconBtn("\uE74D", "Offer to save passwords here again", () => ForgetNever(origin), true)));
        }

        return Block.Card(_skin, rows.ToArray());
    }

    private async Task ForgetNever(string origin)
    {
        if (Never.Remove(App.Cfg.Cur.NeverSave, origin))
            App.Cfg.Save();

        await RefreshPass();
    }

    // ---- editing a login ----

    private void TaskEdit(LoginInfo it)
    {
        var site = new TextBox { Width = 260, Text = it.Site };
        var user = new TextBox { Width = 260, Text = it.User };
        var pass = new PasswordBox { Width = 260 };

        async Task Save()
        {
            var typed = site.Text.Trim();
            var origin = Origin.Of(typed.Contains("://") ? typed : "https://" + typed);

            if (origin is null)
            {
                Fail("Enter the address of a web site, like example.com.");
                return;
            }

            // An empty password box keeps the saved password.
            if (!App.Vault.Update(it.Id, origin, user.Text.Trim(), pass.Password.Length == 0 ? null : pass.Password))
            {
                Fail("That login could not be changed. It may have been deleted, or changed outside NEAT.");
                return;
            }

            CloseTask();
            Say("Saved.");
            await RefreshPass();
        }

        ShowTask(
            "Edit a login",
            new UIElement[]
            {
                Block.Row(_skin, "Web site", null, site),
                Block.Row(_skin, "User name", null, user),
                Block.Row(_skin, "Password", "Leave empty to keep the saved password.", pass),
            },
            Btn("Save", Save),
            Btn("Cancel", () => Start(CloseTask)));
    }

    // ---- import and export ----

    private UIElement ExchangeCard()
    {
        var export = Btn("Export...", () => Start(TaskExport));
        export.IsEnabled = _prot != Protection.None;

        return Block.Card(_skin,
            Block.Row(_skin, "Import from a file",
                "A CSV file exported from Chrome, Edge or Firefox.",
                Btn("Import...", ImportStart)),
            Block.Row(_skin, "Export to a file",
                _prot == Protection.None
                    ? "Set a NEAT password first: nothing can be exported without a way to prove who is asking."
                    : "Writes every saved password to a plain text file.",
                export));
    }

    /// <summary>Asks for a CSV file, reads it and shows what importing it would do.</summary>
    private async Task ImportStart()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".csv");

        var file = await picker.PickSingleFileAsync();
        if (file is null || _body is null)
            return;

        var path = file.Path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Say("NEAT can only read a file from a folder on this PC.");
            return;
        }

        if (new FileInfo(path).Length > ImportMaxBytes)
        {
            Say("That file is too big to be a password export.");
            return;
        }

        var text = await File.ReadAllTextAsync(path);
        var read = Exchange.Read(text);

        if (read.Problem is { } problem)
        {
            Say(problem);
            return;
        }

        var preview = App.Vault.Import(read.Rows, false);
        Say(string.Empty);
        TaskImport(path, read, preview);
    }

    private void TaskImport(string path, ReadResult read, ImportResult preview)
    {
        async Task Go()
        {
            var body = _body;
            var rows = read.Rows;

            var result = await Task.Run(() => App.Vault.Import(rows, true));
            if (_body != body)
                return;

            CloseTask();
            Say("Imported " + result.Added + " login" + (result.Added == 1 ? "." : "s."));
            await RefreshPass();

            TaskDeleteFile(path, "The file you imported holds every password in plain text.");
        }

        var lines = new StringBuilder();
        lines.Append(read.Rows.Count + preview.Skipped).Append(" logins in the file: ");
        lines.Append(preview.Added).Append(" new, ");
        lines.Append(preview.Already).Append(" already saved");

        if (preview.Conflicts > 0)
            lines.Append(", ").Append(preview.Conflicts).Append(" with another password than the one saved (the saved ones stay as they are)");

        if (preview.Skipped + read.Skipped > 0)
            lines.Append(", ").Append(preview.Skipped + read.Skipped).Append(" skipped (not web sites, or no password)");

        lines.Append('.');

        ShowTask(
            "Import logins",
            new UIElement[]
            {
                Explain(lines.ToString()),
                Explain("The file holds every password in plain text. After the import NEAT offers to delete it."),
            },
            Btn("Import", Go),
            Btn("Cancel", () => Start(CloseTask)));
    }

    /// <summary>The card that offers to delete a plain-text password file that is no longer needed.</summary>
    private void TaskDeleteFile(string path, string why)
    {
        ShowTask(
            "Delete the file?",
            new UIElement[]
            {
                Explain(why + " Anyone who can open it can read all of them. Delete it as soon as you are done with it."),
                Explain(path),
            },
            Btn("Delete the file", () =>
            {
                try
                {
                    File.Delete(path);
                    CloseTask();
                    Say("The file was deleted.");
                }
                catch (Exception ex)
                {
                    Log.Write("pass", "could not delete the file (" + ex.GetType().Name + ")");
                    Fail("The file could not be deleted. Delete it yourself.");
                }

                return Task.CompletedTask;
            }),
            Btn("Keep it", () => Start(CloseTask)));
    }

    private void TaskExport()
    {
        async Task Go()
        {
            var access = await Authorize("Export all saved passwords");
            if (access is null || _body is null)
                return;

            var result = App.Vault.Export(access);

            // Never over a file that is already there.
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "neat-passwords-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");

            var bytes = new UTF8Encoding(false).GetBytes(Exchange.Write(result.Rows));
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await stream.WriteAsync(bytes);

            // The count only: what was exported is not logged.
            Log.Write("pass", "exported " + result.Rows.Count + " logins");

            CloseTask();
            Say("Exported " + result.Rows.Count + " login" + (result.Rows.Count == 1 ? "." : "s.")
                + (result.Damaged > 0 ? " " + result.Damaged + " could not be read and were left out." : string.Empty));

            TaskExported(path);
        }

        ShowTask(
            "Export logins",
            new UIElement[]
            {
                Explain("This writes every saved password to a plain text file in your Downloads folder. "
                    + "Anyone who can open that file can read all of them, and it can end up in backups and "
                    + "cloud folders. Windows Hello (or your NEAT password) is asked first."),
            },
            Btn("Export", Go),
            Btn("Cancel", () => Start(CloseTask)));
    }

    private void TaskExported(string path)
    {
        ShowTask(
            "Passwords exported",
            new UIElement[]
            {
                Explain(path),
                Explain("The file holds every password in plain text. Delete it as soon as you have used it."),
            },
            Btn("Show in folder", () =>
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                return Task.CompletedTask;
            }),
            Btn("Delete the file", () =>
            {
                try
                {
                    File.Delete(path);
                    CloseTask();
                    Say("The file was deleted.");
                }
                catch (Exception ex)
                {
                    Log.Write("pass", "could not delete the file (" + ex.GetType().Name + ")");
                    Fail("The file could not be deleted. Delete it yourself.");
                }

                return Task.CompletedTask;
            }),
            Btn("Done", () => Start(CloseTask)));
    }
}
