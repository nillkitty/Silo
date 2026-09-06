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
    bool ItemExists<TItem>(TItem item);
    bool RemoveItem<TItem>(TItem required);
    bool AddItem<TItem>(TItem    item);
}