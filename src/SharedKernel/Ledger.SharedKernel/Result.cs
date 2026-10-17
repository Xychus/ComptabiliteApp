namespace Ledger.SharedKernel;

/// <summary>
/// Erreur de domaine : un rejet attendu, pas une exception. <see cref="Details"/> précise la cause
/// (ex. <c>lineIndex</c> pour la ligne fautive) afin que l'API puisse la restituer telle quelle.
/// </summary>
public sealed record DomainError(string Code, string Message, IReadOnlyDictionary<string, string>? Details = null)
{
    public static DomainError WithDetails(string code, string message, params (string Key, string Value)[] details) =>
        new(code, message, details.ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal));
}

/// <summary>Résultat d'une opération qui peut être rejetée par une règle métier.</summary>
public sealed class Result<T>
{
    private readonly T? _value;
    private readonly DomainError? _error;

    internal Result(T value)
    {
        _value = value;
        IsSuccess = true;
    }

    internal Result(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        _error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Résultat en échec ({_error!.Code}) : aucune valeur.");

    public DomainError Error => IsFailure
        ? _error!
        : throw new InvalidOperationException("Résultat en succès : aucune erreur.");

    public Result<TOut> Map<TOut>(Func<T, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsSuccess ? Result.Success(map(_value!)) : Result.Failure<TOut>(_error!);
    }

    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return IsSuccess ? bind(_value!) : Result.Failure<TOut>(_error!);
    }

    public override string ToString() => IsSuccess ? $"Succès({_value})" : $"Échec({_error!.Code})";
}

/// <summary>Fabriques de <see cref="Result{T}"/>.</summary>
public static class Result
{
    public static Result<T> Success<T>(T value) => new(value);

    public static Result<T> Failure<T>(DomainError error) => new(error);
}
