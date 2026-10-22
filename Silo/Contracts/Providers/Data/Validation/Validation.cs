using System.ComponentModel.DataAnnotations;

namespace Silo.Providers.Data.Validation;

/// <summary>
///     An object which has a Validate method
/// </summary>
public interface IValidatable
{
    /// <summary>
    ///     Validates the state of the object and throws an appropriate exception if the item is not valid.
    /// </summary>
    void Validate();
}

/// <summary>
///     An object which has soft validation (returns an exception instead of throwing it)
/// </summary>
public interface ISoftValidate
{
    Exception? Validate();
}

/// <summary>
///     Validation extension methods
/// </summary>
public static class ValidationExtensions
{
    extension<TType>(TType v) where TType : IValidatable
    {
        /// <summary>
        ///     Gets a soft validator which will not throw an exception upon
        ///     validation exception.
        /// </summary>
        public SoftValidator<TType> ToSoftValidator()
        {
            v.Required();
            return new SoftValidator<TType>(v);
        }

        public void Assert(bool condition, string exMessage)
        {
            if (!condition)
                throw new ValidationException(exMessage);
        }
    }
}

/// <summary>
///     Validator which does not throw exceptions
/// </summary>
public record SoftValidator<TType>(IValidatable Inner) : ISoftValidate
{
    public Exception? Validate()
    {
        try
        {
            Inner.Validate();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}

/// <summary>
///     A service which provides access to the database used internally to a Silo repository.
/// </summary>
public interface IDatabaseProvider
{
    bool             ItemExists<TItem>(TItem item);
    bool             RemoveItem<TItem>(TItem required);
    bool             AddItem<TItem>(TItem    item);
    ISiloUserContext GetUserContext();
    Task<bool>       ShutdownAsync();
    Task             SaveAsync();
    Task             MigrateToCopyAsync(string path);
    Task<bool>       ImportFileAsync(object    fn);
}