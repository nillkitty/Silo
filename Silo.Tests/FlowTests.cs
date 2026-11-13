using Newtonsoft.Json.Linq;
using Silo.Actions;
using Silo.Contracts.Adapters;
using Silo.Flows;
using Silo.Providers.Adapter;

namespace Silo.Tests;

public class AdapterTests
{
    [Test]
    public async Task BasicAdapterParsesAString()
    {
        var a = new BasicAdapter { Target = typeof(int) };
        Assert.That(await a.ConvertAsync("42"), Is.EqualTo(42));
        Assert.That(a.ConversionsFor(typeof(int)), Is.Empty);
    }

    [Test]
    public async Task AFailedConversionIsNullUnlessThrowErrorsIsSet()
    {
        var a = new BasicAdapter { Target = typeof(int) };
        Assert.That(await a.ConvertAsync("nope"), Is.Null);
        a.ThrowErrors = true;
        await Assert.ThrowsAsync<FormatException>(async () => await a.ConvertAsync("nope"));
    }

    [Test]
    public async Task JsonAdapterParsesAndLooksUpAPath()
    {
        var token = await new JsonAdapter().ConvertAsync("""{"a":{"b":[1,2,3]}}""");
        Assert.That(token, Is.InstanceOf<JObject>());
        var value = await new JsonAdapter { Path = "$.a.b[1]", Target = typeof(string) }.ConvertAsync(token!);
        Assert.That(value, Is.EqualTo("2"));
    }

    [Test]
    public async Task YamlRoundTripsThroughJson()
    {
        var token = (JToken) (await new YamlAdapter().ConvertAsync("name: kitten\nlives: 9"))!;
        Assert.That((string?) token["name"], Is.EqualTo("kitten"));
        Assert.That((int?) token["lives"], Is.EqualTo(9));
        var yaml = (string) (await new YamlAdapter().ConvertAsync(token))!;
        Assert.That(yaml, Does.Contain("name: kitten"));
    }

    [Test]
    public async Task XmlAdapterEvaluatesXPath()
    {
        var a = new XmlAdapter { XPath = "/pets/cat/@name", Target = typeof(string) };
        Assert.That(await a.ConvertAsync("""<pets><cat name="Tom"/></pets>"""), Is.EqualTo("Tom"));
    }

    [Test]
    public async Task StringAdapterIsInvariant()
    {
        Assert.That(await new StringAdapter().ConvertAsync(1234.5), Is.EqualTo("1234.5"));
    }
}

public class ActionTests
{
    private static ActionRegistry Registry()
    {
        var r = new ActionRegistry();
        r.Register(DelegateAction.Of("Math.Add", a => (int) a["x"]! + (int) a["y"]!,
                                     [new ActionParameter("x", typeof(int)), new ActionParameter("y", typeof(int), false, 10)],
                                     isRegistered: true));
        r.Register(DelegateAction.Of("Internal.Thing", _ => "hi"));
        return r;
    }

    [Test]
    public async Task ConvertsStringsFillsDefaultsAndIgnoresCase()
    {
        Assert.That(await Registry().InvokeAsync("math.add", new Dictionary<string, object?> { ["X"] = "5" }), Is.EqualTo(15));
    }

    [Test]
    public async Task RejectsMissingAndUnknownArguments()
    {
        var r = Registry();
        await Assert.ThrowsAsync<ActionArgumentException>(async () => await r.InvokeAsync("Math.Add"));
        await Assert.ThrowsAsync<ActionArgumentException>(async () => await r.InvokeAsync("Math.Add",
                                                                                     new Dictionary<string, object?> { ["x"] = 1, ["z"] = 2 }));
        await Assert.ThrowsAsync<ActionArgumentException>(async () => await r.InvokeAsync("Math.Add",
                                                                                     new Dictionary<string, object?> { ["x"] = "abc" }));
        await Assert.ThrowsAsync<ActionNotFoundException>(async () => await r.InvokeAsync("Nope"));
    }

    [Test]
    public async Task OnlyRegisteredActionsAreAvailableFromOutside()
    {
        var r = Registry();
        Assert.That(await r.InvokeExternalAsync("Math.Add", new Dictionary<string, object?> { ["x"] = 1 }), Is.EqualTo(11));
        await Assert.ThrowsAsync<ActionNotFoundException>(async () => await r.InvokeExternalAsync("Internal.Thing"));
        Assert.That(r.Registered.Select(a => a.Name), Is.EqualTo(new[] { "Math.Add" }));
    }
}

public class FlowTests
{
    [Test]
    public async Task RunsStepsInOrderThroughAdapters()
    {
        var flow = new Flow("parse", [
                                         new SourceStep(_ => (object?) """{"n":"7"}"""),
                                         new AdapterStep(new JsonAdapter { Path = "$.n", Target = typeof(string) }),
                                         new AdapterStep(new BasicAdapter(), typeof(int))
                                     ]);
        var result = await flow.RunAsync();
        Assert.Multiple(() =>
                        {
                            Assert.That(result.Status, Is.EqualTo(FlowStatus.Completed));
                            Assert.That(result.Output, Is.EqualTo(7));
                            Assert.That(result.Trace, Has.Count.EqualTo(3));
                        });
    }

    [Test]
    public async Task ConditionsJumpAndStopEndsTheRun()
    {
        var flow = new Flow("branch", [
                                          new ConditionStep(c => (int) c.Current! > 10, "big", "small"),
                                          new ActionStep("Add", new Dictionary<string, object?> { ["x"] = "$current" }) { Label = "small" },
                                          new StopStep(),
                                          new ActionStep("Add", new Dictionary<string, object?> { ["x"] = "$current", ["y"] = 100 }) { Label = "big" }
                                      ]);
        var actions = new ActionRegistry();
        actions.Register(DelegateAction.Of("Add", a => (int) a["x"]! + (int) a["y"]!,
                                           [new ActionParameter("x", typeof(int)), new ActionParameter("y", typeof(int), false, 1)]));
        var small = await flow.RunAsync(5, actions);
        var big   = await flow.RunAsync(50, actions);
        Assert.Multiple(() =>
                        {
                            Assert.That(small.Output, Is.EqualTo(6));
                            Assert.That(small.Status, Is.EqualTo(FlowStatus.Stopped));
                            Assert.That(big.Output, Is.EqualTo(150));
                            Assert.That(big.Status, Is.EqualTo(FlowStatus.Completed));
                        });
    }

    [Test]
    public async Task AFailingStepFailsTheRunWithoutThrowing()
    {
        var flow   = new Flow("bad", [new AdapterStep(new BasicAdapter { ThrowErrors = true }, typeof(int))]);
        var result = await flow.RunAsync("x");
        Assert.Multiple(() =>
                        {
                            Assert.That(result.Status, Is.EqualTo(FlowStatus.Failed));
                            Assert.That(result.Error, Is.InstanceOf<FormatException>());
                        });
    }

    [Test]
    public async Task ARunawayLoopIsStopped()
    {
        var flow = new Flow("loop", [new JumpStep("a") { Label = "a" }]) { MaxSteps = 50 };
        var result = await flow.RunAsync();
        Assert.That(result.Error?.Message, Does.Contain("50"));
    }

    [Test]
    public async Task PureAdaptersAreCachedAndAttachedOnce()
    {
        var counting = new CountingAdapter();
        var flow     = new Flow("cache", [new AdapterStep(counting)]);
        await flow.RunAsync("a");
        await flow.RunAsync("a");
        await flow.RunAsync("b");
        Assert.Multiple(() =>
                        {
                            Assert.That(counting.Conversions, Is.EqualTo(2));
                            Assert.That(counting.Attached, Is.EqualTo(1));
                        });
    }

    [Test]
    public async Task RunAllRunsEachItem()
    {
        var flow    = new Flow("len", [new AdapterStep(new BasicAdapter(), typeof(int))]);
        var results = await flow.RunAllAsync(["1", "2", "3"]).ToListAsync();
        Assert.That(results.Select(r => r.Output), Is.EqualTo(new object[] { 1, 2, 3 }));
    }

    private sealed class CountingAdapter : IAdapter, IFlowAdapter
    {
        public int Conversions, Attached;

        public bool ThrowErrors { get; set; }
        public bool Pure        => true;

        public void AttachToFlow(FlowContext context) => Attached++;

        public Type[] ConversionsFor(Type inputType) => [typeof(string)];

        public Task<object?> ConvertAsync(object inObj)
        {
            Conversions++;
            return Task.FromResult<object?>(inObj.ToString());
        }
    }
}


public class AppActionTests
{
    private sealed class FakeHost : IAppActionHost
    {
        public readonly List<string> Calls = [];

        public Task SaveAllAsync() { Calls.Add("save"); return Task.CompletedTask; }

        public Task<string?> OpenSiloAsync(string path, bool forceReopen) { Calls.Add($"open {path} {forceReopen}"); return Task.FromResult<string?>(path); }

        public Task<string?> NewSiloAsync(string path) { Calls.Add($"new {path}"); return Task.FromResult<string?>(path); }

        public Task ExitAsync(bool saveChanges) { Calls.Add($"exit {saveChanges}"); return Task.CompletedTask; }
    }

    private static (ActionRegistry, FakeHost) Setup()
    {
        var host = new FakeHost();
        var reg  = new ActionRegistry();
        AppActions.Register(reg, host);
        reg.Register(DelegateAction.Of("Private.Thing", _ => "secret"));
        return (reg, host);
    }

    [Test]
    public async Task TheAppActionsReachTheHostWithTypedArguments()
    {
        var (reg, host) = Setup();
        await reg.InvokeAsync("App.SaveAll");
        await reg.InvokeAsync("App.OpenSilo", new Dictionary<string, object?> { ["Path"] = "a.silo", ["ForceReopen"] = "true" });
        await reg.InvokeAsync("App.OpenSilo", new Dictionary<string, object?> { ["Path"] = "b.silo" });
        await reg.InvokeAsync("App.NewSilo", new Dictionary<string, object?> { ["Path"] = "c.silo" });
        await reg.InvokeAsync("App.Exit");
        await reg.InvokeAsync("App.Exit", new Dictionary<string, object?> { ["SaveChanges"] = false });
        Assert.That(host.Calls, Is.EqualTo(new[] { "save", "open a.silo True", "open b.silo False", "new c.silo", "exit True", "exit False" }));
    }

    [Test]
    public async Task AnExternalRequestRunsARegisteredActionAndAnswersWithJson()
    {
        var (reg, host) = Setup();
        var reply = JObject.Parse(await ActionProtocol.HandleAsync(reg, ActionProtocol.BuildRequest("app.opensilo",
                                                                    new Dictionary<string, object?> { ["Path"] = "x.silo", ["ForceReopen"] = true })));
        Assert.Multiple(() =>
                        {
                            Assert.That((bool?) reply["ok"], Is.True);
                            Assert.That((string?) reply["result"], Is.EqualTo("x.silo"));
                            Assert.That(host.Calls, Is.EqualTo(new[] { "open x.silo True" }));
                        });
    }

    [Test]
    public async Task AnExternalRequestCantReachAnUnregisteredActionOrSendBadInput()
    {
        var (reg, _) = Setup();
        foreach (var line in new[]
                 {
                     ActionProtocol.BuildRequest("Private.Thing"), ActionProtocol.BuildRequest("Nope"),
                     "ACTION\tApp.OpenSilo\tnot json", "ACTION\tApp.OpenSilo\t[1]", "ACTION\t\t{}", ActionProtocol.BuildRequest("App.OpenSilo")
                 })
        {
            var reply = JObject.Parse(await ActionProtocol.HandleAsync(reg, line));
            Assert.That((bool?) reply["ok"], Is.False, line);
            Assert.That((string?) reply["error"], Is.Not.Empty, line);
        }
    }

    [Test]
    public async Task ListingShowsOnlyRegisteredActions()
    {
        var (reg, _) = Setup();
        var reply = JObject.Parse(await ActionProtocol.HandleAsync(reg, ActionProtocol.ListVerb));
        Assert.That(reply["result"]!.Select(a => (string?) a["name"]), Is.EqualTo(new[] { "App.Exit", "App.NewSilo", "App.OpenSilo", "App.SaveAll" }));
    }
}
