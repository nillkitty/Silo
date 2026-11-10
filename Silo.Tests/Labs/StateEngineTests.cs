using Silo.Labs;
using Silo.Labs.States;

namespace Silo.Tests.Labs;

public class StateEngineTests
{
    private static StateDoc Sample(string name) => StateSamples.All.First(s => s.Name == name).Make();

    [Test]
    public void A_run_starts_in_the_initial_state_with_default_values()
    {
        var run = new StateRun(Sample("Traffic Light"));

        Assert.That(run.Current, Is.EqualTo("Red"));
        Assert.That(run.Values["Ticks"], Is.EqualTo(0d));
    }

    [Test]
    public void An_update_takes_the_first_transition_that_can_be_taken()
    {
        var run = new StateRun(Sample("Door"));

        var step = run.Update();

        Assert.That(step.Done, Is.True);
        Assert.That(run.Current, Is.EqualTo("Open"));
    }

    [Test]
    public void A_constraint_blocks_a_transition_until_it_holds_and_says_why()
    {
        var run = new StateRun(Sample("Traffic Light"));

        Assert.That(run.Update().Done, Is.False);
        Assert.That(run.Candidates().Single().Reason, Does.Contain("Ticks >= 3").And.Contain("Ticks is 0"));

        Assert.That(run.SetField("Ticks", "3"), Is.Null);
        Assert.That(run.Update().Done, Is.True);
        Assert.That(run.Current, Is.EqualTo("Green"));
        Assert.That(run.Values["Ticks"], Is.EqualTo(0d), "the effect set Ticks back to 0");
        Assert.That(run.Log, Has.Some.Contains("Go"));
    }

    [Test]
    public void The_transition_with_the_higher_priority_goes_first_whatever_the_order_drawn()
    {
        var doc = new StateDoc { Initial = "A" };
        doc.States.AddRange([new StateDef { Name = "A" }, new StateDef { Name = "B" }, new StateDef { Name = "C" }]);
        doc.Transitions.Add(new StateTransitionDef { From = "A", To = "B", Priority = 1 });
        doc.Transitions.Add(new StateTransitionDef { From = "A", To = "C", Priority = 5 });

        var run = new StateRun(doc);
        run.Update();

        Assert.That(run.Current, Is.EqualTo("C"));
    }

    [Test]
    public void A_transition_from_any_state_can_be_taken_from_every_state()
    {
        var doc = new StateDoc { Initial = "A" };
        doc.States.AddRange([new StateDef { Name = "A" }, new StateDef { Name = "B" }, new StateDef { Name = "Dead" }]);
        doc.Transitions.Add(new StateTransitionDef { From = "A", To = "B" });
        var kill = new StateTransitionDef { From = StateTransitionDef.Any, To = "Dead", Priority = -1 };
        doc.Transitions.Add(kill);

        var run = new StateRun(doc);
        Assert.That(run.Fire(kill).Done, Is.True);
        Assert.That(run.Current, Is.EqualTo("Dead"));
        Assert.That(run.Candidates().Select(c => c.Transition), Is.EqualTo(new[] { kill }));
    }

    [Test]
    public void Firing_a_blocked_transition_does_nothing()
    {
        var run = new StateRun(Sample("Order"));
        var pay = run.Doc.Transitions.First(t => t.To == "Paid");

        var step = run.Fire(pay);

        Assert.That(step.Done, Is.False);
        Assert.That(run.Current, Is.EqualTo("Cart"));
        Assert.That(run.Steps, Is.EqualTo(0));
    }

    [Test]
    public void Effects_set_add_toggle_and_log_with_fields_in_the_text()
    {
        var doc = new StateDoc { Initial = "A" };
        doc.States.AddRange([new StateDef { Name = "A" }, new StateDef { Name = "B" }]);
        doc.Fields.AddRange([new StateFieldDef { Name = "N", Kind = StateFieldKind.Number, Default = "1" }, new StateFieldDef { Name = "On", Kind = StateFieldKind.Bool }]);
        doc.Transitions.Add(new StateTransitionDef
                            {
                                From = "A", To = "B",
                                Effects =
                                [
                                    new StateEffectDef { Kind = StateEffectKind.Add, Field = "N", Value = "2.5" },
                                    new StateEffectDef { Kind = StateEffectKind.Toggle, Field = "On" },
                                    new StateEffectDef { Kind = StateEffectKind.Log, Value = "N is {N}, On is {On}" }
                                ]
                            });

        var run = new StateRun(doc);
        run.Update();

        Assert.That(run.Values["N"], Is.EqualTo(3.5));
        Assert.That(run.Values["On"], Is.EqualTo(true));
        Assert.That(run.Log, Has.Some.EqualTo("  N is 3.5, On is true"));
    }

    [Test]
    public void Running_to_rest_stops_a_machine_that_loops()
    {
        var doc = new StateDoc { Initial = "A" };
        doc.States.AddRange([new StateDef { Name = "A" }, new StateDef { Name = "B" }]);
        doc.Transitions.Add(new StateTransitionDef { From = "A", To = "B" });
        doc.Transitions.Add(new StateTransitionDef { From = "B", To = "A" });

        var run = new StateRun(doc);

        Assert.That(run.RunToRest(10), Has.Count.EqualTo(10));
    }

    [Test]
    public void Sync_keeps_the_run_after_the_machine_is_edited()
    {
        var doc = Sample("Traffic Light");
        var run = new StateRun(doc);
        run.SetField("Ticks", "2");
        doc.Fields.Add(new StateFieldDef { Name = "Extra", Kind = StateFieldKind.Text, Default = "x" });
        doc.States.RemoveAll(s => s.Name == "Yellow");

        run.Sync(doc);

        Assert.That(run.Values["Ticks"], Is.EqualTo(2d));
        Assert.That(run.Values["Extra"], Is.EqualTo("x"));
        Assert.That(run.Current, Is.EqualTo("Red"));
    }

    [Test]
    public void The_samples_have_nothing_wrong_with_them()
    {
        foreach (var sample in StateSamples.All)
            Assert.That(StateCheck.Problems(sample.Make()).Where(p => p.Severity != LabProblemSeverity.Info), Is.Empty, sample.Name);
    }

    [Test]
    public void Problems_name_what_does_not_exist_and_what_cannot_be_reached()
    {
        var doc = Sample("Door");
        doc.Transitions.Add(new StateTransitionDef { From = "Open", To = "Gone", Constraints = [new StateConstraintDef { Field = "Nope", Op = "==", Value = "1" }] });
        doc.States.Add(new StateDef { Name = "Island" });

        var messages = StateCheck.Problems(doc).Select(p => p.Message).ToList();

        Assert.That(messages, Has.Some.Contains("the To state 'Gone' does not exist"));
        Assert.That(messages, Has.Some.Contains("uses a field that does not exist"));
        Assert.That(messages, Has.Some.Contains("Island cannot be reached from Closed"));
    }

    [Test]
    public void A_machine_without_an_initial_state_is_an_error()
    {
        var doc = Sample("Door");
        doc.Initial = string.Empty;

        Assert.That(StateCheck.Problems(doc), Has.Some.Matches<StateIssue>(p => p.Severity == LabProblemSeverity.Error && p.Message.Contains("no initial state")));
        Assert.That(new StateRun(doc).Update().Done, Is.False);
    }

    [Test]
    public void The_machine_survives_a_trip_through_JSON()
    {
        var doc = Sample("Order");

        var copy = doc.Clone();

        Assert.That(copy.ToJson(), Is.EqualTo(doc.ToJson()));
        Assert.That(copy.Transitions.Select(t => t.Id), Is.EqualTo(doc.Transitions.Select(t => t.Id)));
    }

    [Test]
    public void The_code_builds_the_machine_with_Telefrag_State()
    {
        string code = StateCode.ToCSharp(Sample("Door"));

        Assert.That(code, Does.Contain("public enum DoorState").And.Contain("Closed,").And.Contain("Locked,"));
        Assert.That(code, Does.Contain("machine.AddState(new State<DoorState> { StateValue = DoorState.Closed"));
        Assert.That(code, Does.Contain("machine.Transitions.Initial(DoorState.Closed);"));
        Assert.That(code, Does.Contain("FromStates = [DoorState.Closed], ToStates = [DoorState.Open]"));
        Assert.That(code, Does.Contain("// constraint: HasKey == true"));
    }
}
