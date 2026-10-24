using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FleetView.Services;
using FleetView.Theming;
using FleetView.ViewModels;

namespace FleetView.Settings;

/// <summary>
/// One theme as a card on the Settings page: its picture, its name, the commander credited under
/// it (or "FleetFinder" for a built-in theme), and (in the page's template) an Apply button.
/// </summary>
public sealed class ThemeCard : ObservableObject
{
    private SavedTheme _theme;
    private ImageSource? _snapshot;
    private bool _isConfirmingDelete;

    public ThemeCard(SavedTheme theme, bool isBuiltIn = false)
    {
        _theme = theme;
        IsBuiltIn = isBuiltIn;
    }

    /// <summary>One of the themes FleetFinder comes with (<see cref="BuiltInThemes"/>): it can be
    /// applied and shared, never deleted or saved over.</summary>
    public bool IsBuiltIn { get; }

    /// <summary>The theme as stored. Replaced in place when a theme of the same name is saved
    /// over it, so the card keeps its place.</summary>
    public SavedTheme Theme
    {
        get => _theme;
        set
        {
            if (!SetProperty(ref _theme, value)) return;
            OnPropertyChanged(nameof(Id));
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Author));
            OnPropertyChanged(nameof(Credit));
        }
    }

    public string Id => _theme.Id;
    public string Name => _theme.Name;

    /// <summary>The line under the name: "CMDR Kailoren", empty when nobody is credited, and
    /// "FleetFinder" on a built-in theme.</summary>
    public string Author => IsBuiltIn ? BuiltInThemes.Author : ThemeShelf.AuthorLine(_theme.Commander);

    /// <summary>Name and credit on one line, as the import dialog shows them (the card's tooltip).</summary>
    public string Credit => IsBuiltIn ? ThemeShelf.BuiltInCreditLine(_theme.Name) : ThemeShelf.CreditLine(_theme.Name, _theme.Commander);

    /// <summary>The preview picture, or null until one is loaded or drawn.</summary>
    public ImageSource? Snapshot
    {
        get => _snapshot;
        set => SetProperty(ref _snapshot, value);
    }

    /// <summary>Delete was chosen from the card's menu: the card asks before it goes.</summary>
    public bool IsConfirmingDelete
    {
        get => _isConfirmingDelete;
        set => SetProperty(ref _isConfirmingDelete, value);
    }
}

/// <summary>One group of built-in cards on the strip, after a line and its label (empty for a
/// group marked by the line alone).</summary>
public sealed record BuiltInSection(string Label, IReadOnlyList<ThemeCard> Cards);

/// <summary>
/// The themes on the colour changer page (C8, D4): the user's saved ones and the built-in ones as
/// cards, and what Apply, Copy share code, Delete, Save as theme and an import do to them. No WPF
/// controls in here, so the self-test can drive all of it with a temporary folder and a pretend
/// clipboard.
/// </summary>
/// <remarks>
/// <para>
/// Apply only touches the draft (<see cref="ThemeDraft.UseTheme"/>), exactly as a picker change
/// does: the preview follows, "Unsaved changes" shows, Save keeps it and Revert undoes it. The
/// theme file itself is never written by Apply.
/// </para>
/// <para>
/// Pictures are read into memory in one go (<see cref="BitmapCacheOption.OnLoad"/>), so no file is
/// held open by a card and Delete can always remove it. A picture that is missing, unreadable, or
/// not a sensibly sized PNG is left null, and the page draws a new one from the theme's colours.
/// </para>
/// <para>
/// The built-in themes (<see cref="BuiltIns"/>) come after the user's own and are never files in
/// the library. Their pictures are drawn the same way the first time Settings opens and kept in
/// <c>Data\themes\built-in\</c>, named by a hash of the theme's colours and the app build, plus
/// the picture's width (<see cref="BuiltInSnapshotName"/>). A theme whose colours change, or a new
/// FleetFinder whose preview may look different, so draws its pictures again rather than showing
/// old ones, and pictures no current theme would look for are deleted from there.
/// </para>
/// </remarks>
public sealed partial class ThemeShelf
{
    public const string CopiedNote = "Share code copied";
    public const string NotDeletedNote = "That theme could not be deleted";

    /// <summary>A snapshot file this size is not one this app made.</summary>
    private const long MaxSnapshotBytes = 8 * 1024 * 1024;

    /// <summary>Widest or tallest snapshot read. Ours are card sized; a picture someone dropped
    /// in the folder could claim to be enormous, and decoding it would eat the memory.</summary>
    private const int MaxSnapshotPixels = 2048;

    private readonly ThemeDraft _draft;

    public ThemeShelf(ThemeLibrary library, ThemeDraft draft, IClipboardText clipboard)
    {
        Library = library;
        _draft = draft;
        Clipboard = clipboard;
        var sections = new List<BuiltInSection>();
        foreach (var theme in BuiltInThemes.All)
        {
            var card = new ThemeCard(new SavedTheme(BuiltInIdPrefix + theme.Id, theme.Name, "", DateTime.MinValue, theme.Colours),
                isBuiltIn: true);
            BuiltIns.Add(card);
            // A theme whose section differs from the one before starts a new group.
            if (sections.Count == 0 || sections[^1].Label != theme.Section)
                sections.Add(new BuiltInSection(theme.Section, new List<ThemeCard>()));
            ((List<ThemeCard>)sections[^1].Cards).Add(card);
        }
        BuiltInSections = sections;
    }

    /// <summary>Starts the id of every built-in card. A library id is sixteen hex digits, so the
    /// two can never be mistaken for each other.</summary>
    public const string BuiltInIdPrefix = "built-in-";

    /// <summary>Where the themes are kept. The app's own <c>Data\themes</c> unless a test hands in
    /// a folder of its own (<see cref="UseLibrary"/>).</summary>
    public ThemeLibrary Library { get; private set; }

    /// <summary>The clipboard Copy share code writes to. The real one unless a test swaps it.</summary>
    public IClipboardText Clipboard { get; set; }

    /// <summary>The user's own cards, oldest theme first, so a new one lands at the end of them.</summary>
    public ObservableCollection<ThemeCard> Cards { get; } = [];

    /// <summary>The built-in themes' cards, in <see cref="BuiltInThemes.All"/> order, after the
    /// user's own on the strip. The same ones for the life of the page.</summary>
    public ObservableCollection<ThemeCard> BuiltIns { get; } = [];

    /// <summary>The same cards in their groups on the strip ("Built in", "Powerplay", then the
    /// rest after a line alone), in order.</summary>
    public IReadOnlyList<BuiltInSection> BuiltInSections { get; }

    /// <summary>The screen's scale (1.25 at 125%), which sets the width of the built-in pictures
    /// looked for in their cache. The page sets it before <see cref="Reload"/>.</summary>
    public double DpiScale { get; set; } = 1;

    /// <summary>Where the built-in themes' pictures are kept: <c>built-in</c> inside the library's
    /// folder, which the library itself never reads.</summary>
    public string BuiltInFolder => Path.Combine(Library.Folder, "built-in");

    /// <summary>"CMDR Kailoren", or empty for a theme nobody is credited with.</summary>
    public static string AuthorLine(string commander) => commander.Length == 0 ? "" : "CMDR " + commander;

    /// <summary>"Green and Blue theme - CMDR BlueGreen", or just the name when nobody is credited.
    /// Both parts are already cleaned by <see cref="ThemeText"/> wherever they came from.</summary>
    public static string CreditLine(string name, string commander) =>
        commander.Length == 0 ? name : name + " - CMDR " + commander;

    /// <summary>"Windows 98 - built into FleetFinder": a built-in card's tooltip.</summary>
    public static string BuiltInCreditLine(string name) => name + " - built into FleetFinder";

    /// <summary>Reads another folder's themes instead (the self-test's temporary one).</summary>
    public void UseLibrary(ThemeLibrary library)
    {
        Library = library;
        Reload();
    }

    /// <summary>Reads every saved theme again and makes its card, with its picture if it has one,
    /// and looks in the cache for any built-in theme's picture not shown yet. One already shown is
    /// kept: it is the same theme whatever folder the user's themes are read from.</summary>
    public void Reload()
    {
        Cards.Clear();
        foreach (var theme in Library.List())
            Cards.Add(new ThemeCard(theme) { Snapshot = LoadSnapshot(Library.SnapshotPath(theme.Id)) });
        int width = BuiltInPixelWidth;
        foreach (var card in BuiltIns.Where(c => c.Snapshot == null))
            card.Snapshot = LoadSnapshot(Path.Combine(BuiltInFolder, BuiltInSnapshotName(card.Theme.Colours, width)));
        // Pictures of themes that are no longer built in (or of an earlier build) would otherwise
        // stay until a new picture happens to be drawn.
        if (Directory.Exists(BuiltInFolder)) PruneBuiltInSnapshots();
    }

    /// <summary>Cards that have no picture yet and need one drawn from their colours: the user's
    /// own first, then the built-in ones.</summary>
    public IReadOnlyList<ThemeCard> MissingSnapshots => Cards.Concat(BuiltIns).Where(c => c.Snapshot == null).ToList();

    /// <summary>
    /// Keeps a picture drawn for a card: shown at once, and saved for next time, beside the theme
    /// for one of the user's own, in the built-in cache for a built-in one. Returns false if the
    /// file could not be written; the card shows the picture all the same.
    /// </summary>
    public bool StoreSnapshot(ThemeCard card, byte[] png)
    {
        card.Snapshot = ReadSnapshot(png);
        if (!card.IsBuiltIn) return Library.SaveSnapshot(card.Id, png);
        return card.Snapshot is BitmapSource picture && SaveBuiltInSnapshot(card, png, picture.PixelWidth);
    }

    // ---- the built-in themes' pictures -----------------------------------------------------------

    /// <summary>A built-in picture's width in pixels at the current <see cref="DpiScale"/>, the
    /// same sum <see cref="ThemeSnapshot.Png"/> does for the nearly square preview.</summary>
    private int BuiltInPixelWidth => (int)Math.Round(ThemeSnapshot.CardWidth * DpiScale);

    [GeneratedRegex(@"^([0-9a-f]{16})-[0-9]{1,5}\.png$", RegexOptions.CultureInvariant)]
    private static partial Regex BuiltInSnapshotFile();

    /// <summary>
    /// The file name a built-in theme's picture is kept under: sixteen hex digits of SHA-256 over
    /// this build of FleetFinder (its module id, which changes whenever the code does) and every
    /// role's colour, then the picture's width. Colours that change, or a new build whose preview
    /// may be drawn differently, give a new name, so the picture is drawn again; the same theme at
    /// another screen scale keeps a file of its own beside the first.
    /// </summary>
    public static string BuiltInSnapshotName(IReadOnlyDictionary<string, Color> colours, int pixelWidth) =>
        BuiltInSnapshotHash(colours) + "-" + pixelWidth + ".png";

    private static string BuiltInSnapshotHash(IReadOnlyDictionary<string, Color> colours)
    {
        var text = new StringBuilder("FleetFinder built-in theme picture ");
        text.Append(typeof(ThemeShelf).Assembly.ManifestModule.ModuleVersionId.ToString("N"));
        foreach (var role in ThemeRoles.All)
        {
            var c = colours.TryGetValue(role.Key, out var found) ? found : role.Default;
            text.Append(' ').Append(role.Key).Append('=').Append(ColourMaths.ToStorageHex(c, withAlpha: true));
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexStringLower(hash, 0, 8);
    }

    private bool SaveBuiltInSnapshot(ThemeCard card, byte[] png, int pixelWidth)
    {
        var path = Path.Combine(BuiltInFolder, BuiltInSnapshotName(card.Theme.Colours, pixelWidth));
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(BuiltInFolder);
            File.WriteAllBytes(temp, png);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Note($"A built-in theme's picture could not be kept ({ex.GetType().Name}); it is drawn again next time.");
            try { File.Delete(temp); } catch (Exception again) when (again is IOException or UnauthorizedAccessException) { }
            return false;
        }
        PruneBuiltInSnapshots();
        return true;
    }

    /// <summary>Deletes the built-in pictures that no current theme would look for: those of a
    /// theme whose colours have changed, and those an earlier build drew. Only files named the way
    /// <see cref="BuiltInSnapshotName"/> names them are touched.</summary>
    private void PruneBuiltInSnapshots()
    {
        var current = BuiltIns.Select(c => BuiltInSnapshotHash(c.Theme.Colours)).ToHashSet(StringComparer.Ordinal);
        try
        {
            foreach (var file in Directory.EnumerateFiles(BuiltInFolder, "*.png"))
            {
                var match = BuiltInSnapshotFile().Match(Path.GetFileName(file));
                if (match.Success && !current.Contains(match.Groups[1].Value)) File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Note($"Old built-in theme pictures could not be tidied away ({ex.GetType().Name}).");
        }
    }

    /// <summary>The card whose theme has this name once cleaned, matched without regard to case
    /// ("green and blue" is the same theme as "Green and Blue"), or null.</summary>
    public ThemeCard? Find(string name)
    {
        var clean = ThemeText.CleanName(name);
        return clean.Length == 0 ? null : Cards.FirstOrDefault(c => string.Equals(c.Name, clean, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True if exactly this theme (name, credit and every colour) is already saved, or is
    /// one of the built-in ones, so importing the same code twice does not make two cards.</summary>
    public bool Has(SharedTheme theme) => Cards.Concat(BuiltIns).Any(c => Same(c, theme));

    /// <summary>True if this is exactly one of the built-in themes, as its Copy share code gives it.</summary>
    public bool HasBuiltIn(SharedTheme theme) => BuiltIns.Any(c => Same(c, theme));

    private static bool Same(ThemeCard card, SharedTheme theme) =>
        card.Name == theme.Name && card.Theme.Commander == theme.Commander
        && ThemeRoles.All.All(r => card.Theme.Colours[r.Key] == theme.Colours[r.Key]);

    /// <summary>Apply: the theme's colours go into the draft. Nothing is saved.</summary>
    public void Apply(ThemeCard card)
    {
        card.IsConfirmingDelete = false;
        _draft.UseTheme(card.Theme.Colours);
    }

    /// <summary>Copy share code: the theme's code on the clipboard. Returns the note to show.</summary>
    public string CopyCode(ThemeCard card)
    {
        var code = ThemeCode.Encode(card.Name, card.Theme.Commander, card.Theme.Colours);
        return Clipboard.SetText(code) ? CopiedNote : HoverClipboard.ClipboardBusyNote;
    }

    /// <summary>Delete from the menu: this card asks first, and any other card asking stops. A
    /// built-in card has no Delete and is never asked about.</summary>
    public void AskDelete(ThemeCard card)
    {
        if (card.IsBuiltIn) return;
        foreach (var other in Cards) other.IsConfirmingDelete = ReferenceEquals(other, card);
    }

    /// <summary>Keep: the card stops asking.</summary>
    public void CancelDelete(ThemeCard card) => card.IsConfirmingDelete = false;

    /// <summary>The card that is asking whether to delete, or null.</summary>
    public ThemeCard? Confirming => Cards.FirstOrDefault(c => c.IsConfirmingDelete);

    /// <summary>Delete, confirmed: the theme and its picture go, and so does the card. False if the
    /// files could not be removed, or for a built-in card; the card stays and stops asking.</summary>
    public bool Delete(ThemeCard card)
    {
        card.IsConfirmingDelete = false;
        if (card.IsBuiltIn || !Library.Delete(card.Id)) return false;
        Cards.Remove(card);
        return true;
    }

    /// <summary>
    /// Save as theme: the draft's colours (what the preview shows, saved or not) under this name
    /// and credit, with its picture. With <paramref name="replacing"/>, saved over that card's
    /// theme, which keeps its place; otherwise a new card at the end. Returns the card, or null if
    /// the name is empty once cleaned or the file could not be written.
    /// </summary>
    public ThemeCard? SaveDraft(string name, string commander, byte[]? png, ThemeCard? replacing)
    {
        var colours = new Dictionary<string, Color>(_draft.Colours, StringComparer.Ordinal);
        if (replacing != null)
        {
            if (Library.Replace(replacing.Id, name, commander, colours, png) is not { } replaced) return null;
            replacing.IsConfirmingDelete = false;
            replacing.Theme = replaced;
            replacing.Snapshot = png == null ? null : ReadSnapshot(png);
            return replacing;
        }
        return Added(Library.Save(name, commander, colours, png), png);
    }

    /// <summary>Add to my themes, from an import: the shared theme as it decoded, with the picture
    /// drawn for it. Returns the new card, or null if it could not be saved.</summary>
    public ThemeCard? Add(SharedTheme theme, byte[]? png) =>
        Added(Library.Save(theme.Name, theme.Commander, theme.Colours, png), png);

    private ThemeCard? Added(SavedTheme? saved, byte[]? png)
    {
        if (saved == null) return null;
        var card = new ThemeCard(saved) { Snapshot = png == null ? null : ReadSnapshot(png) };
        Cards.Add(card);
        return card;
    }

    // ---- pictures ---------------------------------------------------------------------------------

    /// <summary>A snapshot from its file, read whole and closed again, or null if there is no
    /// usable one.</summary>
    public static ImageSource? LoadSnapshot(string? path)
    {
        if (path is null) return null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxSnapshotBytes) return null;
            return ReadSnapshot(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Note($"Theme snapshot {Path.GetFileName(path)} could not be read ({ex.GetType().Name}).");
            return null;
        }
    }

    /// <summary>
    /// PNG bytes as a picture, decoded in full at once (<see cref="BitmapCacheOption.OnLoad"/>) so
    /// nothing keeps hold of them, or null if they are not a PNG of a size this app would make.
    /// Only the PNG decoder is used, whatever other image codecs the machine has.
    /// </summary>
    public static ImageSource? ReadSnapshot(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png, writable: false);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) return null;
            var header = decoder.Frames[0];
            if (header.PixelWidth > MaxSnapshotPixels || header.PixelHeight > MaxSnapshotPixels) return null;

            stream.Position = 0;
            var full = new PngBitmapDecoder(stream, BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
            var frame = full.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException
                                       or ArgumentException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            DiagnosticLog.Note($"A theme snapshot is not a usable PNG ({ex.GetType().Name}); a new one will be drawn.");
            return null;
        }
    }
}
