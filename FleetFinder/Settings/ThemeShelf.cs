using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FleetView.Services;
using FleetView.Theming;
using FleetView.ViewModels;

namespace FleetView.Settings;

/// <summary>
/// One saved theme as a card on the Settings page: its picture, its name, the commander credited
/// under it, and (in the page's template) an Apply button.
/// </summary>
public sealed class ThemeCard : ObservableObject
{
    private SavedTheme _theme;
    private ImageSource? _snapshot;
    private bool _isConfirmingDelete;

    public ThemeCard(SavedTheme theme)
    {
        _theme = theme;
    }

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

    /// <summary>The line under the name: "CMDR Kailoren", or empty when nobody is credited.</summary>
    public string Author => ThemeShelf.AuthorLine(_theme.Commander);

    /// <summary>Name and credit on one line, as the import dialog shows them.</summary>
    public string Credit => ThemeShelf.CreditLine(_theme.Name, _theme.Commander);

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

/// <summary>
/// The saved themes on the colour changer page (C8): the cards, and what Apply, Copy share code,
/// Delete, Save as theme and an import do to them. No WPF controls in here, so the self-test can
/// drive all of it with a temporary folder and a pretend clipboard.
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
/// </remarks>
public sealed class ThemeShelf
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
    }

    /// <summary>Where the themes are kept. The app's own <c>Data\themes</c> unless a test hands in
    /// a folder of its own (<see cref="UseLibrary"/>).</summary>
    public ThemeLibrary Library { get; private set; }

    /// <summary>The clipboard Copy share code writes to. The real one unless a test swaps it.</summary>
    public IClipboardText Clipboard { get; set; }

    /// <summary>The cards, oldest theme first, so a new one lands at the end of the row.</summary>
    public ObservableCollection<ThemeCard> Cards { get; } = [];

    /// <summary>"CMDR Kailoren", or empty for a theme nobody is credited with.</summary>
    public static string AuthorLine(string commander) => commander.Length == 0 ? "" : "CMDR " + commander;

    /// <summary>"Green and Blue theme - CMDR BlueGreen", or just the name when nobody is credited.
    /// Both parts are already cleaned by <see cref="ThemeText"/> wherever they came from.</summary>
    public static string CreditLine(string name, string commander) =>
        commander.Length == 0 ? name : name + " - CMDR " + commander;

    /// <summary>Reads another folder's themes instead (the self-test's temporary one).</summary>
    public void UseLibrary(ThemeLibrary library)
    {
        Library = library;
        Reload();
    }

    /// <summary>Reads every saved theme again and makes its card, with its picture if it has one.</summary>
    public void Reload()
    {
        Cards.Clear();
        foreach (var theme in Library.List())
            Cards.Add(new ThemeCard(theme) { Snapshot = LoadSnapshot(Library.SnapshotPath(theme.Id)) });
    }

    /// <summary>Cards that have no picture yet and need one drawn from their colours.</summary>
    public IReadOnlyList<ThemeCard> MissingSnapshots => Cards.Where(c => c.Snapshot == null).ToList();

    /// <summary>
    /// Keeps a picture drawn for a card: shown at once, and saved beside the theme for next time.
    /// Returns false if the file could not be written; the card shows the picture all the same.
    /// </summary>
    public bool StoreSnapshot(ThemeCard card, byte[] png)
    {
        bool saved = Library.SaveSnapshot(card.Id, png);
        card.Snapshot = ReadSnapshot(png);
        return saved;
    }

    /// <summary>The card whose theme has this name once cleaned, matched without regard to case
    /// ("green and blue" is the same theme as "Green and Blue"), or null.</summary>
    public ThemeCard? Find(string name)
    {
        var clean = ThemeText.CleanName(name);
        return clean.Length == 0 ? null : Cards.FirstOrDefault(c => string.Equals(c.Name, clean, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True if exactly this theme (name, credit and every colour) is already saved, so
    /// importing the same code twice does not make two cards.</summary>
    public bool Has(SharedTheme theme) => Cards.Any(c =>
        c.Name == theme.Name && c.Theme.Commander == theme.Commander
        && ThemeRoles.All.All(r => c.Theme.Colours[r.Key] == theme.Colours[r.Key]));

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

    /// <summary>Delete from the menu: this card asks first, and any other card asking stops.</summary>
    public void AskDelete(ThemeCard card)
    {
        foreach (var other in Cards) other.IsConfirmingDelete = ReferenceEquals(other, card);
    }

    /// <summary>Keep: the card stops asking.</summary>
    public void CancelDelete(ThemeCard card) => card.IsConfirmingDelete = false;

    /// <summary>The card that is asking whether to delete, or null.</summary>
    public ThemeCard? Confirming => Cards.FirstOrDefault(c => c.IsConfirmingDelete);

    /// <summary>Delete, confirmed: the theme and its picture go, and so does the card. False if the
    /// files could not be removed; the card stays and stops asking.</summary>
    public bool Delete(ThemeCard card)
    {
        card.IsConfirmingDelete = false;
        if (!Library.Delete(card.Id)) return false;
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
