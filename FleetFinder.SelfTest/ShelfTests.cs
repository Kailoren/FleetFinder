using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FleetView;
using FleetView.Services;
using FleetView.Settings;
using FleetView.Theming;

// The D3 sections (2026-10-24): the saved themes on the colour changer page (C8). The draft taking a
// whole theme at once and the library saving over a theme first, then the plain classes behind the
// page (the cards, Save as theme, Import code), then the page itself, built in the main window and
// never shown. Every theme folder is a temporary one and every clipboard a pretend one.
internal sealed partial class SelfTest
{
    private static readonly Color Teal = Color.FromRgb(0x26, 0x73, 0x99);
    private static readonly Color Moss = Color.FromRgb(0x33, 0x66, 0x22);

    /// <summary>Ali's palette with a few roles changed, so a theme is told apart from the defaults.</summary>
    private static Dictionary<string, Color> Tinted(Color colour)
    {
        var map = ThemeRoles.Defaults();
        foreach (var key in new[] { "PanelBackground", "TitleBarBackground", "WindowBackground", "BannerBackground" })
            map[key] = colour;
        return map;
    }

    /// <summary>A theme file in the library's own format, with a fixed id and date, so the cards
    /// come out in a known order (themes saved in the same second would tie and sort by their
    /// random ids).</summary>
    private static void WriteTheme(string folder, string id, string name, string cmdr, string created, IReadOnlyDictionary<string, Color> colours)
    {
        Directory.CreateDirectory(folder);
        var map = ThemeRoles.All.ToDictionary(r => r.Key, r => ColourMaths.ToStorageHex(colours[r.Key], r.HasAlpha));
        File.WriteAllText(Path.Combine(folder, id + ".json"),
            JsonSerializer.Serialize(new { version = 1, id, name, cmdr, created, colours = map }));
    }

    /// <summary>A real PNG of one colour.</summary>
    private static byte[] Png(int width, int height, Color colour)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = colour.B;
            pixels[i + 1] = colour.G;
            pixels[i + 2] = colour.R;
            pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static bool Unlocked(string path)
    {
        try
        {
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private (ThemeDraft Draft, string ThemeJson, List<IReadOnlyDictionary<string, Color>> Applied) NewDraft(string name)
    {
        var path = Path.Combine(_dir, name, "Data", "theme.json");
        var applied = new List<IReadOnlyDictionary<string, Color>>();
        return (new ThemeDraft(ThemeRoles.Defaults(), path, c => applied.Add(c)), path, applied);
    }

    // ---- the draft takes a whole theme ------------------------------------------------------------

    private void DraftUseThemeTests()
    {
        var (draft, path, applied) = NewDraft("usetheme");
        var changes = new List<string?>();
        draft.ColourChanged += k => changes.Add(k);
        var theme = Tinted(Teal);
        theme["OverlayDim"] = Color.FromArgb(0x40, 1, 2, 3);
        theme["GridRow"] = Color.FromArgb(0x10, 4, 5, 6);

        draft.BeginEdit("PanelBorder");
        draft.UseTheme(theme);
        Equal("a whole theme goes into the draft", Teal, draft["PanelBackground"]);
        Equal("a see-through role keeps the theme's alpha", Color.FromArgb(0x40, 1, 2, 3), draft["OverlayDim"]);
        Equal("a plain role is made opaque, as Set would", Color.FromRgb(4, 5, 6), draft["GridRow"]);
        Check("the draft is dirty", draft.IsDirty);
        Check("the saved colours are untouched", SameColours(draft.Saved, ThemeRoles.Defaults()));
        Check("nothing is put on the app", applied.Count == 0);
        Check("and theme.json is not written", !File.Exists(path));
        Equal("ColourChanged says many roles changed, once", "null", string.Join(",", changes.Select(k => k ?? "null")));
        Equal("an open picker edit is ended first", null, draft.EditingKey);

        var partial = new Dictionary<string, Color> { ["PanelBackground"] = Moss };
        draft.UseTheme(partial);
        Equal("a theme missing roles gives them their default", ThemeRoles.Find("TitleBarBackground")!.Default, draft["TitleBarBackground"]);
        draft.Revert();
        Check("Revert goes back to the saved colours", SameColours(draft.Colours, ThemeRoles.Defaults()) && !draft.IsDirty);
        draft.UseTheme(theme);
        draft.Save();
        Equal("Save keeps it, as for any other change", Teal, ThemeStore.Load(path)["PanelBackground"]);
    }

    // ---- the library saves over a theme ------------------------------------------------------------

    private void LibraryReplaceTests()
    {
        var folder = Path.Combine(_dir, "replace");
        var library = new ThemeLibrary(folder);
        var first = library.Save("Green and Blue", "BlueGreen", Tinted(Teal), Png(4, 4, Teal))!;
        var other = library.Save("Other", "", Tinted(Moss), null)!;
        var newPng = Png(5, 5, Moss);

        var replaced = library.Replace(first.Id, "green AND blue", "CMDR Someone", Tinted(Moss), newPng);
        Check("Replace saves over an existing theme", replaced != null);
        Equal("keeping its id", first.Id, replaced?.Id);
        Equal("and its saved date, so its card keeps its place", first.Created, replaced?.Created);
        Equal("with the new name as typed", "green AND blue", replaced?.Name);
        Equal("and the new credit, cleaned", "Someone", replaced?.Commander);
        var reloaded = library.Load(first.Id);
        Equal("the file has the new colours", Moss, reloaded?.Colours["PanelBackground"]);
        Check("and the new picture", File.ReadAllBytes(library.SnapshotPath(first.Id)!).SequenceEqual(newPng));
        Equal("still two themes", 2, library.List().Count);
        Equal("the other theme is untouched", Moss, library.Load(other.Id)?.Colours["PanelBackground"]);

        library.Replace(first.Id, "Green and Blue", "", Tinted(Teal), null);
        Check("Replace with no picture removes the old one, which shows the old colours", !File.Exists(library.SnapshotPath(first.Id)!));
        Check("an id with no theme is not replaced", library.Replace("0123456789abcdef", "x", "", Tinted(Teal), null) == null
            && library.List().Count == 2);
        Check("nor is a theme given an empty name", library.Replace(first.Id, " \t ", "", Tinted(Moss), null) == null
            && library.Load(first.Id)?.Name == "Green and Blue");
        Check("nor an id that is not one", library.Replace("..\\..\\x", "x", "", Tinted(Teal), null) == null);
    }

    // ---- the cards ----------------------------------------------------------------------------------

    private void ShelfTests()
    {
        Equal("the credit line, with a commander", "Green and Blue theme - CMDR BlueGreen",
            ThemeShelf.CreditLine("Green and Blue theme", "BlueGreen"));
        Equal("and without one, just the name", "Green and Blue theme", ThemeShelf.CreditLine("Green and Blue theme", ""));
        Equal("the author line under a card's name", "CMDR Kailoren", ThemeShelf.AuthorLine("Kailoren"));
        Equal("is empty for nobody", "", ThemeShelf.AuthorLine(""));

        var folder = Path.Combine(_dir, "shelf");
        WriteTheme(folder, "0000000000000001", "Orange", "Kailoren", "2026-10-20T10:00:00Z", Tinted(Teal));
        WriteTheme(folder, "0000000000000002", "No credit", "", "2026-10-21T10:00:00Z", Tinted(Moss));
        var orangePng = Png(160, 160, Teal);
        File.WriteAllBytes(Path.Combine(folder, "0000000000000001.png"), orangePng);
        var library = new ThemeLibrary(folder);
        var (draft, themeJson, applied) = NewDraft("shelfdraft");
        var clip = new FakeClipboard { Text = "untouched" };
        var shelf = new ThemeShelf(library, draft, clip);
        Equal("a shelf reads nothing until asked", 0, shelf.Cards.Count);
        shelf.Reload();

        Equal("one card per theme, oldest first", "Orange|No credit", string.Join("|", shelf.Cards.Select(c => c.Name)));
        Equal("each with its author line", "CMDR Kailoren|", string.Join("|", shelf.Cards.Select(c => c.Author)));
        Equal("and its credit line", "Orange - CMDR Kailoren|No credit", string.Join("|", shelf.Cards.Select(c => c.Credit)));
        var orange = shelf.Cards[0];
        var plain = shelf.Cards[1];
        Check("a card with a picture on disk shows it", orange.Snapshot is BitmapSource { PixelWidth: 160 });
        Check("a card without one has none yet", plain.Snapshot == null);
        Equal("and is the one of the user's that needs drawing", "No credit",
            string.Join(",", shelf.MissingSnapshots.Where(c => !c.IsBuiltIn).Select(c => c.Name)));
        Check("the picture was read whole: nothing holds its file open", Unlocked(Path.Combine(folder, "0000000000000001.png")));

        // Pictures that are not usable are treated as missing.
        Check("PNG bytes that are not a picture are not one", ThemeShelf.ReadSnapshot([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]) == null);
        Check("nor is a picture far bigger than a card", ThemeShelf.ReadSnapshot(Png(3000, 2, Teal)) == null);
        var jpeg = new JpegBitmapEncoder();
        jpeg.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr32, null, new byte[16], 8)));
        using (var ms = new MemoryStream())
        {
            jpeg.Save(ms);
            Check("nor another kind of picture", ThemeShelf.ReadSnapshot(ms.ToArray()) == null);
        }
        Check("a missing file is no picture", ThemeShelf.LoadSnapshot(Path.Combine(folder, "nothing.png")) == null);

        var drawn = Png(160, 160, Moss);
        Check("a drawn picture is kept", shelf.StoreSnapshot(plain, drawn));
        Check("shown at once", plain.Snapshot != null);
        Check("and saved beside its theme", File.ReadAllBytes(Path.Combine(folder, "0000000000000002.png")).SequenceEqual(drawn));
        Equal("so none of the user's needs drawing now", 0, shelf.MissingSnapshots.Count(c => !c.IsBuiltIn));

        // Find, by name.
        Check("a name is found whatever its case", ReferenceEquals(shelf.Find("orange"), orange));
        Check("and with stray spaces and tabs", ReferenceEquals(shelf.Find("  ORANGE\t"), orange));
        Check("an empty name finds nothing", shelf.Find(" ") == null);
        Check("nor does another name", shelf.Find("Orange two") == null);

        // Apply.
        var fileBefore = File.ReadAllBytes(Path.Combine(folder, "0000000000000001.json"));
        shelf.Apply(orange);
        Check("Apply puts the theme's colours in the draft", SameColours(draft.Colours, orange.Theme.Colours));
        Check("so there are unsaved changes", draft.IsDirty);
        Check("and nothing else: the app is not given them", applied.Count == 0);
        Check("theme.json is not written", !File.Exists(themeJson));
        Check("and the theme's own file is untouched", File.ReadAllBytes(Path.Combine(folder, "0000000000000001.json")).SequenceEqual(fileBefore));
        draft.Revert();
        Check("Revert undoes Apply", SameColours(draft.Colours, ThemeRoles.Defaults()));

        // Copy share code.
        Equal("Copy share code says so", ThemeShelf.CopiedNote, shelf.CopyCode(orange));
        var decoded = ThemeCode.Decode(clip.Text);
        Check("the code on the (pretend) clipboard is the theme", decoded.Ok && decoded.Theme!.Name == "Orange"
            && decoded.Theme.Commander == "Kailoren" && SameColours(decoded.Theme.Colours, orange.Theme.Colours));
        clip.Busy = true;
        Equal("a busy clipboard says so", HoverClipboard.ClipboardBusyNote, shelf.CopyCode(plain));
        clip.Busy = false;

        // Delete asks first.
        shelf.AskDelete(orange);
        Check("Delete asks on the card", orange.IsConfirmingDelete && ReferenceEquals(shelf.Confirming, orange));
        shelf.AskDelete(plain);
        Check("one card asks at a time", !orange.IsConfirmingDelete && plain.IsConfirmingDelete);
        shelf.CancelDelete(plain);
        Check("Keep stops it asking", shelf.Confirming == null);
        Check("and nothing was deleted", File.Exists(Path.Combine(folder, "0000000000000002.json")));
        shelf.AskDelete(orange);
        Check("Delete, confirmed, removes the theme", shelf.Delete(orange));
        Equal("and its card", "No credit", string.Join("|", shelf.Cards.Select(c => c.Name)));
        Check("its file and picture are gone", !File.Exists(Path.Combine(folder, "0000000000000001.json"))
            && !File.Exists(Path.Combine(folder, "0000000000000001.png")));
        Check("a theme already gone cannot be deleted again", !shelf.Delete(orange));

        // Save from the draft, and Add.
        draft.Set("PanelBackground", Moss);
        var saved = shelf.SaveDraft("Mossy", "CMDR Kailoren", Png(160, 160, Moss), null);
        Check("Save as theme saves the draft's colours, unsaved as they are", saved != null && saved.Theme.Colours["PanelBackground"] == Moss);
        Equal("on disk too", Moss, saved == null ? default : library.Load(saved.Id)?.Colours["PanelBackground"]);
        Check("with its picture", saved?.Snapshot != null && saved != null && File.Exists(library.SnapshotPath(saved.Id)!));
        Equal("at the end of the row", "Mossy", shelf.Cards.Last().Name);
        Check("the draft is still unsaved: saving a theme is not Save", draft.IsDirty && !File.Exists(themeJson));
        draft.Set("PanelBackground", Teal);
        var again = shelf.SaveDraft("Mossy", "Kailoren", Png(160, 160, Teal), saved);
        Check("saving over a card keeps the same card", ReferenceEquals(again, saved) && shelf.Cards.Count == 2);
        Equal("in its place", 1, shelf.Cards.IndexOf(saved!));
        Equal("with the new colours", Teal, saved!.Theme.Colours["PanelBackground"]);
        draft.Revert();

        var shared = new SharedTheme("Shared", "Someone", Tinted(Moss));
        Check("a theme not saved yet is not had", !shelf.Has(shared));
        var added = shelf.Add(shared, Png(160, 160, Moss));
        Check("Add saves an imported theme as a card", added != null && shelf.Cards.Last() == added && added.Credit == "Shared - CMDR Someone");
        Check("then it is had", shelf.Has(shared));
        var oneOff = new Dictionary<string, Color>(shared.Colours) { ["PanelBorder"] = Teal };
        Check("but not one with a colour different", !shelf.Has(shared with { Colours = oneOff }));
        Check("nor one with another credit", !shelf.Has(shared with { Commander = "Else" }));
    }

    // ---- Save as theme -------------------------------------------------------------------------------

    private void SaveThemeFormTests()
    {
        var folder = Path.Combine(_dir, "saveform");
        WriteTheme(folder, "0000000000000001", "Green and Blue", "BlueGreen", "2026-10-20T10:00:00Z", Tinted(Teal));
        var library = new ThemeLibrary(folder);
        var (draft, themeJson, _) = NewDraft("saveformdraft");
        var shelf = new ThemeShelf(library, draft, new FakeClipboard());
        shelf.Reload();
        int pictures = 0;
        byte[] Picture()
        {
            pictures++;
            return Png(160, 160, Moss);
        }

        var form = new SaveThemeForm(shelf, "CMDR Kailoren");
        Equal("the CMDR starts as the journal's, without the CMDR", "Kailoren", form.Commander);
        Equal("an empty name is not saved", SaveThemeOutcome.NeedsName, form.Submit(Picture));
        Equal("the box asks for one", SaveThemeForm.NeedsNameMessage, form.Message);
        form.Name = " \t\u200B ";
        Equal("nor is a name of nothing but spaces and invisible characters", SaveThemeOutcome.NeedsName, form.Submit(Picture));
        Check("and no picture was drawn for either", pictures == 0 && shelf.Cards.Count == 1);

        draft.Set("PanelBackground", Moss);
        form.Name = "Night run";
        Equal("a new name saves", SaveThemeOutcome.Saved, form.Submit(Picture));
        Check("one picture drawn", pictures == 1);
        var night = form.Saved;
        Check("as a new card at the end", night != null && shelf.Cards.Count == 2 && ReferenceEquals(shelf.Cards[1], night));
        Equal("credited to the CMDR", "CMDR Kailoren", night?.Author);
        Equal("with the draft's colours", Moss, night?.Theme.Colours["PanelBackground"]);
        Check("and its picture", night?.Snapshot != null);
        Check("theme.json is not written", !File.Exists(themeJson));
        Equal("and the box says nothing", "", form.Message);

        form = new SaveThemeForm(shelf, "Kailoren") { Name = "  green AND blue " };
        Equal("a name that is taken asks first", SaveThemeOutcome.AskReplace, form.Submit(Picture));
        Equal("naming the theme it would replace", "You already have a theme called \"Green and Blue\". Replace it?", form.Message);
        Equal("and Save becomes Replace", "Replace", form.SaveLabel);
        Check("nothing saved, nothing drawn", pictures == 1 && shelf.Cards.Count == 2
            && library.Load("0000000000000001")!.Colours["PanelBackground"] == Teal);
        form.Name = "Green and Blue";
        Check("the same name in other capitals still asks", form.AskingReplace);
        form.Name = "Green and Blue two";
        Check("another name takes the question back", !form.AskingReplace && form.Message == "" && form.SaveLabel == "Save");
        form.Name = "green AND blue";
        Equal("and asks again for a taken one", SaveThemeOutcome.AskReplace, form.Submit(Picture));
        Equal("the second Save replaces it", SaveThemeOutcome.Saved, form.Submit(Picture));
        Check("the same card, in the same place", ReferenceEquals(form.Saved, shelf.Cards[0]) && shelf.Cards.Count == 2);
        Equal("with the draft's colours now", Moss, library.Load("0000000000000001")!.Colours["PanelBackground"]);
        Equal("named as typed this time", "green AND blue", shelf.Cards[0].Name);
        Equal("and the CMDR", "CMDR Kailoren", shelf.Cards[0].Author);

        form = new SaveThemeForm(shelf) { Name = new string('n', 60), Commander = "CMDR \u202EKai\u0007loren\r\n" };
        Equal("an empty CMDR is allowed", "", new SaveThemeForm(shelf).Commander);
        Equal("a long name is saved at the share code's cap", SaveThemeOutcome.Saved, form.Submit(Picture));
        Equal("which is 40", new string('n', ThemeText.MaxNameLength), form.Saved?.Name);
        Equal("and a CMDR is cleaned like any credit", "Kailoren", form.Saved?.Theme.Commander);
        var nobody = new SaveThemeForm(shelf) { Name = "Nobody's" };
        nobody.Submit(Picture);
        Equal("with no CMDR there is no author line", "", nobody.Saved?.Author);

        // A folder that cannot be written.
        var blocked = Path.Combine(_dir, "blocked-file");
        File.WriteAllText(blocked, "not a folder");
        var stuck = new ThemeShelf(new ThemeLibrary(blocked), draft, new FakeClipboard());
        var failing = new SaveThemeForm(stuck) { Name = "Anything" };
        Equal("a theme that cannot be written says so", SaveThemeOutcome.Failed, failing.Submit(Picture));
        Equal("in a sentence", SaveThemeForm.FailedMessage, failing.Message);
        Check("and makes no card", stuck.Cards.Count == 0);
        draft.Revert();
    }

    // ---- Import code ---------------------------------------------------------------------------------

    private void ImportTests()
    {
        var folder = Path.Combine(_dir, "import");
        var library = new ThemeLibrary(folder);
        var (draft, themeJson, _) = NewDraft("importdraft");
        var shelf = new ThemeShelf(library, draft, new FakeClipboard());
        shelf.Reload();
        var code = ThemeCode.Encode("Green and Blue theme", "BlueGreen", Tinted(Teal));

        var import = new ThemeImport(shelf);
        Equal("an empty box asks for a code", ImportState.Empty, import.State);
        Equal("with ThemeCode's own words", ThemeCode.Describe(ThemeCodeProblem.Empty), import.Message);
        Check("and nothing can be added", !import.CanAdd && import.Add(null) == null && library.List().Count == 0);

        foreach (var (what, text) in new (string, string?)[]
                 {
                     ("not a code", "hello there"),
                     ("a code cut short", code[..^6]),
                     ("a code with a character changed", code[..20] + (code[20] == 'a' ? 'b' : 'a') + code[21..]),
                     ("a code from a newer FleetFinder", "FFTheme2abcdef"),
                     ("a code with a character codes never use", code[..30] + "0" + code[31..]),
                 })
        {
            import.Read(text);
            var expected = ThemeCode.Decode(text).Message;
            Check($"{what}: ThemeCode's message, as it is ({expected})", import.State == ImportState.Problem && import.Message == expected);
            Check($"{what}: and nothing else, no theme, no credit, no Add", import.Theme == null && import.Credit == ""
                && !import.CanAdd && import.Add([1]) == null && library.List().Count == 0);
        }

        import.Read(code);
        Equal("a good code is ready to add", ImportState.Ready, import.State);
        Equal("its credit line is name - CMDR commander", "Green and Blue theme - CMDR BlueGreen", import.Credit);
        Equal("and nothing else is said", "", import.Message);
        import.Read("Here's mine!\r\n" + code + "\r\nenjoy");
        Equal("a code inside a whole message reads the same", "Green and Blue theme - CMDR BlueGreen", import.Credit);
        Check("the draft is not touched by reading a code", !draft.IsDirty);

        var card = import.Add(Png(160, 160, Teal));
        Check("Add to my themes makes a card", card != null && shelf.Cards.Count == 1 && card.Credit == "Green and Blue theme - CMDR BlueGreen");
        Check("saved with its picture", card != null && File.Exists(library.SnapshotPath(card.Id)!) && card.Snapshot != null);
        Check("with the code's colours", card != null && SameColours(card.Theme.Colours, ThemeCode.Decode(code).Theme!.Colours));
        Check("and still nothing in the draft or theme.json", !draft.IsDirty && !File.Exists(themeJson));
        Equal("after adding, it is had", ImportState.AlreadyHave, import.State);
        Check("and cannot be added twice", import.Add(null) == null && library.List().Count == 1);
        var twice = new ThemeImport(shelf);
        twice.Read(code);
        Equal("the same code again says it is already saved", ThemeImport.AlreadyHaveMessage, twice.Message);
        Check("and offers no Add", !twice.CanAdd);
        twice.Read(ThemeCode.Encode("Green and Blue theme", "BlueGreen", Tinted(Moss)));
        Check("a theme of the same name with other colours can be added beside it", twice.CanAdd);

        var anonymous = new ThemeImport(shelf);
        anonymous.Read(ThemeCode.Encode("Plain", "", Tinted(Moss)));
        Equal("with no commander the credit is just the name", "Plain", anonymous.Credit);

        // A stranger's text, put in the code by hand past every cleaning ThemeCode.Encode does.
        var hostile = new ThemeImport(shelf);
        hostile.Read(Craft(Body("Evil\u202Egnirts\u0007 <Bold>x</Bold> {Binding}", "CMDR \u2066Sneaky\u0000", OneColour(1, 2, 3), FiveAlphas)));
        Equal("a hostile name and commander are shown cleaned, markup and all as plain text",
            "Evilgnirts <Bold>x</Bold> {Binding} - CMDR Sneaky", hostile.Credit);
        Check("and can still be added", hostile.CanAdd);
    }

    // ---- the page --------------------------------------------------------------------------------------

    private void SettingsThemesTests()
    {
        ThemeService.Apply(ThemeRoles.Defaults());
        var window = new MainWindow();
        var page = (SettingsPage)window.FindName("SettingsView");
        var clip = new FakeClipboard { Text = "untouched" };
        page.ClipboardText = clip;
        int lookupThread = -1;
        page.CommanderLookup = () =>
        {
            lookupThread = Environment.CurrentManagedThreadId;
            return "CMDR Kailoren";
        };
        var appThemeJson = ThemeStore.FilePath;
        var appThemeJsonBefore = File.Exists(appThemeJson) ? File.ReadAllBytes(appThemeJson) : null;

        // Empty first.
        page.ThemeLibrary = new ThemeLibrary(Path.Combine(_dir, "page-empty"));
        Layout(page);
        var empty = (TextBlock)page.FindName("ThemesEmpty");
        var emptyTile = (FrameworkElement)page.FindName("ThemesEmptyTile");
        Check("with no themes of the user's the strip says so, in a tile where the first will go",
            emptyTile.Visibility == Visibility.Visible && empty.Text == "No saved themes yet. Save your colours as a theme, or import a code someone shared.");
        Check("and the built-in themes still show after it", ((FrameworkElement)page.FindName("CardScroll")).Visibility == Visibility.Visible
            && page.BuiltInThemeCards.Count == BuiltInThemes.All.Count && page.CardElementOf(page.BuiltInThemeCards[0]) != null);
        double emptyHeight = ((FrameworkElement)page.FindName("ThemesPanel")).ActualHeight;

        var folder = Path.Combine(_dir, "page");
        WriteTheme(folder, "0000000000000001", "Orange", "Kailoren", "2026-10-20T10:00:00Z", ThemeRoles.Defaults());
        WriteTheme(folder, "0000000000000002", "Teal", "", "2026-10-21T10:00:00Z", Tinted(Teal));
        page.ThemeLibrary = new ThemeLibrary(folder);
        Layout(page);
        Check("with themes the empty tile goes", emptyTile.Visibility == Visibility.Collapsed);
        Near("and the strip is the same height either way, so the preview does not jump", emptyHeight,
            ((FrameworkElement)page.FindName("ThemesPanel")).ActualHeight, 0.5);
        var cards = page.ThemeCards;
        Equal("a card for each theme", "Orange|Teal", string.Join("|", cards.Select(c => c.Name)));

        // A card is its picture, its name, its author and Apply; the menu has the rest.
        var orangeCard = page.CardElementOf(cards[0])!;
        var face = Descendants<StackPanel>(orangeCard).First(p => p.Name == "CardFace");
        Equal("a card shows a picture", 1, Descendants<Image>(orangeCard).Count());
        Equal("and its name, its author and Apply, nothing more", "Orange|CMDR Kailoren|Apply",
            string.Join("|", face.Children.OfType<FrameworkElement>()
                .Select(e => e switch { Button b => b.Content as string, TextBlock t => t.Text, _ => e.GetType().Name })));
        var menu = orangeCard.ContextMenu;
        Equal("its right-click menu has Copy share code and Delete", "Copy share code|Delete",
            string.Join("|", menu?.Items.OfType<MenuItem>().Select(i => i.Header as string) ?? []));
        Check("and is drawn by the window's own right-click menu style", menu != null && menu.Style == null
            && window.TryFindResource(typeof(ContextMenu)) is Style);

        // Pictures: one on disk would be read, the missing ones drawn from their own colours.
        Equal("both pictures are missing", 2, cards.Count(c => c.Snapshot == null));
        Equal("the page draws them, and the built-in ones (none kept in this folder yet)", 2 + BuiltInThemes.All.Count, page.DrawMissingSnapshots());
        double dpi = VisualTreeHelper.GetDpi(page).DpiScaleX;
        var png = File.ReadAllBytes(Path.Combine(folder, "0000000000000002.png"));
        var picture = ThemeShelf.ReadSnapshot(png) as BitmapSource;
        Check($"saved as card-sized PNGs ({picture?.PixelWidth}x{picture?.PixelHeight})",
            picture != null && picture.PixelWidth == (int)Math.Round(ThemeSnapshot.CardWidth * dpi)
            && Math.Abs(picture.PixelHeight - ThemeSnapshot.CardHeight * dpi) <= 1);
        if (picture != null)
        {
            // The title bar is the top 36 of the preview's 1100: about five rows at card size.
            var px = new byte[4];
            new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(picture.PixelWidth / 2, 1, 1, 1), px, 4, 0);
            var top = Color.FromRgb(px[2], px[1], px[0]);
            Check($"drawn in the theme's own colours, not the draft's (title bar {top})",
                Math.Abs(top.R - Teal.R) + Math.Abs(top.G - Teal.G) + Math.Abs(top.B - Teal.B) < 24);
        }
        Equal("drawing them leaves the real preview in the draft's colours", page.Draft["TitleBarBackground"],
            ((SolidColorBrush)page.PreviewControl.Resources["TitleBarBackground"]).Color);

        // Save as theme.
        var nameBox = (TextBox)page.FindName("ThemeNameBox");
        var cmdrBox = (TextBox)page.FindName("ThemeCommanderBox");
        var saveButton = (Button)page.FindName("SaveThemeOkButton");
        var saveMessage = (TextBlock)page.FindName("SaveThemeMessage");
        Equal("the name box has the share code's cap", ThemeText.MaxNameLength, nameBox.MaxLength);
        page.OpenSaveTheme();
        Check("Save as theme opens over the page", page.IsDialogOpen && page.IsOverlayOpen);
        Check("and the keys over the list stand back", HoverClipboard.ActionFor(Key.C, ModifierKeys.Control, true, true,
            page.IsOverlayOpen, false) == HoverAction.None);
        for (int i = 0; i < 200 && cmdrBox.Text.Length == 0; i++)
        {
            Thread.Sleep(10);
            Pump();
        }
        Equal("the CMDR box fills from the journal", "Kailoren", cmdrBox.Text);
        Check("which was read off the UI thread", lookupThread > 0 && lookupThread != Environment.CurrentManagedThreadId);
        Check("Enter with no name keeps it open and asks for one", page.HandlePageKey(Key.Enter) && page.IsDialogOpen
            && saveMessage.Text == SaveThemeForm.NeedsNameMessage && saveMessage.Visibility == Visibility.Visible);

        var odd = Color.FromRgb(0x12, 0xAB, 0x34);
        page.Draft.Set("PanelBackground", odd);
        nameBox.Text = "Night run";
        Check("Enter saves", page.HandlePageKey(Key.Enter) && !page.IsDialogOpen);
        Equal("a third card", "Orange|Teal|Night run", string.Join("|", cards.Select(c => c.Name)));
        Equal("in the draft's colours, which the preview shows", odd, cards[2].Theme.Colours["PanelBackground"]);
        Check("with its picture", cards[2].Snapshot != null);
        Check("the app is as it was", ThemeService.Current["PanelBackground"] == ThemeRoles.Find("PanelBackground")!.Default);

        page.OpenSaveTheme();
        nameBox.Text = "night RUN";
        page.HandlePageKey(Key.Enter);
        Check("a name already taken asks before replacing", page.IsDialogOpen && saveButton.Content as string == "Replace"
            && saveMessage.Text == "You already have a theme called \"Night run\". Replace it?" && cards.Count == 3);
        page.Draft.Set("PanelBackground", Teal);
        page.HandlePageKey(Key.Enter);
        Check("and Enter again replaces it, in its place", !page.IsDialogOpen && cards.Count == 3
            && cards[2].Name == "night RUN" && cards[2].Theme.Colours["PanelBackground"] == Teal);
        page.OpenSaveTheme();
        nameBox.Text = "Not saved";
        Check("Escape cancels", page.HandlePageKey(Key.Escape) && !page.IsDialogOpen && cards.Count == 3);
        page.Draft.Revert();

        // Import code.
        var importBox = (TextBox)page.FindName("ImportCodeBox");
        var importMessage = (TextBlock)page.FindName("ImportMessage");
        var importCredit = (TextBlock)page.FindName("ImportCredit");
        var importFound = (FrameworkElement)page.FindName("ImportFound");
        var addButton = (Button)page.FindName("ImportAddButton");
        page.OpenImport();
        var code = ThemeCode.Encode("Green and Blue theme", "BlueGreen", Tinted(Moss));
        importBox.Text = code[..^4];
        Check("a bad code shows ThemeCode's message", importMessage.Text == ThemeCode.Decode(code[..^4]).Message);
        Check("and nothing else happens", importFound.Visibility != Visibility.Visible && !addButton.IsEnabled);
        Check("Enter does nothing with it", page.HandlePageKey(Key.Enter) && page.IsDialogOpen && cards.Count == 3);
        importBox.Text = code;
        Check("a good one shows its picture", importFound.Visibility == Visibility.Visible
            && ((Image)page.FindName("ImportImage")).Source != null);
        Equal("and the credit line", "Green and Blue theme - CMDR BlueGreen", importCredit.Text);
        Check("as plain text", importCredit.Inlines.Count == 1 && importCredit.Inlines.FirstInline is Run run
            && run.Text == "Green and Blue theme - CMDR BlueGreen");
        Check("and can be added", addButton.IsEnabled);
        Check("the draft is not touched", !page.Draft.IsDirty);
        Check("Enter adds it", page.HandlePageKey(Key.Enter) && !page.IsDialogOpen && cards.Count == 4
            && cards[3].Credit == "Green and Blue theme - CMDR BlueGreen" && cards[3].Snapshot != null);

        // Apply, Copy share code, Delete, from the page.
        var teal = cards[1];
        page.ApplyTheme(teal);
        Equal("Apply puts the theme in the preview", Teal, ((SolidColorBrush)page.PreviewControl.Resources["TitleBarBackground"]).Color);
        Equal("and the foot says Unsaved changes", "Unsaved changes", ((TextBlock)page.FindName("FootNote")).Text);
        Equal("but not on the app", ThemeRoles.Find("TitleBarBackground")!.Default, ThemeService.Current["TitleBarBackground"]);
        page.Draft.Revert();
        Equal("Revert undoes it", ThemeRoles.Find("TitleBarBackground")!.Default, page.Draft["TitleBarBackground"]);

        Equal("Copy share code from the page", ThemeShelf.CopiedNote, page.CopyShareCode(teal));
        Check("puts the code on the (pretend) clipboard", ThemeCode.Decode(clip.Text).Theme?.Name == "Teal");
        Equal("and says so on the card", ThemeShelf.CopiedNote, page.NoteShowing);

        // The menu is never opened here (that needs a window on screen; the harness does it), so
        // it is given its target as opening it would.
        var tealCard = page.CardElementOf(teal)!;
        tealCard.ContextMenu!.PlacementTarget = tealCard;
        tealCard.ContextMenu.Items.OfType<MenuItem>().Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Layout(page);
        var tealFace = Descendants<StackPanel>(tealCard).First(p => p.Name == "CardFace");
        var tealConfirm = Descendants<StackPanel>(tealCard).First(p => p.Name == "CardConfirm");
        Check("Delete in the menu asks on the card", teal.IsConfirmingDelete && tealConfirm.Visibility == Visibility.Visible
            && tealFace.Visibility == Visibility.Hidden);
        Check("Escape keeps it", page.HandlePageKey(Key.Escape) && !teal.IsConfirmingDelete && cards.Contains(teal));
        page.AskDelete(teal);
        Check("asked again and confirmed, it goes", page.DeleteTheme(teal) && !cards.Contains(teal)
            && !File.Exists(Path.Combine(folder, "0000000000000002.json")) && !File.Exists(Path.Combine(folder, "0000000000000002.png")));

        // Words and clipboard.
        var words = Descendants<TextBlock>(page).Select(t => t.Text)
            .Concat(Descendants<Button>(page).Select(b => b.Content as string ?? ""))
            .Concat(cards.SelectMany(c => new[] { c.Name, c.Author, c.Credit }))
            .Concat(page.BuiltInThemeCards.SelectMany(c => new[] { c.Name, c.Author, c.Credit }))
            .Concat([SaveThemeForm.NeedsNameMessage, SaveThemeForm.FailedMessage, ThemeImport.AlreadyHaveMessage,
                ThemeImport.BuiltInMessage, ThemeImport.NotAddedMessage, ThemeShelf.CopiedNote, ThemeShelf.NotDeletedNote]);
        Check("no em dash anywhere in the themes strip or its dialogs", !words.Any(t => t.Contains('\u2014')));
        Equal("the real clipboard was never used", typeof(FakeClipboard), page.ClipboardText.GetType());
        Check("and the app's own theme.json was never written",
            File.Exists(appThemeJson) == (appThemeJsonBefore != null)
            && (appThemeJsonBefore == null || File.ReadAllBytes(appThemeJson).SequenceEqual(appThemeJsonBefore)));
    }

    private static void Layout(FrameworkElement page)
    {
        page.Measure(new Size(1692, 1271));
        page.Arrange(new Rect(0, 0, 1692, 1271));
        page.UpdateLayout();
        Pump();
        page.UpdateLayout();
    }
}
