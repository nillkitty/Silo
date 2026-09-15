namespace Silo.Connectors;

/// <summary>
/// An object which provides other objects to a receiver
/// </summary>
public interface IProvider
{
    /// <summary>
    /// Hooks up the receiver to the provider.
    /// </summary>
    void InitProvider(IReceiver receiver);
}

/// <summary>
/// An object which provides objects of type <typeparamref name="TType"/> to
/// a <see cref="IReceiver{TType}"/>
/// </summary>
public interface IProvider<out TType> : IProvider
{
    /// <summary>
    /// Hooks up the receiver to the provider.
    /// </summary>
    void InitProvider(IReceiver<TType> receiver);

    void IProvider.InitProvider(IReceiver recv) => recv.Receiver(recv);
}

/// <summary>
/// An object which receives other objects
/// </summary>
public interface IReceiver
{
    /// <summary>
    /// Sends the receiver an object
    /// </summary>
    void Receiver(object o);
}

/// <summary>
/// An object which receives objects of type <typeparamref name="TType"/>
/// </summary>
public interface IReceiver<in TType> : IReceiver
{
    /// <summary>
    /// Sends the receiver an object
    /// </summary>
    void Receiver(TType o);

    void IReceiver.Receiver(object o)
    {
        if (o is TType tt) Receiver(tt);
    }
}