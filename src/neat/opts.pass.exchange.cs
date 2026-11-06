using System.Diagnostics;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace neat;

/// <summary>
/// The Passwords page, third part: the list of sites never to offer to save for, editing a login,
/// and moving logins in and out as CSV files (the format Chrome, Edge and Firefox use, with one more
/// column for the key of one-time codes), and saved cards and identities in and out as a second CSV file
/// of NEAT's own (no other browser has one).
///
/// A CSV file holds every password in plain text, and a file of cards every card number. NEAT says so each time it makes one or reads
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

        // The key of the one-time codes: a secret like the password, so it is typed into a box that hides it,
        // and never shown again. An empty box keeps what is saved.
        var otp = new PasswordBox { Width = 260 };
        var drop = new CheckBox { Content = "Remove the saved code key", Visibility = it.Otp ? Visibility.Visible : Visibility.Collapsed };

        async Task Save()
        {
            var typed = site.Text.Trim();
            var origin = Origin.Of(typed.Contains("://") ? typed : "https://" + typed);

            if (origin is null)
            {
                Fail("Enter the address of a web site, like example.com.");
                return;
            }

            var key = otp.Password.Trim();
            if (key.Length > 0 && drop.IsChecked != true && Totp.Parse(key) is null)
            {
                Fail("That is not a one-time code key. Paste the key the site shows (letters and digits), or the otpauth:// address behind its QR code.");
                return;
            }

            // An empty password box keeps the saved password.
            if (!App.Vault.Update(it.Id, origin, user.Text.Trim(), pass.Password.Length == 0 ? null : pass.Password))
            {
                Fail("That login could not be changed. It may have been deleted, or changed outside NEAT.");
                return;
            }

            if (drop.IsChecked == true)
                App.Vault.SetOtp(it.Id, null);
            else if (key.Length > 0)
                App.Vault.SetOtp(it.Id, key);

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
                Block.Row(_skin, "One-time code key",
                    "For sites with two-step sign-in: the key the site shows when you set up an authenticator app. Leave empty to keep what is saved.", otp),
                drop,
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
                "A CSV file exported from Chrome, Edge or Firefox, or from NEAT (logins with their one-time-code keys, cards, identities).",
                Btn("Import...", ImportStart)),
            Block.Row(_skin, "Export to a file",
                _prot == Protection.None
                    ? "Set a NEAT password first: nothing can be exported without a way to prove who is asking."
                    : "Writes every saved password and one-time-code key to a plain text file, and saved cards and identities to a second one.",
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

        // A file of cards and identities (made by NEAT) has its own way in.
        if (Exchange.LooksLikeItems(text))
        {
            ImportItemsStart(path, text);
            return;
        }

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

    /// <summary>Shows what importing a file of cards and identities would do.</summary>
    private void ImportItemsStart(string path, string text)
    {
        var read = Exchange.ReadItems(text);
        if (read.Problem is { } problem)
        {
            Say(problem);
            return;
        }

        var preview = App.Vault.ImportItems(read.Rows, false);
        Say(string.Empty);

        async Task Go()
        {
            var body = _body;
            var rows = read.Rows;

            var result = await Task.Run(() => App.Vault.ImportItems(rows, true));
            if (_body != body)
                return;

            CloseTask();
            Say("Imported " + result.Added + " card" + (result.Added == 1 ? " or identity." : "s and identities."));
            await RefreshPass();

            TaskDeleteFile(path, "The file you imported holds every card number in plain text.");
        }

        var lines = new StringBuilder();
        lines.Append(read.Rows.Count + preview.Skipped + read.Skipped).Append(" cards and identities in the file: ");
        lines.Append(preview.Added).Append(" new, ").Append(preview.Already).Append(" already saved");

        if (preview.Skipped + read.Skipped > 0)
            lines.Append(", ").Append(preview.Skipped + read.Skipped).Append(" skipped (not a card or identity)");

        lines.Append('.');

        ShowTask(
            "Import cards and identities",
            new UIElement[]
            {
                Explain(lines.ToString()),
                Explain("The file holds every card number in plain text. After the import NEAT offers to delete it."),
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
            var items = App.Vault.ExportItems(access);

            // Never over a file that is already there.
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(folder);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var path = Path.Combine(folder, "neat-passwords-" + stamp + ".csv");

            var bytes = new UTF8Encoding(false).GetBytes(Exchange.Write(result.Rows));
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await stream.WriteAsync(bytes);

            var paths = new List<string> { path };

            // Cards and identities are their own file: no other browser reads them, and a file that holds
            // card numbers is better not mixed with the logins.
            if (items.Rows.Count > 0)
            {
                var itemsPath = Path.Combine(folder, "neat-cards-" + stamp + ".csv");
                var itemBytes = new UTF8Encoding(false).GetBytes(Exchange.WriteItems(items.Rows));
                await using (var stream = new FileStream(itemsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await stream.WriteAsync(itemBytes);

                paths.Add(itemsPath);
            }

            // The counts only: what was exported is not logged.
            Log.Write("pass", "exported " + result.Rows.Count + " logins and " + items.Rows.Count + " cards and identities");

            var damaged = result.Damaged + items.Damaged;
            CloseTask();
            Say("Exported " + result.Rows.Count + " login" + (result.Rows.Count == 1 ? string.Empty : "s")
                + (items.Rows.Count > 0 ? " and " + items.Rows.Count + " card" + (items.Rows.Count == 1 ? " or identity" : "s and identities") : string.Empty) + "."
                + (damaged > 0 ? " " + damaged + " could not be read and were left out." : string.Empty));

            TaskExported(paths);
        }

        ShowTask(
            "Export logins",
            new UIElement[]
            {
                Explain("This writes every saved password, and the keys of your one-time codes, to a plain text file in "
                    + "your Downloads folder, and saved cards and identities to a second one. Anyone who can open "
                    + "those files can read all of them, and they can end up in backups and cloud folders. "
                    + "Windows Hello (or your NEAT password) is asked first."),
            },
            Btn("Export", Go),
            Btn("Cancel", () => Start(CloseTask)));
    }

    private void TaskExported(IReadOnlyList<string> paths)
    {
        var shown = new List<UIElement>();
        foreach (var path in paths)
            shown.Add(Explain(path));

        shown.Add(Explain("The files hold every password and card number in plain text. Delete them as soon as you have used them."));

        ShowTask(
            "Passwords exported",
            shown.ToArray(),
            Btn("Show in folder", () =>
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + paths[0] + "\"") { UseShellExecute = true });
                return Task.CompletedTask;
            }),
            Btn(paths.Count == 1 ? "Delete the file" : "Delete the files", () =>
            {
                var left = 0;
                foreach (var path in paths)
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception ex)
                    {
                        left++;
                        Log.Write("pass", "could not delete the file (" + ex.GetType().Name + ")");
                    }
                }

                CloseTask();

                if (left == 0)
                    Say(paths.Count == 1 ? "The file was deleted." : "The files were deleted.");
                else
                    Fail("Some files could not be deleted. Delete them yourself.");

                return Task.CompletedTask;
            }),
            Btn("Done", () => Start(CloseTask)));
    }
}
