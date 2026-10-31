namespace AeroflyConv.Core;

/// <summary>Raised for invalid, unsupported or truncated Aerofly input.</summary>
public sealed class AeroflyFormatException : Exception
{
    public AeroflyFormatException(string message) : base(message) { }
    public AeroflyFormatException(string message, Exception inner) : base(message, inner) { }
}
