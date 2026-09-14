using System.Collections;
using System.Collections.ObjectModel;
using Microsoft.Win32;
using Telefrag.Collections;
using Telefrag.DI;

namespace Silo.Tests;

public class LibraryTests
{
    public Container Container { get; set; }

    [SetUp]
    public void Setup()
    {
        Container = App.Instance?.Components ?? new("App");
        Container.RegisterSingleton(typeof(IObservable<>), typeof(ObservableCollection<>));
        Container.RegisterSingleton(typeof(IList<>),       typeof(FragList<>));
        Container.RegisterSingleton<ITestFoo, TestFooAlpha>();
        Container.RegisterSingleton<ITestFoo, TestFooBravo>();
        Container.RegisterSingleton<ITestFoo, TestFooCharlie>();
    }

    [Test]
    public void ContainerOperations()
    {
        Assert.That(Container != null, "Container not null");

        Assert.That(Container.Resolve<IObservable<RegistryKey>>() is ObservableCollection<RegistryKey>);
        Assert.That(Container.Resolve<IList<RegistryKey>>() is FragList<RegistryKey>);

        Assert.Pass();
    }
}

public interface ITestFoo;

public record TestFooAlpha : ITestFoo;

public record TestFooBravo : ITestFoo;

public record TestFooCharlie : ITestFoo;