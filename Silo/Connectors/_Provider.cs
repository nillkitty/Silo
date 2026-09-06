namespace Silo.Connectors;

public interface IProvider
{
    void InitProvider(IReceiver receiver);
}

public interface IProvider<out TType> : IProvider
{
    void InitProvider(IReceiver<TType> receiver);

    void IProvider.InitProvider(IReceiver recv) => recv.Receiver(recv);
}

public interface IReceiver
{
    void Receiver(object o);
}

public interface IReceiver<in TType> : IReceiver
{
    void Receiver(TType o);

    void IReceiver.Receiver(object o)
    {
        if (o is TType tt) Receiver(tt);
    }
}