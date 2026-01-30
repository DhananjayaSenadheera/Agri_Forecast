namespace AgriForecast.Application.common;

public class Result<T>
{
    public bool IsSuccess { get; set; }
    public string Message { get; set; }
    public string Error { get; set; }
    public T Data { get; set; }
    public string Warnings { get; set; }
    public static Result<T> Success(T data) => new Result<T> { IsSuccess = true, Data = data };
    public static Result<T> Failure(string error) => new Result<T> { IsSuccess = false, Error = error };
    public static Result<T> SuccessWithWarnings(T data, string warnings) => new Result<T>
    {
        IsSuccess = true,
        Data = data,
        Warnings = warnings
    };
}