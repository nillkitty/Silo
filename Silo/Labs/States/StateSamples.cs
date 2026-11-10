namespace Silo.Labs.States;

/// <summary>Ready-made machines to start from or to learn from (the Library lists them).</summary>
public static class StateSamples
{
    public sealed record Sample(string Name, string Description, Func<StateDoc> Make);

    public static IReadOnlyList<Sample> All { get; } =
    [
        new("Door", "A door that can be opened, closed and locked with a key.", Door),
        new("Traffic Light", "Lights that change after a few ticks, counted by a field.", TrafficLight),
        new("Order", "An order from cart to delivery, which can be cancelled until it is shipped.", Order)
    ];

    private static StateDoc Door()
    {
        var doc = new StateDoc { Name = "Door", Description = "A door that can be opened, closed and locked with a key.", Initial = "Closed" };
        doc.States.AddRange([S("Closed", 80, 120), S("Open", 340, 120), S("Locked", 80, 300)]);
        doc.Fields.Add(new StateFieldDef { Name = "HasKey", Kind = StateFieldKind.Bool, Default = "true" });
        doc.Transitions.AddRange([
            T("Closed", "Open"),
            T("Open", "Closed"),
            T("Closed", "Locked", c: [C("HasKey", "==", "true")], e: [Log("Locked with the key")]),
            T("Locked", "Closed", c: [C("HasKey", "==", "true")], e: [Log("Unlocked with the key")])
        ]);
        return doc;
    }

    private static StateDoc TrafficLight()
    {
        var doc = new StateDoc { Name = "Traffic Light", Description = "Lights that change after a few ticks: add 1 to Ticks, and update.", Initial = "Red" };
        doc.States.AddRange([S("Red", 80, 80), S("Green", 340, 80), S("Yellow", 210, 280)]);
        doc.Fields.Add(new StateFieldDef { Name = "Ticks", Kind = StateFieldKind.Number, Default = "0" });
        doc.Transitions.AddRange([
            T("Red", "Green", c: [C("Ticks", ">=", "3")], e: [Set("Ticks", "0"), Log("Go")]),
            T("Green", "Yellow", c: [C("Ticks", ">=", "3")], e: [Set("Ticks", "0"), Log("Slow down")]),
            T("Yellow", "Red", c: [C("Ticks", ">=", "1")], e: [Set("Ticks", "0"), Log("Stop")])
        ]);
        return doc;
    }

    private static StateDoc Order()
    {
        var doc = new StateDoc { Name = "Order", Description = "An order from cart to delivery, which can be cancelled until it is shipped.", Initial = "Cart" };
        doc.States.AddRange([S("Cart", 60, 100), S("Paid", 260, 100), S("Shipped", 460, 100), S("Delivered", 660, 100), S("Cancelled", 260, 300)]);
        doc.Fields.AddRange([
            new StateFieldDef { Name = "Total", Kind = StateFieldKind.Number, Default = "0" },
            new StateFieldDef { Name = "Paid", Kind = StateFieldKind.Bool, Default = "false" }
        ]);
        doc.Transitions.AddRange([
            T("Cart", "Paid", c: [C("Total", ">", "0")], e: [Set("Paid", "true"), Log("Charged {Total}")]),
            T("Paid", "Shipped", c: [C("Paid", "==", "true")]),
            T("Shipped", "Delivered"),
            T("Cart", "Cancelled", priority: -1),
            T("Paid", "Cancelled", priority: -1, e: [Set("Paid", "false"), Log("Refunded {Total}")])
        ]);
        return doc;
    }

    private static StateDef S(string name, double x, double y) => new() { Name = name, X = x, Y = y };

    private static StateConstraintDef C(string field, string op, string value) => new() { Field = field, Op = op, Value = value };

    private static StateEffectDef Set(string field, string value) => new() { Kind = StateEffectKind.Set, Field = field, Value = value };

    private static StateEffectDef Log(string text) => new() { Kind = StateEffectKind.Log, Value = text };

    private static StateTransitionDef T(string from, string to, int priority = 0, StateConstraintDef[]? c = null, StateEffectDef[]? e = null)
    {
        return new StateTransitionDef { From = from, To = to, Priority = priority, Constraints = [..c ?? []], Effects = [..e ?? []] };
    }
}
