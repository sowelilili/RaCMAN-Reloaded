using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// The Combos panel's second table, the Trainer pages' combos (qwark build 59, revision 1.34): a row
/// for each page the module lists, by the module's name and in its order, laid out as the action
/// combos' table is with Page for Action. The rows of the pages with a combo always show; the others
/// wait behind "More...", which shows them until the panel is left. Nothing of it is saved: each
/// visit to the panel starts with them hidden.
/// <para>
/// No ImGui here: <see cref="CombosPanel"/> draws what this says, and the tests ask it the same.
/// </para>
/// </summary>
public sealed class TrainerPageTable
{
    /// <summary>The first column's header, where the action combos' table has "Action".</summary>
    public const string Header = "Page";

    /// <summary>The button under the tables while the pages without a combo are hidden.</summary>
    public const string MoreLabel = "More...";

    /// <summary>The same button while every page shows.</summary>
    public const string FewerLabel = "Fewer";

    /// <summary>Whether every page shows, rather than only those with a combo.</summary>
    public bool Expanded { get; private set; }

    /// <summary>What a press of the button does: every page, or back to the bound ones.</summary>
    public void Toggle() => Expanded = !Expanded;

    /// <summary>
    /// Called every frame with the panel the window is on: anywhere but Combos hides the pages without
    /// a combo again, so coming back to the panel starts with them hidden.
    /// </summary>
    public void Observe(int panel)
    {
        if (panel != PanelNav.Combos) Expanded = false;
    }

    /// <summary>
    /// Whether the panel has the table and its button at all: the module sent its page list in
    /// COMBO_LIST, which a module from build 59 does in every game, Tools of Destruction's included.
    /// An older module names none.
    /// </summary>
    public static bool Shows(IEnumerable<ComboEntry> combos) => combos.Any(entry => entry.IsTrainerPage);

    /// <summary>
    /// Whether the rows are greyed: only where the running game's module says its Trainer cannot come
    /// up on this platform, the console menu row's rule (<see cref="CombosPanel.MenuComboBlocked"/>).
    /// They stay editable, as that row does: a combo recorded now fires wherever the Trainer can.
    /// </summary>
    public static bool Greyed(SessionInfo session, DescribeResult describe) =>
        CombosPanel.MenuComboBlocked(ComboAction.OpenConsoleMenu, session, describe);

    /// <summary>
    /// The page combos the module listed, in its order: every page of the in-game Trainer with its
    /// name. Empty against a module before build 59.
    /// </summary>
    public static IReadOnlyList<ComboEntry> Pages(IEnumerable<ComboEntry> combos) =>
        combos.Where(entry => entry.IsTrainerPage).ToList();

    /// <summary>A page has a bind: a combo in force, the shared one or this user's own.</summary>
    public static bool IsBound(ComboEntry page) => page.Mask != 0 || page.Overridden;

    /// <summary>
    /// The rows the table draws: every page while expanded, otherwise the pages with a combo, and the
    /// page a capture is running on, so a capture never loses its row under it.
    /// </summary>
    public IReadOnlyList<ComboEntry> Rows(IReadOnlyList<ComboEntry> pages, ComboAction? capturing = null) =>
        Expanded ? pages : pages.Where(page => IsBound(page) || page.Action == capturing).ToList();

    /// <summary>
    /// The button's label, or null for no button: there is nothing to show or hide when every page
    /// has a combo, or there are no pages.
    /// </summary>
    public string? ButtonLabel(IReadOnlyList<ComboEntry> pages) =>
        pages.Count == 0 || pages.All(IsBound) ? null
        : Expanded ? FewerLabel
        : MoreLabel;

    /// <summary>What the button says about itself on hover.</summary>
    public string ButtonTooltip =>
        Expanded ? "Hide the Trainer pages that have no combo." : "Show Trainer page combos.";

    /// <summary>A row's first cell: the page, as the module names it.</summary>
    public static string RowLabel(ComboEntry page) => page.Page ?? CombosPanel.Label(page.Action);

    /// <summary>A row's tooltip, naming its page as the module does.</summary>
    public static string Tooltip(ComboEntry page) => $"Opens the Trainer menu directly to the {RowLabel(page)} page.";
}
