using System.Collections.Generic;
using UnityEngine;

public class NatureInteractable : MonoBehaviour
{
    [Header("Display Info")]
    public string displayName;
    public string scientificName;
    public string category = "Flora";
    [TextArea(2, 5)] public string description;
    [TextArea(1, 3)] public string funFact;

    // List<T>, not IReadOnlyList<T>: NatureInfoUI.Update walks this every few
    // frames, and foreach over the interface boxes List<T>.Enumerator into
    // IEnumerator<T> and allocates every time. The concrete type gets the
    // struct enumerator instead — zero allocation for the same loop.
    static readonly List<NatureInteractable> s_all = new();
    public static List<NatureInteractable> All => s_all;

    static readonly Dictionary<string, (string disp, string sci, string desc, string fact)> k_db =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["fern"]    = ("Silver Fern (Ponga)", "Cyathea dealbata",
            "New Zealand's national symbol. The silver underside of its fronds guided Maori travellers " +
            "through forests at night, reflecting moonlight like a natural trail marker.",
            "Silver ferns can grow up to 10 metres tall and live for over 100 years!"),

        ["heather"] = ("Heather", "Calluna vulgaris",
            "A hardy flowering shrub thriving on exposed volcanic plateaus. " +
            "Produces dense purple blooms in late summer, painting the hillside in colour.",
            "Though introduced from Europe, heather now feeds native bees and birds on NZ's central plateau."),

        ["tussock"] = ("Red Tussock", "Chionochloa rubra",
            "A dominant subalpine grass forming golden-red clumps that shelter insects and small lizards. " +
            "Vital for preventing erosion on volcanic slopes.",
            "A single tussock clump can live for hundreds of years, slowly building its own microclimate!"),

        ["manuka"]  = ("Manuka", "Leptospermum scoparium",
            "A small shrubby tree famous for its antibacterial honey. " +
            "Manuka is often the first woody plant to colonise disturbed volcanic ground.",
            "Manuka honey is effective against over 250 clinical pathogens including antibiotic-resistant bacteria!"),

        ["toetoe"]  = ("Toetoe", "Austroderia toetoe",
            "New Zealand's native pampas, with graceful arching leaves and feathery cream plumes. " +
            "Often confused with the invasive pampas grass it has replaced in restoration planting.",
            "Maori used toetoe leaves to weave kete (baskets) and thatch whare (houses)!"),

        ["cabbage"] = ("Cabbage Tree (Ti Kouka)", "Cordyline australis",
            "An iconic NZ landmark tree with a sword-like crown. Survivors of volcanic disturbance, " +
            "they resprout vigorously after fire or ash fall.",
            "The sweet flowering cabbage tree is a sign of spring; Maori used the leaves to weave sandals and baskets!"),
    };

    void OnEnable()  => s_all.Add(this);
    void OnDisable() => s_all.Remove(this);

    void Awake()
    {
        if (!string.IsNullOrEmpty(displayName)) return;
        AutoFill(gameObject.name);
    }

    public void AutoFill(string goName)
    {
        foreach (var kv in k_db)
        {
            if (goName.IndexOf(kv.Key, System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                (displayName, scientificName, description, funFact) = kv.Value;
                return;
            }
        }
        displayName = goName;
        description = "A native plant of the New Zealand alpine environment.";
    }
}
