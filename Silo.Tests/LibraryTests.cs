using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using Telefrag.Collections;
using Telefrag.DI;

namespace Silo.Tests;

public interface ITestFoo
{
}

public class TestFooAlpha : ITestFoo;

public class TestFooBravo : ITestFoo;

public class TestFooCharlie : ITestFoo;

public class LibraryTests
{
    public Container Container { get; set; }

    [SetUp]
    public void Setup()
    {
        Container = App.Instance?.Components ?? new Container("App");
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

    [Test]
    public void FileSystemTests()
    {
        if (Directory.GetDirectoryRoot(Environment.ProcessPath) is { } rr) Console.WriteLine(rr);
    }
}