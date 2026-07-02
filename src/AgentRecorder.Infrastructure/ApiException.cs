using System;
namespace AgentRecorder.Infrastructure;
public class ApiException : Exception
{
    public int Status { get; }
    public string Code { get; }
    public object? Details { get; }
    public ApiException(int status, string code, string message, object? details = null) : base(message)
    { Status = status; Code = code; Details = details; }
}
