using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;
using Silo.Extensions;

namespace Silo.Contracts;

public interface IValidatable
{
    void Validate();
}

public interface ISoftValidate
{
    Exception? Validate();
}

public static class ValidationExtensions
{
    extension<TType>(TType v) where TType : IValidatable
    {
        /// <summary>
        /// Gets a soft validator which will not throw an exception upon
        /// validation exception.
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

public interface ISiloUserContext
{
    /// <summary>
    /// Gets whether this silo must open solo
    /// </summary>
    bool IsSoloSilo { get; }
    /// <summary>
    /// Gets whether this user can author the silo
    /// </summary>
    bool IsAuthorable { get; }
    /// <summary>
    /// Gets whether this user can write to the silo
    /// </summary>
    bool IsWritable   { get; }
    /// <summary>
    /// Gets whether this user can read/see/open the silo
    /// </summary>
    bool IsReadable   { get; }
    /// <summary>
    /// Gets whether this ussr can develop for this Silo
    /// </summary>
    bool IsDeveloper  { get; }
    /// <summary>
    /// Gets whether this user owns the silo
    /// </summary>
    bool IsOwner      { get; }
}